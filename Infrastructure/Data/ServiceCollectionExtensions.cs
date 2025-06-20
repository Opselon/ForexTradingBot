﻿// File: Infrastructure\DependencyInjection.cs // نام فایل ممکن است ServiceCollectionExtensions.cs یا DependencyInjection.cs باشد
using Application.Common.Interfaces;      // اینترفیس‌های Repository و IAppDbContext (لایه Application)
using Application.Common.Interfaces.CoinGeckoApiClient;
using Application.Common.Interfaces.Fred;
using Application.Features.Crypto.Services.CoinGecko;
using Application.Interfaces;             // اینترفیس‌های سرویس‌های کاربردی (لایه Application)
using Application.Services;               // پیاده‌سازی سرویس‌ها (لایه Application)
using Hangfire;                           // برای مدیریت وظایف پس‌زمینه
using Hangfire.PostgreSql;
using Infrastructure.Caching;
using Infrastructure.ExternalServices;    // سرویس‌های خارجی (لایه Infrastructure)
using Infrastructure.Hangfire;            // پیاده‌سازی سرویس‌های Hangfire (لایه Infrastructure)
using Infrastructure.Persistence.Configurations;
using Infrastructure.Repositories;
using Infrastructure.Services;            // پیاده‌سازی سرویس‌های داخلی Infrastructure
using Infrastructure.Services.Admin;
using Infrastructure.Services.Fmp;
using Microsoft.EntityFrameworkCore;      // Entity Framework Core
using Microsoft.Extensions.Configuration; // برای خواندن تنظیمات از فایل پیکربندی
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;
using StackExchange.Redis; // برای افزودن سرویس‌ها به کانتینر DI
namespace Infrastructure.Data
{
    /// <summary>
    /// کلاس کمکی استاتیک برای افزودن سرویس‌های لایه Infrastructure (زیرساخت) به کانتینر تزریق وابستگی (DI).
    /// این کلاس مسئول پیکربندی و رجیستر کردن DbContext، Repositoryها، سرویس‌های خارجی و وظایف پس‌زمینه است.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// این متد توسعه (Extension Method) همه‌ی سرویس‌های مورد نیاز لایه زیرساخت را به <see cref="IServiceCollection"/>
        /// اضافه می‌کند. این شامل پیکربندی پایگاه داده، رجیستر Repositoryها و سرویس‌های خارجی و Hangfire می‌شود.
        /// </summary>
        /// <param name="services">مجموعه سرویس‌هایی که سرویس‌های زیرساخت باید به آن اضافه شوند.</param>
        /// <param name="configuration">تنظیمات برنامه، برای دسترسی به رشته اتصال پایگاه داده و سایر تنظیمات.</param>
        /// <returns>همان <see cref="IServiceCollection"/>، برای امکان زنجیره‌ای کردن متدها.</returns>
        public static IServiceCollection AddInfrastructureServices(
            this IServiceCollection services,
            IConfiguration configuration, bool isSmokeTest)
        {


            if (isSmokeTest)
            {
                // --- SMOKE TEST: Use In-Memory Providers ---
                _ = services.AddDbContext<AppDbContext>(options =>
                    options.UseInMemoryDatabase("SmokeTestDatabase"));
            }
            else
            {

                IConfigurationSection dbProviderSection = configuration.GetSection("DatabaseSettings");
                string? dbProvider = configuration.GetValue<string>("DatabaseSettings:DatabaseProvider");
                string? connectionString = configuration.GetConnectionString("DefaultConnection");

                // ✅ THE DIAGNOSTIC CHECK IS NOW INSIDE THE 'ELSE' BLOCK
                if (string.IsNullOrEmpty(dbProvider) || string.IsNullOrEmpty(connectionString))
                {
                    // This now only runs in a real environment, where these values are required.
                    throw new InvalidOperationException($"FATAL: Critical DB configuration is missing for a non-test environment.");
                }

                if (string.IsNullOrEmpty(dbProvider) || string.IsNullOrEmpty(connectionString))
                {
                    // This now only runs in a real environment, where these values are required.
                    throw new InvalidOperationException($"FATAL: Critical DB configuration is missing for a non-test environment.");
                }


                switch (dbProvider.ToLowerInvariant())
                {
                    case "sqlserver":
                        _ = services.AddDbContext<AppDbContext>(opts =>
                            opts.UseSqlServer(connectionString, sql =>
                            {
                                _ = sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
                                _ = sql.EnableRetryOnFailure(
                                    maxRetryCount: 5,
                                    maxRetryDelay: TimeSpan.FromSeconds(30),
                                    errorNumbersToAdd: null);
                            }));
                        _ = services.AddHangfire(config => config
                            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                            .UseSimpleAssemblyNameTypeSerializer()
                            .UseRecommendedSerializerSettings()
                            .UseSqlServerStorage(connectionString));
                        break;

                    case "postgres":
                        _ = services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(configuration.GetConnectionString("PostgresConnection"), npgsql =>
            {
                _ = npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
            }));

                        // Also update Hangfire if you use it with Postgres
                        _ = services.AddHangfire(config => config
                            .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(configuration.GetConnectionString("PostgresConnection"))));
                        break;

                    default:
                        // Now, even this error will be more informative.
                        string detailedUnsupportedError = $"Unsupported DatabaseProvider: '{dbProvider}'. Please check your configuration. " +
                                                     $"Available keys in 'DatabaseSettings': {string.Join(", ", dbProviderSection.GetChildren().Select(c => c.Key))}";
                        throw new NotSupportedException(detailedUnsupportedError);
                }

