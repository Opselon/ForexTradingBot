﻿#region Usings
using Application.Common.Interfaces; // For INotificationService
using Application.Features.Forwarding.Interfaces;
using Application.Features.Forwarding.Services;
using Domain.Features.Forwarding.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using TelegramPanel.Application.CommandHandlers; // For marker types like StartCommandHandler, MenuCommandHandler
using TelegramPanel.Application.Interfaces;    // For ITelegram...Handler interfaces
using TelegramPanel.Application.Pipeline;
using TelegramPanel.Application.Services;
using TelegramPanel.Application.States;
using TelegramPanel.Infrastructure;         // For concrete service implementations if any are directly used here (less common)
using TelegramPanel.Infrastructure.Services; // For concrete service implementations like TelegramMessageSender
using TelegramPanel.Queue;
using TelegramPanel.Settings;
// using Scrutor; // Scrutor is available via IServiceCollection extensions, no direct using needed here

#endregion

namespace TelegramPanel.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddTelegramPanelServices(this IServiceCollection services, IConfiguration configuration)
        {
            // 1. Configure Settings
            services.Configure<TelegramPanelSettings>(configuration.GetSection(TelegramPanelSettings.SectionName));
            services.Configure<List<ForwardingRule>>(configuration.GetSection("ForwardingRules"));

            // 2. Register ITelegramBotClient
            services.AddSingleton<ITelegramBotClient>(serviceProvider =>
            {
                var settings = serviceProvider.GetRequiredService<IOptions<TelegramPanelSettings>>().Value;
                if (string.IsNullOrWhiteSpace(settings.BotToken))
                {
                    throw new ArgumentNullException(nameof(settings.BotToken), "TelegramPanel: Bot Token is not configured.");
                }
                return new TelegramBotClient(settings.BotToken);
            });

            // 3. Register Core TelegramPanel Services
            services.AddSingleton<ITelegramUpdateChannel, TelegramUpdateChannel>();
            services.AddScoped<ITelegramUpdateProcessor, UpdateProcessingService>();
            services.AddScoped<IMarketDataService, MarketDataService>();

            // 4. Register Middleware
            services.AddScoped<ITelegramMiddleware, LoggingMiddleware>();
            services.AddScoped<ITelegramMiddleware, AuthenticationMiddleware>();

            // --- ✅ CONSOLIDATED HANDLER REGISTRATION ---
            // Assuming ALL your command and callback query handlers for this panel
            // are in the same assembly as StartCommandHandler.
            // If not, you'll need separate scans per assembly.

            // 5. Register ALL ITelegramCommandHandler implementations from the assembly
            services.Scan(scan => scan
                .FromAssemblyOf<StartCommandHandler>() // Scans the assembly of StartCommandHandler
                .AddClasses(classes => classes.AssignableTo<ITelegramCommandHandler>())
                .AsImplementedInterfaces() // Registers them as ITelegramCommandHandler
                .WithScopedLifetime());

            // 5.1. Register ALL ITelegramCallbackQueryHandler implementations from the SAME assembly
            services.Scan(scan => scan
                .FromAssemblyOf<StartCommandHandler>() // Scans the assembly of StartCommandHandler again (or use another marker from same assembly)
                .AddClasses(classes => classes.AssignableTo<ITelegramCallbackQueryHandler>())
                .AsImplementedInterfaces() // Registers them as ITelegramCallbackQueryHandler
            .WithScopedLifetime());

            services.AddScoped<IActualTelegramMessageActions, ActualTelegramMessageActions>(); // << ثبت صحیح برای اجرای واقعی
                                                                                               // سپس ITelegramMessageSender که جاب‌ها را به Hangfire رله می‌کند
            services.AddScoped<ITelegramMessageSender, HangfireRelayTelegramMessageSender>(); // << ثبت صحیح برای انکیو کردن

            // Register Forwarding Services
            services.AddScoped<IForwardingJobActions, ForwardingJobActions>();
            services.AddScoped<MessageForwardingService>();

            // This will pick up:
            // - MenuCommandHandler (if it implements ITelegramCallbackQueryHandler)
            // - MarketAnalysisCallbackHandler (if it implements ITelegramCallbackQueryHandler)
            // - FundamentalAnalysisCallbackHandler (if it implements ITelegramCallbackQueryHandler)
            // - Any other callback handlers in that assembly.

            // REMOVE explicit registration if covered by scan:
            // services.AddScoped<ITelegramCallbackQueryHandler, FundamentalAnalysisCallbackHandler>(); // This is now redundant if scan works

            // ------------------- 6. Register State Machine & States -------------------
            services.AddSingleton<IUserConversationStateService, InMemoryUserConversationStateService>();
            services.AddScoped<ITelegramStateMachine, TelegramStateMachine>();
            services.Scan(scan => scan
                .FromAssemblyOf<TelegramStateMachine>()
                .AddClasses(classes => classes.AssignableTo<ITelegramState>().Where(c => !c.IsAbstract && c.IsClass))
                .AsImplementedInterfaces()
                .WithScopedLifetime());

            // ------------------- 7. Register INotificationService Implementation -------------------
            services.AddScoped<INotificationService, TelegramNotificationService>();

            // ------------------- 8. Register Hosted Services -------------------
            services.AddHostedService<TelegramBotService>();
            services.AddHostedService<UpdateQueueConsumerService>();

            return services;
        }
    }
}