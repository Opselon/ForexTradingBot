// File: TelegramPanel/Application/CommandHandlers/MenuCallbackQueryHandler.cs
#region Usings
using Application.Interfaces;
using Application.DTOs;
using AutoMapper;
using Microsoft.Extensions.Logging;
using System.Collections.Generic; // برای List<InlineKeyboardButton> در ارسال مجدد منو
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Infrastructure;
using TelegramPanel.Formatters;
#endregion

namespace TelegramPanel.Application.CommandHandlers
{
    public class MenuCallbackQueryHandler : ITelegramCommandHandler
    {
        #region Private Fields
        private readonly ILogger<MenuCallbackQueryHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly ITelegramBotClient _botClient;
        private readonly IUserService _userService;
        private readonly ISignalService _signalService;
        private readonly IMapper _mapper;
        // private readonly MenuCommandHandler _menuCommandHandler; // ✅ برای فراخوانی مستقیم (روش جایگزین)
        #endregion

        // Callback Data constants برای دکمه‌های بازگشت
        public const string BackToMainMenuFromProfile = "main_menu_from_profile";
        public const string BackToMainMenuFromSubscribe = "main_menu_from_subscribe";
        public const string BackToMainMenuFromSettings = "main_menu_from_settings";
        // می‌توانید یک CallbackData عمومی برای بازگشت به منو در نظر بگیرید
        public const string GeneralBackToMainMenuCallback = "main_menu_back";


        #region Constructor
        public MenuCallbackQueryHandler(
            ILogger<MenuCallbackQueryHandler> logger,
            ITelegramMessageSender messageSender,
            ITelegramBotClient botClient,
            IUserService userService,
            ISignalService signalService,
            IMapper mapper
            // MenuCommandHandler menuCommandHandler // ✅ تزریق اگر می‌خواهید مستقیم فراخوانی کنید
            )
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
            _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
            _userService = userService ?? throw new ArgumentNullException(nameof(userService));
            _signalService = signalService ?? throw new ArgumentNullException(nameof(signalService));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            // _menuCommandHandler = menuCommandHandler; // ✅ مقداردهی
        }
        #endregion

