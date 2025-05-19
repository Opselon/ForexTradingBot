#region Usings
using Application.Common.Interfaces; // ✅ فقط برای INotificationService (اینترفیس عمومی)
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using TelegramPanel.Application.CommandHandlers; // برای FromAssemblyOf<StartCommandHandler>() و سایر Handler های TelegramPanel
using TelegramPanel.Application.Interfaces;    // برای اینترفیس‌های خاص TelegramPanel
using TelegramPanel.Application.Pipeline;      // برای Middleware های TelegramPanel
using TelegramPanel.Application.Services;      // برای سرویس‌های TelegramPanel مانند TelegramStateMachine
using TelegramPanel.Application.States;        // برای State های TelegramPanel
using TelegramPanel.Infrastructure;            // برای سرویس‌های Infrastructure خاص TelegramPanel
using TelegramPanel.Infrastructure.Services;   // برای MarketDataService
using TelegramPanel.Queue;                     // برای سرویس‌های صف TelegramPanel
using TelegramPanel.Settings;                  // برای TelegramPanelSettings
#endregion

namespace TelegramPanel.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddTelegramPanelServices(this IServiceCollection services, IConfiguration configuration)
        {
            // ------------------- 1. پیکربندی Settings خاص TelegramPanel -------------------
            services.Configure<TelegramPanelSettings>(configuration.GetSection(TelegramPanelSettings.SectionName));

            // ------------------- 2. رجیستر کردن کلاینت تلگرام (ITelegramBotClient) -------------------
            services.AddSingleton<ITelegramBotClient>(serviceProvider =>
            {
                var settings = serviceProvider.GetRequiredService<IOptions<TelegramPanelSettings>>().Value;
                if (string.IsNullOrWhiteSpace(settings.BotToken))
                {
                    throw new ArgumentNullException(nameof(settings.BotToken), "TelegramPanel: Bot Token is not configured.");
                }
                return new TelegramBotClient(settings.BotToken);
            });

            // ------------------- 3. رجیستر کردن سرویس‌های پایه TelegramPanel -------------------
            services.AddSingleton<ITelegramUpdateChannel, TelegramUpdateChannel>();
            services.AddScoped<ITelegramMessageSender, TelegramMessageSender>(); // پیاده‌سازی در TelegramPanel.Infrastructure
            services.AddScoped<ITelegramUpdateProcessor, UpdateProcessingService>(); // پیاده‌سازی در TelegramPanel.Infrastructure
            services.AddSingleton<IBotCommandSetupService, BotCommandSetupService>();
            services.AddHttpClient(); // Add HttpClient for market data service
            services.AddScoped<IMarketDataService, MarketDataService>(); // Add market data service

            // ------------------- 4. رجیستر کردن Middleware های TelegramPanel -------------------
            services.AddScoped<ITelegramMiddleware, LoggingMiddleware>();     // پیاده‌سازی در TelegramPanel.Application.Pipeline
            services.AddScoped<ITelegramMiddleware, AuthenticationMiddleware>();// پیاده‌سازی در TelegramPanel.Application.Pipeline

            // ------------------- 5. رجیستر کردن Command Handler های TelegramPanel با Scrutor -------------------
            services.Scan(scan => scan
                .FromAssemblyOf<StartCommandHandler>() // از اسمبلی TelegramPanel.Application
                .AddClasses(classes => classes.AssignableTo<ITelegramCommandHandler>())
                .AsImplementedInterfaces()
                .WithScopedLifetime()
                .AddClasses(classes => classes.AssignableTo<ITelegramCallbackQueryHandler>())
                .AsImplementedInterfaces()
                .WithScopedLifetime());

            // ------------------- 6. رجیستر کردن State Machine و State های TelegramPanel -------------------
            services.AddSingleton<IUserConversationStateService, InMemoryUserConversationStateService>(); // پیاده‌سازی در TelegramPanel.Application.States
            services.AddScoped<ITelegramStateMachine, TelegramStateMachine>();         // پیاده‌سازی در TelegramPanel.Application.Services
            services.Scan(scan => scan
                .FromAssemblyOf<TelegramStateMachine>() // از اسمبلی TelegramPanel.Application
                .AddClasses(classes => classes.AssignableTo<ITelegramState>().Where(c => !c.IsAbstract && c.IsClass))
                .AsImplementedInterfaces()
                .WithScopedLifetime());
            //  اگر IdleState دارید و با Scan پیدا نمی‌شود، دستی رجیستر کنید:
            //  services.AddScoped<ITelegramState, IdleState>(); // پیاده‌سازی در TelegramPanel.Application.States

            // ------------------- 7. رجیستر کردن پیاده‌سازی واقعی INotificationService -------------------
            // این پیاده‌سازی، از ITelegramMessageSender (که در همین لایه TelegramPanel است) استفاده می‌کند.
            // این رجیستری، پیاده‌سازی DummyNotificationService را که در AddApplicationServices رجیستر شده بود، override می‌کند.
            services.AddScoped<INotificationService, TelegramNotificationService>(); // پیاده‌سازی در TelegramPanel.Infrastructure

            // ------------------- 8. رجیستر کردن سرویس‌های Hosted برای TelegramPanel -------------------
            services.AddHostedService<TelegramBotService>();
            services.AddHostedService<UpdateQueueConsumerService>();
            services.AddHostedService<BotCommandSetupHostedService>();

            // 📛📛📛 حذف رجیستری‌های مربوط به سرویس‌های Application اصلی از اینجا 📛📛📛
            // services.AddScoped<ISubscriptionService, SubscriptionService>(); //  نباید اینجا باشد
            // services.AddScoped<IPaymentService, PaymentService>(); //  نباید اینجا باشد
            // services.AddScoped<IPaymentConfirmationService, PaymentConfirmationService>(); //  نباید اینجا باشد

            // Register specific handlers that need special configuration
            services.AddScoped<MarketAnalysisCallbackHandler>();

            return services;
        }
    }

    public class BotCommandSetupHostedService : IHostedService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<BotCommandSetupHostedService> _logger;

        public BotCommandSetupHostedService(
            IServiceProvider serviceProvider,
            ILogger<BotCommandSetupHostedService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var commandSetupService = scope.ServiceProvider.GetRequiredService<IBotCommandSetupService>();
                await commandSetupService.SetupCommandsAsync(cancellationToken);
                _logger.LogInformation("Bot commands have been set up successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while setting up bot commands");
                throw;
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}