                // 3. THROW A DETAILED EXCEPTION IF ANYTHING IS WRONG
                // This is our new, intelligent error reporting.
                if (string.IsNullOrEmpty(dbProvider) || string.IsNullOrEmpty(connectionString))
                {
                    string errorMessage = "FATAL: Critical configuration is missing." + Environment.NewLine;
                    errorMessage += "------------------- DIAGNOSTIC REPORT -------------------" + Environment.NewLine;

                    // Report on dbProvider
                    if (string.IsNullOrEmpty(dbProvider))
                    {
                        errorMessage += "❌ Error: 'DatabaseSettings:DatabaseProvider' is NULL or EMPTY." + Environment.NewLine;
                        errorMessage += $"   - Raw value from GetValue: '{configuration.GetValue<string>("DatabaseSettings:DatabaseProvider")}'" + Environment.NewLine;
                        errorMessage += $"   - Path exists check: {dbProviderSection.Exists()}" + Environment.NewLine;
                    }
                    else
                    {
                        errorMessage += $"✅ OK: DatabaseProvider was found: '{dbProvider}'" + Environment.NewLine;
                    }

                    // Report on ConnectionString
                    if (string.IsNullOrEmpty(connectionString))
                    {
                        errorMessage += "❌ Error: 'ConnectionStrings:DefaultConnection' is NULL or EMPTY." + Environment.NewLine;
                    }
                    else
                    {
                        errorMessage += "✅ OK: ConnectionString was found (value is hidden for security)." + Environment.NewLine;
                    }
                    Dictionary<string, string?> allConfig = configuration.AsEnumerable().ToDictionary(x => x.Key, x => x.Value);
                    string allConfigString = string.Join(Environment.NewLine, allConfig.Select(kv => $"  - Key: '{kv.Key}', Value: '{kv.Value}'"));

                    errorMessage += "------------------- FULL CONFIGURATION DUMP -------------------" + Environment.NewLine;
                    errorMessage += allConfigString + Environment.NewLine;
                    errorMessage += "-------------------------------------------------------------" + Environment.NewLine;

                    // Throw the new, super-detailed exception.
                    throw new InvalidOperationException(errorMessage);
                }
            }



            _ = services.AddMemoryCache();
            _ = services.AddSingleton(typeof(IMemoryCacheService<>), typeof(MemoryCacheService<>));

            Polly.Retry.AsyncRetryPolicy<HttpResponseMessage> retryPolicy = HttpPolicyExtensions
               .HandleTransientHttpError() // Handles HttpRequestException, 5xx, and 408
               .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests) // Also handle 429
               .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // Exponential backoff: 2, 4, 8 seconds
                   onRetry: (outcome, timespan, retryAttempt, context) =>
                   {
                       // Log the retry attempt. This is great for diagnostics.
                       // You would need to inject ILogger into this scope or use a static logger.
                       Console.WriteLine($"--> Polly: Retrying API request... Delaying for {timespan.TotalSeconds}s, then making retry {retryAttempt}");
                   });


            // 2. Read the connection string

            string? redisConnectionString = configuration.GetConnectionString("Redis");
            if (string.IsNullOrEmpty(redisConnectionString))
            {
                throw new InvalidOperationException("FATAL: Redis connection string is missing or empty.");
            }
            ConfigurationOptions options = ConfigurationOptions.Parse(redisConnectionString);
            options.AbortOnConnectFail = false; // Make it resilient
            _ = services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(options));



            // ========================= END OF DIAGNOSTIC BLOCK =========================


            // The rest of your code remains UNCHANGED as it is correct.


            // 4. رجیستر <see cref="IAppDbContext"/> به عنوان یک سرویس Scoped
            // این امکان را فراهم می‌کند که <see cref="AppDbContext"/> از طریق اینترفیس در لایه Application تزریق شود،
            // که برای تست‌پذیری و جداسازی دغدغه‌ها (Separation of Concerns) مفید است.
            _ = services.AddScoped<IAppDbContext>(
                sp => sp.GetRequiredService<AppDbContext>());

            // افزودن سرویس‌های Hangfire برای مدیریت و پردازش وظایف پس‌زمینه.
            // Hangfire به برنامه اجازه می‌دهد تا وظایف را خارج از چرخه درخواست اصلی انجام دهد (مثلاً ارسال ایمیل، پردازش فایل).


            // افزودن سرور پردازش Hangfire به عنوان یک سرویس میزبانی شده (IHostedService).
            // این سرور مسئول اجرای وظایف زمان‌بندی شده Hangfire است.
            _ = services.AddHangfireServer();


            // رجیستر کردن سرویس‌های ارتباط با APIهای خارجی با استفاده از IHttpClientFactory.
            // IHttpClientFactory به مدیریت بهینه نمونه‌های HttpClient کمک می‌کند (مثل مدیریت Pool و Lifetime).
            _ = services.AddHttpClient<ICryptoPayApiClient, CryptoPayApiClient>(); // برای ارتباط با CryptoPay API
            _ = services.AddScoped<IRssFetchingCoordinatorService, RssFetchingCoordinatorService>(); // هماهنگ کننده برای خواندن RSS
                                                                                                     // This line reads the "RssReaderService" section from your appsettings.json
            services.Configure<RssReaderServiceSettings>(
                configuration.GetSection(RssReaderServiceSettings.ConfigurationSectionName)
            );

            // رجیستر کردن یک HttpClient نام‌گذاری شده برای سرویس <see cref="RssReaderService"/>.
            // این رویکرد امکان پیکربندی خاص برای HttpClient‌های مختلف را فراهم می‌کند.
            services.AddHttpClient(RssReaderService.HttpClientNamedClient, (serviceProvider, client) =>
            {
                // Get the strongly-typed settings from the DI container.
                var settings = serviceProvider.GetRequiredService<IOptions<RssReaderServiceSettings>>().Value;

                // Set the User-Agent from the centralized settings class.
                client.DefaultRequestHeaders.UserAgent.ParseAdd(settings.UserAgent);

                // Timeout is handled per-request inside RssReaderService, so we do not set it here.
            });
            // می‌توانید Handlerهای بیشتری برای Polly (مثلاً سیاست‌های تلاش مجدد یا قطع مدار) یا موارد دیگر در اینجا اضافه کنید.
            // .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
            // .AddPolicyHandler(GetRetryPolicy()); //  مثال: افزودن سیاست تلاش مجدد با Polly
            ;


            // Register the CoinGecko API client with the retry policy
            _ = services.AddHttpClient<ICoinGeckoApiClient, CoinGeckoApiClient>().AddPolicyHandler(retryPolicy);

            // Register the FMP API client with the retry policy
            _ = services.AddHttpClient<IFmpApiClient, FmpApiClient>().AddPolicyHandler(retryPolicy);


            // 5. رجیستر کردن Repositoryها و سایر سرویس‌های دامنه/کاربردی
            // هر Repository مسئول تعامل با یک موجودیت خاص در پایگاه داده است.
            // به عنوان Singleton ثبت شده تا در طول عمر برنامه فقط یک نمونه از آن وجود داشته باشد
            _ = services.AddSingleton<ITelegramUserApiClient, TelegramUserApiClient>();
            // یک نمونه از TelegramUserApiClient را به تنهایی نیز رجیستر می‌کند (ممکن است برای Scenario خاصی لازم باشد)
            _ = services.AddSingleton<TelegramUserApiClient>();
            // افزودن یک سرویس میزبانی شده برای مقداردهی اولیه (Initialization) کلاینت API تلگرام در زمان راه‌اندازی برنامه.
            _ = services.AddHostedService<TelegramUserApiInitializationService>();
            _ = services.AddHttpClient<IFredApiClient, FredApiClient>();
            _ = services.AddScoped<INewsItemRepository, NewsItemRepository>(); // رجیستر NewsItemRepository
            _ = services.AddScoped<IRssReaderService, RssReaderService>(); // رجیستر RssReaderService
            _ = services.AddSingleton<INotificationJobScheduler, HangfireNotificationJobScheduler>(); // رجیستر NotificationJobScheduler (Singleton برای زمان‌بندی)
            _ = services.AddScoped<INotificationDispatchService, NotificationDispatchService>(); // رجیستر NotificationDispatchService (برای ارسال اعلان‌ها)
            _ = services.AddScoped<IUserRepository, UserRepository>(); // رجیستر UserRepository
            _ = services.AddScoped<ITokenWalletRepository, TokenWalletRepository>(); // رجیستر TokenWalletRepository
            _ = services.AddScoped<ISubscriptionRepository, SubscriptionRepository>(); // رجیستر SubscriptionRepository
            _ = services.AddScoped<ISignalRepository, SignalRepository>(); // رجیستر SignalRepository
            _ = services.AddScoped<ISignalCategoryRepository, SignalCategoryRepository>(); // رجیستر SignalCategoryRepository
            _ = services.AddScoped<IRssSourceRepository, RssSourceRepository>(); // رجیستر RssSourceRepository
            _ = services.AddScoped<IUserSignalPreferenceRepository, UserSignalPreferenceRepository>(); // رجیستر UserSignalPreferenceRepository
            _ = services.AddScoped<ISignalAnalysisRepository, SignalAnalysisRepository>(); // رجیستر SignalAnalysisRepository
            _ = services.AddScoped<ITransactionRepository, TransactionRepository>(); // رجیستر TransactionRepository
            _ = services.AddScoped<IAdminService, AdminService>();


            // بازگرداندن IServiceCollection به‌روزرسانی شده برای فعال کردن زنجیره‌ای کردن متدها در Program.cs
            return services;
        }
    }
}