// File: Infrastructure\DependencyInjection.cs // نام فایل ممکن است ServiceCollectionExtensions.cs یا DependencyInjection.cs باشد
using Application.Common.Interfaces;      // اینترفیس‌های Repository و IAppDbContext (لایه Application)
using Application.Interfaces;             // اینترفیس‌های سرویس‌های کاربردی (لایه Application)
using Application.Services;               // پیاده‌سازی سرویس‌ها (لایه Application)
using Hangfire;                           // برای مدیریت وظایف پس‌زمینه
using Hangfire.SqlServer;                 // برای استفاده از SQL Server به عنوان ذخیره‌ساز Hangfire
using Infrastructure.Data;                // AppDbContext (لایه Infrastructure)
using Infrastructure.ExternalServices;    // سرویس‌های خارجی (لایه Infrastructure)
using Infrastructure.Hangfire;            // پیاده‌سازی سرویس‌های Hangfire (لایه Infrastructure)
using Infrastructure.Persistence.Repositories; // پیاده‌سازی Repositoryها (لایه Infrastructure)
using Infrastructure.Services;            // پیاده‌سازی سرویس‌های داخلی Infrastructure
using Microsoft.EntityFrameworkCore;      // Entity Framework Core
using Microsoft.Extensions.Configuration; // برای خواندن تنظیمات از فایل پیکربندی
using Microsoft.Extensions.DependencyInjection; // برای افزودن سرویس‌ها به کانتینر DI
using System.Net.Http; // برای DecompressionMethods در صورت فعال کردن
using System.Net; // برای DecompressionMethods در صورت فعال کردن

