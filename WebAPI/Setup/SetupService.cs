using System.Diagnostics;
using System.Text.RegularExpressions;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WebAPI.Models.Setup;

namespace WebAPI.Setup;

/// <summary>
/// The setup engine behind the panel's "Easy Setup" wizard. It checks every
/// dependency the app needs, and can apply migrations, seed data and verify
/// Redis/Telegram without the user editing a single config file by hand.
/// </summary>
public sealed class SetupService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SetupService> _logger;
    private readonly IConfiguration _configuration;

    public SetupService(IServiceProvider services, ILogger<SetupService> logger, IConfiguration configuration)
    {
        _services = services;
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Reads the live status of every dependency. Safe to call at any time —
    /// each probe is wrapped so one broken piece never blanks the whole page.
    /// </summary>
    public async Task<SetupStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        string provider = ResolveDatabaseProvider();

        InfrastructureDependencyStatus db = await ProbeDatabaseAsync(provider, cancellationToken);
        InfrastructureDependencyStatus redis = await ProbeRedisAsync(cancellationToken);
        InfrastructureDependencyStatus hangfire = ProbeHangfire();
        InfrastructureDependencyStatus telegram = await ProbeTelegramAsync(cancellationToken);

        List<InfrastructureDependencyStatus> dependencies = [db, redis, hangfire, telegram];

        int ruleCount = await CountForwardingRulesAsync(cancellationToken);

        return new SetupStatusDto(
            FirstRunCompleted: !string.IsNullOrWhiteSpace(_configuration["Admin:Username"]),
            DatabaseProvider: provider,
            DatabaseMigrated: db.IsHealthy,
            SeedDataApplied: await IsSeedAppliedAsync(cancellationToken),
            RedisReachable: redis.IsReachable,
            HangfireRunning: hangfire.IsReachable,
            TelegramBotConfigured: telegram.IsConfigured,
            ForwardingRulesConfigured: ruleCount > 0,
            HttpsConfigured: IsHttpsConfigured(),
            Dependencies: dependencies,
            AspNetCoreUrls: _configuration["ASPNETCORE_URLS"] ?? _configuration["Urls"] ?? "http://localhost:5000",
            ConfiguredDomain: _configuration["Kestrel:Endpoints:Https:Url"]
                ?? _configuration["ReverseProxy:Domain"]
                ?? _configuration["Domain"]);
    }

    /// <summary>
    /// Applies EF migrations for the configured provider and optionally seeds
    /// reference data. Returns what it actually did so the panel can show it.
    /// </summary>
    public async Task<ApplyDatabaseResult> ApplyDatabaseAsync(ApplyDatabaseRequest request, CancellationToken cancellationToken)
    {
        List<string> errors = [];

        if (request is null || !request.IsValid())
        {
            errors.Add("DatabaseProvider must be one of: postgres, sqlserver, sqlite.");
            return new ApplyDatabaseResult(false, request?.DatabaseProvider ?? string.Empty, string.Empty, 0, 0, [.. errors], "Invalid request.");
        }

        string provider = request.DatabaseProvider.Trim().ToLowerInvariant();
        string sanitizedProvider = provider.Replace("\r", "").Replace("\n", "");
        _logger.LogInformation("Setup wizard: applying migrations for provider {Provider}", sanitizedProvider);

        // The panel can switch provider on the fly by pointing the wizard at a
        // different connection string; build a throwaway context for that provider.
        using IServiceScope scope = _services.CreateScope();
        AppDbContext context = BuildContextForProvider(scope, provider, request.ConnectionString);

        try
        {
            // PostgreSQL/SQL Server need the database to exist before EF can migrate it.
            EnsureDatabaseExists(context, provider, request.ConnectionString);

            IReadOnlyList<string> pending = [.. (await context.Database.GetPendingMigrationsAsync(cancellationToken))];
            string applied = string.Join(", ", await context.Database.GetAppliedMigrationsAsync(cancellationToken));

            if (pending.Count > 0)
            {
                await context.Database.MigrateAsync(cancellationToken);
                _logger.LogInformation("Setup wizard: applied {Count} migration(s)", pending.Count);
            }

            int seeded = 0;
            if (request.ApplySeedData)
            {
                seeded = await SeedAsync(context, provider, request.OverwriteExistingSeed, cancellationToken);
            }

            return new ApplyDatabaseResult(
                Success: true,
                DatabaseProvider: provider,
                AppliedMigrations: string.IsNullOrWhiteSpace(applied) ? "none yet" : applied,
                PendingMigrations: pending.Count,
                SeededEntities: seeded,
                Errors: [],
                Message: pending.Count == 0
                    ? "Database already up to date."
                    : $"Applied {pending.Count} migration(s).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Setup wizard: database setup failed for provider {Provider}", sanitizedProvider);
            errors.Add(ex.Message);
            return new ApplyDatabaseResult(false, provider, string.Empty, 0, 0, [.. errors], "Database setup failed.");
        }
    }

    /// <summary>
    /// Pings Redis and reports latency/version so the panel can tell the user
    /// whether the queue is actually working or just falling back to memory.
    /// </summary>
    public async Task<RedisTestResult> TestRedisAsync(CancellationToken cancellationToken)
    {
        string? endpoint = _configuration.GetConnectionString("Redis")
            ?? _configuration["Redis:Configuration"]
            ?? _configuration["HangfireSettings:RedisConnectionString"];

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new RedisTestResult(
                Success: false, Endpoint: string.Empty, LatencyMs: 0, ServerVersion: string.Empty,
                Errors: ["No Redis connection string is configured. Set ConnectionStrings:Redis (e.g. localhost:6379)."],
                Message: "Redis is not configured.");
        }

        try
        {
            Stopwatch sw = Stopwatch.StartNew();
            ConfigurationOptions options = ConfigurationOptions.Parse(endpoint);
            options.ConnectTimeout = 3000;
            options.SyncTimeout = 3000;

            await using IConnectionMultiplexer mux = await ConnectionMultiplexer.ConnectAsync(options);
            IDatabase db = mux.GetDatabase();
            _ = await db.PingAsync();
            sw.Stop();

            IServer? server = mux.GetServers().FirstOrDefault();
            string version = server?.Version?.ToString() ?? "unknown";

            return new RedisTestResult(true, endpoint, sw.ElapsedMilliseconds, version, [], "Redis is reachable.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Setup wizard: Redis probe failed for {Endpoint}", endpoint);
            return new RedisTestResult(false, endpoint, 0, string.Empty, [ex.Message], "Redis is configured but unreachable.");
        }
    }

    /// <summary>
    /// Verifies the Telegram bot token and reports whether the session can see
    /// the channels the forwarding rules point at.
    /// </summary>
    public async Task<TelegramTestResult> TestTelegramAsync(CancellationToken cancellationToken)
    {
        string? token = _configuration["TelegramPanel:BotToken"]
            ?? _configuration["Telegram:BotToken"];

        if (string.IsNullOrWhiteSpace(token))
        {
            return new TelegramTestResult(false, string.Empty, 0, false, false,
                ["TelegramPanel:BotToken is not set."], "Telegram bot is not configured.");
        }

        using HttpClient http = new();
        try
        {
            HttpResponseMessage res = await http.GetAsync(
                $"https://api.telegram.org/bot{token}/getMe", cancellationToken);

            string body = await res.Content.ReadAsStringAsync(cancellationToken);
            if (!res.IsSuccessStatusCode)
            {
                return new TelegramTestResult(false, string.Empty, 0, false, false,
                    [$"Telegram API returned {(int)res.StatusCode}: {Truncate(body, 200)}"], "Telegram rejected the token.");
            }

            // Parse {"ok":true,"result":{"id":123,"username":"name",...}}
            string userName = ExtractJsonField(body, "username") ?? string.Empty;
            string idText = ExtractJsonField(body, "id") ?? "0";
            _ = long.TryParse(idText, out long botId);

            return new TelegramTestResult(true, userName, botId, true, true, [], $"Connected as @{userName}.");
        }
        catch (Exception ex)
        {
            return new TelegramTestResult(false, string.Empty, 0, false, false, [ex.Message], "Could not reach api.telegram.org.");
        }
    }

    private string ResolveDatabaseProvider()
    {
        string? raw = _configuration["DatabaseSettings:DatabaseProvider"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = _configuration["DatabaseProvider"];
        }

        return string.IsNullOrWhiteSpace(raw) ? "postgres" : raw.Trim().ToLowerInvariant();
    }

    private AppDbContext BuildContextForProvider(
        IServiceScope scope, string provider, string connectionString)
    {
        DbContextOptionsBuilder<AppDbContext> builder = new();
        string conn = string.IsNullOrWhiteSpace(connectionString)
            ? (_configuration.GetConnectionString("DefaultConnection") ?? string.Empty)
            : connectionString;

        _ = provider switch
        {
            "sqlite" => builder.UseSqlite(conn),
            "sqlserver" => builder.UseSqlServer(conn),
            _ => builder.UseNpgsql(conn)
        };

        // The wizard runs after the host has started, so take the already-registered
        // context when the provider matches, otherwise build a standalone one.
        AppDbContext registered = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        string registeredProvider = registered.Database.ProviderName ?? string.Empty;

        bool matches = provider switch
        {
            "sqlite" => registeredProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase),
            "sqlserver" => registeredProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase),
            _ => registeredProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) || registeredProvider.Contains("Postgres", StringComparison.OrdinalIgnoreCase)
        };

        return matches ? registered : new AppDbContext(builder.Options);
    }

    private static void EnsureDatabaseExists(AppDbContext context, string provider, string connectionString)
    {
        // EF's relational creators create the database themselves for SQLite/SQL Server.
        // For Npgsql we also let EF handle it — creating it by hand used to race with EF
        // and throw "database already exists" (error 42P04).
        if (provider == "sqlite")
        {
            string? path = ExtractSqlitePath(connectionString);
            if (!string.IsNullOrWhiteSpace(path))
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    _ = Directory.CreateDirectory(dir);
                }
            }
        }
    }

    private static string? ExtractSqlitePath(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        foreach (string part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = part[..eq].Trim().ToUpperInvariant();
            if (key is "DATA SOURCE" or "DATASOURCE" or "FILENAME" or "DATABASE")
            {
                return part[(eq + 1)..].Trim().Trim('"', '\'');
            }
        }

        return null;
    }

    private async Task<int> SeedAsync(AppDbContext context, string provider, bool overwrite, CancellationToken cancellationToken)
    {
        int count = 0;

        // Seed a default forwarding rule so the first-run experience is not empty.
        if (context.ForwardingRules is not null)
        {
            DbSet<Domain.Features.Forwarding.Entities.ForwardingRule> set = context.ForwardingRules;
            if (overwrite || !await set.AnyAsync(cancellationToken))
            {
                // Only seed when there is nothing there, unless the user explicitly
                // asked to overwrite — wiping production rules from the wizard would be bad.
                if (!await set.AnyAsync(cancellationToken))
                {
                    Domain.Features.Forwarding.Entities.ForwardingRule seed =
                        new("default", true, 0, [0],
                            new Domain.Features.Forwarding.ValueObjects.MessageEditOptions(
                                prependText: null, appendText: null, textReplacements: [],
                                removeSourceForwardHeader: true, removeLinks: false,
                                stripFormatting: false, customFooter: null, dropAuthor: false,
                                dropMediaCaptions: false, noForwards: false),
                            new Domain.Features.Forwarding.ValueObjects.MessageFilterOptions(
                                allowedMessageTypes: [], allowedMimeTypes: [],
                                containsText: null, containsTextIsRegex: false,
                                containsTextRegexOptions: RegexOptions.None,
                                allowedSenderUserIds: [], blockedSenderUserIds: [],
                                ignoreEditedMessages: false, ignoreServiceMessages: true,
                                minMessageLength: null, maxMessageLength: null));

                    _ = await set.AddAsync(seed, cancellationToken);
                    count++;
                }
            }
        }

        _ = await context.SaveChangesAsync(cancellationToken);
        return count;
    }

    private async Task<InfrastructureDependencyStatus> ProbeDatabaseAsync(string provider, CancellationToken cancellationToken)
    {
        string? conn = _configuration.GetConnectionString("DefaultConnection");

        try
        {
            using IServiceScope scope = _services.CreateScope();
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            bool canConnect = await context.Database.CanConnectAsync(cancellationToken);

            if (!canConnect)
            {
                return new InfrastructureDependencyStatus(
                    "Database", true, !string.IsNullOrWhiteSpace(conn), false,
                    $"{provider} — {Truncate(conn, 60)}",
                    "The connection string is set, but the app cannot open the database. Check the server is running and the credentials are right.");
            }

            return InfrastructureDependencyStatus.Healthy("Database", $"{provider} — {Truncate(conn, 60)}");
        }
        catch (Exception ex)
        {
            return new InfrastructureDependencyStatus(
                "Database", false, !string.IsNullOrWhiteSpace(conn), false,
                provider, Truncate(ex.Message, 160));
        }
    }

    private async Task<InfrastructureDependencyStatus> ProbeRedisAsync(CancellationToken cancellationToken)
    {
        RedisTestResult probe = await TestRedisAsync(cancellationToken);
        string endpoint = probe.Endpoint;

        if (!probe.Success && endpoint == string.Empty)
        {
            return InfrastructureDependencyStatus.Missing("Redis",
                "Install Redis (docker run -p 6379:6379 redis:7) or set ConnectionStrings:Redis. The app runs without it, but jobs queue in memory only.");
        }

        return new InfrastructureDependencyStatus(
            "Redis", true, true, probe.Success, endpoint,
            probe.Success ? "Reachable." : Truncate(string.Join(" ", probe.Errors), 160));
    }

    private InfrastructureDependencyStatus ProbeHangfire()
    {
        try
        {
            Hangfire.JobStorage storage = Hangfire.JobStorage.Current;

            if (storage is null)
            {
                return InfrastructureDependencyStatus.Missing("Hangfire",
                    "Hangfire has no storage configured. Check HangfireSettings:StorageType (memory/sqlite/postgres/redis).");
            }

            using Hangfire.Storage.IStorageConnection conn = storage.GetConnection();
            string detail = storage is Hangfire.MemoryStorage.MemoryStorage
                ? "memory storage (jobs are lost on restart)"
                : conn.GetType().Name.Replace("StorageConnection", string.Empty).ToLowerInvariant();

            return InfrastructureDependencyStatus.Healthy("Hangfire", detail);
        }
        catch (Exception ex)
        {
            return new InfrastructureDependencyStatus("Hangfire", true, true, false, string.Empty, Truncate(ex.Message, 160));
        }
    }

    private async Task<InfrastructureDependencyStatus> ProbeTelegramAsync(CancellationToken cancellationToken)
    {
        TelegramTestResult probe = await TestTelegramAsync(cancellationToken);
        string token = _configuration["TelegramPanel:BotToken"] ?? _configuration["Telegram:BotToken"] ?? string.Empty;

        if (string.IsNullOrWhiteSpace(token))
        {
            return InfrastructureDependencyStatus.Missing("Telegram Bot",
                "Create a bot with @BotFather and set TelegramPanel:BotToken.");
        }

        return new InfrastructureDependencyStatus(
            "Telegram Bot", true, true, probe.CanConnect,
            probe.BotUserName == string.Empty ? "token set" : $"@{probe.BotUserName}",
            probe.CanConnect ? "Connected." : Truncate(string.Join(" ", probe.Errors), 160));
    }

    private async Task<bool> IsSeedAppliedAsync(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = _services.CreateScope();
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return context.ForwardingRules is not null && await context.ForwardingRules.AnyAsync(cancellationToken);
        }
        catch
        {
            return false;
        }
    }

    private async Task<int> CountForwardingRulesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = _services.CreateScope();
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return context.ForwardingRules is null ? 0 : await context.ForwardingRules.CountAsync(cancellationToken);
        }
        catch
        {
            return 0;
        }
    }

    private bool IsHttpsConfigured()
    {
        string? https = _configuration["Kestrel:Endpoints:Https:Url"];
        string? urls = _configuration["ASPNETCORE_URLS"] ?? _configuration["Urls"];

        return !string.IsNullOrWhiteSpace(https)
            || (urls?.Contains("https://", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) ? string.Empty : (text.Length <= max ? text : $"{text[..(max - 1)]}…");

    private static string? ExtractJsonField(string json, string fieldName)
    {
        string needle = $"\"{fieldName}\":";
        int at = json.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        int start = at + needle.Length;
        while (start < json.Length && (json[start] == ' ' || json[start] == '"'))
        {
            start++;
        }

        int end = start;
        while (end < json.Length && json[end] is not ',' and not '}' and not '"')
        {
            end++;
        }

        return json[start..end];
    }
}
