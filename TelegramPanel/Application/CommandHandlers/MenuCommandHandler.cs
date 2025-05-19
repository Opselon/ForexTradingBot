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
        public const string MarketAnalysisCallback = "market_analysis";


        #endregion

        #region Constructor
        public MenuCommandHandler(ILogger<MenuCommandHandler> logger, ITelegramMessageSender messageSender)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
        }
        #endregion

        #region Static Menu Markup Generation

        /// <summary>
        /// Generates the inline keyboard markup for the main application menu.
        /// </summary>
        public static InlineKeyboardMarkup GetMainMenuKeyboard()
        {
            return new InlineKeyboardMarkup(new[]
            {
                new [] // Row 1
                {
                    InlineKeyboardButton.WithCallbackData("📈 Gold Signals", SignalsCallbackData),
                    InlineKeyboardButton.WithCallbackData("📊 Market Analysis", MarketAnalysisCallback)
                },
                new [] // Row 2
                {
                    InlineKeyboardButton.WithCallbackData("💎 VIP Signals", SubscribeCallbackData),
                    InlineKeyboardButton.WithCallbackData("⚙️ Settings", SettingsCallbackData)
                },
                new [] // Row 3
                {
                    InlineKeyboardButton.WithCallbackData("📱 My Profile", ProfileCallbackData)
                }
            });
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

            var text = "🌟 *Main Menu*\n\nChoose an option below:";

            var inlineKeyboard = GetMainMenuKeyboard();

            await _messageSender.SendTextMessageAsync(
                chatId: chatId,
                text: text,
                parseMode: ParseMode.MarkdownV2,
                replyMarkup: inlineKeyboard,
                cancellationToken: cancellationToken);

            _logger.LogDebug("Main menu sent to ChatID {ChatId}", chatId);
        }
        #endregion

    }
}