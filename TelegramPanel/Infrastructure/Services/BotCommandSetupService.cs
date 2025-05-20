using Microsoft.Extensions.Logging;
using System.Collections.Generic;
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

        // Command definitions
        private static readonly (string Command, string Description) StartCmd = ("start", "Start the bot and register");
        private static readonly (string Command, string Description) HelpCmd = ("help", "Show help information");
        private static readonly (string Command, string Description) MenuCmd = ("menu", "Open the main menu");
        private static readonly (string Command, string Description) CommandsCmd = ("commands", "Show all available commands");
        private static readonly (string Command, string Description) SignalsCmd = ("signals", "View available trading signals");
        private static readonly (string Command, string Description) AnalysisCmd = ("analysis", "Get market analysis");
        private static readonly (string Command, string Description) PortfolioCmd = ("portfolio", "View your trading portfolio");
        private static readonly (string Command, string Description) ProfileCmd = ("profile", "View your profile");
        private static readonly (string Command, string Description) SubscribeCmd = ("subscribe", "View subscription plans");
        private static readonly (string Command, string Description) SettingsCmd = ("settings", "Configure your preferences");
        private static readonly (string Command, string Description) ContactCmd = ("contact", "Contact support");
        private static readonly (string Command, string Description) FeedbackCmd = ("feedback", "Send feedback");
        private static readonly (string Command, string Description) FaqCmd = ("faq", "View frequently asked questions");

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
                    new() { Command = StartCmd.Command, Description = StartCmd.Description },
                    new() { Command = HelpCmd.Command, Description = HelpCmd.Description },
                    new() { Command = MenuCmd.Command, Description = MenuCmd.Description },
                    new() { Command = CommandsCmd.Command, Description = CommandsCmd.Description },
                    new() { Command = SignalsCmd.Command, Description = SignalsCmd.Description },
                    new() { Command = AnalysisCmd.Command, Description = AnalysisCmd.Description },
                    new() { Command = PortfolioCmd.Command, Description = PortfolioCmd.Description },
                    new() { Command = ProfileCmd.Command, Description = ProfileCmd.Description },
                    new() { Command = SubscribeCmd.Command, Description = SubscribeCmd.Description },
                    new() { Command = SettingsCmd.Command, Description = SettingsCmd.Description },
                    new() { Command = ContactCmd.Command, Description = ContactCmd.Description },
                    new() { Command = FeedbackCmd.Command, Description = FeedbackCmd.Description },
                    new() { Command = FaqCmd.Command, Description = FaqCmd.Description }
                };

                await _botClient.SetMyCommands(
                    commands: commands,
                    scope: new BotCommandScopeDefault(),
                    languageCode: null,
                    cancellationToken: cancellationToken);

                _logger.LogInformation("Successfully set up bot commands ({Count})", commands.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting up bot commands");
                throw;
            }
        }
    }
}