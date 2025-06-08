// File: TelegramPanel/Application/CommandHandlers/SettingsCallbackQueryHandler.cs

#region Usings
// Standard .NET & NuGet
// Project specific: Application Layer (Core - پروژه اصلی شما)
using Application.Common.Interfaces; // برای IUserRepository, IUserSignalPreferenceRepository, ISignalCategoryRepository, IAppDbContext, INotificationService
using Application.DTOs;
using Application.Interface; // برای UserDto, SubscriptionDto, SignalCategoryDto از پروژه اصلی Application
using Application.Interfaces;        // برای IUserService, ISubscriptionService از پروژه اصلی Application
using Microsoft.Extensions.Logging; // برای ILogger
using System.Text;                  // برای StringBuilder
// Telegram.Bot
using Telegram.Bot;                 // برای ITelegramBotClient
using Telegram.Bot.Exceptions;      // برای ApiRequestException
using Telegram.Bot.Types;           // برای Update, CallbackQuery, Message, Chat, User (از نوع تلگرام)
using Telegram.Bot.Types.Enums;     // برای UpdateType, ParseMode
using Telegram.Bot.Types.ReplyMarkups; // برای InlineKeyboardMarkup, InlineKeyboardButton, IReplyMarkup
using TelegramPanel.Application.CommandHandlers.MainMenu;


// Project specific: TelegramPanel Layer
using TelegramPanel.Application.Interfaces; // برای ITelegramCommandHandler, ITelegramStateMachine
using TelegramPanel.Application.States;   // برای IUserConversationStateService, UserConversationState
using TelegramPanel.Formatters;           // برای TelegramMessageFormatter
using TelegramPanel.Infrastructure;
using TelegramPanel.Infrastructure.Helpers;       // برای ITelegramMessageSender
#endregion

namespace TelegramPanel.Application.CommandHandlers.Settings
{
    /// <summary>
    /// Handles callback queries originating from the "/settings" menu and its various sub-options.
    /// This handler allows users to:
    /// - Manage their signal category preferences (which RSS feeds/signal types they want to follow).
    /// - Configure notification settings (general, VIP signals, RSS news).
    /// - View their current subscription status and access upgrade/renewal options.
    /// - (Future) Access language settings and privacy options.
    /// It interacts with user data, conversation state, and sends messages back to the user.
    /// </summary>
    public class SettingsCallbackQueryHandler : ITelegramCallbackQueryHandler
    {
        #region Private Readonly Fields
        private readonly ILogger<SettingsCallbackQueryHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;         // Service to send messages via Telegram
        private readonly ITelegramBotClient _botClient;                 // Raw Telegram Bot API client for specific actions like AnswerCallbackQuery
        private readonly IUserService _userService;                     // Core application service for user-related operations
        private readonly ISubscriptionService _subscriptionService;     // Core application service for subscription management
        private readonly IUserSignalPreferenceRepository _userPrefsRepository; // Repository for user's signal category choices
        private readonly ISignalCategoryRepository _categoryRepository;     // Repository for available signal categories
        private readonly ITelegramStateMachine _stateMachine;             // For managing complex, multi-step conversations (future use)
        private readonly IUserConversationStateService _userConversationStateService; // For storing temporary user choices during a conversation
        private readonly IAppDbContext _appDbContext;                   // For committing changes after User entity modifications
        private readonly IUserRepository _userRepository;               // For fetching and updating the User entity directly
        #endregion

        #region Public Callback Data Constants
        // These constants define the data strings for callback buttons.
        // They should align with constants in SettingsCommandHandler for consistency.

        // Signal Category Preferences
        public const string SaveSignalPreferencesCallback = "settings_save_signal_prefs";
        public const string ToggleSignalCategoryPrefix = "settings_toggle_cat_"; // Suffix: CategoryId (Guid)
        public const string SelectAllSignalCategoriesCallback = "settings_select_all_cats";
        public const string DeselectAllSignalCategoriesCallback = "settings_deselect_all_cats";

        // Notification Settings
        public const string ToggleNotificationPrefix = "settings_notify_toggle_"; // Suffix: NotificationType (string)
        public const string NotificationTypeGeneral = "general";
        public const string NotificationTypeVipSignal = "vip_signal";
        public const string NotificationTypeRssNews = "rss_news";

        // Language Settings (for future expansion)
        public const string LanguageSettingsCallback = "settings_language";
        public const string SelectLanguagePrefix = "settings_lang_"; // Suffix: LanguageCode (e.g., "en", "fa")

