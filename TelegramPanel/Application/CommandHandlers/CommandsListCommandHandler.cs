using System.Text;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters;
using TelegramPanel.Infrastructure;

namespace TelegramPanel.Application.CommandHandlers
{
    public class CommandsListCommandHandler : ITelegramCommandHandler
    {
        private readonly ILogger<CommandsListCommandHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;

        public CommandsListCommandHandler(
            ILogger<CommandsListCommandHandler> logger,
            ITelegramMessageSender messageSender)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
        }

        public bool CanHandle(Update update)
        {
            return update.Type == UpdateType.Message &&
                   update.Message?.Text?.Trim().Equals("/commands", StringComparison.OrdinalIgnoreCase) == true;
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            var message = update.Message;
            if (message == null) return;

            var chatId = message.Chat.Id;
            _logger.LogInformation("Handling /commands command for ChatID {ChatId}", chatId);

            var commandsText = new StringBuilder();
            
            // Header
            commandsText.AppendLine(TelegramMessageFormatter.Bold("📋 Available Commands"));
            commandsText.AppendLine();

            // Basic Commands
            commandsText.AppendLine(TelegramMessageFormatter.Bold("🔹 Basic Commands"));
            commandsText.AppendLine($"`/start` - Start the bot and register");
            commandsText.AppendLine($"`/help` - Show help information");
            commandsText.AppendLine($"`/menu` - Open the main menu");
            commandsText.AppendLine($"`/commands` - Show this commands list");
            commandsText.AppendLine();

            // Trading Commands
            commandsText.AppendLine(TelegramMessageFormatter.Bold("📊 Trading Commands"));
            commandsText.AppendLine($"`/signals` - View available trading signals");
            commandsText.AppendLine($"`/analysis` - Get market analysis");
            commandsText.AppendLine($"`/portfolio` - View your trading portfolio");
            commandsText.AppendLine();

            // Account Commands
            commandsText.AppendLine(TelegramMessageFormatter.Bold("👤 Account Commands"));
            commandsText.AppendLine($"`/profile` - View your profile");
            commandsText.AppendLine($"`/subscribe` - View subscription plans");
            commandsText.AppendLine($"`/settings` - Configure your preferences");
            commandsText.AppendLine();

            // Support Commands
            commandsText.AppendLine(TelegramMessageFormatter.Bold("💬 Support Commands"));
            commandsText.AppendLine($"`/contact` - Contact support");
            commandsText.AppendLine($"`/feedback` - Send feedback");
            commandsText.AppendLine($"`/faq` - View frequently asked questions");
            commandsText.AppendLine();

            // Footer
            commandsText.AppendLine(TelegramMessageFormatter.Italic("Tip: Use /help for detailed information about each command"));

            // Create inline keyboard for quick access to main features
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new []
                {
                    InlineKeyboardButton.WithCallbackData("📊 View Signals", MenuCommandHandler.SignalsCallbackData),
                    InlineKeyboardButton.WithCallbackData("👤 My Profile", MenuCommandHandler.ProfileCallbackData)
                },
                new []
                {
                    InlineKeyboardButton.WithCallbackData("💎 Subscribe", MenuCommandHandler.SubscribeCallbackData),
                    InlineKeyboardButton.WithCallbackData("⚙️ Settings", MenuCommandHandler.SettingsCallbackData)
                }
            });

            await _messageSender.SendTextMessageAsync(
                chatId,
                commandsText.ToString(),
                ParseMode.MarkdownV2,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
    }
} 