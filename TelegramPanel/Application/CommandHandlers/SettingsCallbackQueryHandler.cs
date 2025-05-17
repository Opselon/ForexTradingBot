// File: TelegramPanel/Application/CommandHandlers/SettingsCallbackQueryHandler.cs
#region Usings
using Application.Common.Interfaces; // برای IUserSignalPreferenceRepository, ISignalCategoryRepository
using Application.DTOs;              // برای UserDto, SubscriptionDto, SignalCategoryDto
using Application.Interfaces;        // برای IUserService, ISubscriptionService
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Application.Interface;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.Interfaces; // برای ITelegramCommandHandler, ITelegramStateMachine
using TelegramPanel.Application.States;   // برای IUserConversationStateService (اگر برای انتخاب دسته‌بندی‌ها استفاده شود)
using TelegramPanel.Infrastructure;       // برای ITelegramMessageSender
using TelegramPanel.Formatters;           // برای TelegramMessageFormatter
#endregion

namespace TelegramPanel.Application.CommandHandlers
{
    public class SettingsCallbackQueryHandler : ITelegramCommandHandler
    {
        #region Private Readonly Fields
        private readonly ILogger<SettingsCallbackQueryHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly ITelegramBotClient _botClient;
        private readonly IUserService _userService;
        private readonly ISubscriptionService _subscriptionService;
        private readonly IUserSignalPreferenceRepository _userPrefsRepository; // ✅ برای تنظیمات برگزیده سیگنال
        private readonly ISignalCategoryRepository _categoryRepository;     // ✅ برای دریافت لیست دسته‌بندی‌ها
        private readonly ITelegramStateMachine _stateMachine;             // ✅ برای مکالمات چند مرحله‌ای (انتخاب دسته‌بندی)
        private readonly IUserConversationStateService _userConversationStateService; // برای ذخیره داده‌های وضعیت
        #endregion

        #region Callback Data Constants
        // این ثابت‌ها باید با مقادیر در SettingsCommandHandler یکی باشند
        // SettingsCommandHandler.PrefsSignalCategoriesCallback
        // SettingsCommandHandler.PrefsNotificationsCallback
        // SettingsCommandHandler.MySubscriptionInfoCallback

        // Callback Data برای ذخیره تنظیمات برگزیده سیگنال
        public const string SaveSignalPreferencesCallback = "settings_save_prefs";
        // پیشوند برای انتخاب/عدم انتخاب یک دسته سیگنال
        public const string ToggleSignalCategoryPrefix = "toggle_cat_"; // مثال: "toggle_cat_GUID"
        #endregion

        #region Constructor
        public SettingsCallbackQueryHandler(
            ILogger<SettingsCallbackQueryHandler> logger,
            ITelegramMessageSender messageSender,
            ITelegramBotClient botClient,
            IUserService userService,
            ISubscriptionService subscriptionService,
            IUserSignalPreferenceRepository userPrefsRepository,
            ISignalCategoryRepository categoryRepository,
            ITelegramStateMachine stateMachine,
            IUserConversationStateService userConversationStateService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
            _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
            _userService = userService ?? throw new ArgumentNullException(nameof(userService));
            _subscriptionService = subscriptionService ?? throw new ArgumentNullException(nameof(subscriptionService));
            _userPrefsRepository = userPrefsRepository ?? throw new ArgumentNullException(nameof(userPrefsRepository));
            _categoryRepository = categoryRepository ?? throw new ArgumentNullException(nameof(categoryRepository));
            _stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
            _userConversationStateService = userConversationStateService ?? throw new ArgumentNullException(nameof(userConversationStateService));
        }
        #endregion

