using Microsoft.Extensions.Logging;
using System.Text; // برای StringBuilder
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters; // برای TelegramMessageFormatter
using TelegramPanel.Infrastructure;

namespace TelegramPanel.Application.CommandHandlers
{
    public class HelpCommandHandler : ITelegramCommandHandler
    {
        private readonly ILogger<HelpCommandHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;

        public HelpCommandHandler(ILogger<HelpCommandHandler> logger, ITelegramMessageSender messageSender)
        {
            _logger = logger;
            _messageSender = messageSender;
        }

        public bool CanHandle(Update update)
        {
            return update.Type == UpdateType.Message &&
                   update.Message?.Text?.Trim().Equals("/help", StringComparison.OrdinalIgnoreCase) == true;
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            var message = update.Message;
            if (message == null) return;

            var chatId = message.Chat.Id;
            _logger.LogInformation("Handling /help command for ChatID {ChatId}", chatId);

            var helpText = new StringBuilder();
            helpText.AppendLine(TelegramMessageFormatter.Bold("Forex Signal Bot Help"));
            helpText.AppendLine("Here are the available commands:");
            helpText.AppendLine(); // خط خالی
            helpText.AppendLine($"`/start` - Start interacting with the bot and register.");
            helpText.AppendLine($"`/menu` - Show the main menu with options.");
            helpText.AppendLine($"`/signals` - View available trading signals (premium feature).");
            helpText.AppendLine($"`/subscribe` - View subscription plans and subscribe.");
            helpText.AppendLine($"`/profile` - View your profile and subscription status.");
            helpText.AppendLine($"`/settings` - Change your preferences (e.g., signal notifications).");
            helpText.AppendLine($"`/help` - Show this help message.");
            helpText.AppendLine();
            helpText.AppendLine("For more assistance, please contact support.");

            await _messageSender.SendTextMessageAsync(chatId, helpText.ToString(), ParseMode.MarkdownV2, cancellationToken: cancellationToken);
        }
    }
}