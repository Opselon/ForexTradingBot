// File: TelegramPanel/Application/CommandHandlers/MenuCallbackQueryHandler.cs
#region Usings
using Application.Interfaces;
using AutoMapper;
using Microsoft.Extensions.Logging;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.CommandHandlers.Settings;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Application.States;
using TelegramPanel.Formatters;
using TelegramPanel.Infrastructure;
using TelegramPanel.Infrastructure.Helper;
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions;
#endregion

namespace TelegramPanel.Application.CommandHandlers.MainMenu
{
    public class MenuCallbackQueryHandler : ITelegramCommandHandler, ITelegramCallbackQueryHandler
    {
        #region Private Fields
        private readonly ILogger<MenuCallbackQueryHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly ITelegramBotClient _botClient;
        private readonly IUserService _userService;
        private readonly ISignalService _signalService;
        private readonly IMapper _mapper;
        private readonly IPaymentService _paymentService; //✅ تزریق سرویس پرداخت
        private readonly IUserConversationStateService _stateService; // Inject the state service
        public const string BackToMainMenuGeneral = "main_menu_back"; // ✅ این ثابت تعریف شد

        // Callback Data Prefix برای انتخاب پلن
        public const string SelectPlanPrefix = "select_plan_";

        // Callback Data Prefix برای انتخاب ارز و نهایی کردن پرداخت
        public const string PayWithCryptoPrefix = "pay_";

        // شناسه پلن‌ها
        private static readonly Guid PremiumMonthlyPlanId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        private static readonly Guid PremiumQuarterlyPlanId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        // private readonly MenuCommandHandler _menuCommandHandler; // ✅ برای فراخوانی مستقیم (روش جایگزین)
        #endregion

        // Callback Data constants برای دکمه‌های بازگشت
        public const string BackToMainMenuFromProfile = "main_menu_from_profile";
        public const string BackToMainMenuFromSubscribe = "main_menu_from_subscribe";
        public const string BackToMainMenuFromSettings = "main_menu_from_settings";
        // می‌توانید یک CallbackData عمومی برای بازگشت به منو در نظر بگیرید
        public const string GeneralBackToMainMenuCallback = "main_menu_back";
        // Callback data برای انتخاب پلن‌ها
        public const string SelectPlanPremiumMonthly = "select_plan_premium_1m";
        public const string SelectPlanPremiumQuarterly = "select_plan_premium_3m";

        // Callback data برای انتخاب ارز دیجیتال برای پرداخت
        public const string PayWithUsdtForPremiumMonthly = "pay_usdt_premium_1m";

        #region Constructor
        public MenuCallbackQueryHandler(
            ILogger<MenuCallbackQueryHandler> logger,
            ITelegramMessageSender messageSender,
            ITelegramBotClient botClient,
            IUserService userService,
            ISignalService signalService,
            IMapper mapper,
            IPaymentService paymentService
            // MenuCommandHandler menuCommandHandler // ✅ تزریق اگر می‌خواهید مستقیم فراخوانی کنید,
            , IUserConversationStateService stateService
            )
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
            _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
            _userService = userService ?? throw new ArgumentNullException(nameof(userService));
            _signalService = signalService ?? throw new ArgumentNullException(nameof(signalService));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            _paymentService = paymentService ?? throw new ArgumentNullException(nameof(paymentService));
            _stateService = stateService ?? throw new ArgumentNullException(nameof(stateService)); // Initialize the state service
            // _menuCommandHandler = menuCommandHandler; // ✅ مقداردهی
        }
        #endregion