        #region ITelegramCommandHandler Implementation
        public bool CanHandle(Update update)
        {
            return update.Type == UpdateType.CallbackQuery &&
                   update.CallbackQuery?.Data != null &&
                   (update.CallbackQuery.Data.Equals(SettingsCommandHandler.PrefsSignalCategoriesCallback) ||
                    update.CallbackQuery.Data.Equals(SettingsCommandHandler.PrefsNotificationsCallback) ||
                    update.CallbackQuery.Data.Equals(SettingsCommandHandler.MySubscriptionInfoCallback) ||
                    update.CallbackQuery.Data.Equals(SettingsCommandHandler.SignalHistoryCallback) || // اگر اضافه کردید
                    update.CallbackQuery.Data.Equals(SettingsCommandHandler.PublicSignalsCallback) || // اگر اضافه کردید
                    update.CallbackQuery.Data.StartsWith(ToggleSignalCategoryPrefix) || // برای تیک زدن دسته‌ها
                    update.CallbackQuery.Data.Equals(SaveSignalPreferencesCallback) ||    // برای ذخیره تنظیمات
                    update.CallbackQuery.Data.Equals(MenuCallbackQueryHandler.BackToMainMenuGeneral)); // دکمه بازگشت عمومی
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            var callbackQuery = update.CallbackQuery;
            if (callbackQuery?.Message?.Chat == null || callbackQuery.From == null || string.IsNullOrWhiteSpace(callbackQuery.Data))
            {
                _logger.LogWarning("SettingsCallback: Essential CallbackQuery data is null/empty.");
                if (callbackQuery != null) await AnswerCallbackQuerySilentAsync(callbackQuery.Id, cancellationToken, "Error processing.");
                return;
            }

            var chatId = callbackQuery.Message.Chat.Id;
            var userId = callbackQuery.From.Id;
            var messageId = callbackQuery.Message.MessageId;
            var data = callbackQuery.Data;

            using (_logger.BeginScope(new Dictionary<string, object> { /* ... Log Scope ... */ }))
            {
                _logger.LogInformation("Handling Settings CallbackQuery. Data: {CallbackData}", data);
                await AnswerCallbackQuerySilentAsync(callbackQuery.Id, cancellationToken, "Processing...");

                try
                {
                    if (data.Equals(SettingsCommandHandler.PrefsSignalCategoriesCallback))
                    {
                        await ShowSignalCategoryPreferencesAsync(chatId, userId, messageId, cancellationToken);
                    }
                    else if (data.StartsWith(ToggleSignalCategoryPrefix))
                    {
                        await HandleToggleSignalCategoryAsync(chatId, userId, messageId, data, cancellationToken);
                    }
                    else if (data.Equals(SaveSignalPreferencesCallback))
                    {
                        await HandleSaveSignalPreferencesAsync(chatId, userId, messageId, cancellationToken);
                    }
                    else if (data.Equals(SettingsCommandHandler.PrefsNotificationsCallback))
                    {
                        await HandleNotificationSettingsAsync(chatId, userId, messageId, cancellationToken);
                    }
                    else if (data.Equals(SettingsCommandHandler.MySubscriptionInfoCallback))
                    {
                        await HandleMySubscriptionInfoAsync(chatId, userId, messageId, cancellationToken);
                    }
                    else if (data.Equals(MenuCallbackQueryHandler.BackToMainMenuGeneral))
                    {
                        //  فراخوانی متد برای نمایش منوی اصلی (باید در یک سرویس مشترک یا MenuCommandHandler باشد)
                        //  فعلاً یک پیام ساده ارسال می‌کنیم یا به StateMachine می‌گوییم وضعیت را پاک کند.
                        //  برای این کار، نیاز به یک روش برای نمایش مجدد منوی قبلی (منوی /settings) یا منوی اصلی (/menu) داریم.
                        //  بهتر است ShowMainMenuAsync را از MenuCallbackQueryHandler فراخوانی کنیم
                        //  یا یک Command جدید برای نمایش منوی اصلی ارسال کنیم.
                        //  فعلاً فرض می‌کنیم می‌خواهیم منوی /settings را دوباره نمایش دهیم
                        await ReshowSettingsMenuAsync(chatId, messageId, cancellationToken); //  متد جدید
                    }
                    else
                    {
                        _logger.LogWarning("Unhandled Settings CallbackQuery data: {CallbackData}", data);
                        await _messageSender.SendTextMessageAsync(chatId, "This setting option is not yet implemented.", cancellationToken: cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling settings callback query data '{CallbackData}'.", data);
                    await _messageSender.SendTextMessageAsync(chatId, "An error occurred. Please try again.", cancellationToken: cancellationToken);
                }
            }
        }
        #endregion

        #region Settings Logic Methods

        /// <summary>
        /// نمایش فرم انتخاب دسته‌بندی‌های سیگنال مورد علاقه کاربر.
        /// از UserConversationStateService برای نگهداری انتخاب‌های موقت کاربر استفاده می‌کند.
        /// </summary>
        private async Task ShowSignalCategoryPreferencesAsync(long chatId, long telegramUserId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            _logger.LogInformation("UserID {TelegramUserId} requesting to set signal category preferences.", telegramUserId);

            var userSystem = await _userService.GetUserByTelegramIdAsync(telegramUserId.ToString(), cancellationToken);
            if (userSystem == null)
            {
                await EditMessageOrSendNewAsync(chatId, messageIdToEdit, "Please /start the bot first to access settings.", null, cancellationToken: cancellationToken);
                return;
            }

            // دریافت تمام دسته‌بندی‌های موجود
            var allCategories = (await _categoryRepository.GetAllAsync(cancellationToken)).ToList();
            if (!allCategories.Any())
            {
                await EditMessageOrSendNewAsync(chatId, messageIdToEdit, "No signal categories are currently available to choose from.",
                    new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("⬅️ Back to Settings", "settings_menu_placeholder")), // باید به منوی تنظیمات برگردد
                    ParseMode.MarkdownV2, cancellationToken);
                return;
            }

            // دریافت تنظیمات برگزیده فعلی کاربر
            var currentUserPreferences = (await _userPrefsRepository.GetPreferencesByUserIdAsync(userSystem.Id, cancellationToken))
                                         .Select(p => p.CategoryId).ToHashSet();

            // ذخیره انتخاب‌های موقت در وضعیت مکالمه کاربر
            var conversationState = await _userConversationStateService.GetAsync(telegramUserId, cancellationToken) ?? new UserConversationState();
            conversationState.CurrentStateName = "AwaitingSignalCategorySelection"; // نام وضعیت برای StateMachine (اگر از آن استفاده می‌کنید)
            // کلید برای ذخیره انتخاب‌های موقت در StateData
            string tempSelectedCategoriesKey = $"temp_selected_categories_{telegramUserId}";
            // کپی کردن انتخاب‌های فعلی به انتخاب‌های موقت
            conversationState.StateData[tempSelectedCategoriesKey] = new HashSet<Guid>(currentUserPreferences);
            await _userConversationStateService.SetAsync(telegramUserId, conversationState, cancellationToken);

            var text = TelegramMessageFormatter.Bold("📊 My Signal Preferences", escapePlainText: false) + "\n\n" +
                       "Select the signal categories you are interested in. You will receive notifications for selected categories.\n" +
                       "Tap a category to toggle its selection (✅ Selected / ⬜ Not Selected).";

            var keyboardRows = new List<IEnumerable<InlineKeyboardButton>>();
            foreach (var category in allCategories)
            {
                bool isSelected = ((HashSet<Guid>)conversationState.StateData[tempSelectedCategoriesKey]).Contains(category.Id);
                string buttonText = $"{(isSelected ? "✅" : "⬜")} {category.Name}";
                keyboardRows.Add(new[] { InlineKeyboardButton.WithCallbackData(buttonText, $"{ToggleSignalCategoryPrefix}{category.Id}") });
            }

            keyboardRows.Add(new[] { InlineKeyboardButton.WithCallbackData("💾 Save Preferences", SaveSignalPreferencesCallback) });
            keyboardRows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Back to Settings Menu", "settings_menu_placeholder") }); //  باید به منوی /settings برگردد

            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, text, new InlineKeyboardMarkup(keyboardRows), ParseMode.MarkdownV2, cancellationToken);
        }