        // Privacy Settings (for future expansion)
        public const string PrivacySettingsCallback = "settings_privacy";
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
            IUserConversationStateService userConversationStateService,
            IAppDbContext appDbContext,
            IUserRepository userRepository)
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
            _appDbContext = appDbContext ?? throw new ArgumentNullException(nameof(appDbContext));
            _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
        }
        #endregion

        #region ITelegramCommandHandler Implementation
        /// <summary>
        /// Determines if this handler can process the given Telegram update based on its callback data.
        /// </summary>
        public bool CanHandle(Update update)
        {
            // This handler only processes CallbackQuery updates.
            if (update.Type != UpdateType.CallbackQuery || update.CallbackQuery?.Data == null)
                return false;

            string data = update.CallbackQuery.Data;

            // Check if the callback data matches any of the patterns this handler is responsible for.
            return
                data.Equals(SettingsCommandHandler.PrefsSignalCategoriesCallback, StringComparison.Ordinal) || // From main settings menu
                data.Equals(SettingsCommandHandler.PrefsNotificationsCallback, StringComparison.Ordinal) ||  // From main settings menu
                data.Equals(SettingsCommandHandler.MySubscriptionInfoCallback, StringComparison.Ordinal) ||  // From main settings menu
                data.Equals(SettingsCommandHandler.ShowSettingsMenuCallback, StringComparison.Ordinal) ||     // Action to re-show settings menu
                data.Equals(LanguageSettingsCallback, StringComparison.Ordinal) ||                           // To show language settings
                data.Equals(PrivacySettingsCallback, StringComparison.Ordinal) ||                            // To show privacy settings
                data.Equals(MenuCallbackQueryHandler.BackToMainMenuGeneral, StringComparison.Ordinal) ||    // General "Back to Main Menu"
                data.StartsWith(ToggleSignalCategoryPrefix, StringComparison.Ordinal) ||                    // Toggling a signal category
                data.Equals(SaveSignalPreferencesCallback, StringComparison.Ordinal) ||                       // Saving signal preferences
                data.Equals(SelectAllSignalCategoriesCallback, StringComparison.Ordinal) ||                   // Selecting all categories
                data.Equals(DeselectAllSignalCategoriesCallback, StringComparison.Ordinal) ||                 // Deselecting all categories
                data.StartsWith(ToggleNotificationPrefix, StringComparison.Ordinal) ||                      // Toggling a notification type
                data.StartsWith(SelectLanguagePrefix, StringComparison.Ordinal);                            // Selecting a language
        }

        /// <summary>
        /// Asynchronously handles the incoming CallbackQuery by dispatching to the appropriate method.
        /// </summary>
        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            if (update.CallbackQuery == null || update.CallbackQuery.Message == null)
            {
                _logger.LogWarning("Received invalid callback query or message is null");
                return;
            }

            var callbackQuery = update.CallbackQuery;
            var message = callbackQuery.Message;
            var telegramFromUser = callbackQuery.From;

            if (telegramFromUser == null)
            {
                _logger.LogWarning("Received callback query with null From user");
                return;
            }

            long chatId = message.Chat.Id;
            long telegramUserId = telegramFromUser.Id;
            int originalMessageId = message.MessageId;
            string callbackData = callbackQuery.Data ?? string.Empty;

            // Using a logging scope adds context (like UserId, CallbackData) to all logs within this handler's execution.
            using (_logger.BeginScope(new Dictionary<string, object?>
            {
                ["TelegramUserId"] = telegramUserId,
                ["TelegramUsername"] = telegramFromUser.Username ?? $"{telegramFromUser.FirstName} {telegramFromUser.LastName}".Trim(),
                ["ChatId"] = chatId,
                ["CallbackData"] = callbackData,
                ["OriginalMessageId"] = originalMessageId
            }))
            {
                _logger.LogInformation("Processing settings-related CallbackQuery.");

                // Immediately answer the callback query to remove the "loading" spinner from the button on the user's client.
                await AnswerCallbackQuerySilentAsync(callbackQuery.Id, cancellationToken, "Processing your request...");

                // Retrieve our system's User entity. This is crucial for most settings operations.
                var userEntity = await GetUserEntityByTelegramIdAsync(telegramUserId, chatId, originalMessageId, cancellationToken);
                if (userEntity == null)
                {
                    // GetUserEntityByTelegramIdAsync already sent a message if user not found.
                    _logger.LogWarning("User entity not found for TelegramUserID {TelegramUserId}. Settings callback cannot proceed.", telegramUserId);
                    return;
                }

                try
                {
                    // Routing logic based on the callback_data
                    if (callbackData.Equals(SettingsCommandHandler.PrefsSignalCategoriesCallback))
                        await ShowSignalCategoryPreferencesAsync(userEntity, chatId, originalMessageId, cancellationToken);
                    else if (callbackData.StartsWith(ToggleSignalCategoryPrefix))
                        await HandleToggleSignalCategoryAsync(userEntity, chatId, originalMessageId, callbackData, callbackQuery.Id, cancellationToken);
                    else if (callbackData.Equals(SaveSignalPreferencesCallback))
                        await HandleSaveSignalPreferencesAsync(userEntity, chatId, originalMessageId, cancellationToken);
                    else if (callbackData.Equals(SelectAllSignalCategoriesCallback))
                        await HandleSelectAllSignalCategoriesAsync(userEntity, chatId, originalMessageId, callbackQuery.Id, cancellationToken);
                    else if (callbackData.Equals(DeselectAllSignalCategoriesCallback))
                        await HandleDeselectAllSignalCategoriesAsync(userEntity, chatId, originalMessageId, callbackQuery.Id, cancellationToken);
                    else if (callbackData.Equals(SettingsCommandHandler.PrefsNotificationsCallback))
                        await ShowNotificationSettingsAsync(userEntity, chatId, originalMessageId, cancellationToken);
                    else if (callbackData.StartsWith(ToggleNotificationPrefix))
                        await HandleToggleNotificationAsync(userEntity, chatId, originalMessageId, callbackData, callbackQuery.Id, cancellationToken);
                    else if (callbackData.Equals(SettingsCommandHandler.MySubscriptionInfoCallback))
                        await ShowMySubscriptionInfoAsync(userEntity, chatId, originalMessageId, cancellationToken);
                    else if (callbackData.Equals(LanguageSettingsCallback))
                        await ShowLanguageSettingsAsync(userEntity, chatId, originalMessageId, cancellationToken);
                    else if (callbackData.StartsWith(SelectLanguagePrefix))
                        await HandleSelectLanguageAsync(userEntity, chatId, originalMessageId, callbackData, callbackQuery.Id, cancellationToken);
                    else if (callbackData.Equals(PrivacySettingsCallback))
                        await ShowPrivacySettingsAsync(userEntity, chatId, originalMessageId, cancellationToken);
                    else if (callbackData.Equals(SettingsCommandHandler.ShowSettingsMenuCallback)) // Action to re-show the main settings menu
                        await ReshowSettingsMenuAsync(chatId, originalMessageId, cancellationToken);
                    else if (callbackData.Equals(MenuCallbackQueryHandler.BackToMainMenuGeneral)) // Action to go to the app's main menu
                    {
                        _logger.LogInformation("User {TelegramUserId} requested to return to the main application menu.", telegramUserId);
                        var (mainMenuText, mainMenuKeyboard) = MenuCommandHandler.GetMainMenuMarkup(); // Assumes this static method exists
                        await EditMessageOrSendNewAsync(chatId, originalMessageId, mainMenuText, mainMenuKeyboard, ParseMode.MarkdownV2, cancellationToken);
                    }
                    else
                    {
                        _logger.LogWarning("Unhandled CallbackQuery data in Settings context: {CallbackData}", callbackData);
                        await _messageSender.SendTextMessageAsync(chatId, "This specific setting option is not yet implemented or recognized.", cancellationToken: cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing settings callback query");
                    await _messageSender.SendTextMessageAsync(chatId, "An error occurred while processing your request. Please try again later.", cancellationToken: cancellationToken);
                }
            }
        }
        #endregion

        #region User Entity Helper









        /// <summary>
        /// Retrieves the application's User entity based on the Telegram User ID.
        /// If not found, sends an error message to the user and returns null.
        /// This is a crucial first step for most operations in this handler.
        /// </summary>
        private async Task<Domain.Entities.User?> GetUserEntityByTelegramIdAsync(long telegramUserId, long chatId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            var userEntity = await _userRepository.GetByTelegramIdAsync(telegramUserId.ToString(), cancellationToken);
            if (userEntity == null)
            {
                _logger.LogWarning("Application User entity not found for TelegramID {TelegramUserId} (ChatID: {ChatId}). This user may need to /start the bot.", telegramUserId, chatId);
                // Attempt to edit the existing message to inform the user.
                await EditMessageOrSendNewAsync(chatId, messageIdToEdit,
                    "Your user profile was not found in our system. Please use the /start command first to register or re-initialize your session with the bot.",
                    null, ParseMode.MarkdownV2, cancellationToken);
                return null;
            }
            return userEntity;
        }
        #endregion

        #region Signal Category Preferences Methods
        /// <summary>
        /// Displays the signal category preference selection/editing interface to the user.
        /// It shows all available categories and the user's current selections.
        /// Temporary selections are stored in UserConversationStateService.
        /// </summary>
        private async Task ShowSignalCategoryPreferencesAsync(Domain.Entities.User userEntity, long chatId, int messageIdToEdit, CancellationToken cancellationToken)
        {





            _logger.LogInformation("UserID {SystemUserId} (TelegramID: {TelegramId}): Displaying signal category preferences.", userEntity.Id, userEntity.TelegramId);

            var allCategories = (await _categoryRepository.GetAllAsync(cancellationToken))
                                .Where(c => c.IsActive) // Only show active categories
                                .OrderBy(c => c.SortOrder).ThenBy(c => c.Name) // Order by SortOrder then Name
                                .ToList();

            if (!allCategories.Any())
            {
                _logger.LogInformation("No active signal categories available for UserID {SystemUserId}.", userEntity.Id);
                await EditMessageOrSendNewAsync(chatId, messageIdToEdit,
                    "Currently, there are no signal categories available to set preferences for. Please check back later.",
                    new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("⬅️ Back to Settings", SettingsCommandHandler.ShowSettingsMenuCallback)),
                    ParseMode.MarkdownV2, cancellationToken);
                return;
            }

            // Retrieve or initialize temporary selections from conversation state
            var conversationState = await _userConversationStateService.GetAsync(long.Parse(userEntity.TelegramId), cancellationToken) ?? new UserConversationState();
            string tempSelectedCategoriesKey = $"temp_signal_prefs_{userEntity.Id}"; // Using system User.Id for a more robust key

            HashSet<Guid> tempSelectedCategories;
            if (conversationState.StateData.TryGetValue(tempSelectedCategoriesKey, out var selectedObj) && selectedObj is HashSet<Guid> existingSet)
            {
                tempSelectedCategories = existingSet;
                _logger.LogDebug("Loaded temporary signal preferences for UserID {SystemUserId} from conversation state.", userEntity.Id);
            }
            else // First time or state cleared, load from saved preferences
            {
                var currentSavedPreferences = (await _userPrefsRepository.GetPreferencesByUserIdAsync(userEntity.Id, cancellationToken))
                                              .Select(p => p.CategoryId).ToHashSet();
                tempSelectedCategories = new HashSet<Guid>(currentSavedPreferences);
                conversationState.StateData[tempSelectedCategoriesKey] = tempSelectedCategories;
                await _userConversationStateService.SetAsync(long.Parse(userEntity.TelegramId), conversationState, cancellationToken); // Persist initial temp state
                _logger.LogDebug("Initialized temporary signal preferences for UserID {SystemUserId} from saved DB preferences.", userEntity.Id);
            }

            var text = TelegramMessageFormatter.Bold("📊 My Signal Preferences", escapePlainText: false) + "\n\n" +
                       "Tap a category to select or deselect it (✅ Selected / ⬜ Not Selected).\n" +
                       "You will receive signals from your chosen categories.\n" +
                       TelegramMessageFormatter.Italic("Note: Access to VIP category signals requires an active VIP subscription.", escapePlainText: false) + "\n\n" +
                       "Press 'Save Preferences' when you are done.";
            var keyboardRowArrays = new List<InlineKeyboardButton[]>(); //  

            // دکمه‌های Select All / Deselect All
            keyboardRowArrays.Add(new[]
{
    InlineKeyboardButton.WithCallbackData("✅ Select All", SelectAllSignalCategoriesCallback),
    InlineKeyboardButton.WithCallbackData("⬜ Deselect All", DeselectAllSignalCategoriesCallback)
});
            // ...
            // await EditMessageOrSendNewAsync(chatId, messageIdToEdit, text, new InlineKeyboardMarkup(keyboardRows), ...);
            // به جای خط بالا، از MarkupBuilder استفاده کنید اگر keyboardRows از نوع IEnumerable<IEnumerable<...>> است:

            foreach (var category in allCategories)
            {
                bool isSelected = tempSelectedCategories.Contains(category.Id);
                //  Add visual cues for VIP categories if applicable, e.g., "🌟 Gold Signals (VIP)"
                //  string categoryDisplayName = category.IsVip ? $"🌟 {category.Name} (VIP)" : category.Name;
                string categoryDisplayName = category.Name; // Assuming Name is already good for display
                string buttonText = $"{(isSelected ? "✅" : "⬜")} {TelegramMessageFormatter.EscapeMarkdownV2(categoryDisplayName)}";
                keyboardRowArrays.Add(new[] { InlineKeyboardButton.WithCallbackData(buttonText, $"{ToggleSignalCategoryPrefix}{category.Id}") });
            }

            keyboardRowArrays.Add(new[] { InlineKeyboardButton.WithCallbackData("💾 Save Preferences", SaveSignalPreferencesCallback) });
            keyboardRowArrays.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Back to Settings Menu", SettingsCommandHandler.ShowSettingsMenuCallback) });

            var finalKeyboard = MarkupBuilder.CreateInlineKeyboard(keyboardRowArrays.ToArray());


            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, text, finalKeyboard, ParseMode.Markdown, cancellationToken);
        }

        /// <summary>
        /// Handles toggling the selection state of a signal category in the user's temporary preferences.
        /// </summary>
        private async Task HandleToggleSignalCategoryAsync(Domain.Entities.User userEntity, long chatId, int messageIdToEdit, string callbackData, string originalCallbackQueryId, CancellationToken cancellationToken)
        {
            string categoryIdString = callbackData.Substring(ToggleSignalCategoryPrefix.Length);
            if (!Guid.TryParse(categoryIdString, out Guid categoryId))
            {
                _logger.LogWarning("Invalid CategoryID in toggle signal category callback: {CallbackData} for UserID {SystemUserId}.", callbackData, userEntity.Id);
                await AnswerCallbackQuerySilentAsync(originalCallbackQueryId, cancellationToken, "Error: Invalid category selection.", showAlert: true);
                return;
            }

            _logger.LogDebug("UserID {SystemUserId}: Toggling category preference for CategoryID {CategoryId}.", userEntity.Id, categoryId);

            var conversationState = await _userConversationStateService.GetAsync(long.Parse(userEntity.TelegramId), cancellationToken);
            string tempSelectedCategoriesKey = $"temp_signal_prefs_{userEntity.Id}";

            if (conversationState?.StateData.TryGetValue(tempSelectedCategoriesKey, out var selectedObj) == true && selectedObj is HashSet<Guid> tempSelectedCategories)
            {
                if (tempSelectedCategories.Contains(categoryId))
                {
                    tempSelectedCategories.Remove(categoryId);
                    _logger.LogInformation("UserID {SystemUserId}: CategoryID {CategoryId} deselected (temporarily).", userEntity.Id, categoryId);
                }
                else
                {
                    tempSelectedCategories.Add(categoryId);
                    _logger.LogInformation("UserID {SystemUserId}: CategoryID {CategoryId} selected (temporarily).", userEntity.Id, categoryId);
                }
                // The HashSet is modified by reference, so the object in conversationState.StateData is updated.
                // We still need to persist the conversationState if its storage is external or needs explicit save.
                await _userConversationStateService.SetAsync(long.Parse(userEntity.TelegramId), conversationState, cancellationToken);

                // Refresh the preference selection view with the updated temporary selections
                await ShowSignalCategoryPreferencesAsync(userEntity, chatId, messageIdToEdit, cancellationToken);
                await AnswerCallbackQuerySilentAsync(originalCallbackQueryId, cancellationToken); // Acknowledge the button press
            }
            else
            {
                _logger.LogError("User conversation state for signal preferences not found for UserID {SystemUserId} during toggle. Re-initializing preference view.", userEntity.Id);
                // Attempt to re-initialize the view, which should recreate the temporary state.
                await ShowSignalCategoryPreferencesAsync(userEntity, chatId, messageIdToEdit, cancellationToken);
                await AnswerCallbackQuerySilentAsync(originalCallbackQueryId, cancellationToken, "Session may have expired. Preferences reloaded. Please try toggling again.", showAlert: true);
            }
        }

        /// <summary>
        /// Handles the "Select All" action for signal category preferences.
        /// </summary>
        private async Task HandleSelectAllSignalCategoriesAsync(Domain.Entities.User userEntity, long chatId, int messageIdToEdit, string originalCallbackQueryId, CancellationToken cancellationToken)
        {
            _logger.LogInformation("UserID {SystemUserId}: Selecting all signal categories.", userEntity.Id);
            var allActiveCategoryIds = (await _categoryRepository.GetAllAsync(cancellationToken))
                                       .Where(c => c.IsActive)
                                       .Select(c => c.Id)
                                       .ToHashSet();

            var conversationState = await _userConversationStateService.GetAsync(long.Parse(userEntity.TelegramId), cancellationToken) ?? new UserConversationState();
            string tempSelectedCategoriesKey = $"temp_signal_prefs_{userEntity.Id}";
            conversationState.StateData[tempSelectedCategoriesKey] = allActiveCategoryIds;
            await _userConversationStateService.SetAsync(long.Parse(userEntity.TelegramId), conversationState, cancellationToken);

            await ShowSignalCategoryPreferencesAsync(userEntity, chatId, messageIdToEdit, cancellationToken);
            await AnswerCallbackQuerySilentAsync(originalCallbackQueryId, cancellationToken, "All categories selected.");
        }

        /// <summary>
        /// Handles the "Deselect All" action for signal category preferences.
        /// </summary>
        private async Task HandleDeselectAllSignalCategoriesAsync(Domain.Entities.User userEntity, long chatId, int messageIdToEdit, string originalCallbackQueryId, CancellationToken cancellationToken)
        {
            _logger.LogInformation("UserID {SystemUserId}: Deselecting all signal categories.", userEntity.Id);
            var conversationState = await _userConversationStateService.GetAsync(long.Parse(userEntity.TelegramId), cancellationToken) ?? new UserConversationState();
            string tempSelectedCategoriesKey = $"temp_signal_prefs_{userEntity.Id}";
            conversationState.StateData[tempSelectedCategoriesKey] = new HashSet<Guid>(); // Empty set
            await _userConversationStateService.SetAsync(long.Parse(userEntity.TelegramId), conversationState, cancellationToken);

            await ShowSignalCategoryPreferencesAsync(userEntity, chatId, messageIdToEdit, cancellationToken);
            await AnswerCallbackQuerySilentAsync(originalCallbackQueryId, cancellationToken, "All categories deselected.");
        }

        /// <summary>
        /// Saves the user's temporarily selected signal category preferences to the database.
        /// </summary>
        private async Task HandleSaveSignalPreferencesAsync(Domain.Entities.User userEntity, long chatId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            _logger.LogInformation("UserID {SystemUserId}: Attempting to save signal category preferences.", userEntity.Id);

            var conversationState = await _userConversationStateService.GetAsync(long.Parse(userEntity.TelegramId), cancellationToken);
            string tempSelectedCategoriesKey = $"temp_signal_prefs_{userEntity.Id}";

            if (conversationState?.StateData.TryGetValue(tempSelectedCategoriesKey, out var selectedObj) == true && selectedObj is HashSet<Guid> finalSelectedCategoryIds)
            {
                try
                {
                    // Use the repository to persist the preferences.
                    // The repository's SetUserPreferencesAsync should handle adding new and removing old preferences.
                    await _userPrefsRepository.SetUserPreferencesAsync(userEntity.Id, finalSelectedCategoryIds, cancellationToken);
                    await _appDbContext.SaveChangesAsync(cancellationToken); // Commit the changes to the database.

                    _logger.LogInformation("UserID {SystemUserId}: Signal preferences saved successfully. Count: {Count}",
                        userEntity.Id, finalSelectedCategoryIds.Count);

                    // Clean up the temporary state from conversation service.
                    conversationState.StateData.Remove(tempSelectedCategoriesKey);
                    await _userConversationStateService.SetAsync(long.Parse(userEntity.TelegramId), conversationState, cancellationToken);

                    await EditMessageOrSendNewAsync(chatId, messageIdToEdit,
                        "✅ Your signal preferences have been successfully saved!",
                        new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("⬅️ Back to Settings Menu", SettingsCommandHandler.ShowSettingsMenuCallback)),
                        ParseMode.MarkdownV2, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save signal preferences for UserID {SystemUserId}.", userEntity.Id);
                    await EditMessageOrSendNewAsync(chatId, messageIdToEdit,
                        "❌ An error occurred while saving your preferences. Please try again or contact support if the issue persists.",
                        new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("⬅️ Back to Settings Menu", SettingsCommandHandler.ShowSettingsMenuCallback)),
                        ParseMode.MarkdownV2, cancellationToken);
                }
            }
            else
            {
                _logger.LogWarning("Temporary signal preferences not found for UserID {SystemUserId} during save. No action taken.", userEntity.Id);
                await EditMessageOrSendNewAsync(chatId, messageIdToEdit,
                    "⚠️ No changes to save, or your session might have expired. Please try selecting your preferences again.",
                    new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("📊 Try Preferences Again", SettingsCommandHandler.PrefsSignalCategoriesCallback)),
                    ParseMode.MarkdownV2, cancellationToken);
            }
        }
        #endregion

        #region Notification Settings Methods
        /// <summary>
        /// Displays the notification settings interface to the user.
        /// Allows toggling general, VIP signal, and RSS news notifications.
        /// </summary>
        private async Task ShowNotificationSettingsAsync(Domain.Entities.User userEntity, long chatId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            _logger.LogInformation("UserID {SystemUserId}: Displaying notification settings.", userEntity.Id);

            var text = TelegramMessageFormatter.Bold("🔔 Notification Settings", escapePlainText: false) + "\n\n" +
                       "Manage your notification preferences. Tap an option to toggle it (✅ Enabled / ⬜ Disabled):";

            var activeSubscription = await _subscriptionService.GetActiveSubscriptionByUserIdAsync(userEntity.Id, cancellationToken);
            bool isVipUser = IsUserVip(activeSubscription);

            //  ایجاد لیست ردیف‌های دکمه
            var keyboardRowList = new List<List<InlineKeyboardButton>>();

            // ردیف اول: General Bot Updates
            keyboardRowList.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData(
        $"{(userEntity.EnableGeneralNotifications ? "✅" : "⬜")} General Bot Updates",
        $"{ToggleNotificationPrefix}{NotificationTypeGeneral}")
    });

            // ردیف دوم: VIP Signal Alerts (شرطی)
            if (isVipUser)
            {
                keyboardRowList.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData(
            $"{(userEntity.EnableVipSignalNotifications ? "✅" : "⬜")} ✨ VIP Signal Alerts",
            $"{ToggleNotificationPrefix}{NotificationTypeVipSignal}")
        });
            }
            else
            {
                keyboardRowList.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData(
            "💎 Enable VIP Signal Alerts (Upgrade Required)",
            MenuCommandHandler.SubscribeCallbackData) // لینک به صفحه اشتراک
        });
            }

            // ردیف سوم: RSS News Updates
            keyboardRowList.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData(
        $"{(userEntity.EnableRssNewsNotifications ? "✅" : "⬜")} RSS News Updates",
        $"{ToggleNotificationPrefix}{NotificationTypeRssNews}")
    });
            var finalKeyboard = new InlineKeyboardMarkup(keyboardRowList);
            // ردیف چهارم: Back to Settings Menu
            keyboardRowList.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("⬅️ Back to Settings Menu", SettingsCommandHandler.ShowSettingsMenuCallback) });

            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, text, finalKeyboard, ParseMode.Markdown, cancellationToken);

        }

        /// <summary>
        /// Handles toggling a specific notification setting for the user.
        /// </summary>
        private async Task HandleToggleNotificationAsync(Domain.Entities.User userEntity, long chatId, int messageIdToEdit, string callbackData, string originalCallbackQueryId, CancellationToken cancellationToken)
        {
            string notificationType = callbackData.Substring(ToggleNotificationPrefix.Length);
            _logger.LogInformation("UserID {SystemUserId}: Attempting to toggle notification setting for '{NotificationType}'.", userEntity.Id, notificationType);

            bool newStatus;
            string statusMessage;

            switch (notificationType)
            {
                case NotificationTypeGeneral:
                    userEntity.EnableGeneralNotifications = !userEntity.EnableGeneralNotifications;
                    newStatus = userEntity.EnableGeneralNotifications;
                    statusMessage = $"General Bot Updates are now {(newStatus ? "ENABLED" : "DISABLED")}.";
                    break;
                case NotificationTypeVipSignal:
                    var activeSub = await _subscriptionService.GetActiveSubscriptionByUserIdAsync(userEntity.Id, cancellationToken);
                    if (!IsUserVip(activeSub))
                    {
                        await AnswerCallbackQuerySilentAsync(originalCallbackQueryId, cancellationToken, "VIP subscription is required to change VIP signal alerts.", showAlert: true);
                        await ShowNotificationSettingsAsync(userEntity, chatId, messageIdToEdit, cancellationToken); // Refresh menu
                        return;
                    }
                    userEntity.EnableVipSignalNotifications = !userEntity.EnableVipSignalNotifications;
                    newStatus = userEntity.EnableVipSignalNotifications;
                    statusMessage = $"VIP Signal Alerts are now {(newStatus ? "ENABLED" : "DISABLED")}.";
                    break;
                case NotificationTypeRssNews:
                    userEntity.EnableRssNewsNotifications = !userEntity.EnableRssNewsNotifications;
                    newStatus = userEntity.EnableRssNewsNotifications;
                    statusMessage = $"RSS News Updates are now {(newStatus ? "ENABLED" : "DISABLED")}.";
                    break;
                default:
                    _logger.LogWarning("UserID {SystemUserId}: Unknown notification type '{NotificationType}' requested to toggle.", userEntity.Id, notificationType);
                    await AnswerCallbackQuerySilentAsync(originalCallbackQueryId, cancellationToken, "Error: Unknown notification setting.", showAlert: true);
                    return;
            }
            userEntity.UpdatedAt = DateTime.UtcNow; // Mark user entity as updated

            try
            {
                // The User entity was fetched by GetUserEntityForProcessingAsync, so it's tracked by the DbContext.
                await _appDbContext.SaveChangesAsync(cancellationToken); // Save changes to the User entity
                _logger.LogInformation("UserID {SystemUserId}: Notification setting '{NotificationType}' successfully changed to {NewStatus}.",
                    userEntity.Id, notificationType, newStatus);

                await AnswerCallbackQuerySilentAsync(originalCallbackQueryId, cancellationToken, statusMessage, showAlert: false); // Show a toast
                await ShowNotificationSettingsAsync(userEntity, chatId, messageIdToEdit, cancellationToken); // Refresh the settings view
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save notification settings for UserID {SystemUserId} after toggle.", userEntity.Id);
                // Attempt to revert the change in the entity if save failed, though this is complex if other changes were made.
                // For simplicity, just inform user and re-show.
                await AnswerCallbackQuerySilentAsync(originalCallbackQueryId, cancellationToken, "Error saving your setting. Please try again.", showAlert: true);
                // Re-fetch the entity to show the non-persisted state (or the old state if an error occurred before modification)
                var refreshedUserEntity = await _userRepository.GetByIdAsync(userEntity.Id, cancellationToken);
                if (refreshedUserEntity != null)
                    await ShowNotificationSettingsAsync(refreshedUserEntity, chatId, messageIdToEdit, cancellationToken);
            }
        }

        // Helper to determine if a user's active subscription grants VIP access
        private bool IsUserVip(SubscriptionDto? activeSubscription)
        {
            // This logic needs to be based on your plan definitions.
            // Example: Check PlanId, or a specific property on SubscriptionDto indicating VIP status.
            // For now, a simple check if any active subscription exists.
            return activeSubscription != null; // Replace with actual VIP check
        }
        #endregion

        #region Subscription Info Methods
        /// <summary>
        /// Displays the user's current subscription status and provides options to view/manage plans.
        /// </summary>
        // File: TelegramPanel/Application/CommandHandlers/SettingsCallbackQueryHandler.cs
        // در متد ShowMySubscriptionInfoAsync:

        private async Task ShowMySubscriptionInfoAsync(Domain.Entities.User userEntity, long chatId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            _logger.LogInformation("UserID {SystemUserId}: Showing subscription information.", userEntity.Id);
            var userDto = await _userService.GetUserByIdAsync(userEntity.Id, cancellationToken); // پاس دادن Guid
            if (userDto == null)
            {
                await EditMessageOrSendNewAsync(chatId, messageIdToEdit, "Could not retrieve your user profile. Please use /start first.", null, ParseMode.MarkdownV2, cancellationToken);
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine(TelegramMessageFormatter.Bold("⭐ My Subscription Status", escapePlainText: false));
            sb.AppendLine();

            if (userDto.ActiveSubscription != null)
            {
                string planNameForDisplay = "Your Current Plan"; //  جایگزین با نام واقعی پلن
                sb.AppendLine($"You are currently subscribed to the {TelegramMessageFormatter.Bold(planNameForDisplay, escapePlainText: true)}.");
                sb.AppendLine($"Your subscription is active until: {TelegramMessageFormatter.Bold($"{userDto.ActiveSubscription.EndDate:yyyy-MM-dd HH:mm} UTC")}");
                sb.AppendLine("\nThank you for your support! You have access to all premium features.");
            }
            else
            {
                sb.AppendLine("You currently do not have an active subscription.");
                sb.AppendLine("Upgrade to a premium plan to unlock exclusive signals, advanced analytics, and more benefits!");
            }

            //  ایجاد لیست ردیف‌های دکمه
            var keyboardRowList = new List<List<InlineKeyboardButton>>();

            // ردیف اول: دکمه مدیریت/خرید اشتراک
            keyboardRowList.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData(
        userDto.ActiveSubscription != null ? "🔄 Manage / Renew Subscription" : "💎 View Subscription Plans",
        MenuCommandHandler.SubscribeCallbackData
    )});

            var finalKeyboard = new InlineKeyboardMarkup(keyboardRowList);
            // ردیف دوم: دکمه بازگشت
            keyboardRowList.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("⬅️ Back to Settings Menu", SettingsCommandHandler.ShowSettingsMenuCallback) });

            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, sb.ToString(), finalKeyboard, ParseMode.Markdown, cancellationToken);
        }

        #endregion

        #region Language & Privacy Settings Methods (Placeholders - To be fully implemented)
        /// <summary>
        /// Displays language selection options to the user.
        /// </summary>
        private async Task ShowLanguageSettingsAsync(Domain.Entities.User userEntity, long chatId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            _logger.LogInformation("UserID {SystemUserId}: Displaying language settings. Current language: {CurrentLang}", userEntity.Id, userEntity.PreferredLanguage);

            var text = TelegramMessageFormatter.Bold("🌐 Language Settings", escapePlainText: false) + "\n\n" +
                       $"Your current language is: {TelegramMessageFormatter.Bold(userEntity.PreferredLanguage.ToUpperInvariant())}\n" +
                       "Select your preferred language for the bot interface:";
            var keyboard = MarkupBuilder.CreateInlineKeyboard(
                new[] { InlineKeyboardButton.WithCallbackData($"{(userEntity.PreferredLanguage == "en" ? "🔹 " : "")}🇬🇧 English", $"{SelectLanguagePrefix}en") },
                new[] { InlineKeyboardButton.WithCallbackData("⬅️ Back to Settings Menu", SettingsCommandHandler.ShowSettingsMenuCallback) }
            );
            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, text, keyboard, ParseMode.MarkdownV2, cancellationToken);
        }

        /// <summary>
        /// Handles the user's language selection and updates it in the database.
        /// </summary>
        private async Task HandleSelectLanguageAsync(Domain.Entities.User userEntity, long chatId, int messageIdToEdit, string callbackData, string originalCallbackQueryId, CancellationToken cancellationToken)
        {
            string langCode = callbackData.Substring(SelectLanguagePrefix.Length).ToLowerInvariant(); // e.g., "en", "fa"
            _logger.LogInformation("UserID {SystemUserId}: Attempting to set preferred language to '{LangCode}'.", userEntity.Id, langCode);

            // Validate if langCode is supported (e.g., against a list of supported languages)
            var supportedLanguages = new List<string> { "en" /*, "fa" */ };
            if (!supportedLanguages.Contains(langCode))
            {
                _logger.LogWarning("UserID {SystemUserId}: Attempted to set unsupported language '{LangCode}'.", userEntity.Id, langCode);
                await AnswerCallbackQuerySilentAsync(originalCallbackQueryId, cancellationToken, "Selected language is not supported.", showAlert: true);
                await ShowLanguageSettingsAsync(userEntity, chatId, messageIdToEdit, cancellationToken); // Refresh
                return;
            }

            if (userEntity.PreferredLanguage == langCode)
            {
                await AnswerCallbackQuerySilentAsync(originalCallbackQueryId, cancellationToken, $"Language is already set to {langCode.ToUpperInvariant()}.");
                await ShowLanguageSettingsAsync(userEntity, chatId, messageIdToEdit, cancellationToken); // Refresh (no change needed but good UX)
                return;
            }

            userEntity.PreferredLanguage = langCode;
            userEntity.UpdatedAt = DateTime.UtcNow;

            try
            {
                await _appDbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("UserID {SystemUserId}: Preferred language successfully changed to '{LangCode}'.", userEntity.Id, langCode);
                await AnswerCallbackQuerySilentAsync(originalCallbackQueryId, cancellationToken, $"Language preferences updated to {langCode.ToUpperInvariant()}.");
                await ShowLanguageSettingsAsync(userEntity, chatId, messageIdToEdit, cancellationToken); // Refresh to show new selection
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save preferred language '{LangCode}' for UserID {SystemUserId}.", langCode, userEntity.Id);
                await AnswerCallbackQuerySilentAsync(originalCallbackQueryId, cancellationToken, "Error saving language preference. Please try again.", showAlert: true);
                // Optionally revert userEntity.PreferredLanguage to its old value if save failed.
                // For now, just re-show the menu.
                await ShowLanguageSettingsAsync(userEntity, chatId, messageIdToEdit, cancellationToken);
            }
        }





        /// <summary>
        /// Displays privacy-related information and options.
        /// </summary>
        private async Task ShowPrivacySettingsAsync(Domain.Entities.User userEntity, long chatId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            _logger.LogInformation("UserID {SystemUserId} (TelegramID: {TelegramId}): Displaying privacy settings and policy information.", userEntity.Id, userEntity.TelegramId);

            // TODO: Replace with your actual Privacy Policy URL and Support Contact information.
            string privacyPolicyUrl = "https://your-forex-bot-domain.com/privacy-policy"; //  آدرس واقعی سیاست حفظ حریم خصوصی شما
            string supportContactCommand = "/support"; //  دستوری که کاربر می‌تواند برای تماس با پشتیبانی استفاده کند
            string supportEmail = "support@your-forex-bot-domain.com"; //  ایمیل پشتیبانی شما

            // Constructing the message text using TelegramMessageFormatter for consistent styling.
            var textBuilder = new StringBuilder();
            textBuilder.AppendLine(TelegramMessageFormatter.Bold("🔒 Privacy & Data Management", escapePlainText: false));
            textBuilder.AppendLine(); // Blank line for readability
            textBuilder.AppendLine("We are committed to protecting your privacy and handling your data responsibly.");
            textBuilder.AppendLine("You can review our full privacy policy to understand how we collect, use, and protect your information.");
            textBuilder.AppendLine();
            textBuilder.AppendLine(TelegramMessageFormatter.Link("📜 Read our Full Privacy Policy", privacyPolicyUrl, escapeLinkText: false)); // escapeLinkText=false as "📜..." is already formatted
            textBuilder.AppendLine();
            textBuilder.AppendLine(TelegramMessageFormatter.Bold("Data Requests:", escapePlainText: false));
            textBuilder.AppendLine("If you wish to request access to your data, or request data deletion, please contact our support team.");
            textBuilder.AppendLine($"You can reach us via the {TelegramMessageFormatter.Code(supportContactCommand)} command or by emailing us at {TelegramMessageFormatter.Link(supportEmail, $"mailto:{supportEmail}", escapeLinkText: false)}.");
            textBuilder.AppendLine();
            textBuilder.AppendLine(TelegramMessageFormatter.Italic("Note: Data deletion requests will be processed according to our data retention policy and applicable regulations.", escapePlainText: true));

            // Constructing the inline keyboard
            var keyboardRows = new List<IEnumerable<InlineKeyboardButton>>
            {
                // First row: Link to the privacy policy
                new[]
                {
                    InlineKeyboardButton.WithUrl("📜 View Privacy Policy Online", privacyPolicyUrl)
                }
            };

            // (Optional) Button for initiating a data deletion request.
            // This would typically trigger a new callback or a conversation state.
            // For now, it's commented out as it requires further backend implementation.
            /*
            public const string RequestDataDeletionCallback = "privacy_request_data_deletion"; //  باید در CallbackData Constants تعریف شود
            keyboardRows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("🗑️ Request My Data Deletion", RequestDataDeletionCallback)
            });
            */

            // Last row: Back to the main settings menu
            var keyboardRowArrays = new List<InlineKeyboardButton[]>();
            keyboardRowArrays.Add(new[] { InlineKeyboardButton.WithUrl("📜 View Privacy Policy Online", privacyPolicyUrl) });
            keyboardRowArrays.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Back to Settings Menu", SettingsCommandHandler.ShowSettingsMenuCallback) });
            var finalKeyboard = MarkupBuilder.CreateInlineKeyboard(keyboardRowArrays.ToArray());
            // await EditMessageOrSendNewAsync(chatId, messageIdToEdit, textBuilder.ToString(), inlineKeyboard, ...); // قبلی
            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, textBuilder.ToString(), finalKeyboard, ParseMode.Markdown, cancellationToken);
        }
        #endregion

        #region Menu Display & Helper Methods
        /// <summary>
        /// Re-displays the main settings menu by editing the previous message.
        /// </summary>
        private async Task ReshowSettingsMenuAsync(long chatId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Reshowing main settings menu for ChatID {ChatId}, MessageID {MessageIdToEdit}", chatId, messageIdToEdit);
            // Uses the static method from SettingsCommandHandler to get consistent menu markup
            var (settingsMenuText, settingsKeyboard) = SettingsCommandHandler.GetSettingsMenuMarkup();
            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, settingsMenuText, settingsKeyboard, ParseMode.MarkdownV2, cancellationToken);
        }

        /// <summary>
        /// Silently answers a callback query to remove the loading state from the button.
        /// Optionally shows a short text notification (toast) to the user.
        /// </summary>
        private async Task AnswerCallbackQuerySilentAsync(string callbackQueryId, CancellationToken cancellationToken, string? text = null, bool showAlert = false)
        {
            try
            {
                await _botClient.AnswerCallbackQuery(callbackQueryId, text, showAlert, cancellationToken: cancellationToken);
                if (!string.IsNullOrWhiteSpace(text))
                    _logger.LogDebug("Answered CallbackQueryID: {CallbackQueryId} with text: '{Text}', ShowAlert: {ShowAlert}", callbackQueryId, text, showAlert);
                else
                    _logger.LogDebug("Answered CallbackQueryID: {CallbackQueryId} silently.", callbackQueryId);
            }
            catch (ApiRequestException apiEx) when (apiEx.ErrorCode == 400 && (apiEx.Message.Contains("query is too old", StringComparison.OrdinalIgnoreCase) || apiEx.Message.Contains("QUERY_ID_INVALID", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("Attempted to answer callback query {CallbackQueryId}, but it was too old, invalid, or already answered. Telegram API Error: {ApiErrorMessage}", callbackQueryId, apiEx.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred while trying to answer CallbackQueryID {CallbackQueryId}.", callbackQueryId);
            }
        }

        /// <summary>
        /// Attempts to edit an existing message. If editing fails (e.g., message too old, not found, or not modified),
        /// it sends a new message with the same content and markup instead.
        /// This provides a more resilient user experience.
        /// </summary>
        private async Task EditMessageOrSendNewAsync(
            long chatId,
            int messageId,
            string text,
            InlineKeyboardMarkup? replyMarkup, // Explicitly InlineKeyboardMarkup for clarity
            ParseMode? parseMode = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug("Attempting to edit message. ChatID: {ChatId}, MessageID: {MessageId}, NewText (partial): {TextStart}", chatId, messageId, text.Length > 50 ? text.Substring(0, 50) + "..." : text);
                await _botClient.EditMessageText(
                    chatId: chatId,
                    messageId: messageId,
                    text: text,
                    parseMode: ParseMode.Markdown,
                    replyMarkup: replyMarkup,
                    cancellationToken: cancellationToken
                );
                _logger.LogInformation("Message edited successfully. ChatID: {ChatId}, MessageID: {MessageId}", chatId, messageId);
            }
            catch (ApiRequestException ex) when (
                MessageWasNotModified(ex) ||
                MessageToEditNotFound(ex) ||
                ChatNotFound(ex) || // If the chat itself is not found (e.g., user blocked bot and message is old)
                IsBadRequestFromOldQuery(ex) // General "Bad Request" often means message is too old to edit, or invalid state
            )
            {
                _logger.LogWarning(ex, "Could not edit message (MessageID: {MessageId}, ChatID: {ChatId}) due to a common Telegram API reason: {ApiError}. Sending a new message instead.", messageId, chatId, ex.Message);
                await _messageSender.SendTextMessageAsync(chatId, text, parseMode, replyMarkup, cancellationToken);
            }
            catch (Exception ex)
            {
                // Catch-all for other unexpected errors during editing
                _logger.LogError(ex, "Unexpected error while attempting to edit message (MessageID: {MessageId}, ChatID: {ChatId}). A new message will be sent as a fallback.", messageId, chatId);
                await _messageSender.SendTextMessageAsync(chatId, text, parseMode, replyMarkup, cancellationToken);
            }
        }

        // Helper methods to check specific ApiRequestException conditions for better readability
        private bool MessageWasNotModified(ApiRequestException ex) => ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase);
        private bool MessageToEditNotFound(ApiRequestException ex) => ex.Message.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase);
        private bool ChatNotFound(ApiRequestException ex) => ex.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase); // Added
        private bool IsBadRequestFromOldQuery(ApiRequestException ex) => ex.ErrorCode == 400 && (ex.Message.Contains("query is too old", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("MESSAGE_ID_INVALID", StringComparison.OrdinalIgnoreCase)); // Added MESSAGE_ID_INVALID
        #endregion
    }
}