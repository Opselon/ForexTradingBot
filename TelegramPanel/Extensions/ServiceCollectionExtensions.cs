#region Usings
using Application.Common.Interfaces; // For INotificationService
using Application.Common.Interfaces.Fred;
using Application.Features.Forwarding.Interfaces;
using Application.Features.Forwarding.Services;
using Application.Services.FredApi;
using Domain.Features.Forwarding.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using StackExchange.Redis;
using Telegram.Bot;
using TelegramPanel.Application.CommandHandlers.Entry;
using TelegramPanel.Application.CommandHandlers.Features.Analysis;
using TelegramPanel.Application.CommandHandlers.Features.CoinGecko;
using TelegramPanel.Application.CommandHandlers.Features.EconomicCalendar;
using TelegramPanel.Application.Interfaces;    // For ITelegram...Handler interfaces
using TelegramPanel.Application.Pipeline;
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
            _ = services.Configure<TelegramPanelSettings>(configuration.GetSection(TelegramPanelSettings.SectionName));
            _ = services.Configure<List<ForwardingRule>>(configuration.GetSection("ForwardingRules"));

            // 2. Register ITelegramBotClient
            _ = services.AddSingleton<ITelegramBotClient>(serviceProvider =>
            {
                var settings = serviceProvider.GetRequiredService<IOptions<TelegramPanelSettings>>().Value;
                return string.IsNullOrWhiteSpace(settings.BotToken)
                    ? throw new ArgumentNullException(nameof(settings.BotToken), "TelegramPanel: Bot Token is not configured.")
                    : (ITelegramBotClient)new TelegramBotClient(settings.BotToken);
            });



            // 3. Register ITelegramMessageSender
            _ = services.AddScoped<ITelegramCallbackQueryHandler, CryptoCallbackHandler>();

            // 4. Register ITelegramUpdateChannel and related services
            _ = services.AddSingleton<ITelegramUpdateChannel, TelegramUpdateChannel>();

            //  5. Register ITelegramUpdateProcessor and related services
            _ = services.AddScoped<ITelegramUpdateProcessor, UpdateProcessingService>();

            // 6. Register ITelegramUpdateJobService for Hangfire
            _ = services.AddScoped<IDirectMessageSender, DirectTelegramMessageSender>();

            // 7. Register ITelegramUpdateJobService for Hangfire
            _ = services.AddSingleton<ITelegramUpdateChannel>(serviceProvider =>
            {
                // 1. Get the necessary services from the DI container.
                var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();

                // 2. Try to get the Redis connection. Use GetService, which returns null
                // if the service is not registered, preventing a crash.
                var redisConnection = serviceProvider.GetService<IConnectionMultiplexer>();

                // 3. The DECISION LOGIC.
                if (redisConnection != null && redisConnection.IsConnected)
                {
                    // --- Strategy 1: Redis is available and connected ---
                    var redisLogger = loggerFactory.CreateLogger<RedisUpdateChannel>();
                    Log.Information("✅ Redis connection is active. Registering RedisUpdateChannel for the queue.");

                    // Create and return the Redis-based implementation.
                    return new RedisUpdateChannel(redisConnection, redisLogger);
                }
                else
                {
                    // --- Strategy 2: Redis is not available, fall back to in-memory ---
                    var inMemoryLogger = loggerFactory.CreateLogger<TelegramUpdateChannel>();
                    Log.Warning("⚠️ Redis connection is NOT active or not registered. Falling back to In-Memory queue. Note: Updates will be lost on application restart.");

                    // Create and return the original, in-memory implementation.
                    return new TelegramUpdateChannel(inMemoryLogger);
                }
            });

            // 7. Register IConnectionMultiplexer for Redis
            services.AddSingleton<IConnectionMultiplexer>(sp =>
            {
                var config = sp.GetRequiredService<IConfiguration>();
                var connectionString = config.GetConnectionString("Redis");

                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    throw new InvalidOperationException("Redis connection string is not configured.");
                }

                var options = ConfigurationOptions.Parse(connectionString);
                options.AbortOnConnectFail = false; // For startup resiliency  
                return ConnectionMultiplexer.Connect(options);
            });


            // 8. Register ITelegramUpdateJobService for Hangfire
            _ = services.AddScoped<IMarketDataService, MarketDataService>();

            // 9. Register ITelegramUpdateJobService for Hangfire
            _ = services.AddScoped<ITelegramMiddleware, LoggingMiddleware>();

            // 10. Register ITelegramMiddleware for authentication
            _ = services.AddScoped<ITelegramMiddleware, AuthenticationMiddleware>();

            // 11. Register ITelegramMiddleware for rate limiting
            _ = services.AddScoped<IBroadcastScheduler, BroadcastScheduler>();

            // 12. Register ITelegramMiddleware for broadcast scheduling
            _ = services.AddTransient<IBotCommandSetupService, BotCommandSetupService>();




            // --- ✅ CONSOLIDATED HANDLER REGISTRATION ---
            // Assuming ALL your command and callback query handlers for this panel
            // are in the same assembly as StartCommandHandler.
            // If not, you'll need separate scans per assembly.

            // 5. Register ALL ITelegramCommandHandler implementations from the assembly

            // This will scan the assembly of StartCommandHandler
            _ = services.Scan(scan => scan
                .FromAssemblyOf<StartCommandHandler>() // A marker from your TelegramPanel.Application assembly
                .AddClasses(classes => classes.AssignableTo<ITelegramCommandHandler>())
                    .AsImplementedInterfaces().WithScopedLifetime()
                .AddClasses(classes => classes.AssignableTo<ITelegramCallbackQueryHandler>())
                    .AsImplementedInterfaces().WithScopedLifetime()
                .AddClasses(classes => classes.AssignableTo<ITelegramState>())
                    .AsImplementedInterfaces().WithScopedLifetime());

            // 6. Register State Machine

            // ------------------- 6. Register State Machine & States -------------------
            _ = services.AddScoped<ITelegramCallbackQueryHandler, AnalysisCallbackHandler>();

            // Register Analysis Services
            _ = services.AddScoped<IEconomicCalendarService, EconomicCalendarService>();

            // Register Economic Calendar Handlers
            _ = services.AddScoped<ITelegramCallbackQueryHandler, EconomicCalendarCallbackHandler>();

            // Register CoinGecko Handlers
            _ = services.AddScoped<IActualTelegramMessageActions, ActualTelegramMessageActions>(); // << ثبت صحیح برای اجرای واقعی
                                                                                                   // سپس ITelegramMessageSender که جاب‌ها را به Hangfire رله می‌کند
            _ = services.AddScoped<ITelegramMessageSender, HangfireRelayTelegramMessageSender>(); // << ثبت صحیح برای انکیو کردن

            // Register Forwarding Services
            _ = services.AddScoped<ITelegramState, NewsSearchState>();
            _ = services.AddScoped<IForwardingJobActions, ForwardingJobActions>();
            _ = services.AddScoped<MessageForwardingService>();

            // This will pick up:
            // - MenuCommandHandler (if it implements ITelegramCallbackQueryHandler)
            // - MarketAnalysisCallbackHandler (if it implements ITelegramCallbackQueryHandler)
            // - FundamentalAnalysisCallbackHandler (if it implements ITelegramCallbackQueryHandler)
            // - Any other callback handlers in that assembly.

            // REMOVE explicit registration if covered by scan:
            // services.AddScoped<ITelegramCallbackQueryHandler, FundamentalAnalysisCallbackHandler>(); // This is now redundant if scan works
            _ = services.AddScoped<ITelegramState, FredSearchState>();
            // ------------------- 6. Register State Machine & States -------------------
            _ = services.AddSingleton<IUserConversationStateService, InMemoryUserConversationStateService>();
            _ = services.AddScoped<ITelegramStateMachine, TelegramStateMachine>();
            _ = services.Scan(scan => scan
                .FromAssemblyOf<TelegramStateMachine>()
                .AddClasses(classes => classes.AssignableTo<ITelegramState>().Where(c => !c.IsAbstract && c.IsClass))
                .AsImplementedInterfaces()
                .WithScopedLifetime());

            // ------------------- 7. Register INotificationService Implementation -------------------
            _ = services.AddScoped<INotificationService, TelegramNotificationService>();
            _ = services.AddScoped<ITelegramCallbackQueryHandler, CryptoCallbackHandler>();
            // ------------------- 8. Register Hosted Services -------------------
            _ = services.AddHostedService<TelegramBotService>();
            _ = services.AddHostedService<UpdateQueueConsumerService>();

            // Register the new CryptoCallbackHandler


            return services;
        }
    }
}