namespace Infrastructure
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
            // 1. خواندن تنظیمات مربوط به DatabaseProvider
            // این تنظیم تعیین می‌کند که کدام نوع پایگاه داده (مانند SQL Server یا PostgreSQL) استفاده شود.
            var dbProvider = configuration
                .GetValue<string>("DatabaseProvider")?
                .ToLowerInvariant()
                ?? throw new InvalidOperationException(
                    "DatabaseProvider is not configured in the application settings."); // اگر تنظیمات یافت نشود، استثنا پرتاب می‌کند.

            // 2. خواندن رشته اتصال اصلی پایگاه داده
            // DefaultConnection رشته اتصالی است که برای ارتباط با پایگاه داده اصلی برنامه استفاده می‌شود.
            var connectionString = configuration
                .GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException(
                    "DefaultConnection is not configured in the application settings."); // اگر رشته اتصال یافت نشود، استثنا پرتاب می‌کند.

            // 3. پیکربندی DbContext بر اساس Provider پایگاه داده انتخاب شده
            switch (dbProvider)
            {
                case "sqlserver":
                    services.AddDbContext<AppDbContext>(opts =>
                        opts.UseSqlServer(connectionString, sql =>
                        {
                            // مشخص کردن اسمبلی حاوی Migrationها برای Entity Framework Core.
                            // این اطمینان حاصل می‌کند که EF Core می‌تواند migrationهای صحیح را پیدا و اعمال کند.
                            sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
                            // فعال کردن استراتژی تلاش مجدد (retry) برای افزایش پایداری در برابر خطاهای موقتی پایگاه داده (مانند مشکلات شبکه).
                            sql.EnableRetryOnFailure(
                                maxRetryCount: 5, // حداکثر 5 بار تلاش مجدد
                                maxRetryDelay: TimeSpan.FromSeconds(30), // تا 30 ثانیه تأخیر بین تلاش‌ها
                                errorNumbersToAdd: null); // از خطاهای پیش‌فرض EF Core استفاده می‌کند
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
                    // در صورت عدم پشتیبانی از DatabaseProvider مشخص شده، استثنا پرتاب می‌کند.
                    throw new NotSupportedException(
                        $"Unsupported DatabaseProvider: '{dbProvider}'. Please check your configuration.");
            }

            // 4. رجیستر <see cref="IAppDbContext"/> به عنوان یک سرویس Scoped
            // این امکان را فراهم می‌کند که <see cref="AppDbContext"/> از طریق اینترفیس در لایه Application تزریق شود،
            // که برای تست‌پذیری و جداسازی دغدغه‌ها (Separation of Concerns) مفید است.
            services.AddScoped<IAppDbContext>(
                sp => sp.GetRequiredService<AppDbContext>());

            // افزودن سرویس‌های Hangfire برای مدیریت و پردازش وظایف پس‌زمینه.
            // Hangfire به برنامه اجازه می‌دهد تا وظایف را خارج از چرخه درخواست اصلی انجام دهد (مثلاً ارسال ایمیل، پردازش فایل).
            services.AddHangfire(config => config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180) // تنظیم سطح سازگاری داده
                .UseSimpleAssemblyNameTypeSerializer() // استفاده از سریالایزر ساده برای نام اسمبلی‌ها
                .UseRecommendedSerializerSettings() // استفاده از تنظیمات سریالایزر توصیه شده
                .UseSqlServerStorage(connectionString)); // پیکربندی Hangfire برای استفاده از SQL Server به عنوان ذخیره‌ساز

            // افزودن سرور پردازش Hangfire به عنوان یک سرویس میزبانی شده (IHostedService).
            // این سرور مسئول اجرای وظایف زمان‌بندی شده Hangfire است.
            services.AddHangfireServer();


            // رجیستر کردن سرویس‌های ارتباط با APIهای خارجی با استفاده از IHttpClientFactory.
            // IHttpClientFactory به مدیریت بهینه نمونه‌های HttpClient کمک می‌کند (مثل مدیریت Pool و Lifetime).
            services.AddHttpClient<ICryptoPayApiClient, CryptoPayApiClient>(); // برای ارتباط با CryptoPay API
            services.AddScoped<IRssFetchingCoordinatorService, RssFetchingCoordinatorService>(); // هماهنگ کننده برای خواندن RSS

            // رجیستر کردن یک HttpClient نام‌گذاری شده برای سرویس <see cref="RssReaderService"/>.
            // این رویکرد امکان پیکربندی خاص برای HttpClient‌های مختلف را فراهم می‌کند.
            services.AddHttpClient(RssReaderService.HttpClientNamedClient, client =>
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(RssReaderService.DefaultUserAgent); // تنظیم User-Agent پیش‌فرض برای درخواست‌ها
                client.Timeout = TimeSpan.FromSeconds(RssReaderService.DefaultHttpClientTimeoutSeconds); // تنظیم مهلت زمانی (timeout) برای درخواست‌های HTTP
            })
                // می‌توانید Handlerهای بیشتری برای Polly (مثلاً سیاست‌های تلاش مجدد یا قطع مدار) یا موارد دیگر در اینجا اضافه کنید.
                // .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
                // .AddPolicyHandler(GetRetryPolicy()); //  مثال: افزودن سیاست تلاش مجدد با Polly
                ;

            // 5. رجیستر کردن Repositoryها و سایر سرویس‌های دامنه/کاربردی
            // هر Repository مسئول تعامل با یک موجودیت خاص در پایگاه داده است.
            // به عنوان Singleton ثبت شده تا در طول عمر برنامه فقط یک نمونه از آن وجود داشته باشد
            services.AddSingleton<ITelegramUserApiClient, TelegramUserApiClient>();
            // یک نمونه از TelegramUserApiClient را به تنهایی نیز رجیستر می‌کند (ممکن است برای Scenario خاصی لازم باشد)
            services.AddSingleton<TelegramUserApiClient>();
            // افزودن یک سرویس میزبانی شده برای مقداردهی اولیه (Initialization) کلاینت API تلگرام در زمان راه‌اندازی برنامه.
            services.AddHostedService<TelegramUserApiInitializationService>();


            services.AddScoped<INewsItemRepository, NewsItemRepository>(); // رجیستر NewsItemRepository
            services.AddScoped<IRssReaderService, RssReaderService>(); // رجیستر RssReaderService
            services.AddSingleton<INotificationJobScheduler, HangfireNotificationJobScheduler>(); // رجیستر NotificationJobScheduler (Singleton برای زمان‌بندی)
            services.AddScoped<INotificationDispatchService, NotificationDispatchService>(); // رجیستر NotificationDispatchService (برای ارسال اعلان‌ها)
            services.AddScoped<IUserRepository, UserRepository>(); // رجیستر UserRepository
            services.AddScoped<ITokenWalletRepository, TokenWalletRepository>(); // رجیستر TokenWalletRepository
            services.AddScoped<ISubscriptionRepository, SubscriptionRepository>(); // رجیستر SubscriptionRepository
            services.AddScoped<ISignalRepository, SignalRepository>(); // رجیستر SignalRepository
            services.AddScoped<ISignalCategoryRepository, SignalCategoryRepository>(); // رجیستر SignalCategoryRepository
            services.AddScoped<IRssSourceRepository, RssSourceRepository>(); // رجیستر RssSourceRepository
            services.AddScoped<IUserSignalPreferenceRepository, UserSignalPreferenceRepository>(); // رجیستر UserSignalPreferenceRepository
            services.AddScoped<ISignalAnalysisRepository, SignalAnalysisRepository>(); // رجیستر SignalAnalysisRepository
            services.AddScoped<ITransactionRepository, TransactionRepository>(); // رجیستر TransactionRepository

            // بازگرداندن IServiceCollection به‌روزرسانی شده برای فعال کردن زنجیره‌ای کردن متدها در Program.cs
            return services;
        }
    }
}