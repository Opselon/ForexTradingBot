// File: Infrastructure/DependencyInjection.cs

#region Usings
// --- Application ---
using Application.Common.Interfaces;
using Application.Common.Interfaces.CoinGeckoApiClient;
using Application.Common.Interfaces.Fred;
using Application.Features.Crypto.Services.CoinGecko;
using Application.Interfaces;
using Application.Services;
// --- Third-Party ---
using Hangfire;
using Hangfire.MemoryStorage;
using Hangfire.PostgreSql;
using Hangfire.Storage.SQLite;
// --- Infrastructure ---
using Infrastructure.Caching;
using Infrastructure.ExternalServices;
using Infrastructure.Hangfire;
using Infrastructure.Persistence.Configurations;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Infrastructure.Services.Admin;
using Infrastructure.Services.CoinGecko;
using Infrastructure.Services.Fmp;
// --- Microsoft ---
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;
using Polly.Retry;
using Serilog;
using StackExchange.Redis;
using System.Net;
using System.Net.Http.Headers;
#endregion

namespace Infrastructure.Data
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddInfrastructureServices(
            this IServiceCollection services,
            IConfiguration configuration,
            bool isSmokeTest)
        {
            // =================================================================
            // --- 1. DATABASE AND HANGFIRE CONFIGURATION ---
            // =================================================================
            #region Database and Hangfire Setup

            _ = services.AddSingleton<DbProviderService>();
            _ = services.AddSingleton<UserSqlProvider>();

            // Try primary connection string
            string? connectionString = configuration.GetConnectionString("DefaultConnection");
            string? dbProvider = configuration.GetValue<string>("DatabaseSettings:DatabaseProvider")?.ToLowerInvariant();


            // -----------------------------------------------------------------
            // Fallback: auto-build connection string for self-contained Postgres
            // -----------------------------------------------------------------
            // This matches your "download Postgres & run it" flow.
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                IConfigurationSection selfContainedSection = configuration.GetSection("SelfContainedPostgres");

                string host = selfContainedSection.GetValue<string>("Host", "localhost");
                int port = selfContainedSection.GetValue<int>("Port", 5433); // matches your process logs
                string database = selfContainedSection.GetValue<string>("Database", "forex_local_db");
                string username = selfContainedSection.GetValue<string>("Username", "forex_user");
                string? password = selfContainedSection.GetValue<string>("Password");

                if (!string.IsNullOrWhiteSpace(password))
                {
                    // Build a safe, pooled Postgres connection string dynamically
                    connectionString =
                        $"Host={host};Port={port};Database={database};Username={username};Password={password};Pooling=true;";

                    if (string.IsNullOrEmpty(dbProvider))
                    {
                        dbProvider = "postgres";
                    }

                    Log.Information(
                        "DefaultConnection not found. Using self-contained PostgreSQL connection string for local environment (Host={Host}, Port={Port}, Database={Database}, User={User}).",
                        host,
                        port,
                        database,
                        username);
                }
            }

            // -------------------------
            // SMOKE TEST BRANCH
            // -------------------------
            if (isSmokeTest)
            {
                // For smoke tests, use a fast, in-memory database
                _ = services.AddDbContext<AppDbContext>(options =>
                    options.UseInMemoryDatabase("SmokeTestDatabase"));

                // Use in-memory storage for Hangfire during smoke tests
                _ = services.AddHangfire(config =>
                {
                    _ = config.UseSerializerSettings(new Newtonsoft.Json.JsonSerializerSettings
                    {
                        TypeNameHandling = Newtonsoft.Json.TypeNameHandling.All
                    });

                    _ = config.UseMemoryStorage();
                    Log.Information("Hangfire configured with in-memory storage for smoke tests.");
                });
            }
            else
            {
                // -------------------------
                // REAL ENVIRONMENT BRANCH
                // -------------------------

                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    // At this point:
                    //  - DefaultConnection is missing
                    //  - SelfContainedPostgres either missing or missing password
                    // => unsafe to guess credentials, so we fail hard.
                    throw new InvalidOperationException(
                        "No database connection string could be resolved. " +
                        "Ensure either 'ConnectionStrings:DefaultConnection' is configured, " +
                        "or provide 'SelfContainedPostgres:Host/Port/Database/Username/Password' for the embedded PostgreSQL.");
                }

                // From here on, connectionString is guaranteed non-null
                string conn = connectionString;

                // Detect provider if not explicitly set
                if (string.IsNullOrEmpty(dbProvider))
                {
                    dbProvider = conn.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase) ||
                        conn.Contains("postgres", StringComparison.OrdinalIgnoreCase)
                        ? "postgres"
                        : conn.Contains("Server=", StringComparison.OrdinalIgnoreCase)
                            ? "sqlserver"
                            : conn.Contains("Data Source=", StringComparison.OrdinalIgnoreCase)
                            ? "sqlite"
                            : throw new NotSupportedException(
                                "Database provider could not be inferred safely from the connection string. " +
                                "Configure 'DatabaseSettings:DatabaseProvider' explicitly.");

                    Log.Information("Database provider auto-detected as: {Provider}", dbProvider);
                }

                // --- 1. Configure the Main DbContext (fatal on failure) ---
                _ = dbProvider switch
                {
                    "sqlite" => services.AddDbContext<AppDbContext>(opts =>
                                                opts.UseSqlite(conn, sqlite =>
                                                    sqlite.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))),
                    "postgres" or "postgresql" => services.AddDbContextPool<AppDbContext>(opts =>
                                                    opts.UseNpgsql(conn, npgsql =>
                                                        npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName)),
                                                    poolSize: 32),
                    "sqlserver" => services.AddDbContext<AppDbContext>(opts =>
                                                opts.UseSqlServer(conn, sql =>
                                                    sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))),
                    _ => throw new NotSupportedException($"Unsupported DatabaseProvider: '{dbProvider}'."),
                };

                // --- 2. Configure Hangfire with durable database storage ---
                _ = services.AddHangfire(config =>
                {
                    _ = config.UseSerializerSettings(new Newtonsoft.Json.JsonSerializerSettings
                    {
                        TypeNameHandling = Newtonsoft.Json.TypeNameHandling.All
                    });

                    try
                    {
                        Log.Information(
                            "Attempting to configure Hangfire with '{DbProvider}' database storage.",
                            dbProvider);

                        switch (dbProvider)
                        {
                            case "sqlite":
                                _ = config.UseSQLiteStorage(conn);
                                Log.Information("Hangfire successfully configured with SQLite storage.");
                                break;

                            case "postgres":
                            case "postgresql":
                                _ = config.UsePostgreSqlStorage(
                                    options => options.UseNpgsqlConnection(conn),
                                    new PostgreSqlStorageOptions
                                    {
                                        QueuePollInterval = TimeSpan.FromSeconds(5),
                                        JobExpirationCheckInterval = TimeSpan.FromHours(6)
                                    });

                                Log.Information("Hangfire successfully configured with PostgreSQL storage and tuned intervals.");
                                break;

                            case "sqlserver":
                                _ = config.UseSqlServerStorage(conn);
                                Log.Information("Hangfire successfully configured with SQL Server storage.");
                                break;

                            default:
                                throw new NotSupportedException(
                                    $"Unsupported Hangfire DB provider: '{dbProvider}'.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(
                            ex,
                            "FAILED to configure Hangfire with database storage. The application will not start with a silently non-persistent Hangfire backend.");

                        throw new InvalidOperationException(
                            $"Failed to configure Hangfire database storage for provider '{dbProvider}'.",
                            ex);
                    }
                });
            }

            // HttpClient for crypto price service
            _ = services.AddHttpClient<ICryptoPriceService, CoinGeckoPriceService>();

            // Hangfire cleaner registrations
            _ = services.AddTransient<IHangfireCleaner, HangfireCleaner>();
            _ = services.AddScoped<IHangfireCleaner, HangfireCleaner>();

            // Register the Scoped DbContext interface
            _ = services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

            _ = services.AddSingleton<IAdviceService, AdviceService>();
            _ = services.AddSingleton<IHashtagService, HashtagService>();
            _ = services.AddSingleton<MarkdownParserService>();
            _ = services.AddScoped<IProMonitoringLogRepository, ProMonitoringLogRepository>();

            #endregion

            _ = services.AddSingleton<IExternalDependencyManager, ExternalDependencyManager>();

            // =================================================================
            // --- 2. DAPPER CONNECTION FACTORY REGISTRATION ---
            // =================================================================
            #region Dapper Connection Factory

            _ = services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();

            #endregion

            // =================================================================
            // --- 3. CACHING AND EXTERNAL SERVICES ---
            // =================================================================
            #region Caching and External Services

            _ = services.AddMemoryCache();
            _ = services.AddSingleton(typeof(IMemoryCacheService<>), typeof(MemoryCacheService<>));

            string? redisConnectionString = configuration.GetConnectionString("Redis");

            if (!string.IsNullOrWhiteSpace(redisConnectionString))
            {
                try
                {
                    _ = services.AddSingleton<IConnectionMultiplexer>(
                        ConnectionMultiplexer.Connect(redisConnectionString));

                    Console.WriteLine("✅ Redis is configured and will be used for distributed caching and features.");
                }
                catch (RedisConnectionException ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(
                        $"FATAL ERROR: Could not connect to Redis using the provided connection string. " +
                        $"Please check the server and configuration. Error: {ex.Message}");
                    Console.ResetColor();
                    throw;
                }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(
                    "⚠️ Redis connection string not found. Caching will be in-memory and non-persistent. " +
                    "Distributed features like global image deduplication will be disabled.");
                Console.ResetColor();
            }

            AsyncRetryPolicy<HttpResponseMessage> retryPolicy = HttpPolicyExtensions
               .HandleTransientHttpError()
               .OrResult(msg => msg.StatusCode == HttpStatusCode.TooManyRequests)
               .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

            _ = services.Configure<RssReaderServiceSettings>(
                configuration.GetSection(RssReaderServiceSettings.ConfigurationSectionName));

            _ = services.AddHttpClient(RssReaderService.HttpClientNamedClient, (serviceProvider, client) =>
            {
                RssReaderServiceSettings settings =
                    serviceProvider.GetRequiredService<IOptions<RssReaderServiceSettings>>().Value;

                client.DefaultRequestHeaders.UserAgent.ParseAdd(settings.UserAgent);
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/rss+xml"));
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip |
                                          DecompressionMethods.Deflate |
                                          DecompressionMethods.Brotli
            });

            _ = services.AddHttpClient<ICoinGeckoApiClient, CoinGeckoApiClient>()
                        .AddPolicyHandler(retryPolicy);

            _ = services.AddHttpClient<IFmpApiClient, FmpApiClient>()
                        .AddPolicyHandler(retryPolicy);

            _ = services.AddHttpClient<IFredApiClient, FredApiClient>();

            #endregion

            // =================================================================
            // --- 4. APPLICATION SERVICES AND REPOSITORIES ---
            // =================================================================
            #region Application Services and Repositories

            _ = services.AddSingleton<ITelegramUserApiClient, TelegramUserApiClient>();
            _ = services.AddSingleton<TelegramUserApiClient>();
            _ = services.AddHostedService<TelegramUserApiInitializationService>();

            _ = services.AddScoped<IAiApiConfigurationRepository, AiApiConfigurationRepository>();
            _ = services.AddScoped<IRssFetchingCoordinatorService, RssFetchingCoordinatorService>();
            _ = services.AddScoped<IRssReaderService, RssReaderService>();
            _ = services.AddTransient<INotificationJobScheduler, HangfireJobScheduler>();
            _ = services.AddScoped<INotificationDispatchService, NotificationDispatchService>();
            _ = services.AddScoped<IAdminService, AdminService>();
            _ = services.AddScoped<ISettingsService, SettingsService>();
            _ = services.AddScoped<INewsItemRepository, NewsItemRepository>();
            _ = services.AddScoped<IUserRepository, UserRepository>();
            _ = services.AddScoped<ITokenWalletRepository, TokenWalletRepository>();
            _ = services.AddScoped<ISubscriptionRepository, SubscriptionRepository>();
            _ = services.AddScoped<ISignalRepository, SignalRepository>();
            _ = services.AddScoped<ISignalCategoryRepository, SignalCategoryRepository>();
            _ = services.AddScoped<IRssSourceRepository, RssSourceRepository>();
            _ = services.AddScoped<IUserSignalPreferenceRepository, UserSignalPreferenceRepository>();
            _ = services.AddScoped<ISignalAnalysisRepository, SignalAnalysisRepository>();
            _ = services.AddScoped<ITransactionRepository, TransactionRepository>();

            #endregion

            return services;
        }
    }
}