        #region ITelegramCommandHandler Implementation
        public bool CanHandle(Update update)
        {
            return update.Type == UpdateType.CallbackQuery &&
                   update.CallbackQuery?.Data != null &&
                   (update.CallbackQuery.Data.Equals(MenuCommandHandler.SignalsCallbackData) ||
                    update.CallbackQuery.Data.Equals(MenuCommandHandler.ProfileCallbackData) ||
                    update.CallbackQuery.Data.Equals(MenuCommandHandler.SubscribeCallbackData) ||
                    update.CallbackQuery.Data.Equals(MenuCommandHandler.SettingsCallbackData) ||
                    // ✅ اضافه کردن CallbackData های جدید برای دکمه‌های بازگشت
                    update.CallbackQuery.Data.Equals(BackToMainMenuFromProfile) ||
                    update.CallbackQuery.Data.Equals(BackToMainMenuFromSubscribe) ||
                    update.CallbackQuery.Data.Equals(BackToMainMenuFromSettings) ||
                    update.CallbackQuery.Data.Equals(GeneralBackToMainMenuCallback) // اگر از این استفاده می‌کنید
                    );
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            var callbackQuery = update.CallbackQuery;
            if (callbackQuery?.Message == null)
            {
                _logger.LogWarning("MenuCallback: CallbackQuery or its Message is null in UpdateID {UpdateId}.", update.Id);
                if (callbackQuery != null) await AnswerCallbackQuerySilentAsync(callbackQuery.Id, cancellationToken);
                return;
            }

            var chatId = callbackQuery.Message.Chat.Id;
            var userId = callbackQuery.From.Id;
            var messageId = callbackQuery.Message.MessageId;
            var data = callbackQuery.Data;

            _logger.LogInformation("Handling CallbackQuery for UserID {UserId}, ChatID {ChatId}, MessageID {MessageId}, Data: {CallbackData}",
                userId, chatId, messageId, data);

            await AnswerCallbackQuerySilentAsync(callbackQuery.Id, cancellationToken, $"Processing request...");

            try
            {
                switch (data)
                {
                    case MenuCommandHandler.SignalsCallbackData:
                        await HandleViewSignalsAsync(chatId, userId, messageId, cancellationToken);
                        break;

                    case MenuCommandHandler.ProfileCallbackData:
                        await HandleMyProfileAsync(chatId, userId, messageId, cancellationToken);
                        break;

                    case MenuCommandHandler.SubscribeCallbackData:
                        await HandleSubscribeAsync(chatId, userId, messageId, cancellationToken);
                        break;

                    case MenuCommandHandler.SettingsCallbackData:
                        await HandleSettingsAsync(chatId, userId, messageId, cancellationToken);
                        break;

                    // ✅ اضافه کردن case برای دکمه‌های بازگشت
                    case BackToMainMenuFromProfile:
                    case BackToMainMenuFromSubscribe:
                    case BackToMainMenuFromSettings:
                    case GeneralBackToMainMenuCallback: // اگر از این استفاده می‌کنید
                        _logger.LogInformation("User {UserId} requested to go back to main menu from {SourcePage}", userId, data);
                        await ShowMainMenuAsync(chatId, messageId, cancellationToken); //  فراخوانی متد برای نمایش مجدد منو
                        break;

                    default:
                        _logger.LogWarning("Unhandled CallbackQuery data in MenuCallback: {CallbackData}", data);
                        await _messageSender.SendTextMessageAsync(chatId, "Sorry, this option is not implemented or recognized yet.", cancellationToken: cancellationToken);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling callback query data '{CallbackData}' for UserID {UserId}, ChatID {ChatId}", data, userId, chatId);
                await _messageSender.SendTextMessageAsync(chatId, "An error occurred while processing your selection. Please try again.", cancellationToken: cancellationToken);
            }
        }
        #endregion

        #region Private Handler Methods for Callbacks

        // ... (متدهای HandleViewSignalsAsync, HandleMyProfileAsync, HandleSubscribeAsync, HandleSettingsAsync بدون تغییر عمده) ...
        // فقط متن UI را بهبود می‌دهیم و از دکمه بازگشت عمومی استفاده می‌کنیم

        private async Task HandleViewSignalsAsync(long chatId, long telegramUserId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            _logger.LogInformation("User {TelegramUserId} requested to view signals.", telegramUserId);
            var signals = await _signalService.GetRecentSignalsAsync(3, includeCategory: true, cancellationToken: cancellationToken); //  تعداد سیگنال‌ها کمتر برای نمایش بهتر
            var sb = new StringBuilder();

            if (signals.Any())
            {
                sb.AppendLine(TelegramMessageFormatter.Bold("📊 Recent Trading Signals:"));
                sb.AppendLine(); // Add a blank line for better readability
                foreach (var signalDto in signals)
                {
                    var formattedSignal = SignalFormatter.FormatSignal(signalDto, ParseMode.MarkdownV2);
                    sb.AppendLine(formattedSignal);
                    sb.AppendLine("─".PadRight(20, '─')); // Separator line
                }
            }
            else
            {
                sb.AppendLine("No active signals available at the moment. Please check back later!");
            }

            var backKeyboard = new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("⬅️ Back to Main Menu", GeneralBackToMainMenuCallback));
            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, sb.ToString(), backKeyboard, ParseMode.MarkdownV2, cancellationToken);
        }

        private async Task HandleMyProfileAsync(long chatId, long telegramUserId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            _logger.LogInformation("User {TelegramUserId} requested to view profile.", telegramUserId);
            var userDto = await _userService.GetUserByTelegramIdAsync(telegramUserId.ToString(), cancellationToken);

            if (userDto == null)
            {
                await EditMessageOrSendNewAsync(chatId, messageIdToEdit, "Your profile could not be retrieved. Please try using /start again.", null, cancellationToken: cancellationToken);
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine(TelegramMessageFormatter.Bold("🔐 Your Profile:"));
            sb.AppendLine($"👤 Username: {TelegramMessageFormatter.Code(userDto.Username)}");
            sb.AppendLine($"📧 Email: {TelegramMessageFormatter.Code(userDto.Email)}");
            sb.AppendLine($"🆔 Telegram ID: {TelegramMessageFormatter.Code(userDto.TelegramId)}");
            sb.AppendLine($"⭐ Access Level: {TelegramMessageFormatter.Bold(GetLevelTitle((int)userDto.Level))}");
            sb.AppendLine($"💰 Token Balance: {TelegramMessageFormatter.Code(userDto.TokenBalance.ToString("N2"))}");

            // تابع داخلی برای نمایش سطح دسترسی بدون نیاز به enum خارجی
            string GetLevelTitle(int level)
            {
                return level switch
                {
                    0 => "🟢 Free",
                    1 => "🥉 Bronze",
                    2 => "🥈 Silver",
                    3 => "🥇 Gold",
                    4 => "💎 Platinum",
                    100 => "🛠️ Admin",
                    _ when level > 100 => $"👑 Custom ({level})",
                    _ => $"❓ Unknown ({level})"
                };
            }

            if (userDto.TokenWallet != null)
            {
                sb.AppendLine($"Token Balance: {TelegramMessageFormatter.Code(userDto.TokenWallet.Balance.ToString("N2"))} Tokens");
            }
            if (userDto.ActiveSubscription != null)
            {
                sb.AppendLine($"Active Subscription: Plan XXX (Expires: {userDto.ActiveSubscription.EndDate:yyyy-MM-dd})"); // نام پلن را اضافه کنید
            }
            else
            {
                sb.AppendLine("Subscription: No active subscription.");
            }

            var backKeyboard = new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("⬅️ Back to Main Menu", GeneralBackToMainMenuCallback));
            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, sb.ToString(), backKeyboard, ParseMode.MarkdownV2, cancellationToken);
        }

        private async Task HandleSubscribeAsync(long chatId, long telegramUserId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            _logger.LogInformation("User {TelegramUserId} requested subscription plans.", telegramUserId);

            var plansText = TelegramMessageFormatter.Bold("💎 Subscription Plans:\n\n") +
                            "▫️ *Free Tier*: Access to basic signals and features.\n" +
                            "▫️ *Premium Tier* (Monthly): Full access to all signals, advanced analytics, and priority updates.\n" +
                            "▫️ *Premium Tier* (Quarterly): Same as monthly premium with a discount.\n\n" +
                            "Please select a plan to learn more or subscribe:";

            var plansKeyboard = new InlineKeyboardMarkup(new[]
            {
                new [] { InlineKeyboardButton.WithCallbackData("🌟 Premium Monthly", "subscribe_premium_1m") }, // callback data برای هر پلن
                new [] { InlineKeyboardButton.WithCallbackData("✨ Premium Quarterly", "subscribe_premium_3m") },
                new [] { InlineKeyboardButton.WithCallbackData("⬅️ Back to Main Menu", GeneralBackToMainMenuCallback) }
            });

            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, plansText, plansKeyboard, ParseMode.MarkdownV2, cancellationToken);
        }

        private async Task HandleSettingsAsync(long chatId, long telegramUserId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            _logger.LogInformation("User {TelegramUserId} requested settings.", telegramUserId);

            var settingsText = "⚙️ *User Settings*\n\n" +
                               "This section is under development.\n" +
                               "Soon you'll be able to customize your signal notifications and preferences here!";
            var backKeyboard = new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("⬅️ Back to Main Menu", GeneralBackToMainMenuCallback));

            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, settingsText, backKeyboard, ParseMode.MarkdownV2, cancellationToken);
        }


        /// <summary>
        /// منوی اصلی را دوباره به کاربر نمایش می‌دهد (با ویرایش پیام قبلی).
        /// </summary>
        private async Task ShowMainMenuAsync(long chatId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Showing main menu again for ChatID {ChatId}", chatId);
            var text = "Welcome to the Main Menu! Please choose an option:";

            var inlineKeyboard = new InlineKeyboardMarkup(new[]
            {
                new []
                {
                    InlineKeyboardButton.WithCallbackData("📈 View Signals", MenuCommandHandler.SignalsCallbackData),
                    InlineKeyboardButton.WithCallbackData("👤 My Profile", MenuCommandHandler.ProfileCallbackData),
                },
                new []
                {
                    InlineKeyboardButton.WithCallbackData("💎 Subscribe", MenuCommandHandler.SubscribeCallbackData),
                    InlineKeyboardButton.WithCallbackData("⚙️ Settings", MenuCommandHandler.SettingsCallbackData),
                }
            });

            //  ویرایش پیام قبلی برای نمایش مجدد منو
            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, text, inlineKeyboard, cancellationToken: cancellationToken);
        }

        #endregion

        #region Helper Methods
        private async Task AnswerCallbackQuerySilentAsync(string callbackQueryId, CancellationToken cancellationToken, string? text = null, bool showAlert = false)
        {
            try
            {
                await _botClient.AnswerCallbackQuery( // ✅ نام متد صحیح
                    callbackQueryId: callbackQueryId,
                    text: text,
                    showAlert: showAlert,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to answer callback query {CallbackQueryId}. This might happen if the query is too old or already answered.", callbackQueryId);
            }
        }

        private async Task EditMessageOrSendNewAsync(long chatId, int messageId, string text, ReplyMarkup? replyMarkup, ParseMode? parseMode = null, CancellationToken cancellationToken = default)
        {
            try
            {
                await _botClient.EditMessageText( // ✅ نام متد صحیح
                    chatId: chatId,
                    messageId: messageId,
                    text: text,
                    parseMode: ParseMode.Markdown, // ✅ ارسال پارامتر parseMode
                    replyMarkup: (InlineKeyboardMarkup?)replyMarkup,
                    cancellationToken: cancellationToken
                );
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase) ||
                                                 ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) ||
                                                 ex.ErrorCode == 400 /* Bad Request, e.g. query is too old */)
            {
                _logger.LogWarning(ex, "Could not edit message (MessageId: {MessageId}, ChatID: {ChatId}) - it might be too old, not found, or not modified. Sending a new message instead.", messageId, chatId);
                await _messageSender.SendTextMessageAsync(chatId, text, parseMode, replyMarkup, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error editing message (MessageId: {MessageId}, ChatID: {ChatId}). Sending a new message instead.", messageId, chatId);
                await _messageSender.SendTextMessageAsync(chatId, text, parseMode, replyMarkup, cancellationToken);
            }
        }
        #endregion
    }
}