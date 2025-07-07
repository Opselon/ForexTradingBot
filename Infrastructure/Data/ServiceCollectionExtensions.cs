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
using Infrastructure.Persistence; // For DbConnectionFactory
using Infrastructure.Data;
using Infrastructure.Services;
using Infrastructure.Services.Admin;
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
using Infrastructure.Persistence.Configurations;
using Infrastructure.Repositories;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Infrastructure.Services;
#endregion

namespace Infrastructure.Data
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddInfrastructureServices(
            this IServiceCollection services,
            IConfiguration configuration, bool isSmokeTest)
        {
            // =================================================================
            // --- 1. DATABASE AND HANGFIRE CONFIGURATION ---
            // =================================================================
            #region Database and Hangfire Setup
            _ = services.AddSingleton<DbProviderService>();
            _ = services.AddSingleton<UserSqlProvider>();
            if (isSmokeTest)
            {
                // For smoke tests, use a fast, in-memory database
                _ = services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase("SmokeTestDatabase"));

                // Use in-memory storage for Hangfire during smoke tests
                _ = services.AddHangfire(config => config.UseMemoryStorage());
            }
            else // Not a smoke test, configure for real environments
            {
                string? dbProvider = configuration.GetValue<string>("DatabaseSettings:DatabaseProvider")?.ToLowerInvariant();
                string? connectionString = configuration.GetConnectionString("DefaultConnection");

                // --- IMPROVED: Don't auto-default to SQLite, let Program.cs handle user prompt ---
                if (string.IsNullOrEmpty(connectionString))
                {
                    throw new InvalidOperationException(
                        "DefaultConnection string not found in configuration. " +
                        "The application should prompt the user for database connection details in Program.cs before reaching this point.");
                }

                if (string.IsNullOrEmpty(dbProvider))
                {
                    // Try to detect provider from connection string
                    if (connectionString.Contains("PostgreSQL") || connectionString.Contains("postgres"))
                    {
                        dbProvider = "postgres";
                    }
                    else if (connectionString.Contains("Server=") || connectionString.Contains("Data Source="))
                    {
                        dbProvider = "sqlserver";
                    }
                    else
                    {
                        dbProvider = "sqlite"; // Default fallback only if we can't detect
                    }

                    Log.Information("Database provider auto-detected as: {Provider}", dbProvider);
                }

                // --- 1. Configure the Main DbContext First ---
                // We do this separately because a failure here should be fatal. The app can't run without its main DB.
                switch (dbProvider)
                {

                    case "sqlite":
                        _ = services.AddDbContext<AppDbContext>(opts =>
                            opts.UseSqlite(connectionString, sqlite =>
                                sqlite.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName)));

                        // --- ADDED: Hangfire config for SQLite ---
                        _ = services.AddHangfire(config => config.UseSQLiteStorage(connectionString));
                        break;

                    case "postgres":
                    case "postgresql":
                        _ = services.AddDbContextPool<AppDbContext>(opts =>
                            opts.UseNpgsql(connectionString, npgsql =>
                                npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName)),
                            poolSize: 32);

                        // --- MODIFIED THIS SECTION ---
                        _ = services.AddHangfire(config =>
                    {
                        _ = config.UseSerializerSettings(new Newtonsoft.Json.JsonSerializerSettings
                        {
                            TypeNameHandling = Newtonsoft.Json.TypeNameHandling.All
                        });

                        // Configure Hangfire to use PostgreSQL storage with tuned options:
                        _ = config.UsePostgreSqlStorage(
                            options => options.UseNpgsqlConnection(connectionString),
                            new PostgreSqlStorageOptions // <-- This is now recognized
                            {
                                // Balance DB load vs. responsiveness
                                QueuePollInterval = TimeSpan.FromSeconds(5),
                                // How often to scan for expired jobs (cleanup)
                                JobExpirationCheckInterval = TimeSpan.FromHours(6) // Set to a longer duration
                            }
                        );
                        Log.Information("✅ Hangfire successfully configured with PostgreSQL storage and tuned intervals.");
                    });
                        break;
                    case "sqlserver":
                        _ = services.AddDbContext<AppDbContext>(opts =>
                            opts.UseSqlServer(connectionString, sql =>
                                sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName)));

                        // --- ADDED: Hangfire config for SQL Server ---
                        _ = services.AddHangfire(config => config.UseSqlServerStorage(connectionString));
                        break;

                    default:
                        throw new NotSupportedException($"Unsupported DatabaseProvider: '{dbProvider}'.");
                }

                // --- 2. Configure Hangfire with a Resilient Fallback ---
                _ = services.AddHangfire(config =>
                    {
                        _ = config.UseSerializerSettings(new Newtonsoft.Json.JsonSerializerSettings
                        {
                            TypeNameHandling = Newtonsoft.Json.TypeNameHandling.All
                        });

                        try
                        {
                            Log.Information("Attempting to configure Hangfire with '{DbProvider}' database storage.", dbProvider);
                            switch (dbProvider)
                            {
                                case "sqlite":
                                    _ = config.UseSQLiteStorage(connectionString);
                                    Log.Information("✅ Hangfire successfully configured with SQLite storage.");
                                    break;

                                case "postgres":
                                case "postgresql":
                                    _ = config.UsePostgreSqlStorage(options => options.UseNpgsqlConnection(connectionString));
                                    Log.Information("✅ Hangfire successfully configured with PostgreSQL storage.");
                                    break;

                                case "sqlserver":
                                    _ = config.UseSqlServerStorage(connectionString);
                                    Log.Information("✅ Hangfire successfully configured with SQL Server storage.");
                                    break;

                                default:
                                    // This case should ideally not be hit due to the check above, but as a safeguard:
                                    Log.Warning("Unsupported Hangfire DB provider '{DbProvider}'. Falling back to in-memory storage.", dbProvider);
                                    _ = config.UseMemoryStorage();
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            // --- THIS IS THE FALLBACK LOGIC ---
                            Log.Error(ex, "FAILED to configure Hangfire with database storage. The database may be offline or the connection string is invalid. FALLING BACK TO IN-MEMORY STORAGE.");
                            Log.Warning("Hangfire jobs will NOT be persisted. They will be lost if the application restarts.");

                            // Re-configure with MemoryStorage on failure
                            _ = config.UseMemoryStorage();
                        }
                    });
            }
            _ =  services.AddHttpClient<ICryptoPriceService, CoinGeckoPriceService>();
            // This original line registers the IHangfireCleaner.
            _ = services.AddTransient<IHangfireCleaner, HangfireCleaner>();

            // ✅ UPGRADE: Re-registering with Scoped lifetime. Scoped is safer for services doing database work
            // within a request or job, ensuring they get fresh dependencies. This new registration
            // will be used by the DI container instead of the Transient one above for new resolutions.
            _ = services.AddScoped<IHangfireCleaner, HangfireCleaner>();

            _ = services.AddSingleton<UserSqlProvider>();
            // Register the Scoped DbContext interface.
            _ = services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
            _ = services.AddSingleton<IAdviceService, AdviceService>();
            _ = services.AddSingleton<IHashtagService, HashtagService>();
            #endregion

            // =================================================================
            // --- 2. DAPPER CONNECTION FACTORY REGISTRATION ---
            // =================================================================
            #region Dapper Connection Factory

            // This is the key to making Dapper repositories database-agnostic.
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

                    // Redis IS configured. Register the real ConnectionMultiplexer.
                    _ = services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisConnectionString));
                    Console.WriteLine("✅ Redis is configured and will be used for distributed caching and features."); // Using Console for early startup info
                }
                catch (RedisConnectionException ex)
                {
                    // If the connection string is present but invalid, it's a critical configuration error.
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"FATAL ERROR: Could not connect to Redis using the provided connection string. Please check the server and configuration. Error: {ex.Message}");
                    Console.ResetColor();
                    throw; // Fail fast if Redis is configured but unreachable.
                }
            }
            else
            {
                // Redis is NOT configured. We will log a warning and continue.
                // The application will run without distributed caching features.
                // We do NOT register IConnectionMultiplexer, so services requesting it will get null.
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("⚠️ Redis connection string not found. Caching will be in-memory and non-persistent. Distributed features like global image deduplication will be disabled.");
                Console.ResetColor();
            }
            AsyncRetryPolicy<HttpResponseMessage> retryPolicy = HttpPolicyExtensions
               .HandleTransientHttpError()
               .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
               .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));


            _ = services.Configure<RssReaderServiceSettings>(configuration.GetSection(RssReaderServiceSettings.ConfigurationSectionName));
            _ = services.AddHttpClient(RssReaderService.HttpClientNamedClient, (serviceProvider, client) =>
            {
                // Configure default headers for every request made by this client.
                RssReaderServiceSettings settings = serviceProvider.GetRequiredService<IOptions<RssReaderServiceSettings>>().Value;
                client.DefaultRequestHeaders.UserAgent.ParseAdd(settings.UserAgent);
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/rss+xml"));
            }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            });
            _ = services.AddHttpClient<ICoinGeckoApiClient, CoinGeckoApiClient>().AddPolicyHandler(retryPolicy);
            _ = services.AddHttpClient<IFmpApiClient, FmpApiClient>().AddPolicyHandler(retryPolicy);
            _ = services.AddHttpClient<IFredApiClient, FredApiClient>();

            #endregion

            // =================================================================
            // --- 4. APPLICATION SERVICES AND REPOSITORIES ---
            // =================================================================
            #region Application Services and Repositories

            _ = services.AddSingleton<ITelegramUserApiClient, TelegramUserApiClient>();

            // This line is redundant if TelegramUserApiClient is only resolved via its interface.
            // Leaving it in as requested. The DI will create a separate singleton instance for this registration.
            _ = services.AddSingleton<TelegramUserApiClient>();

            _ = services.AddHostedService<TelegramUserApiInitializationService>();

            // All these registrations are correct.
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
            _ = services.AddScoped<IWebPanelConfigRepository, WebPanelConfigRepository>(); // Added this line


            #endregion

            return services;
        }
    }
}