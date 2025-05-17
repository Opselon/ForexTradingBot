using Application.Common.Interfaces;      // اینترفیس‌های Repository و IAppDbContext
using Infrastructure.Data;               // AppDbContext
using Infrastructure.Persistence.Repositories; // مسیر Repositoryها
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

            // 5. رجیستر کردن Repositoryها
            services.AddScoped<IUserRepository, UserRepository>();
            services.AddScoped<ITokenWalletRepository, TokenWalletRepository>();
            services.AddScoped<ISubscriptionRepository, SubscriptionRepository>();
            services.AddScoped<ISignalRepository, SignalRepository>();
            services.AddScoped<ISignalCategoryRepository, SignalCategoryRepository>();
            services.AddScoped<IRssSourceRepository, RssSourceRepository>();
            services.AddScoped<IUserSignalPreferenceRepository, UserSignalPreferenceRepository>();
            services.AddScoped<ISignalAnalysisRepository, SignalAnalysisRepository>();

            // در اینجا می‌توانید سایر سرویس‌های زیرساختی مثل IRssReader یا EmailService را هم اضافه کنید.

            return services;
        }
    }
}
