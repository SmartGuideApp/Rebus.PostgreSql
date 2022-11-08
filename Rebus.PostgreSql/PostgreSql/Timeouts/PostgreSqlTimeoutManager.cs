﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NpgsqlTypes;
using Rebus.Internals;
using Rebus.Logging;
using Rebus.Serialization;
using Rebus.Time;
using Rebus.Timeouts;
// ReSharper disable AccessToDisposedClosure

#pragma warning disable 1998

namespace Rebus.PostgreSql.Timeouts;

/// <summary>
/// Implementation of <see cref="ITimeoutManager"/> that uses PostgreSql to do its thing. Can be used safely by multiple processes competing
/// over the same table of timeouts because row-level locking is used when querying for due timeouts.
/// </summary>
public class PostgreSqlTimeoutManager : ITimeoutManager
{
    readonly DictionarySerializer _dictionarySerializer = new();
    readonly IPostgresConnectionProvider _connectionHelper;
    readonly TableName _tableName;
    readonly IRebusTime _rebusTime;
    readonly ILog _log;

    /// <summary>
    /// Constructs the timeout manager
    /// </summary>
    public PostgreSqlTimeoutManager(IPostgresConnectionProvider connectionHelper, string tableName, IRebusLoggerFactory rebusLoggerFactory, IRebusTime rebusTime)
    {
        if (rebusLoggerFactory == null) throw new ArgumentNullException(nameof(rebusLoggerFactory));
        _connectionHelper = connectionHelper ?? throw new ArgumentNullException(nameof(connectionHelper));
        _tableName = new TableName(tableName ?? throw new ArgumentNullException(nameof(tableName)));
        _rebusTime = rebusTime ?? throw new ArgumentNullException(nameof(rebusTime));
        _log = rebusLoggerFactory.GetLogger<PostgreSqlTimeoutManager>();
    }

    /// <summary>
    /// Stores the message with the given headers and body data, delaying it until the specified <paramref name="approximateDueTime" />
    /// </summary>
    public async Task Defer(DateTimeOffset approximateDueTime, Dictionary<string, string> headers, byte[] body)
    {
        using var connection = await _connectionHelper.GetConnection();

        using var command = connection.CreateCommand();
        
        command.CommandText = $@"INSERT INTO {_tableName} (""due_time"", ""headers"", ""body"") VALUES (@due_time, @headers, @body)";

        command.Parameters.Add("due_time", NpgsqlDbType.Timestamp).Value = approximateDueTime.ToUniversalTime().DateTime;
        command.Parameters.Add("headers", NpgsqlDbType.Text).Value = _dictionarySerializer.SerializeToString(headers);
        command.Parameters.Add("body", NpgsqlDbType.Bytea).Value = body;

        await command.ExecuteNonQueryAsync();

        await connection.Complete();
    }

    /// <summary>
    /// Gets due messages as of now, given the approximate due time that they were stored with when <see cref="M:Rebus.Timeouts.ITimeoutManager.Defer(System.DateTimeOffset,System.Collections.Generic.Dictionary{System.String,System.String},System.Byte[])" /> was called
    /// </summary>
    public async Task<DueMessagesResult> GetDueMessages()
    {
        var connection = await _connectionHelper.GetConnection();

        try
        {
            using var command = connection.CreateCommand();
            
            command.CommandText =
                $@"

SELECT
    ""id"",
    ""headers"", 
    ""body"" 

FROM {_tableName} 

WHERE ""due_time"" <= @current_time 

ORDER BY ""due_time""

FOR UPDATE;

";
            command.Parameters.Add("current_time", NpgsqlDbType.Timestamp).Value = _rebusTime.Now.ToUniversalTime().DateTime;

            await using var reader = await command.ExecuteReaderAsync();
            var dueMessages = new List<DueMessage>();

            while (reader.Read())
            {
                var id = (long)reader["id"];
                var headers = _dictionarySerializer.DeserializeFromString((string) reader["headers"]);
                var body = (byte[]) reader["body"];

                dueMessages.Add(new DueMessage(headers, body, async () =>
                {
                    using var deleteCommand = connection.CreateCommand();
                    deleteCommand.CommandText = $@"DELETE FROM {_tableName} WHERE ""id"" = @id";
                    deleteCommand.Parameters.Add("id", NpgsqlDbType.Bigint).Value = id;
                    await deleteCommand.ExecuteNonQueryAsync();
                }));
            }

            return new DueMessagesResult(dueMessages, async () =>
            {
                await connection.Complete();
                connection.Dispose();
            });
        }
        catch (Exception)
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Checks if the configured timeouts table exists - if it doesn't, it will be created.
    /// </summary>
    public void EnsureTableIsCreated()
    {
        AsyncHelpers.RunSync(async () =>
        {
            using var connection = await _connectionHelper.GetConnection();
            
            var tableNames = connection.GetTableNames();

            if (tableNames.Contains(_tableName))
            {
                return;
            }

            _log.Info("Table {tableName} does not exist - it will be created now", _tableName);

            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    $@"
CREATE TABLE {_tableName} (
    ""id"" BIGSERIAL NOT NULL,
    ""due_time"" TIMESTAMP WITH TIME ZONE NOT NULL,
    ""headers"" TEXT NULL,
    ""body"" BYTEA NULL,
    PRIMARY KEY (""id"")
);
";

                command.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = $@"
CREATE INDEX ON {_tableName} (""due_time"");
";

                command.ExecuteNonQuery();
            }

            await connection.Complete();
        });
    }
}