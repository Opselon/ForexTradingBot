using Microsoft.Extensions.Logging;
using System.Text; // برای StringBuilder
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters; // برای TelegramMessageFormatter
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions;

namespace TelegramPanel.Application.CommandHandlers.Entry
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
            Message? message = update.Message;
            if (message == null)
            {
                return;
            }

            long chatId = message.Chat.Id;
            _logger.LogInformation("Handling /help command for ChatID {ChatId}", chatId);

            StringBuilder helpText = new();
            _ = helpText.AppendLine(TelegramMessageFormatter.Bold("Forex Signal Bot Help"));
            _ = helpText.AppendLine("Here are the available commands:");
            _ = helpText.AppendLine(); // خط خالی
            _ = helpText.AppendLine($"`/start` - Start interacting with the bot and register.");
            _ = helpText.AppendLine($"`/menu` - Show the main menu with options.");
            _ = helpText.AppendLine($"`/signals` - View available trading signals (premium feature).");
            _ = helpText.AppendLine($"`/subscribe` - View subscription plans and subscribe.");
            _ = helpText.AppendLine($"`/profile` - View your profile and subscription status.");
            _ = helpText.AppendLine($"`/settings` - Change your preferences (e.g., signal notifications).");
            _ = helpText.AppendLine($"`/help` - Show this help message.");
            _ = helpText.AppendLine();
            _ = helpText.AppendLine("For more assistance, please contact support.");

            await _messageSender.SendTextMessageAsync(chatId, helpText.ToString(), ParseMode.MarkdownV2, cancellationToken: cancellationToken);
        }
    }
}