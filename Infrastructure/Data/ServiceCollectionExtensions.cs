using Application.Common.Interfaces;      // اینترفیس‌های Repository و IAppDbContext
using Application.Interfaces;
using Application.Services;
using Hangfire;
using Hangfire.SqlServer;
using Infrastructure.Data;               // AppDbContext
using Infrastructure.ExternalServices;
using Infrastructure.Hangfire;
using Infrastructure.Persistence.Repositories;
using Infrastructure.Services; // مسیر Repositoryها
using Microsoft.EntityFrameworkCore;      // EF Core
using Microsoft.Extensions.Configuration; // IConfiguration
using Microsoft.Extensions.DependencyInjection; // IServiceCollection

namespace Infrastructure
{
    /// <summary>
    /// کلاس کمکی برای افزودن سرویس‌های لایه Infrastructure به کانتینر DI.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// این متد همه‌ی سرویس‌های زیرساخت (DbContext، Repositoryها و...)
        /// را به IServiceCollection اضافه می‌کند.
        /// </summary>
        public static IServiceCollection AddInfrastructureServices(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            // 1. خواندن تنظیمات مربوط به DatabaseProvider
            var dbProvider = configuration
                .GetValue<string>("DatabaseProvider")?
                .ToLowerInvariant()
                ?? throw new InvalidOperationException(
                    "DatabaseProvider is not configured.");

            // 2. خواندن Connection String
            var connectionString = configuration
                .GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException(
                    "DefaultConnection is not configured.");

            // 3. پیکربندی DbContext بر اساس Provider
            switch (dbProvider)
            {
                case "sqlserver":
                    services.AddDbContext<AppDbContext>(opts =>
                        opts.UseSqlServer(connectionString, sql =>
                        {
                            // مشخص کردن اسمبلی حاوی Migrationها
                            sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
                            // فعال کردن retry برای افزایش پایداری
                            sql.EnableRetryOnFailure(
                                maxRetryCount: 5,
                                maxRetryDelay: TimeSpan.FromSeconds(30),
                                errorNumbersToAdd: null);
                        }));
                    break;

                case "postgres":
                case "postgresql":
                    services.AddDbContext<AppDbContext>(opts =>
                        opts.UseNpgsql(connectionString, npgsql =>
                        {
                            npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
                            npgsql.EnableRetryOnFailure(
                                maxRetryCount: 5,
                                maxRetryDelay: TimeSpan.FromSeconds(30),
                                errorCodesToAdd: null);
                        }));
                    break;

                default:
                    throw new NotSupportedException(
                        $"Unsupported DatabaseProvider: '{dbProvider}'.");
            }

            // 4. رجیستر IAppDbContext برای استفاده در لایه Application
            services.AddScoped<IAppDbContext>(
                sp => sp.GetRequiredService<AppDbContext>());

            // Add Hangfire services
            services.AddHangfire(config => config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseSqlServerStorage(connectionString));

            // Add the processing server as IHostedService
            services.AddHangfireServer();



            // رجیستر کردن کلاینت CryptoPay با IHttpClientFactory
            // این کار به مدیریت بهتر HttpClient instance ها کمک می‌کند.
            services.AddHttpClient<ICryptoPayApiClient, CryptoPayApiClient>();
            services.AddScoped<IRssFetchingCoordinatorService, RssFetchingCoordinatorService>();
            services.AddHttpClient(RssReaderService.HttpClientNamedClient, client => // استفاده از ثابت نام کلاینت
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(RssReaderService.DefaultUserAgent); // استفاده از ثابت UserAgent
                    client.Timeout = TimeSpan.FromSeconds(RssReaderService.DefaultHttpClientTimeoutSeconds); // استفاده از ثابت Timeout
                })
                //  می‌توانید Handler های بیشتری برای Polly یا موارد دیگر در اینجا اضافه کنید اگر Policy را اینجا مدیریت می‌کنید
                // .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
                // .AddPolicyHandler(GetRetryPolicy()); //  مثال
                ;

            // 5. رجیستر کردن Repository
            services.AddSingleton<ITelegramUserApiClient, TelegramUserApiClient>();
            services.AddSingleton<TelegramUserApiClient>();
            services.AddHostedService<TelegramUserApiInitializationService>();


            services.AddScoped<INewsItemRepository, NewsItemRepository>();
            services.AddScoped<IRssReaderService, RssReaderService>();
            services.AddSingleton<INotificationJobScheduler, HangfireNotificationJobScheduler>();
            services.AddScoped<INotificationDispatchService, NotificationDispatchService>();
            services.AddScoped<IUserRepository, UserRepository>();
            services.AddScoped<ITokenWalletRepository, TokenWalletRepository>();
            services.AddScoped<ISubscriptionRepository, SubscriptionRepository>();
            services.AddScoped<ISignalRepository, SignalRepository>();
            services.AddScoped<ISignalCategoryRepository, SignalCategoryRepository>();
            services.AddScoped<IRssSourceRepository, RssSourceRepository>();
            services.AddScoped<IUserSignalPreferenceRepository, UserSignalPreferenceRepository>();
            services.AddScoped<ISignalAnalysisRepository, SignalAnalysisRepository>();
            services.AddScoped<ITransactionRepository, TransactionRepository>();
            // در اینجا می‌توانید سایر سرویس‌های زیرساختی مثل IRssReader یا EmailService را هم اضافه کنید.

            return services;
        }
    }
}