        #region ITelegramCommandHandler Implementation
        public bool CanHandle(Update update)
        {
            if (update.Type != UpdateType.CallbackQuery || update.CallbackQuery?.Data == null)
            {
                return false;
            }

            string callbackData = update.CallbackQuery.Data;

            _logger.LogTrace("MenuCBQHandler.CanHandle: Checking callbackData '{CallbackData}' against known prefixes.", callbackData);

            bool canHandleIt =
                callbackData.Equals(MenuCommandHandler.SignalsCallbackData, StringComparison.Ordinal) || // "menu_view_signals"
                callbackData.Equals(MenuCommandHandler.ProfileCallbackData, StringComparison.Ordinal) || // "menu_my_profile"
                callbackData.Equals(MenuCommandHandler.SubscribeCallbackData, StringComparison.Ordinal) || // "menu_subscribe_plans"
                callbackData.Equals(MenuCommandHandler.SettingsCallbackData, StringComparison.Ordinal) || // "menu_user_settings"
                 callbackData.Equals(MenuCommandHandler.AnalysisCallbackData, StringComparison.Ordinal) || // <<< NEW: Handle analysis button
                callbackData.StartsWith("select_plan_", StringComparison.Ordinal) ||
                callbackData.StartsWith("pay_", StringComparison.Ordinal) ||
                callbackData.Equals(BackToMainMenuFromProfile, StringComparison.Ordinal) ||
                callbackData.Equals(BackToMainMenuFromSubscribe, StringComparison.Ordinal) ||
                callbackData.Equals(BackToMainMenuFromSettings, StringComparison.Ordinal) ||
                callbackData.Equals(GeneralBackToMainMenuCallback, StringComparison.Ordinal);

            _logger.LogTrace("MenuCBQHandler.CanHandle for '{CallbackData}': Result = {Result}", callbackData, canHandleIt);
            return canHandleIt;
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            // استخراج اطلاعات ضروری از CallbackQuery
            var callbackQuery = update.CallbackQuery;
            // بررسی null بودن callbackQuery و Message آن در ابتدای متد، قبل از استفاده
            if (callbackQuery?.Message?.Chat == null || callbackQuery.From == null || string.IsNullOrWhiteSpace(callbackQuery.Data))
            {
                _logger.LogWarning("MenuCallbackHandler: CallbackQuery, its Message, Chat, From user, or Data is null/empty in UpdateID {UpdateId}.", update.Id);
                if (callbackQuery != null)
                {
                    await AnswerCallbackQuerySilentAsync(callbackQuery.Id, cancellationToken, "Error processing request.");
                }

                return;
            }

            var chatId = callbackQuery.Message.Chat.Id;          // شناسه چتی که پیام در آن قرار دارد
            var userId = callbackQuery.From.Id;                 // شناسه کاربر تلگرامی که دکمه را فشار داده
            var messageId = callbackQuery.Message.MessageId;    // شناسه پیامی که دکمه‌ها روی آن قرار دارند (برای ویرایش پیام)
            var callbackData = callbackQuery.Data;              // داده مرتبط با دکمه فشرده شده

            using (_logger.BeginScope(new Dictionary<string, object> // ایجاد Log Scope برای ردیابی بهتر
            {
                ["TelegramUserId"] = userId,
                ["ChatId"] = chatId,
                ["CallbackData"] = callbackData,
                ["MessageId"] = messageId
            }))
            {
                _logger.LogInformation("Handling CallbackQuery.");

                //  پاسخ اولیه به CallbackQuery برای حذف حالت "loading" از روی دکمه.
                //  این کار باید سریع انجام شود. متن آن اختیاری است و به کاربر نمایش داده نمی‌شود مگر اینکه showAlert = true باشد.
                await AnswerCallbackQuerySilentAsync(callbackQuery.Id, cancellationToken, "Processing...");

                try
                {
                    // مسیریابی بر اساس callbackData
                    if (callbackData.StartsWith(PayWithCryptoPrefix)) //  پرداخت با کریپتو (مثلاً "pay_usdt_for_plan_GUID")
                    {
                        await HandleCryptoPaymentSelectionAsync(chatId, userId, messageId, callbackData, cancellationToken);
                    }
                    else if (callbackData.StartsWith(SelectPlanPrefix)) //  انتخاب پلن (مثلاً "select_plan_GUID")
                    {
                        await HandlePlanSelectionAsync(chatId, userId, messageId, callbackData, cancellationToken);
                    }
                    else // سایر Callback های ثابت
                    {
                        switch (callbackData)
                        {
                            case MenuCommandHandler.SignalsCallbackData:
                                await HandleViewSignalsAsync(chatId, userId, messageId, cancellationToken);
                                break;
                            case MenuCommandHandler.ProfileCallbackData:
                                await HandleMyProfileAsync(chatId, userId, messageId, cancellationToken);
                                break;
                            case MenuCommandHandler.SubscribeCallbackData: // دکمه اصلی "Subscribe" که لیست پلن‌ها را نشان می‌دهد
                                await ShowSubscriptionPlansAsync(chatId, messageId, cancellationToken);
                                break;
                            case MenuCommandHandler.SettingsCallbackData:
                                await HandleSettingsAsync(chatId, userId, messageId, cancellationToken);
                                break;
                            case MenuCommandHandler.AnalysisCallbackData:
                                await ShowAnalysisMenuAsync(chatId, messageId, cancellationToken);
                                break;
                            case BackToMainMenuGeneral:
                                _logger.LogInformation("User requested to go back to main menu.");
                                await ShowMainMenuAndClearStateAsync(chatId, userId, messageId, cancellationToken);
                                break;
                            default:
                                _logger.LogWarning("Unhandled CallbackQuery data: {CallbackData}", callbackData);
                                await _messageSender.SendTextMessageAsync(chatId, "Sorry, this option is not recognized or is under development.", cancellationToken: cancellationToken);
                                break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // ثبت خطای جامع
                    _logger.LogError(ex, "An error occurred while handling callback query data '{CallbackData}'.", callbackData);
                    // ارسال پیام خطای عمومی به کاربر
                    await _messageSender.SendTextMessageAsync(chatId, "An unexpected error occurred while processing your request. Please try again or contact support.", cancellationToken: cancellationToken);
                }
            }
        }

        #endregion

        // VVVVVV NEW METHOD VVVVVV
        /// <summary>
        /// Displays the news analysis sub-menu.
        /// </summary>
          // VVVVVV MODIFIED METHOD VVVVVV
        /// <summary>
        /// Displays the news analysis sub-menu.
        /// </summary>
        private async Task ShowAnalysisMenuAsync(long chatId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Showing news analysis menu to ChatID {ChatId}", chatId);

            var text = TelegramMessageFormatter.Bold("🔍 News Analysis Tools") + "\n\n" +
                       "Select a tool to analyze news content from our indexed sources:";

            var keyboard = MarkupBuilder.CreateInlineKeyboard(
                 new[]
                {
                    // VVVVVV NEW BUTTON VVVVVV
                    InlineKeyboardButton.WithCallbackData("📊 Market Sentiment", "analysis_sentiment")
                },
                new[]
                { 
                    // New "Central Bank Watch" button added
                    InlineKeyboardButton.WithCallbackData("🏛️ Central Bank Watch", "analysis_cb_watch")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🔎 Search by Keywords", "analysis_search_keywords")
                },
                // new[] 
                // { 
                //    InlineKeyboardButton.WithCallbackData("📚 Search by Source", "analysis_search_source") // Future feature
                // },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("⬅️ Back to Main Menu", BackToMainMenuGeneral)
                }
            );

            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, text, keyboard, ParseMode.Markdown, cancellationToken);
        }

