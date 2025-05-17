// File: TelegramPanel/Application/CommandHandlers/SettingsCommandHandler.cs
#region Usings
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Infrastructure;
using TelegramPanel.Formatters;
#endregion

namespace TelegramPanel.Application.CommandHandlers
{
    public class SettingsCommandHandler : ITelegramCommandHandler
    {
        #region Private Readonly Fields
        private readonly ILogger<SettingsCommandHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        #endregion

        #region Callback Data Constants for Settings Menu
        // این ثابت‌ها برای CallbackQuery هایی که از دکمه‌های این منو می‌آیند، استفاده می‌شوند.
        public const string PrefsSignalCategoriesCallback = "settings_prefs_categories";
        public const string PrefsNotificationsCallback = "settings_prefs_notifications";
        public const string MySubscriptionInfoCallback = "settings_my_subscription";
        public const string SignalHistoryCallback = "settings_signal_history"; // اختیاری
        public const string PublicSignalsCallback = "settings_public_signals"; // اختیاری
        //  BackToMainMenuGeneral از MenuCallbackQueryHandler استفاده می‌شود.
        #endregion

        #region Constructor
        public SettingsCommandHandler(
            ILogger<SettingsCommandHandler> logger,
            ITelegramMessageSender messageSender)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
        }
        #endregion

        #region ITelegramCommandHandler Implementation
        public bool CanHandle(Update update)
        {
            return update.Type == UpdateType.Message &&
                   update.Message?.Text?.Trim().Equals("/settings", StringComparison.OrdinalIgnoreCase) == true;
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            var message = update.Message;
            if (message?.From == null)
            {
                _logger.LogWarning("SettingsCommand: Message or From user is null in UpdateID {UpdateId}.", update.Id);
                return;
            }

            var chatId = message.Chat.Id;
            var userId = message.From.Id;

            _logger.LogInformation("Handling /settings command for UserID {TelegramUserId}, ChatID {ChatId}", userId, chatId);

            var settingsMenuText = TelegramMessageFormatter.Bold("⚙️ User Settings", escapePlainText: false) + "\n\n" +
                                   "Please choose a category to configure:";

            var settingsKeyboard = new InlineKeyboardMarkup(new[]
            {
                new [] { InlineKeyboardButton.WithCallbackData("📊 My Signal Preferences", PrefsSignalCategoriesCallback) },
                new [] { InlineKeyboardButton.WithCallbackData("🔔 Notification Settings", PrefsNotificationsCallback) },
                new [] { InlineKeyboardButton.WithCallbackData("⭐ My Subscription", MySubscriptionInfoCallback) },
                //  دکمه‌های اختیاری (می‌توانید بعداً اضافه کنید)
                // new [] { InlineKeyboardButton.WithCallbackData("📜 Signal History", SignalHistoryCallback) },
                // new [] { InlineKeyboardButton.WithCallbackData("📢 Public Signals", PublicSignalsCallback) },
                new [] { InlineKeyboardButton.WithCallbackData("⬅️ Back to Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral) }
            });

            // ارسال پیام جدید با منوی تنظیمات
            await _messageSender.SendTextMessageAsync(
                chatId,
                settingsMenuText,
                ParseMode.MarkdownV2,
                replyMarkup: settingsKeyboard,
                cancellationToken: cancellationToken);
        }
        #endregion
    }
}