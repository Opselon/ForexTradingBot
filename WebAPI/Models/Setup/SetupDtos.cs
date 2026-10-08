namespace WebAPI.Models.Setup;

/// <summary>
/// Status of one piece of infrastructure the panel's setup wizard checks and repairs.
/// </summary>
public sealed record InfrastructureDependencyStatus(
    string Name,
    bool IsInstalled,
    bool IsConfigured,
    bool IsReachable,
    string CurrentValue,
    string Recommendation)
{
    public bool IsHealthy => IsInstalled && IsConfigured && IsReachable;

    public static InfrastructureDependencyStatus Healthy(string name, string currentValue) =>
        new(name, true, true, true, currentValue, "Configured and reachable.");

    public static InfrastructureDependencyStatus Missing(string name, string recommendation) =>
        new(name, false, false, false, string.Empty, recommendation);
}

/// <summary>
/// The shape returned by GET /api/setup/status — drives the setup wizard screen.
/// </summary>
public sealed record SetupStatusDto(
    bool FirstRunCompleted,
    string DatabaseProvider,
    bool DatabaseMigrated,
    bool SeedDataApplied,
    bool RedisReachable,
    bool HangfireRunning,
    bool TelegramBotConfigured,
    bool ForwardingRulesConfigured,
    bool HttpsConfigured,
    IReadOnlyList<InfrastructureDependencyStatus> Dependencies,
    string AspNetCoreUrls,
    string? ConfiguredDomain)
{
    /// <summary>True when every required dependency is healthy and the app is usable.</summary>
    public bool IsFullyConfigured =>
        DatabaseMigrated
        && SeedDataApplied
        && TelegramBotConfigured
        && ForwardingRulesConfigured;

    /// <summary>0-100 progress for the wizard progress bar.</summary>
    public int SetupProgress
    {
        get
        {
            int steps = 0;
            int done = 0;
            void Count(bool ok) { steps++; if (ok) done++; }

            Count(DatabaseMigrated);
            Count(SeedDataApplied);
            Count(RedisReachable);
            Count(HangfireRunning);
            Count(TelegramBotConfigured);
            Count(ForwardingRulesConfigured);

            return steps == 0 ? 0 : (int)Math.Round((double)done / steps * 100);
        }
    }
}

/// <summary>
/// Request body for POST /api/setup/database — applies migrations and seed data.
/// </summary>
public sealed record ApplyDatabaseRequest(
    string DatabaseProvider,
    string ConnectionString,
    bool ApplySeedData,
    bool OverwriteExistingSeed)
{
    public bool IsValid()
    {
        if (string.IsNullOrWhiteSpace(DatabaseProvider))
            return false;

        string p = DatabaseProvider.Trim().ToLowerInvariant();
        return p is "postgres" or "sqlserver" or "sqlite";
    }
}

public sealed record ApplyDatabaseResult(
    bool Success,
    string DatabaseProvider,
    string AppliedMigrations,
    int PendingMigrations,
    int SeededEntities,
    string[] Errors,
    string Message);

public sealed record RedisTestResult(
    bool Success,
    string Endpoint,
    long LatencyMs,
    string ServerVersion,
    string[] Errors,
    string Message);

public sealed record TelegramTestResult(
    bool Success,
    string BotUserName,
    long BotId,
    bool CanConnect,
    bool HasForwardingScope,
    string[] Errors,
    string Message);
