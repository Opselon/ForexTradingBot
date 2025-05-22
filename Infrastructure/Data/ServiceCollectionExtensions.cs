using Application.Common.Interfaces;      // اینترفیس‌های Repository و IAppDbContext
using Application.Interfaces;
using Application.Services;
using Infrastructure.Data;               // AppDbContext
using Infrastructure.ExternalServices;
using Infrastructure.Hangfire;
using Infrastructure.Persistence.Repositories;
using Infrastructure.Services; // مسیر Repositoryها
using Microsoft.EntityFrameworkCore;      // EF Core
using Microsoft.Extensions.Configuration; // IConfiguration
using Microsoft.Extensions.DependencyInjection; // IServiceCollection
using Hangfire;
using Hangfire.PostgreSql;
using Domain.Features.Forwarding.Repositories;
using Infrastructure.Features.Forwarding.Repositories;

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
            // 1. Configure PostgreSQL
            var connectionString = configuration
                .GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException(
                    "DefaultConnection is not configured.");

            services.AddDbContext<AppDbContext>(opts =>
                opts.UseNpgsql(connectionString, npgsql =>
                {
                    npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
                    npgsql.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(30),
                        errorCodesToAdd: null);
                }));

            // 2. Register IAppDbContext
            services.AddScoped<IAppDbContext>(
                sp => sp.GetRequiredService<AppDbContext>());

            // 3. Configure Hangfire with PostgreSQL
            var hangfireConnectionString = configuration.GetConnectionString("Hangfire")
                ?? configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Hangfire connection string is not configured.");

            services.AddHangfire(config => config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UsePostgreSqlStorage(hangfireConnectionString));

            services.AddHangfireServer();

            // 4. Register Repositories
            services.AddScoped<IUserRepository, UserRepository>();
            services.AddScoped<ISubscriptionRepository, SubscriptionRepository>();
            services.AddScoped<INewsItemRepository, NewsItemRepository>();
            services.AddScoped<IForwardingRuleRepository, ForwardingRuleRepository>();

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

            services.AddScoped<IRssReaderService, RssReaderService>();
            services.AddSingleton<INotificationJobScheduler, HangfireNotificationJobScheduler>();
            services.AddScoped<INotificationDispatchService, NotificationDispatchService>();
            services.AddScoped<ITokenWalletRepository, TokenWalletRepository>();
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