        /// <summary>
        /// انتخاب یا عدم انتخاب یک دسته سیگنال را در وضعیت موقت کاربر تغییر می‌دهد.
        /// </summary>
        private async Task HandleToggleSignalCategoryAsync(long chatId, long telegramUserId, int messageIdToEdit, string callbackData, CancellationToken cancellationToken)
        {
            string categoryIdString = callbackData.Substring(ToggleSignalCategoryPrefix.Length);
            if (!Guid.TryParse(categoryIdString, out Guid categoryId))
            {
                _logger.LogWarning("Invalid CategoryID in toggle callback: {CallbackData}", callbackData);
                await AnswerCallbackQuerySilentAsync(callbackData.Split(':')[0], cancellationToken, "Error: Invalid category.", showAlert: true); // callbackQuery.Id
                return;
            }

            var conversationState = await _userConversationStateService.GetAsync(telegramUserId, cancellationToken);
            string tempSelectedCategoriesKey = $"temp_selected_categories_{telegramUserId}";

            if (conversationState == null || !conversationState.StateData.TryGetValue(tempSelectedCategoriesKey, out var selectedCategoriesObj) || !(selectedCategoriesObj is HashSet<Guid> tempSelectedCategories))
            {
                _logger.LogError("User conversation state or temporary selected categories not found for UserID {TelegramUserId} during toggle.", telegramUserId);
                await EditMessageOrSendNewAsync(chatId, messageIdToEdit, "An error occurred with your session. Please try selecting preferences again.",
                    new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("⬅️ Back to Settings", "settings_menu_placeholder")), cancellationToken: cancellationToken);
                return;
            }

