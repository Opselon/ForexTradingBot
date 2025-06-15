using Microsoft.Extensions.Logging;
using System.Text;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.CommandHandlers.MainMenu;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters;
using TelegramPanel.Infrastructure.Helper;
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions;

namespace TelegramPanel.Application.CommandHandlers.Entry
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
            Message? message = update.Message;
            if (message == null)
            {
                return;
            }

            long chatId = message.Chat.Id;
            _logger.LogInformation("Handling /commands command for ChatID {ChatId}", chatId);

            StringBuilder commandsText = new();

            // Header
            _ = commandsText.AppendLine(TelegramMessageFormatter.Bold("📋 Available Commands"));
            _ = commandsText.AppendLine();

            // Basic Commands
            _ = commandsText.AppendLine(TelegramMessageFormatter.Bold("🔹 Basic Commands"));
            _ = commandsText.AppendLine($"`/start` - Start the bot and register");
            _ = commandsText.AppendLine($"`/help` - Show help information");
            _ = commandsText.AppendLine($"`/menu` - Open the main menu");
            _ = commandsText.AppendLine($"`/commands` - Show this commands list");
            _ = commandsText.AppendLine();

            // Trading Commands
            _ = commandsText.AppendLine(TelegramMessageFormatter.Bold("📊 Trading Commands"));
            _ = commandsText.AppendLine($"`/signals` - View available trading signals");
            _ = commandsText.AppendLine($"`/analysis` - Get market analysis");
            _ = commandsText.AppendLine($"`/portfolio` - View your trading portfolio");
            _ = commandsText.AppendLine();

            // Account Commands
            _ = commandsText.AppendLine(TelegramMessageFormatter.Bold("👤 Account Commands"));
            _ = commandsText.AppendLine($"`/profile` - View your profile");
            _ = commandsText.AppendLine($"`/subscribe` - View subscription plans");
            _ = commandsText.AppendLine($"`/settings` - Configure your preferences");
            _ = commandsText.AppendLine();

            // Support Commands
            _ = commandsText.AppendLine(TelegramMessageFormatter.Bold("💬 Support Commands"));
            _ = commandsText.AppendLine($"`/contact` - Contact support");
            _ = commandsText.AppendLine($"`/feedback` - Send feedback");
            _ = commandsText.AppendLine($"`/faq` - View frequently asked questions");
            _ = commandsText.AppendLine();

            // Footer
            _ = commandsText.AppendLine(TelegramMessageFormatter.Italic("Tip: Use /help for detailed information about each command"));

            // Create inline keyboard for quick access to main features
            InlineKeyboardMarkup? keyboard = MarkupBuilder.CreateInlineKeyboard(
         new[]
         {
            InlineKeyboardButton.WithCallbackData("📊 View Signals", MenuCommandHandler.SignalsCallbackData),
            InlineKeyboardButton.WithCallbackData("👤 My Profile", MenuCommandHandler.ProfileCallbackData)
         },
         new[]
         {
            InlineKeyboardButton.WithCallbackData("💎 Subscribe", MenuCommandHandler.SubscribeCallbackData),
            InlineKeyboardButton.WithCallbackData("⚙️ Settings", MenuCommandHandler.SettingsCallbackData)
         }
     );

            await _messageSender.SendTextMessageAsync(
                chatId,
                commandsText.ToString(),
                ParseMode.Markdown, // یا V2
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
    }
}