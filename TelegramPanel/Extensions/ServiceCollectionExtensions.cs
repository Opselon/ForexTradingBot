using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using TelegramPanel.Application.CommandHandlers; // برای FromAssemblyOf<StartCommandHandler>() و سایر Handler ها
using TelegramPanel.Application.Interfaces;    // برای ITelegramUpdateProcessor, ITelegramMiddleware, ITelegramCommandHandler, ITelegramStateMachine, ITelegramState
using TelegramPanel.Application.Pipeline;      // برای Middleware ها (LoggingMiddleware, AuthenticationMiddleware)
using TelegramPanel.Application.Services;      // برای TelegramStateMachine
using TelegramPanel.Application.States;        // برای IUserConversationStateService, InMemoryUserConversationStateService, و پیاده‌سازی‌های ITelegramState
using TelegramPanel.Infrastructure;            // برای TelegramMessageSender, TelegramBotService (ITelegramMessageSender, UpdateProcessingService در همین namespace هستند)
using TelegramPanel.Queue;                     // برای ITelegramUpdateChannel, TelegramUpdateChannel, UpdateQueueConsumerService
using TelegramPanel.Settings;                  // برای TelegramPanelSettings
using Scrutor;                                 // برای services.Scan(...) - اگر نصب شده باشد

namespace TelegramPanel.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddTelegramPanelServices(this IServiceCollection services, IConfiguration configuration)
        {
            // 1. پیکربندی Settings
            services.Configure<TelegramPanelSettings>(configuration.GetSection(TelegramPanelSettings.SectionName));

            // 2. رجیستر کردن کلاینت تلگرام
            services.AddSingleton<ITelegramBotClient>(serviceProvider =>
            {
                var settings = serviceProvider.GetRequiredService<IOptions<TelegramPanelSettings>>().Value;
                if (string.IsNullOrWhiteSpace(settings.BotToken))
                {
                    throw new ArgumentNullException(nameof(settings.BotToken), "Telegram Bot Token is not configured in settings. Please check your configuration.");
                }
                return new TelegramBotClient(settings.BotToken);
            });

            // 3. رجیستر کردن صف آپدیت
            services.AddSingleton<ITelegramUpdateChannel, TelegramUpdateChannel>();
            // 4. رجیستر کردن سرویس ارسال پیام
            services.AddScoped<ITelegramMessageSender, TelegramMessageSender>();
            // 5. رجیستر کردن پردازشگر آپدیت
            services.AddScoped<ITelegramUpdateProcessor, UpdateProcessingService>();
            // 6. رجیستر کردن Middleware ها
            services.AddScoped<ITelegramMiddleware, LoggingMiddleware>();
            services.AddScoped<ITelegramMiddleware, AuthenticationMiddleware>();

            // 7. رجیستر کردن Command Handler ها با Scrutor
            services.Scan(scan => scan
                .FromAssemblyOf<StartCommandHandler>()
                .AddClasses(classes => classes.AssignableTo<ITelegramCommandHandler>())
                .AsImplementedInterfaces()
                .WithScopedLifetime());

            // 8. رجیستر کردن State Machine و State ها
            services.AddSingleton<IUserConversationStateService, InMemoryUserConversationStateService>();
            services.AddScoped<ITelegramStateMachine, TelegramStateMachine>();
            services.Scan(scan => scan
                .FromAssemblyOf<TelegramStateMachine>()
                .AddClasses(classes => classes.AssignableTo<ITelegramState>().Where(c => !c.IsAbstract && c.IsClass))
                .AsImplementedInterfaces()
                .WithScopedLifetime());

            // 9. رجیستر کردن سرویس Hosted برای شروع ربات
            services.AddHostedService<TelegramBotService>();

            // 10. رجیستر کردن سرویس Hosted برای خواندن از صف آپدیت‌ها
            services.AddHostedService<UpdateQueueConsumerService>(); // ✅ فقط این رجیستری باقی بماند

            return services;
        }
    }
}