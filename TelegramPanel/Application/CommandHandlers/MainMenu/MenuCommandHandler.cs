// File: TelegramPanel/Application/CommandHandlers/MenuCommandHandler.cs
#region Usings
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Infrastructure;
using TelegramPanel.Infrastructure.Helpers;
#endregion

namespace TelegramPanel.Application.CommandHandlers.MainMenu
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
        public const string MarketAnalysisData = "market_analysis";
        public const string AnalysisCallbackData = "menu_analysis";
        public const string EconomicCalendarCallbackData = "menu_econ_calendar"; // <<< NEW
        public const string BackToMainMenuGeneral = "back_to_main_menu"; //  Add this
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
        /// Generates the text and inline keyboard markup for the main application menu.
        /// </summary>
        public static (string text, InlineKeyboardMarkup keyboard) GetMainMenuMarkup()
        {
            var text = "Welcome to the Main Menu!\nChoose one of the available options:";

            var keyboard = MarkupBuilder.CreateInlineKeyboard(
                new[] // Row 1: Core Features
                {
                    InlineKeyboardButton.WithCallbackData("📈 View Signals", SignalsCallbackData),
                    InlineKeyboardButton.WithCallbackData("📊 Market Analysis", MarketAnalysisData)
                },
                new[] // Row 2: NEW Analysis Button
                {
                    InlineKeyboardButton.WithCallbackData("🔍 News Analysis", AnalysisCallbackData),
                    InlineKeyboardButton.WithCallbackData("🗓️ Economic Calendar", EconomicCalendarCallbackData)

                },
                new[] // Row 3: Subscription
                {
                    InlineKeyboardButton.WithCallbackData("💎 Subscribe / Plans", SubscribeCallbackData)
                },
                new[] // Row 4: Account Management
                {
                    InlineKeyboardButton.WithCallbackData("⚙️ Settings", SettingsCallbackData),
                    InlineKeyboardButton.WithCallbackData("👤 My Profile", ProfileCallbackData)
                }
            );
            return (text, keyboard);
        }

        #endregion

        #region ITelegramCommandHandler Implementation
        public bool CanHandle(Update update)
        {
            // THIS IS THE CORRECTED LOGIC FOR A COMMAND HANDLER
            return update.Type == UpdateType.Message &&
                   update.Message?.Text?.Trim().Equals("/menu", StringComparison.OrdinalIgnoreCase) == true;
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            // This logic correctly handles the /menu command by sending the menu.
            var message = update.Message; // This is now guaranteed to be from a Message update.
            if (message == null) // Should not happen if CanHandle is correct, but good practice.
            {
                _logger.LogWarning("MenuCommand: Message is null in UpdateID {UpdateId}, despite CanHandle passing.", update.Id);
                return;
            }

            var chatId = message.Chat.Id;
            var userId = message.From?.Id; // For logging

            _logger.LogInformation("Handling /menu command for ChatID {ChatId}, UserID {UserId}", chatId, userId);

            // Use the static GetMainMenuMarkup method
            var (text, inlineKeyboard) = GetMainMenuMarkup(); //  اطمینان حاصل کنید که GetMainMenuMarkup اصلاح شده است

            await _messageSender.SendTextMessageAsync(
                chatId: chatId,
                text: text, // Use the text from GetMainMenuMarkup
                parseMode: ParseMode.MarkdownV2, // Assuming text might have Markdown
                replyMarkup: inlineKeyboard,
                cancellationToken: cancellationToken);

            _logger.LogDebug("Main menu sent to ChatID {ChatId} via /menu command.", chatId);
        }
        #endregion

    }
}