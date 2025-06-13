// File: Infrastructure\DependencyInjection.cs // نام فایل ممکن است ServiceCollectionExtensions.cs یا DependencyInjection.cs باشد
using Application.Common.Interfaces;      // اینترفیس‌های Repository و IAppDbContext (لایه Application)
using Application.Common.Interfaces.CoinGeckoApiClient;
using Application.Common.Interfaces.Fred;
using Application.Features.Crypto.Services.CoinGecko;
using Application.Interfaces;             // اینترفیس‌های سرویس‌های کاربردی (لایه Application)
using Application.Services;               // پیاده‌سازی سرویس‌ها (لایه Application)
using Hangfire;                           // برای مدیریت وظایف پس‌زمینه
using Hangfire.MemoryStorage;
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
            IConfiguration configuration)
        {

     
            _ = services.AddMemoryCache();
            _ = services.AddSingleton(typeof(IMemoryCacheService<>), typeof(MemoryCacheService<>));

            var retryPolicy = HttpPolicyExtensions
               .HandleTransientHttpError() // Handles HttpRequestException, 5xx, and 408
               .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests) // Also handle 429
               .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // Exponential backoff: 2, 4, 8 seconds
                   onRetry: (outcome, timespan, retryAttempt, context) =>
                   {
                       // Log the retry attempt. This is great for diagnostics.
                       // You would need to inject ILogger into this scope or use a static logger.
                       Console.WriteLine($"--> Polly: Retrying API request... Delaying for {timespan.TotalSeconds}s, then making retry {retryAttempt}");
                   });

            var dbProviderSection = configuration.GetSection("DatabaseSettings");
            var dbProvider = dbProviderSection.GetValue<string>("DatabaseProvider")?.ToLowerInvariant();

            // 2. Read the connection string
            var connectionString = configuration.GetConnectionString("DefaultConnection");

            // --- ✅ Conditional Configuration based on Environment ---
            bool isSmokeTest = configuration.GetValue<bool>("IsSmokeTest");

            if (isSmokeTest)
            {

                // --- SMOKE TEST ENVIRONMENT SETUP ---
                // In a smoke test, we only need to register the bare minimum for the app to start.
                // We don't need real database connections.

                // Use a fast, in-memory database that requires no external connection.
                services.AddDbContext<AppDbContext>(options =>
                {
                    // Ensure the following line is present in the method where the error occurs.  
                    // This resolves the CS1061 error by ensuring the 'UseInMemoryDatabase' extension method is available.  
                    options.UseInMemoryDatabase("SmokeTestDatabase");
                });

                // Configure Hangfire to use in-memory storage for the test.
                services.AddHangfire(config => config.UseMemoryStorage());
            }
            else
            {

                // Your excellent diagnostic check for production configurations.
                if (string.IsNullOrEmpty(dbProvider) || string.IsNullOrEmpty(connectionString))
                {
                    throw new InvalidOperationException($"FATAL: Critical DB configuration is missing. DBProvider: '{dbProvider}', ConnectionString Found: {!string.IsNullOrEmpty(connectionString)}");
                }
               _ = services.AddHangfire(config => config
             .SetDataCompatibilityLevel(CompatibilityLevel.Version_180) // تنظیم سطح سازگاری داده
             .UseSimpleAssemblyNameTypeSerializer() // استفاده از سریالایزر ساده برای نام اسمبلی‌ها
             .UseRecommendedSerializerSettings() // استفاده از تنظیمات سریالایزر توصیه شده
             .UseSqlServerStorage(connectionString)); // پیکربندی Hangfire برای استفاده از SQL Server به عنوان ذخیره‌ساز
                switch (dbProvider)
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
                        break;

                    case "postgres":
                    case "postgresql":
                        _ = services.AddDbContext<AppDbContext>(opts =>
                            opts.UseNpgsql(connectionString, npgsql =>
                            {
                                _ = npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
                                _ = npgsql.EnableRetryOnFailure(
                                    maxRetryCount: 5,
                                    maxRetryDelay: TimeSpan.FromSeconds(30),
                                    errorCodesToAdd: null);
                            }));
                        break;

                    default:
                        // Now, even this error will be more informative.
                        var detailedUnsupportedError = $"Unsupported DatabaseProvider: '{dbProvider}'. Please check your configuration. " +
                                                     $"Available keys in 'DatabaseSettings': {string.Join(", ", dbProviderSection.GetChildren().Select(c => c.Key))}";
                        throw new NotSupportedException(detailedUnsupportedError);
                }
            }

            var redisConnectionString = configuration.GetConnectionString("Redis");
            var options = ConfigurationOptions.Parse(redisConnectionString);
            options.AbortOnConnectFail = false; // Make it resilient
            services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(options));

            var allConfig = configuration.AsEnumerable().ToDictionary(x => x.Key, x => x.Value);
            var allConfigString = string.Join(Environment.NewLine, allConfig.Select(kv => $"  - Key: '{kv.Key}', Value: '{kv.Value}'"));

            // 1. Read the DatabaseProvider setting
            // var dbProvider = configuration.GetValue<string>("DatabaseSettings:DatabaseProvider")?.ToLowerInvariant();
    



            // 3. THROW A DETAILED EXCEPTION IF ANYTHING IS WRONG
            // This is our new, intelligent error reporting.
            if (string.IsNullOrEmpty(dbProvider) || string.IsNullOrEmpty(connectionString))
            {
                var errorMessage = "FATAL: Critical configuration is missing." + Environment.NewLine;
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

                errorMessage += "------------------- FULL CONFIGURATION DUMP -------------------" + Environment.NewLine;
                errorMessage += allConfigString + Environment.NewLine;
                errorMessage += "-------------------------------------------------------------" + Environment.NewLine;

                // Throw the new, super-detailed exception.
                throw new InvalidOperationException(errorMessage);
            }
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

            // رجیستر کردن یک HttpClient نام‌گذاری شده برای سرویس <see cref="RssReaderService"/>.
            // این رویکرد امکان پیکربندی خاص برای HttpClient‌های مختلف را فراهم می‌کند.
            _ = services.AddHttpClient(RssReaderService.HttpClientNamedClient, client =>
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(RssReaderService.DefaultUserAgent); // تنظیم User-Agent پیش‌فرض برای درخواست‌ها
                client.Timeout = TimeSpan.FromSeconds(RssReaderService.DefaultHttpClientTimeoutSeconds); // تنظیم مهلت زمانی (timeout) برای درخواست‌های HTTP
            })
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