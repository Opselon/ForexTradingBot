// File: Infrastructure/Data/SettingsDbHelper.cs
using System;
using System.Data;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Application.Common.Interfaces;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Data;

/// <summary>
/// Provider-agnostic read/write helper for the single-row JSON settings table.
/// </summary>
/// <remarks>
/// The settings table stores a JSON blob per key. PostgreSQL needs the value cast to
/// <c>jsonb</c>, SQLite stores it as TEXT and SQL Server as NVARCHAR(MAX), and only
/// SQL Server has a native <c>UPSERT</c>. This helper hides those differences so the
/// admin panel works identically on every database the installer supports — a
/// hard-coded NpgsqlConnection here is what made "it does not start on SQLite"
/// reports happen.
/// </remarks>
public static class SettingsDbHelper
{
    /// <summary>Reads and deserializes one settings row, or a caller-supplied default.</summary>
    public static async Task<T?> GetAsync<T>(IDbConnection connection, string key,
        Func<T> defaultFactory, ILogger logger, CancellationToken cancellationToken = default) where T : class
    {
        const string sql = "SELECT \"Value\" FROM \"Settings\" WHERE \"Key\" = @Key;";

        string? json = await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(sql, new { Key = key }, cancellationToken: cancellationToken));

        if (string.IsNullOrWhiteSpace(json))
        {
            logger.LogInformation("Settings key {SettingsKey} was not found; returning the default value.", key);
            return defaultFactory();
        }

        return JsonSerializer.Deserialize<T>(json);
    }

    /// <summary>Writes one settings row, inserting when it does not exist yet.</summary>
    public static async Task SetAsync<T>(IDbConnection connection, DatabaseProvider provider, string key,
        T value, ILogger logger, CancellationToken cancellationToken = default) where T : class
    {
        string json = JsonSerializer.Serialize(value);
        string sql = provider switch
        {
            // PostgreSQL: needs an explicit jsonb cast.
            DatabaseProvider.Postgres => """
                INSERT INTO "Settings" ("Key", "Value")
                VALUES (@Key, @JsonValue::jsonb)
                ON CONFLICT ("Key") DO UPDATE
                SET "Value" = EXCLUDED."Value";
                """,

            // SQLite: no UPSERT before 3.24, and the installer ships older builds,
            // so use the portable INSERT-OR-REPLACE form.
            DatabaseProvider.SQLite => """
                INSERT OR REPLACE INTO "Settings" ("Key", "Value")
                VALUES (@Key, @JsonValue);
                """,

            // SQL Server: no ON CONFLICT — split into update + conditional insert.
            DatabaseProvider.SqlServer => """
                IF EXISTS (SELECT 1 FROM "Settings" WHERE "Key" = @Key)
                    UPDATE "Settings" SET "Value" = @JsonValue WHERE "Key" = @Key;
                ELSE
                    INSERT INTO "Settings" ("Key", "Value") VALUES (@Key, @JsonValue);
                """,

            _ => throw new NotSupportedException($"Settings persistence is not implemented for provider '{provider}'.")
        };

        await connection.ExecuteAsync(
            new CommandDefinition(sql, new { Key = key, JsonValue = json }, cancellationToken: cancellationToken));

        logger.LogInformation("Settings key {SettingsKey} persisted to the {Provider} database.", key, provider);
    }

    /// <summary>Deletes one settings row. No-op when the row is already absent.</summary>
    public static async Task RemoveAsync(IDbConnection connection, string key, ILogger logger,
        CancellationToken cancellationToken = default)
    {
        const string sql = "DELETE FROM \"Settings\" WHERE \"Key\" = @Key;";
        await connection.ExecuteAsync(new CommandDefinition(sql, new { Key = key }, cancellationToken: cancellationToken));
        logger.LogInformation("Settings key {SettingsKey} removed.", key);
    }
}
