using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TelegramPanel.Infrastructure
{
    public interface IBotCommandSetupService
    {
        Task SetupCommandsAsync(CancellationToken cancellationToken = default);
    }

    public class BotCommandSetupService : IBotCommandSetupService
    {
        private readonly ITelegramBotClient _botClient;
        private readonly ILogger<BotCommandSetupService> _logger;

        public BotCommandSetupService(
            ITelegramBotClient botClient,
            ILogger<BotCommandSetupService> logger)
        {
            _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task SetupCommandsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var commands = new List<BotCommand>
                {
                    new() { Command = "start", Description = "Start the bot and register" },
                    new() { Command = "help", Description = "Show help information" },
                    new() { Command = "menu", Description = "Open the main menu" },
                    new() { Command = "commands", Description = "Show all available commands" },
                    new() { Command = "signals", Description = "View available trading signals" },
                    new() { Command = "analysis", Description = "Get market analysis" },
                    new() { Command = "portfolio", Description = "View your trading portfolio" },
                    new() { Command = "profile", Description = "View your profile" },
                    new() { Command = "subscribe", Description = "View subscription plans" },
                    new() { Command = "settings", Description = "Configure your preferences" },
                    new() { Command = "contact", Description = "Contact support" },
                    new() { Command = "feedback", Description = "Send feedback" },
                    new() { Command = "faq", Description = "View frequently asked questions" }
                };

                await _botClient.SetMyCommands(
                    commands: commands,
                    scope: new BotCommandScopeDefault(),
                    languageCode: null,
                    cancellationToken: cancellationToken);

                _logger.LogInformation("Successfully set up bot commands");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting up bot commands");
                throw;
            }
        }
    }
}