            if (tempSelectedCategories.Contains(categoryId))
            {
                tempSelectedCategories.Remove(categoryId);
            }
            else
            {
                tempSelectedCategories.Add(categoryId);
            }
            conversationState.StateData[tempSelectedCategoriesKey] = tempSelectedCategories; // بروزرسانی در StateData
            await _userConversationStateService.SetAsync(telegramUserId, conversationState, cancellationToken);

            // بازسازی و ویرایش پیام با دکمه‌های به‌روز شده
            await ShowSignalCategoryPreferencesAsync(chatId, telegramUserId, messageIdToEdit, cancellationToken); //  نمایش مجدد فرم با انتخاب‌های جدید
            await AnswerCallbackQuerySilentAsync(callbackData.Split(':')[0], cancellationToken); // callbackQuery.Id
        }

        /// <summary>
        /// تنظیمات برگزیده سیگنال انتخاب شده توسط کاربر را در پایگاه داده ذخیره می‌کند.
        /// </summary>
        private async Task HandleSaveSignalPreferencesAsync(long chatId, long telegramUserId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            _logger.LogInformation("UserID {TelegramUserId} attempting to save signal preferences.", telegramUserId);

            var userSystem = await _userService.GetUserByTelegramIdAsync(telegramUserId.ToString(), cancellationToken);
            if (userSystem == null) { /* ... handle error ... */ return; }

            var conversationState = await _userConversationStateService.GetAsync(telegramUserId, cancellationToken);
            string tempSelectedCategoriesKey = $"temp_selected_categories_{telegramUserId}";

            if (conversationState == null || !conversationState.StateData.TryGetValue(tempSelectedCategoriesKey, out var selectedCategoriesObj) || !(selectedCategoriesObj is HashSet<Guid> finalSelectedCategoryIds))
            {
                _logger.LogError("User conversation state or temporary selected categories not found for UserID {TelegramUserId} during save.", telegramUserId);
                await EditMessageOrSendNewAsync(chatId, messageIdToEdit, "An error occurred saving preferences. Please try again.",
                     new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("⬅️ Back to Settings", "settings_menu_placeholder")), cancellationToken: cancellationToken);
                return;
            }

            // ذخیره تنظیمات در پایگاه داده با استفاده از Repository
            await _userPrefsRepository.SetUserPreferencesAsync(userSystem.Id, finalSelectedCategoryIds, cancellationToken);
            // SaveChangesAsync باید در Unit of Work (مثلاً IAppDbContext در یک سرویس Application یا Command Handler) فراخوانی شود.
            // اگر UserPrefsRepository مستقیماً SaveChanges را فراخوانی نمی‌کند، باید اینجا انجام شود.
            // فرض می‌کنیم SetUserPreferencesAsync خودش تغییرات را commit می‌کند یا بخشی از یک UoW بزرگتر است.
            // await _context.SaveChangesAsync(cancellationToken); //  اگر لازم است

