// File: TelegramPanel/Application/CommandHandlers/MenuCommandHandler.cs
#region Usings
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Infrastructure;
#endregion

namespace TelegramPanel.Application.CommandHandlers
{
    public class MenuCommandHandler : ITelegramCommandHandler
    {
        #region Private Fields
        private readonly ILogger<MenuCommandHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;

        // Callback Data constants for menu buttons
        public const string SignalsCallbackData = "menu_view_signals";
        public const string ProfileCallbackData = "menu_my_profile";
        public const string SubscribeCallbackData = "menu_subscribe_plans";
        public const string SettingsCallbackData = "menu_user_settings";
        #endregion

        #region Constructor
        public MenuCommandHandler(ILogger<MenuCommandHandler> logger, ITelegramMessageSender messageSender)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
        }
        #endregion

        #region ITelegramCommandHandler Implementation
        public bool CanHandle(Update update)
        {
            return update.Type == UpdateType.Message &&
                   update.Message?.Text?.Trim().Equals("/menu", StringComparison.OrdinalIgnoreCase) == true;
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            var message = update.Message;
            if (message == null)
            {
                _logger.LogWarning("MenuCommand: Message is null in UpdateID {UpdateId}.", update.Id);
                return;
            }

            var chatId = message.Chat.Id;
            var userId = message.From?.Id; // For logging

            _logger.LogInformation("Handling /menu command for ChatID {ChatId}, UserID {UserId}", chatId, userId);

            var text = "Welcome to the Main Menu! Please choose an option:";

            var inlineKeyboard = new InlineKeyboardMarkup(new[]
            {
                // Row 1
                new []
                {
                    InlineKeyboardButton.WithCallbackData("📈 View Signals", SignalsCallbackData),
                    InlineKeyboardButton.WithCallbackData("👤 My Profile", ProfileCallbackData),
                },
                // Row 2
                new []
                {
                    InlineKeyboardButton.WithCallbackData("💎 Subscribe", SubscribeCallbackData),
                    InlineKeyboardButton.WithCallbackData("⚙️ Settings", SettingsCallbackData),
                }
                // You can add more rows or buttons here
            });

            await _messageSender.SendTextMessageAsync(
                chatId: chatId,
                text: text,
                replyMarkup: inlineKeyboard,
                cancellationToken: cancellationToken);

            _logger.LogDebug("Main menu sent to ChatID {ChatId}", chatId);
        }
        #endregion
    }
}