        private async Task HandlePlanSelectionAsync(long chatId, long telegramUserId, int messageIdToEdit, string callbackData, CancellationToken cancellationToken)
        {
            _logger.LogInformation("UserID {TelegramUserId} selected a plan. CallbackData: {CallbackData}", telegramUserId, callbackData);
            string planIdString = callbackData.Substring(SelectPlanPrefix.Length);
            if (!Guid.TryParse(planIdString, out Guid selectedPlanId))
            {
                _logger.LogWarning("Invalid PlanID format in callback data: {CallbackData}", callbackData);
                // EditMessageOrSendNewAsync باید ParseMode را از DefaultParseMode بگیرد یا به آن پاس داده شود
                await EditMessageOrSendNewAsync(chatId, messageIdToEdit, "Invalid plan selection. Please try again.", null, ParseMode.Markdown, cancellationToken);
                return;
            }

            string planNameForDisplay = selectedPlanId == PremiumMonthlyPlanId ? "Premium Monthly" :
                                        selectedPlanId == PremiumQuarterlyPlanId ? "Premium Quarterly" :
                                        "Selected Plan";
            _logger.LogInformation("UserID {TelegramUserId} selected PlanID: {PlanId} ({PlanName})", telegramUserId, selectedPlanId, planNameForDisplay);

            // استفاده از DefaultParseMode برای TelegramMessageFormatter
            var paymentOptionsText = $"You have selected: {TelegramMessageFormatter.Bold(planNameForDisplay)}.\n\n" + // اطمینان از escapePlainText صحیح
                                     "Please choose your preferred cryptocurrency for payment:";

            // ساخت paymentKeyboard با MarkupBuilder
            var paymentKeyboard = MarkupBuilder.CreateInlineKeyboard(
     new[] { InlineKeyboardButton.WithCallbackData("💳 Pay with USDT", $"{PayWithCryptoPrefix}usdt_for_plan_{selectedPlanId}") },
     new[] { InlineKeyboardButton.WithCallbackData("💳 Pay with TON", $"{PayWithCryptoPrefix}ton_for_plan_{selectedPlanId}") },
     new[] { InlineKeyboardButton.WithCallbackData("💳 Pay with BTC", $"{PayWithCryptoPrefix}btc_for_plan_{selectedPlanId}") },
     new[] { InlineKeyboardButton.WithCallbackData("⬅️ Change Plan", MenuCommandHandler.SubscribeCallbackData) }
 );

            // پاس دادن DefaultParseMode به EditMessageOrSendNewAsync
            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, paymentOptionsText, paymentKeyboard, ParseMode.Markdown, cancellationToken);
        }



        private async Task HandleCryptoPaymentSelectionAsync(long chatId, long telegramUserId, int messageIdToEdit, string callbackData, CancellationToken cancellationToken)
        {
            _logger.LogInformation("UserID {TelegramUserId} selected crypto payment option. CallbackData: {CallbackData}", telegramUserId, callbackData);
            var parts = callbackData.Substring(PayWithCryptoPrefix.Length).Split(new[] { "_for_plan_" }, StringSplitOptions.None);
            if (parts.Length != 2)
            {
                _logger.LogWarning("Invalid payment callback data format: {CallbackData}", callbackData);
                await EditMessageOrSendNewAsync(chatId, messageIdToEdit, "Invalid payment option. Please try again.", null, ParseMode.MarkdownV2, cancellationToken);
                return;
            }

            string selectedCryptoAsset = parts[0].ToUpper();
            if (!Guid.TryParse(parts[1], out Guid selectedPlanId))
            {
                _logger.LogWarning("Invalid PlanID in payment callback data: {CallbackData}", callbackData);
                await EditMessageOrSendNewAsync(chatId, messageIdToEdit, "Invalid plan ID in payment option. Please try again.", null, ParseMode.MarkdownV2, cancellationToken);
                return;
            }

            _logger.LogInformation("UserID {TelegramUserId} attempting to pay for PlanID {PlanId} with Asset {Asset}", telegramUserId, selectedPlanId, selectedCryptoAsset);
            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, $"⏳ Please wait, generating payment invoice for {selectedCryptoAsset}...", null, ParseMode.MarkdownV2, cancellationToken);

            var userDto = await _userService.GetUserByTelegramIdAsync(telegramUserId.ToString(), cancellationToken);
            if (userDto == null)
            {
                _logger.LogError("CRITICAL: User with TelegramID {TelegramUserId} not found when creating payment invoice.", telegramUserId);
                await _messageSender.SendTextMessageAsync(chatId, "Error: Your user profile could not be found. Please use /start again.", cancellationToken: cancellationToken);
                return;
            }

            var invoiceResult = await _paymentService.CreateCryptoPaymentInvoiceAsync(userDto.Id, selectedPlanId, selectedCryptoAsset, cancellationToken);

            if (invoiceResult.Succeeded && invoiceResult.Data != null)
            {
                var invoice = invoiceResult.Data;
                _logger.LogInformation("CryptoPay invoice created for UserID {UserId}. InvoiceID: {CryptoInvoiceId}, BotPayUrl: {PayUrl}", userDto.Id, invoice.InvoiceId, invoice.BotInvoiceUrl);
                var paymentMessage = $"✅ Your payment invoice for {TelegramMessageFormatter.Bold(selectedCryptoAsset, escapePlainText: false)} has been created!\n\n" +
                                     $"Please use the button below or copy the link to complete your payment:\n" +
                                     $"{TelegramMessageFormatter.Link("➡️ Click here to Pay ⬅️", invoice.BotInvoiceUrl!)}\n\n" +
                                     $"Invoice ID: {TelegramMessageFormatter.Code(invoice.InvoiceId.ToString())}\n" +
                                     $"Status: {TelegramMessageFormatter.Italic(invoice.Status ?? "Unknown")}\n\n" +
                                     "This link may expire. Please complete your payment promptly.";
                var paymentLinkKeyboard = MarkupBuilder.CreateInlineKeyboard(new[] { InlineKeyboardButton.WithUrl($"🚀 Pay with {selectedCryptoAsset} Now", invoice.BotInvoiceUrl!) },
                                                                             new[] { InlineKeyboardButton.WithCallbackData("⬅️ Back to Main Menu", BackToMainMenuGeneral) });
                _ = await _botClient.SendMessage(chatId, paymentMessage, ParseMode.Markdown, replyMarkup: paymentLinkKeyboard, cancellationToken: cancellationToken);
                //  می‌توانید پیام "در حال پردازش" را حذف کنید
                // await _botClient.DeleteMessageAsync(chatId, messageIdToEdit, cancellationToken);
            }
            else
            {
                _logger.LogError("Failed to create CryptoPay invoice for UserID {UserId}. Errors: {Errors}", userDto.Id, string.Join("; ", invoiceResult.Errors));
                var failureMessage = $"⚠️ Sorry, we couldn't create your payment invoice for {TelegramMessageFormatter.Bold(selectedCryptoAsset, escapePlainText: false)}.\n" +
                                     $"Details: {string.Join("; ", invoiceResult.Errors)}\n\n" +
                                     "Please try a different payment method or contact support.";
                _ = await _botClient.SendMessage(chatId, failureMessage, ParseMode.Markdown, cancellationToken: cancellationToken);
                // بازگشت به منوی انتخاب پلن پس از خطا در ایجاد فاکتور
                await ShowSubscriptionPlansAsync(chatId, messageIdToEdit, cancellationToken);
            }
        }



        #region Private Handler Methods for Callbacks


        // --- متدهای مربوط به نمایش اطلاعات (بدون تغییر عمده نسبت به قبل) ---


        // --- متدهای جدید برای فرآیند اشتراک و پرداخت ---

        /// <summary>
        /// لیست پلن‌های اشتراک را به کاربر نمایش می‌دهد.
        /// </summary>
        private async Task ShowSubscriptionPlansAsync(long chatId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Showing subscription plans to ChatID {ChatId}.", chatId);

            //  اطلاعات پلن‌ها باید از یک منبع معتبر (سرویس، دیتابیس، کانفیگ) خوانده شود.
            //  فعلاً متن و قیمت‌ها به صورت ثابت تعریف شده‌اند. 
            var plansText = TelegramMessageFormatter.Bold("💎 Available Subscription Plans:", escapePlainText: false) + "\n\n" +
                            $"1. {TelegramMessageFormatter.Bold("Premium Monthly")} - Access to all signals and features for 30 days. " +
                            $"(Price: ~$10 USD)\n\n" +
                            $"2. {TelegramMessageFormatter.Bold("Premium Quarterly")} - Same as monthly, but for 90 days with a discount! " +
                            $"(Price: ~$25 USD)\n\n" +
                            "Select a plan to proceed with payment options:";

            var plansKeyboard = MarkupBuilder.CreateInlineKeyboard(
             new[] { InlineKeyboardButton.WithCallbackData("🌟 Premium Monthly", $"{SelectPlanPrefix}{PremiumMonthlyPlanId}") },
             new[] { InlineKeyboardButton.WithCallbackData("✨ Premium Quarterly", $"{SelectPlanPrefix}{PremiumQuarterlyPlanId}") },
             new[] { InlineKeyboardButton.WithCallbackData("⬅️ Back to Main Menu", BackToMainMenuGeneral) }
         );

            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, plansText, plansKeyboard, ParseMode.Markdown, cancellationToken);
        }


        // ... (متدهای HandleViewSignalsAsync, HandleMyProfileAsync, HandleSubscribeAsync, HandleSettingsAsync بدون تغییر عمده) ...
        // فقط متن UI را بهبود می‌دهیم و از دکمه بازگشت عمومی استفاده می‌کنیم

        private async Task HandleViewSignalsAsync(long chatId, long telegramUserId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            _logger.LogInformation("User {TelegramUserId} requested to view signals.", telegramUserId);
            var signals = await _signalService.GetRecentSignalsAsync(3, includeCategory: true, cancellationToken: cancellationToken); //  تعداد سیگنال‌ها کمتر برای نمایش بهتر
            var sb = new StringBuilder();

            if (signals.Any())
            {
                _ = sb.AppendLine(TelegramMessageFormatter.Bold("📊 Recent Trading Signals:"));
                _ = sb.AppendLine(); // Add a blank line for better readability
                foreach (var signalDto in signals)
                {
                    var formattedSignal = SignalFormatter.FormatSignal(signalDto, ParseMode.Markdown);
                    _ = sb.AppendLine(formattedSignal);
                    _ = sb.AppendLine("─".PadRight(20, '─')); // Separator line
                }
            }
            else
            {
                _ = sb.AppendLine("No active signals available at the moment. Please check back later!");
            }

            var backKeyboard = MarkupBuilder.CreateInlineKeyboard(
        InlineKeyboardButton.WithCallbackData("⬅️ Back to Main Menu", BackToMainMenuGeneral)
        );

            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, sb.ToString(), backKeyboard, ParseMode.Markdown, cancellationToken);
        }

        /// <summary>
        /// Handles the "Back to Main Menu" callback data.
        /// Shows the main menu and clears the user's conversation state.
        /// </summary>
        private async Task ShowMainMenuAndClearStateAsync(long chatId, long userId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            _logger.LogInformation("User requested to go back to main menu. Clearing state for UserID {UserId}.", userId);
            await ShowMainMenuAsync(chatId, messageIdToEdit, cancellationToken);
            await _stateService.ClearAsync(userId, cancellationToken); // Clear the user's state
        }
        // مثال برای ShowSubscriptionPlansAsync




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
            _ = sb.AppendLine(TelegramMessageFormatter.Bold("🔐 Your Profile:"));
            _ = sb.AppendLine($"👤 Username: {TelegramMessageFormatter.Code(userDto.Username)}");
            _ = sb.AppendLine($"📧 Email: {TelegramMessageFormatter.Code(userDto.Email)}");
            _ = sb.AppendLine($"🆔 Telegram ID: {TelegramMessageFormatter.Code(userDto.TelegramId)}");
            _ = sb.AppendLine($"⭐ Access Level: {TelegramMessageFormatter.Bold(GetLevelTitle((int)userDto.Level))}");
            _ = sb.AppendLine($"💰 Token Balance: {TelegramMessageFormatter.Code(userDto.TokenBalance.ToString("N2"))}");

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
                _ = sb.AppendLine($"Token Balance: {TelegramMessageFormatter.Code(userDto.TokenWallet.Balance.ToString("N2"))} Tokens");
            }
            if (userDto.ActiveSubscription != null)
            {
                _ = sb.AppendLine($"Active Subscription: Plan XXX (Expires: {userDto.ActiveSubscription.EndDate:yyyy-MM-dd})"); // نام پلن را اضافه کنید
            }
            else
            {
                _ = sb.AppendLine("Subscription: No active subscription.");
            }

            var backKeyboard = MarkupBuilder.CreateInlineKeyboard(
     InlineKeyboardButton.WithCallbackData("⬅️ Back to Main Menu", BackToMainMenuGeneral));
            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, sb.ToString(), backKeyboard, ParseMode.MarkdownV2, cancellationToken);
        }



        /// <summary>
        /// Handles the "Settings" button callback from the main menu.
        /// It should display the main settings menu to the user.
        /// This method essentially replicates what SettingsCommandHandler does for the /settings command.
        /// </summary>
        private async Task HandleSettingsAsync(long chatId, long telegramUserId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            _logger.LogInformation("UserID {TelegramUserId} in ChatID {ChatId} selected 'Settings' from main menu (via callback). Displaying settings menu.", telegramUserId, chatId);

            //  متن و دکمه‌های این بخش باید دقیقاً مشابه چیزی باشد که
            //  SettingsCommandHandler برای دستور /settings نمایش می‌دهد.
            //  برای جلوگیری از تکرار کد، می‌توانید این متن و کیبورد را در یک متد کمکی
            //  در یک کلاس جداگانه (مثلاً یک SettingsMenuBuilder) یا حتی در خود SettingsCommandHandler
            //  (به صورت یک متد استاتیک یا یک متد در یک سرویس که هر دو Handler به آن دسترسی دارند) تعریف کنید.

            //  فعلاً، منطق را مستقیماً اینجا بازنویسی می‌کنیم:
            var settingsMenuText = TelegramMessageFormatter.Bold("⚙️ User Settings", escapePlainText: false) + "\n\n" +
                                   "Please choose a category to configure:";

            // دکمه‌های منوی تنظیمات (اینها باید با ثابت‌های CallbackData در SettingsCommandHandler و SettingsCallbackQueryHandler مطابقت داشته باشند)
            var settingsKeyboard = MarkupBuilder.CreateInlineKeyboard(
     new[] { InlineKeyboardButton.WithCallbackData("📊 My Signal Preferences", SettingsCommandHandler.PrefsSignalCategoriesCallback) },
     new[] { InlineKeyboardButton.WithCallbackData("🔔 Notification Settings", SettingsCommandHandler.PrefsNotificationsCallback) },
     new[] { InlineKeyboardButton.WithCallbackData("⭐ My Subscription", SettingsCommandHandler.MySubscriptionInfoCallback) },
     new[] { InlineKeyboardButton.WithCallbackData("⬅️ Back to Main Menu", BackToMainMenuGeneral) });

            // ویرایش پیام قبلی (که دکمه‌های منوی اصلی را داشت) با منوی تنظیمات جدید
            await EditMessageOrSendNewAsync(
                chatId: chatId,
                messageId: messageIdToEdit,
                text: settingsMenuText,
                replyMarkup: settingsKeyboard,
                parseMode: ParseMode.MarkdownV2,
                cancellationToken: cancellationToken);
        }

        // ... (بقیه متدها مانند ShowMainMenuAsync, EditMessageOrSendNewAsync, AnswerCallbackQuerySilentAsync) ...


        #endregion


        /// <summary>
        /// منوی اصلی را دوباره به کاربر نمایش می‌دهد (با ویرایش پیام قبلی).
        /// </summary>
        private async Task ShowMainMenuAsync(long chatId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Showing main menu again for ChatID {ChatId}", chatId);
                // Use the static GetMainMenuMarkup method from MenuCommandHandler
                var (text, inlineKeyboard) = MenuCommandHandler.GetMainMenuMarkup();
                await EditMessageOrSendNewAsync(chatId, messageIdToEdit, text, inlineKeyboard, cancellationToken: cancellationToken);
            }
            catch (Exception sendEx)
            {
                // If even the fallback fails, log it critically.
                _logger.LogCritical(sendEx, "FALLBACK FAILED: Could not send new message to ChatID {ChatId} after an edit error.", chatId);
            }
        }


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
                _ = await _botClient.EditMessageText( // 
                    chatId: chatId,
                    messageId: messageId,
                    text: text,
                    parseMode: ParseMode.Markdown, // 
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