            // پاک کردن وضعیت موقت
            conversationState.StateData.Remove(tempSelectedCategoriesKey);
            // conversationState.CurrentStateName = null; // بازگشت به وضعیت Idle
            await _userConversationStateService.SetAsync(telegramUserId, conversationState, cancellationToken);
            // یا await _stateMachine.ClearStateAsync(telegramUserId, cancellationToken);

            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, "✅ Your signal preferences have been saved successfully!",
                new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("⬅️ Back to Settings Menu", "settings_menu_placeholder")), // یا به منوی اصلی
                ParseMode.MarkdownV2, cancellationToken);
        }


        private async Task HandleNotificationSettingsAsync(long chatId, long telegramUserId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            _logger.LogInformation("User {TelegramUserId} requested notification settings.", telegramUserId);
            var text = TelegramMessageFormatter.Bold("🔔 Notification Settings", escapePlainText: false) + "\n\n" +
                       "This feature is under development. Soon you'll be able to customize when and how you receive notifications.";
            var backKeyboard = new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("⬅️ Back to Settings Menu", "settings_menu_placeholder"));
            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, text, backKeyboard, ParseMode.MarkdownV2, cancellationToken);
        }

        private async Task HandleMySubscriptionInfoAsync(long chatId, long telegramUserId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            _logger.LogInformation("User {TelegramUserId} requested subscription info.", telegramUserId);
            var userDto = await _userService.GetUserByTelegramIdAsync(telegramUserId.ToString(), cancellationToken);
            if (userDto == null) { /* ... handle error ... */ return; }

            var sb = new StringBuilder();
            sb.AppendLine(TelegramMessageFormatter.Bold("⭐ My Subscription Status", escapePlainText: false));

            if (userDto.ActiveSubscription != null)
            {
                // string planName = userDto.ActiveSubscription.PlanName ?? "Your Active Plan"; // اگر PlanName دارید
                string planName = "Premium"; // Placeholder
                sb.AppendLine($"You are currently on the {TelegramMessageFormatter.Bold(planName, escapePlainText: false)}.");
                sb.AppendLine($"Your subscription is active until: {userDto.ActiveSubscription.EndDate:yyyy-MM-dd HH:mm} UTC.");
                sb.AppendLine("\nThank you for being a valued member!");
            }
            else
            {
                sb.AppendLine("You do not have an active subscription at the moment.");
                sb.AppendLine("Consider subscribing to access premium signals and features!");
            }

            var keyboardButtons = new List<InlineKeyboardButton>();
            if (userDto.ActiveSubscription == null)
            {
                keyboardButtons.Add(InlineKeyboardButton.WithCallbackData("💎 View Subscription Plans", MenuCommandHandler.SubscribeCallbackData));
            }
            keyboardButtons.Add(InlineKeyboardButton.WithCallbackData("⬅️ Back to Settings Menu", "settings_menu_placeholder"));

            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, sb.ToString(), new InlineKeyboardMarkup(keyboardButtons), ParseMode.MarkdownV2, cancellationToken);
        }

        /// <summary>
        /// منوی تنظیمات را دوباره به کاربر نمایش می‌دهد.
        /// </summary>
        private async Task ReshowSettingsMenuAsync(long chatId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            // این متد مشابه HandleAsync در SettingsCommandHandler عمل می‌کند
            _logger.LogInformation("Reshowing settings menu for ChatID {ChatId}", chatId);
            var settingsMenuText = TelegramMessageFormatter.Bold("⚙️ User Settings", escapePlainText: false) + "\n\n" +
                                   "Please choose a category to configure:";
            var settingsKeyboard = new InlineKeyboardMarkup(new[]
            {
                new [] { InlineKeyboardButton.WithCallbackData("📊 My Signal Preferences", SettingsCommandHandler.PrefsSignalCategoriesCallback) },
                new [] { InlineKeyboardButton.WithCallbackData("🔔 Notification Settings", SettingsCommandHandler.PrefsNotificationsCallback) },
                new [] { InlineKeyboardButton.WithCallbackData("⭐ My Subscription", SettingsCommandHandler.MySubscriptionInfoCallback) },
                new [] { InlineKeyboardButton.WithCallbackData("⬅️ Back to Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral) }
            });
            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, settingsMenuText, settingsKeyboard, ParseMode.MarkdownV2, cancellationToken);
        }

        #endregion

        #region Helper Methods (AnswerCallbackQuerySilentAsync, EditMessageOrSendNewAsync - بدون تغییر)
        private async Task AnswerCallbackQuerySilentAsync(string callbackQueryId, CancellationToken cancellationToken, string? text = null, bool showAlert = false)
        {
            try
            {
                await _botClient.AnswerCallbackQuery(callbackQueryId, text, showAlert, cancellationToken: cancellationToken);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to answer cbq {Id}", callbackQueryId); }
        }

        private async Task EditMessageOrSendNewAsync(long chatId, int messageId, string text, InlineKeyboardMarkup? replyMarkup, ParseMode? parseMode = null, CancellationToken cancellationToken = default)
        {
            try
            {
                await _botClient.EditMessageText(chatId, messageId, text, parseMode : ParseMode.Markdown, replyMarkup: replyMarkup, cancellationToken: cancellationToken);
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) || ex.ErrorCode == 400)
            {
                _logger.LogWarning(ex, "Could not edit message ({MsgId},{ChatId}), sending new. Error: {ApiErr}", messageId, chatId, ex.Message);
                await _messageSender.SendTextMessageAsync(chatId, text, parseMode, replyMarkup, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error editing message ({MsgId},{ChatId}), sending new.", messageId, chatId);
                await _messageSender.SendTextMessageAsync(chatId, text, parseMode, replyMarkup, cancellationToken);
            }
        }
        #endregion
    }
}