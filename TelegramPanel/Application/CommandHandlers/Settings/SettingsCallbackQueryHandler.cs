// File: TelegramPanel/Application/CommandHandlers/SettingsCallbackQueryHandler.cs

#region Usings
// Standard .NET & NuGet
// Project specific: Application Layer (Core - پروژه اصلی شما)
using Application.Common.Interfaces; // برای IUserRepository, IUserSignalPreferenceRepository, ISignalCategoryRepository, IAppDbContext, INotificationService
using Application.DTOs;
using Application.Interfaces;        // برای IUserService, ISubscriptionService از پروژه اصلی Application
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging; // برای ILogger
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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
using TelegramPanel.Infrastructure.Helper;
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions;
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
            {
                return false;
            }

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
        // --- Add this field to your class for rate limiting ---
        private static readonly ConcurrentDictionary<long, DateTime> _lastRequestTimestamps = new();
        private const double RateLimitSeconds = 0.5; // Allow max 2 requests per second per user.

        /// <summary>
        /// Handles an incoming callback query from the settings menu with a focus on security, performance, and reliability.
        /// This method acts as the main entry point, orchestrating validation, rate limiting, and dispatching.
        /// </summary>
        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            // --- 1. Initial Validation ---
            if (!ValidateRequest(update, out CallbackQuery? callbackQuery, out Message? message, out User? fromUser))
            {
                // 2. The compiler knows if it gets here, ValidateRequest returned `false`.
                //    Therefore, `message` might be null, but that's okay because you `return`.
                return;
            }

            long chatId = message.Chat.Id;
            long telegramUserId = fromUser.Id;

            // --- 2. Security: Per-User Rate Limiting (Throttling) ---
            if (IsRateLimited(telegramUserId))
            {
                _logger.LogWarning("Rate limit triggered for UserID {TelegramUserId}. Ignoring callback '{CallbackData}'.", telegramUserId, callbackQuery.Data);
                // Provide gentle feedback to the user instead of failing silently.
                await AnswerCallbackQuerySilentAsync(callbackQuery.Id, cancellationToken, text: "You're clicking too fast. Please wait a moment.").ConfigureAwait(false);
                return;
            }

            // --- 3. Contextual Logging ---
            // CORRECTED: Use a 'using' statement to ensure the logging scope is properly disposed, preventing leaks.
            using var logScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["TelegramUserId"] = telegramUserId,
                ["CallbackQueryId"] = callbackQuery.Id,
                ["CallbackData"] = callbackQuery.Data!,
                ["OriginalMessageId"] = message.MessageId
            });

            _logger.LogInformation("Processing settings-related CallbackQuery.");

            try
            {
                // --- 4. Immediate Acknowledgement (User Feedback) ---
                // Acknowledge the button press instantly so the user knows their action was received.
                // We do this *before* dispatching. Specific success/failure messages will be sent by the sub-handlers.
                await AnswerCallbackQuerySilentAsync(callbackQuery.Id, cancellationToken).ConfigureAwait(false);

                // --- 5. Logic Dispatching (Command Pattern) ---
                // The main work is delegated to a dedicated dispatcher method.
                await DispatchCallbackAsync(callbackQuery, cancellationToken).ConfigureAwait(false);
            }
            // 6. ENHANCED EXCEPTION HANDLING
            catch (OperationCanceledException)
            {
                // This is an expected exception when the application is shutting down or the request is canceled. Log as info.
                _logger.LogInformation("Operation was canceled for CallbackQueryId {CallbackQueryId}.", callbackQuery.Id);
            }
            catch (ApiRequestException apiEx)
            {
                // Handle API-specific errors that might occur during processing.
                // These are often recoverable or indicate a client-side issue (e.g., bot blocked).
                _logger.LogError(apiEx, "A Telegram API error occurred while processing CallbackQueryId {CallbackQueryId}.", callbackQuery.Id);
                // It's often not necessary to message the user again, as a previous API call likely already failed.
            }
            catch (Exception ex)
            {
                // This is a safety net for any truly unexpected exceptions within the dispatchers.
                _logger.LogCritical(ex, "A critical unhandled error occurred while processing CallbackQueryId {CallbackQueryId}.", callbackQuery.Id);

                try
                {
                    // Inform the user that something went wrong without revealing internal details.
                    await _messageSender.SendTextMessageAsync(
                        chatId,
                        "An unexpected error occurred. Our team has been notified. Please try again later.",
                        cancellationToken: cancellationToken
                    ).ConfigureAwait(false);
                }
                catch (ApiRequestException finalApiEx)
                {
                    // If even sending the error message fails, log it to avoid obscuring the original, more important exception.
                    _logger.LogError(finalApiEx, "Failed to send the final error message to UserID {TelegramUserId} after a critical error.", telegramUserId);
                }
            }
        }

        /// <summary>
        /// Validates the incoming update to ensure it's a well-formed callback query we can process.
        /// </summary>
        /// <returns>True if the request is valid, otherwise false.</returns>
        /// <summary>
        /// Validates the incoming update to ensure it's a usable CallbackQuery.
        /// This method uses a "guard clause" pattern to fail fast on invalid data.
        /// </summary>
        /// <returns>
        /// True if the request is a valid, well-formed CallbackQuery; otherwise, false.
        /// </returns>
        private bool ValidateRequest(
            Update update,
            [NotNullWhen(true)] out CallbackQuery? callbackQuery,
            [NotNullWhen(true)] out Message? message,
            [NotNullWhen(true)] out User? fromUser)
        {
            // 1. EXPLICIT INITIALIZATION: Start by setting out parameters to a known default state (null).
            // This is a robust pattern that ensures they have a predictable value if validation fails.
            callbackQuery = null;
            message = null;
            fromUser = null;

            // 2. PRIMARY GUARD CLAUSE: Ensure the update is a CallbackQuery.
            // This is the most fundamental check.
            if (update.CallbackQuery is null)
            {
                _logger.LogWarning("Handler invoked with a non-CallbackQuery update. UpdateType: {UpdateType}", update.Type);
                return false;
            }

            callbackQuery = update.CallbackQuery; // It's safe to assign now.

            // 3. NESTED ENTITY GUARD CLAUSES: Ensure essential nested objects exist.
            // We check them sequentially, providing specific logs for each failure case.
            if (callbackQuery.Message is null)
            {
                _logger.LogWarning("Received CallbackQuery (ID: {CallbackQueryId}) but its associated Message is null. This is unexpected for standard button presses.", callbackQuery.Id);
                return false;
            }

            if (callbackQuery.From is null)
            {
                _logger.LogWarning("Received CallbackQuery (ID: {CallbackQueryId}) but its 'From' User is null.", callbackQuery.Id);
                return false;
            }

            // Now that we've confirmed nested entities exist, we can assign them.
            message = callbackQuery.Message;
            fromUser = callbackQuery.From;

            // 4. PAYLOAD VALIDATION GUARD CLAUSE: Check the callback_data itself.
            // Telegram's API specifies a 1-64 byte limit for this payload.
            if (string.IsNullOrEmpty(callbackQuery.Data) || callbackQuery.Data.Length > 64)
            {
                _logger.LogError(
                    "Received an invalid CallbackQuery (ID: {CallbackQueryId}) from User {UserId} with null, empty, or oversized data. Length: {Length}. Data: '{Data}'",
                    callbackQuery.Id,
                    fromUser.Id,
                    callbackQuery.Data?.Length ?? 0,
                    callbackQuery.Data);
                return false;
            }

            // If all guard clauses have been passed, the request is valid.
            return true;
        }

        /// <summary>
        /// Checks if a user is making requests too frequently.
        /// </summary>
        /// <returns>True if the user should be rate-limited, otherwise false.</returns>
        private bool IsRateLimited(long telegramUserId)
        {
            DateTime now = DateTime.UtcNow;

            if (_lastRequestTimestamps.TryGetValue(telegramUserId, out DateTime lastRequestTime))
            {
                if ((now - lastRequestTime).TotalSeconds < RateLimitSeconds)
                {
                    return true; // Too soon.
                }
            }

            // Update the timestamp for the current request.
            _lastRequestTimestamps[telegramUserId] = now;
            return false;
        }

        /// <summary>
        /// Determines if a "Processing..." toast notification should be shown based on the callback data.
        /// Simple navigation actions (like 'back') don't need it.
        /// </summary>
        private bool ShouldShowProcessingToast(string callbackData)
        {
            // Actions that change data or require database lookups should show a toast.
            return !callbackData.Equals(SettingsCommandHandler.ShowSettingsMenuCallback) &&
                   !callbackData.Equals(MenuCallbackQueryHandler.BackToMainMenuGeneral);
        }

        /// <summary>
        /// Routes the callback query to the appropriate handler method. This implementation
        /// is robust against null data and uses a clear, flat control flow for routing.
        /// </summary>
        private async Task DispatchCallbackAsync(CallbackQuery callbackQuery, CancellationToken cancellationToken)
        {
            // 1. ROBUSTNESS: Explicitly check for null data to prevent NullReferenceException.
            // This removes the need for the null-forgiving operator (`!`) and makes the code safer.
            if (callbackQuery.Message is null || callbackQuery.Data is null)
            {
                _logger.LogCritical("Received a callback query with null Message or Data. CallbackQuery ID: {CallbackQueryId}", callbackQuery.Id);
                // We cannot proceed, but we should at least try to answer the query silently to dismiss the loading indicator.
                await AnswerCallbackQuerySilentAsync(callbackQuery.Id, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Now it's safe to access these properties.
            long chatId = callbackQuery.Message.Chat.Id;
            int messageId = callbackQuery.Message.MessageId;
            long telegramUserId = callbackQuery.From.Id;
            string data = callbackQuery.Data;

            // The dictionaries are kept local because their lambdas need to capture the method's local variables.
            // This is a reasonable trade-off for clean, self-contained handlers.
            var exactMatchActions = new Dictionary<string, Func<Task>>
    {
        { SettingsCommandHandler.ShowSettingsMenuCallback, () => ReshowSettingsMenuAsync(chatId, messageId, cancellationToken) },
        { MenuCallbackQueryHandler.BackToMainMenuGeneral, async () => {
            (string text, InlineKeyboardMarkup k) = MenuCommandHandler.GetMainMenuMarkup();
            await EditMessageOrSendNewAsync(chatId, messageId, text, k, ParseMode.MarkdownV2, cancellationToken);
        }},
        { SettingsCommandHandler.PrefsSignalCategoriesCallback, () => ShowSignalCategoryPreferencesAsync(telegramUserId, chatId, messageId, cancellationToken) },
        { SettingsCommandHandler.PrefsNotificationsCallback, () => ShowNotificationSettingsAsync(telegramUserId, chatId, messageId, cancellationToken) },
        { SettingsCommandHandler.MySubscriptionInfoCallback, () => ShowMySubscriptionInfoAsync(telegramUserId, chatId, messageId, cancellationToken) },
        { LanguageSettingsCallback, () => ShowLanguageSettingsAsync(telegramUserId, chatId, messageId, cancellationToken) },
        { PrivacySettingsCallback, () => ShowPrivacySettingsAsync(telegramUserId, chatId, messageId, cancellationToken) },
        { SaveSignalPreferencesCallback, () => HandleSaveSignalPreferencesAsync(telegramUserId, chatId, messageId, cancellationToken) },
        { SelectAllSignalCategoriesCallback, () => HandleSelectAllSignalCategoriesAsync(telegramUserId, chatId, messageId, callbackQuery.Id, cancellationToken) },
        { DeselectAllSignalCategoriesCallback, () => HandleDeselectAllSignalCategoriesAsync(telegramUserId, chatId, messageId, callbackQuery.Id, cancellationToken) }
    };

            var prefixMatchActions = new Dictionary<string, Func<Task>>
    {
        { ToggleSignalCategoryPrefix, () => HandleToggleSignalCategoryAsync(telegramUserId, chatId, messageId, data, callbackQuery.Id, cancellationToken) },
        { ToggleNotificationPrefix, () => HandleToggleNotificationAsync(telegramUserId, chatId, messageId, data, callbackQuery.Id, cancellationToken) },
        { SelectLanguagePrefix, () => HandleSelectLanguageAsync(telegramUserId, chatId, messageId, data, callbackQuery.Id, cancellationToken) }
    };

            // 2. READABILITY: Use a clear, flat "early return" pattern for routing.
            // Check for an exact match first.
            if (exactMatchActions.TryGetValue(data, out Func<Task>? action))
            {
                await action().ConfigureAwait(false);
                return; // Action found and executed, so we're done.
            }

            // 3. CLARITY: Use a more explicit loop for prefix matching.
            // If no exact match, check for a prefix match.
            foreach (var (prefix, prefixAction) in prefixMatchActions)
            {
                if (data.StartsWith(prefix))
                {
                    await prefixAction().ConfigureAwait(false);
                    return; // Action found and executed, so we're done.
                }
            }

            // 4. BETTER UX: If no action is found, log it and provide direct feedback to the user.
            _logger.LogWarning("Unhandled CallbackQuery data in Settings context: '{CallbackData}' from User {TelegramUserId}", data, telegramUserId);
            await AnswerCallbackQuerySilentAsync(
                callbackQuery.Id,
                cancellationToken,
                "This action is no longer valid or recognized.",
                showAlert: true).ConfigureAwait(false);
        }


        #endregion

        #region Signal Category Preferences Methods
        /// <summary>
        /// Displays the signal category preference selection/editing interface to the user.
        /// It shows all available categories and the user's current selections.
        /// Temporary selections are stored in UserConversationStateService.
        /// </summary>
        /// <summary>
        /// Displays the signal category preference selection/editing interface to the user.
        /// It shows all available categories and the user's current selections.
        /// Temporary selections are stored in UserConversationStateService.
        /// </summary>
        private async Task ShowSignalCategoryPreferencesAsync(long telegramUserId, long chatId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            Domain.Entities.User? userEntity = await _userRepository.GetByTelegramIdAsync(telegramUserId.ToString(), cancellationToken);
            if (userEntity == null)
            {
                _logger.LogWarning("Cannot show signal preferences, user not found for Telegram ID {TelegramUserId}", telegramUserId);
                await EditMessageOrSendNewAsync(chatId, messageIdToEdit, "Your user profile could not be found.", null, cancellationToken: cancellationToken);
                return;
            }

            _logger.LogInformation("UserID {SystemUserId}: Displaying signal category preferences.", userEntity.Id);

            List<Domain.Entities.SignalCategory> allCategories = (await _categoryRepository.GetAllAsync(cancellationToken))
                                .Where(c => c.IsActive)
                                .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
                                .ToList();

            if (!allCategories.Any())
            {
                await EditMessageOrSendNewAsync(chatId, messageIdToEdit,
                    "Currently, there are no signal categories available to set preferences for.",
                    new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("⬅️ Back to Settings", SettingsCommandHandler.ShowSettingsMenuCallback)),
                    ParseMode.MarkdownV2, cancellationToken);
                return;
            }

            UserConversationState conversationState = await _userConversationStateService.GetAsync(telegramUserId, cancellationToken) ?? new UserConversationState();
            string tempSelectedCategoriesKey = $"temp_signal_prefs_{userEntity.Id}";

            if (!conversationState.StateData.TryGetValue(tempSelectedCategoriesKey, out object? selectedObj) || selectedObj is not HashSet<Guid> tempSelectedCategories)
            {
                HashSet<Guid> currentSavedPreferences = (await _userPrefsRepository.GetPreferencesByUserIdAsync(userEntity.Id, cancellationToken))
                                              .Select(p => p.CategoryId).ToHashSet();
                tempSelectedCategories = new HashSet<Guid>(currentSavedPreferences);
                conversationState.StateData[tempSelectedCategoriesKey] = tempSelectedCategories;
                await _userConversationStateService.SetAsync(telegramUserId, conversationState, cancellationToken);
            }

            string text = "📊 *My Signal Preferences*\n\n" +
                       "Tap a category to select or deselect it (✅/⬜).\n" +
                       "Press 'Save Preferences' when you are done.";

            List<List<InlineKeyboardButton>> keyboardRows =
            [
        [
            InlineKeyboardButton.WithCallbackData("✅ Select All", SelectAllSignalCategoriesCallback),
            InlineKeyboardButton.WithCallbackData("⬜ Deselect All", DeselectAllSignalCategoriesCallback)
        ]
    ];

            foreach (Domain.Entities.SignalCategory? category in allCategories)
            {
                bool isSelected = tempSelectedCategories.Contains(category.Id);
                string buttonText = $"{(isSelected ? "✅" : "⬜")} {TelegramMessageFormatter.EscapeMarkdownV2(category.Name)}";
                keyboardRows.Add([InlineKeyboardButton.WithCallbackData(buttonText, $"{ToggleSignalCategoryPrefix}{category.Id}")]);
            }

            keyboardRows.Add([InlineKeyboardButton.WithCallbackData("💾 Save Preferences", SaveSignalPreferencesCallback)]);
            keyboardRows.Add([InlineKeyboardButton.WithCallbackData("⬅️ Back to Settings", SettingsCommandHandler.ShowSettingsMenuCallback)]);

            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, text, new InlineKeyboardMarkup(keyboardRows), ParseMode.Markdown, cancellationToken);
        }
        /// <summary>
        /// Handles toggling the selection state of a signal category in the user's temporary preferences.
        /// </summary>
        /// <summary>
        /// Handles toggling the selection state of a single signal category in the user's temporary preferences.
        /// </summary>
        private async Task HandleToggleSignalCategoryAsync(long telegramUserId, long chatId, int messageIdToEdit, string callbackData, string originalCallbackQueryId, CancellationToken cancellationToken)
        {
            // --- 1. Fetch Fresh User Entity ---
            Domain.Entities.User? userEntity = await _userRepository.GetByTelegramIdAsync(telegramUserId.ToString(), cancellationToken);
            if (userEntity == null)
            {
                _logger.LogWarning("Cannot toggle signal category, user not found for Telegram ID {TelegramUserId}", telegramUserId);

                // ✅ FIX: Use named arguments for clarity and to resolve compiler error.
                await AnswerCallbackQuerySilentAsync(
                    callbackQueryId: originalCallbackQueryId,
                    cancellationToken: cancellationToken,
                    text: "Your user profile could not be found. Please try restarting with /start.",
                    showAlert: true
                );
                return;
            }

            // --- 2. Parse Category ID from Callback Data ---
            string categoryIdString = callbackData[ToggleSignalCategoryPrefix.Length..];
            if (!Guid.TryParse(categoryIdString, out Guid categoryId))
            {
                _logger.LogWarning("Invalid CategoryID in toggle callback: {CallbackData} for UserID {SystemUserId}.", callbackData, userEntity.Id);

                // ✅ FIX: Use named arguments for clarity and to resolve compiler error.
                await AnswerCallbackQuerySilentAsync(
                    callbackQueryId: originalCallbackQueryId,
                    cancellationToken: cancellationToken,
                    text: "Error: Invalid category selection.",
                    showAlert: true
                );
                return;
            }

            _logger.LogDebug("UserID {SystemUserId}: Toggling category preference for CategoryID {CategoryId}.", userEntity.Id, categoryId);

            // --- 3. Update Temporary Conversation State ---
            UserConversationState? conversationState = await _userConversationStateService.GetAsync(telegramUserId, cancellationToken);
            string tempSelectedCategoriesKey = $"temp_signal_prefs_{userEntity.Id}";

            if (conversationState?.StateData.TryGetValue(tempSelectedCategoriesKey, out object? selectedObj) == true && selectedObj is HashSet<Guid> tempSelectedCategories)
            {
                // Toggle the presence of the category ID in the temporary set.
                if (tempSelectedCategories.Contains(categoryId))
                {
                    _ = tempSelectedCategories.Remove(categoryId);
                    _logger.LogInformation("UserID {SystemUserId}: CategoryID {CategoryId} deselected (temporarily).", userEntity.Id, categoryId);
                }
                else
                {
                    _ = tempSelectedCategories.Add(categoryId);
                    _logger.LogInformation("UserID {SystemUserId}: CategoryID {CategoryId} selected (temporarily).", userEntity.Id, categoryId);
                }

                // Persist the updated temporary state.
                await _userConversationStateService.SetAsync(telegramUserId, conversationState, cancellationToken);

                // --- 4. Refresh the UI and Acknowledge the Action ---
                // The Show... method will re-fetch everything, guaranteeing the UI reflects the new temporary state.
                await ShowSignalCategoryPreferencesAsync(telegramUserId, chatId, messageIdToEdit, cancellationToken);

                // This call is correct as it only provides the required parameters.
                await AnswerCallbackQuerySilentAsync(originalCallbackQueryId, cancellationToken); // Acknowledge the button press silently.
            }
            else
            {
                // This case handles if the temporary state expired or was lost.
                _logger.LogError("User conversation state for signal preferences not found for UserID {SystemUserId} during toggle. Re-initializing preference view.", userEntity.Id);

                // Re-initialize the view, which will recreate the temporary state from the database.
                await ShowSignalCategoryPreferencesAsync(telegramUserId, chatId, messageIdToEdit, cancellationToken);

                // ✅ FIX: Use named arguments for clarity and to resolve compiler error.
                await AnswerCallbackQuerySilentAsync(
                    callbackQueryId: originalCallbackQueryId,
                    cancellationToken: cancellationToken,
                    text: "Session may have expired. Preferences reloaded. Please try again.",
                    showAlert: true
                );
            }
        }

        /// <summary>
        /// Handles the "Select All" action for signal category preferences by updating the temporary state.
        /// </summary>
        private async Task HandleSelectAllSignalCategoriesAsync(long telegramUserId, long chatId, int messageIdToEdit, string originalCallbackQueryId, CancellationToken cancellationToken)
        {
            // --- 1. Fetch Fresh User Entity ---
            // Although we only need the ID for the state key, fetching the entity ensures the user exists.
            Domain.Entities.User? userEntity = await _userRepository.GetByTelegramIdAsync(telegramUserId.ToString(), cancellationToken);
            if (userEntity == null)
            {
                _logger.LogWarning("Cannot select all categories, user not found for Telegram ID {TelegramUserId}", telegramUserId);
                await AnswerCallbackQuerySilentAsync(
                    callbackQueryId: originalCallbackQueryId,
                    cancellationToken: cancellationToken,
                    text: "Your user profile could not be found.",
                    showAlert: true
                );
                return;
            }

            _logger.LogInformation("UserID {SystemUserId}: Selecting all signal categories.", userEntity.Id);

            // --- 2. Get All Active Category IDs ---
            HashSet<Guid> allActiveCategoryIds = (await _categoryRepository.GetAllAsync(cancellationToken))
                                       .Where(c => c.IsActive)
                                       .Select(c => c.Id)
                                       .ToHashSet();

            // --- 3. Update Temporary Conversation State ---
            UserConversationState conversationState = await _userConversationStateService.GetAsync(telegramUserId, cancellationToken) ?? new UserConversationState();
            string tempSelectedCategoriesKey = $"temp_signal_prefs_{userEntity.Id}";
            conversationState.StateData[tempSelectedCategoriesKey] = allActiveCategoryIds;
            await _userConversationStateService.SetAsync(telegramUserId, conversationState, cancellationToken);

            // --- 4. Refresh the UI and Acknowledge the Action ---
            // ✅ FIX: Call the refresh method with the 'telegramUserId' (long) instead of the user entity.
            await ShowSignalCategoryPreferencesAsync(telegramUserId, chatId, messageIdToEdit, cancellationToken);

            // ✅ FIX: Use named arguments for clarity, although this call was likely already correct.
            await AnswerCallbackQuerySilentAsync(
                callbackQueryId: originalCallbackQueryId,
                cancellationToken: cancellationToken,
                text: "All categories selected."
            );
        }

        /// <summary>
        /// Handles the "Deselect All" action for signal category preferences by clearing the temporary state.
        /// </summary>
        private async Task HandleDeselectAllSignalCategoriesAsync(long telegramUserId, long chatId, int messageIdToEdit, string originalCallbackQueryId, CancellationToken cancellationToken)
        {
            // --- 1. Fetch Fresh User Entity ---
            // Fetching the entity ensures the user exists and we have their system ID for the state key.
            Domain.Entities.User? userEntity = await _userRepository.GetByTelegramIdAsync(telegramUserId.ToString(), cancellationToken);
            if (userEntity == null)
            {
                _logger.LogWarning("Cannot deselect all categories, user not found for Telegram ID {TelegramUserId}", telegramUserId);
                await AnswerCallbackQuerySilentAsync(
                    callbackQueryId: originalCallbackQueryId,
                    cancellationToken: cancellationToken,
                    text: "Your user profile could not be found.",
                    showAlert: true
                );
                return;
            }

            _logger.LogInformation("UserID {SystemUserId}: Deselecting all signal categories.", userEntity.Id);

            // --- 2. Update Temporary Conversation State ---
            UserConversationState conversationState = await _userConversationStateService.GetAsync(telegramUserId, cancellationToken) ?? new UserConversationState();
            string tempSelectedCategoriesKey = $"temp_signal_prefs_{userEntity.Id}";

            // Set the value to a new, empty HashSet to clear all selections.
            conversationState.StateData[tempSelectedCategoriesKey] = new HashSet<Guid>();

            await _userConversationStateService.SetAsync(telegramUserId, conversationState, cancellationToken);

            // --- 3. Refresh the UI and Acknowledge the Action ---
            // ✅ FIX: Call the refresh method with the 'telegramUserId' (long).
            await ShowSignalCategoryPreferencesAsync(telegramUserId, chatId, messageIdToEdit, cancellationToken);

            // ✅ FIX: Use named arguments for clarity.
            await AnswerCallbackQuerySilentAsync(
                callbackQueryId: originalCallbackQueryId,
                cancellationToken: cancellationToken,
                text: "All categories deselected."
            );
        }

        /// <summary>
        /// Saves the user's temporarily selected signal category preferences to the database.
        /// </summary>
        private async Task HandleSaveSignalPreferencesAsync(long telegramUserId, long chatId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            // ✅ FIX: The method now accepts 'telegramUserId' and fetches the User entity itself.
            Domain.Entities.User? userEntity = await _userRepository.GetByTelegramIdAsync(telegramUserId.ToString(), cancellationToken);
            if (userEntity == null)
            {
                _logger.LogWarning("Cannot save signal preferences, user not found for Telegram ID {TelegramUserId}", telegramUserId);
                await EditMessageOrSendNewAsync(chatId, messageIdToEdit, "Your user profile could not be found. Please use /start first.", null, cancellationToken: cancellationToken);
                return;
            }

            _logger.LogInformation("UserID {SystemUserId}: Attempting to save signal category preferences.", userEntity.Id);

            // ✅ FIX: Use 'telegramUserId' for conversation state service.
            UserConversationState? conversationState = await _userConversationStateService.GetAsync(telegramUserId, cancellationToken);
            string tempSelectedCategoriesKey = $"temp_signal_prefs_{userEntity.Id}";

            if (conversationState?.StateData.TryGetValue(tempSelectedCategoriesKey, out object? selectedObj) == true && selectedObj is HashSet<Guid> finalSelectedCategoryIds)
            {
                try
                {
                    // Use the repository to persist the preferences.
                    await _userPrefsRepository.SetUserPreferencesAsync(userEntity.Id, finalSelectedCategoryIds, cancellationToken);
                    _ = await _appDbContext.SaveChangesAsync(cancellationToken); // Commit the changes.

                    _logger.LogInformation("UserID {SystemUserId}: Signal preferences saved successfully. Count: {Count}",
                        userEntity.Id, finalSelectedCategoryIds.Count);

                    // Clean up the temporary state from conversation service.
                    _ = conversationState.StateData.Remove(tempSelectedCategoriesKey);
                    await _userConversationStateService.SetAsync(telegramUserId, conversationState, cancellationToken);

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
        /// This method is designed to be dual-purpose:
        /// 1. If a 'preFetchedUserEntity' is provided, it uses that entity to render the UI. This is crucial for providing immediate feedback after a setting change.
        /// 2. If no entity is provided, it fetches the latest version from the database, ensuring the user always sees the most up-to-date, persisted settings.
        /// </summary>
        /// <param name="preFetchedUserEntity">
        /// (Optional) A pre-fetched and potentially modified User entity. If provided, this method
        /// will use it instead of fetching from the database to guarantee UI consistency after a toggle action.
        /// </param>
        /// </param>
        private async Task ShowNotificationSettingsAsync(long telegramUserId, long chatId, int messageIdToEdit, CancellationToken cancellationToken, Domain.Entities.User? preFetchedUserEntity = null)
        {
            // **FIX 1: INTELLIGENT DATA SOURCING**
            // Use the passed-in entity if it exists; otherwise, fetch a fresh, complete copy from the database.
            Domain.Entities.User? userEntity = preFetchedUserEntity ?? await _userRepository.GetByTelegramIdWithNotificationsAsync(telegramUserId.ToString(), cancellationToken);

            if (userEntity == null)
            {
                _logger.LogWarning("Cannot show notification settings, user not found for Telegram ID {TelegramUserId}", telegramUserId);
                await EditMessageOrSendNewAsync(chatId, messageIdToEdit, "Your user profile could not be found. Please use /start first.", null, ParseMode.MarkdownV2, cancellationToken);
                return;
            }

            _logger.LogInformation("UserID {SystemUserId}: Displaying notification settings.", userEntity.Id);

            string text = TelegramMessageFormatter.Bold("🔔 Notification Settings", escapePlainText: false) + "\n\n" +
                       "Manage your notification preferences\\. Tap an option to toggle it \\(✅ Enabled / ⬜ Disabled\\):";

            // Check for an active subscription directly from the loaded entity's collection.
            bool isVipUser = userEntity.Subscriptions.Any(s => s.Status == "Active" && s.EndDate > DateTime.UtcNow);

            List<List<InlineKeyboardButton>> keyboardRows =
            [
                [
                    InlineKeyboardButton.WithCallbackData(
                        $"{(userEntity.EnableGeneralNotifications ? "✅" : "⬜")} General Bot Updates",
                        $"{ToggleNotificationPrefix}{NotificationTypeGeneral}"
                    )
                ],
                isVipUser ? ([
                    InlineKeyboardButton.WithCallbackData(
                        $"{(userEntity.EnableVipSignalNotifications ? "✅" : "⬜")} ✨ VIP Signal Alerts",
                        $"{ToggleNotificationPrefix}{NotificationTypeVipSignal}"
                    )
                ]) : ([
                    InlineKeyboardButton.WithCallbackData(
                        "💎 Enable VIP Signal Alerts (Upgrade Required)",
                        MenuCommandHandler.SubscribeCallbackData
                    )
                ]),
                [
                    InlineKeyboardButton.WithCallbackData(
                        $"{(userEntity.EnableRssNewsNotifications ? "✅" : "⬜")} RSS News Updates",
                        $"{ToggleNotificationPrefix}{NotificationTypeRssNews}"
                    )
                ],
                [
                    InlineKeyboardButton.WithCallbackData("⬅️ Back to Settings Menu", SettingsCommandHandler.ShowSettingsMenuCallback)
                ],
            ];

            InlineKeyboardMarkup finalKeyboard = new(keyboardRows);

            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, text, finalKeyboard, ParseMode.MarkdownV2, cancellationToken);
        }


private async Task HandleToggleNotificationAsync(long telegramUserId, long chatId, int messageIdToEdit, string callbackData, string originalCallbackQueryId, CancellationToken cancellationToken)
    {
        // Fetch the user with all their related data to ensure we have the full context.
        Domain.Entities.User? userEntity = await _userRepository.GetByTelegramIdWithNotificationsAsync(telegramUserId.ToString(), cancellationToken).ConfigureAwait(false);
        if (userEntity == null)
        {
            _logger.LogWarning("Cannot toggle notification, user not found for Telegram ID {TelegramUserId}", telegramUserId);
            await AnswerCallbackQuerySilentAsync(originalCallbackQueryId, cancellationToken, "Your user profile could not be found.", showAlert: true).ConfigureAwait(false);
            return;
        }

        string notificationType = callbackData[ToggleNotificationPrefix.Length..];
        _logger.LogInformation("UserID {SystemUserId}: Attempting to toggle setting '{NotificationType}'.", userEntity.Id, notificationType);

        // 1. PRE-COMPUTE VIP STATUS FOR CLEANER LOGIC
        // This makes the switch statement easier to read. The logic itself is preserved from your original code.
        bool isVipUser = userEntity.Subscriptions.Any(s => s.Status == "Active" && s.EndDate > DateTime.UtcNow);

        // This section modifies the entity in memory. We do this before the `try` block for saving.
        switch (notificationType)
        {
            case NotificationTypeGeneral:
                userEntity.EnableGeneralNotifications = !userEntity.EnableGeneralNotifications;
                break;
            case NotificationTypeVipSignal:
                if (!isVipUser)
                {
                    await AnswerCallbackQuerySilentAsync(originalCallbackQueryId, cancellationToken, "VIP subscription required to change this setting.", showAlert: true).ConfigureAwait(false);
                    // We still want to refresh the UI to ensure it's not left in a weird state.
                    await ShowNotificationSettingsAsync(telegramUserId, chatId, messageIdToEdit, cancellationToken, userEntity).ConfigureAwait(false);
                    return;
                }
                userEntity.EnableVipSignalNotifications = !userEntity.EnableVipSignalNotifications;
                break;
            case NotificationTypeRssNews:
                userEntity.EnableRssNewsNotifications = !userEntity.EnableRssNewsNotifications;
                break;
            default:
                _logger.LogWarning("Unknown notification type '{Type}' for user {UserId}", notificationType, userEntity.Id);
                await AnswerCallbackQuerySilentAsync(originalCallbackQueryId, cancellationToken, "Unknown setting.", showAlert: true).ConfigureAwait(false);
                return;
        }

        // Now, attempt to persist the changes and update the UI.
        try
        {
            userEntity.UpdatedAt = DateTime.UtcNow;

            // This is your original, safe logic. It works perfectly.
            await _userRepository.UpdateAsync(userEntity, cancellationToken).ConfigureAwait(false);

            // On success, construct the message and log it.
            bool newStatus = GetNotificationStatus(userEntity, notificationType);
            string statusMessage = GetStatusMessage(notificationType, newStatus);

            _logger.LogInformation("Notification '{Type}' for user {UserId} successfully set to {Status}", notificationType, userEntity.Id, newStatus);

            // Answer the callback to give the user immediate feedback.
            await AnswerCallbackQuerySilentAsync(originalCallbackQueryId, cancellationToken, statusMessage).ConfigureAwait(false);
        }
        // 2. MORE SPECIFIC ERROR HANDLING
        catch (DbUpdateException dbEx) // Catch database-specific errors first
        {
            _logger.LogError(dbEx, "Database error while updating notification settings for UserID {SystemUserId}.", userEntity.Id);
            await AnswerCallbackQuerySilentAsync(originalCallbackQueryId, cancellationToken, "Error: Could not save setting due to a database issue.", showAlert: true).ConfigureAwait(false);
        }
        catch (Exception ex) // Catch any other unexpected errors
        {
            _logger.LogError(ex, "Failed to save notification settings for UserID {SystemUserId}.", userEntity.Id);
            await AnswerCallbackQuerySilentAsync(originalCallbackQueryId, cancellationToken, "Error saving your setting. Please try again.", showAlert: true).ConfigureAwait(false);
        }
        finally
        {
            // 3. GUARANTEED UI CONSISTENCY
            // This block executes whether the `try` succeeded or failed. This ensures that the user's
            // menu is ALWAYS refreshed, preventing a stale UI. If the save failed, the menu will
            // revert to its original state visually, which is the correct behavior.
            await ShowNotificationSettingsAsync(telegramUserId, chatId, messageIdToEdit, cancellationToken, userEntity).ConfigureAwait(false);
        }
    }

    // Helper to get the status without repeating the switch statement
    private bool GetNotificationStatus(Domain.Entities.User user, string notificationType) => notificationType switch
    {
        NotificationTypeGeneral => user.EnableGeneralNotifications,
        NotificationTypeVipSignal => user.EnableVipSignalNotifications,
        NotificationTypeRssNews => user.EnableRssNewsNotifications,
        _ => false
    };

    // Helper to get the message without repeating the switch statement
    private string GetStatusMessage(string notificationType, bool newStatus)
    {
        string subject = notificationType switch
        {
            NotificationTypeGeneral => "General Updates",
            NotificationTypeVipSignal => "VIP Signal Alerts",
            NotificationTypeRssNews => "RSS News Updates",
            _ => "Unknown Setting"
        };
        return $"{subject} are now {(newStatus ? "ENABLED" : "DISABLED")}.";
    }

    #endregion

    #region Subscription Info Methods
    /// <summary>
    /// Displays the user's current subscription status and provides options to view/manage plans.
    /// </summary>
    // File: TelegramPanel/Application/CommandHandlers/SettingsCallbackQueryHandler.cs
    // در متد ShowMySubscriptionInfoAsync:

    private async Task ShowMySubscriptionInfoAsync(long telegramUserId, long chatId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            // ✅ FIX: The method now accepts 'telegramUserId' and fetches the User entity itself.
            Domain.Entities.User? userEntity = await _userRepository.GetByTelegramIdAsync(telegramUserId.ToString(), cancellationToken);
            if (userEntity == null)
            {
                _logger.LogWarning("Cannot show subscription info, user not found for Telegram ID {TelegramUserId}", telegramUserId);
                await EditMessageOrSendNewAsync(chatId, messageIdToEdit, "Your user profile could not be found. Please use /start first.", null, ParseMode.MarkdownV2, cancellationToken);
                return;
            }

            _logger.LogInformation("UserID {SystemUserId}: Showing subscription information.", userEntity.Id);

            // This call is correct, as it uses the Guid from the fetched entity.
            UserDto? userDto = await _userService.GetUserByIdAsync(userEntity.Id, cancellationToken);
            if (userDto == null)
            {
                await EditMessageOrSendNewAsync(chatId, messageIdToEdit, "Could not retrieve your user profile. Please use /start first.", null, ParseMode.MarkdownV2, cancellationToken);
                return;
            }

            StringBuilder sb = new();
            _ = sb.AppendLine(TelegramMessageFormatter.Bold("⭐ My Subscription Status", escapePlainText: false));
            _ = sb.AppendLine();

            if (userDto.ActiveSubscription != null)
            {
                string planNameForDisplay = "Your Current Plan"; // Replace with real plan name
                _ = sb.AppendLine($"You are currently subscribed to the {TelegramMessageFormatter.Bold(planNameForDisplay, escapePlainText: true)}.");
                _ = sb.AppendLine($"Your subscription is active until: {TelegramMessageFormatter.Bold($"{userDto.ActiveSubscription.EndDate:yyyy-MM-dd HH:mm} UTC")}");
                _ = sb.AppendLine("\nThank you for your support! You have access to all premium features.");
            }
            else
            {
                _ = sb.AppendLine("You currently do not have an active subscription.");
                _ = sb.AppendLine("Upgrade to a premium plan to unlock exclusive signals, advanced analytics, and more benefits!");
            }

            List<List<InlineKeyboardButton>> keyboardRowList =
            [
        [
            InlineKeyboardButton.WithCallbackData(
                userDto.ActiveSubscription != null ? "🔄 Manage / Renew Subscription" : "💎 View Subscription Plans",
                MenuCommandHandler.SubscribeCallbackData
            )
        ],
        [
            InlineKeyboardButton.WithCallbackData("⬅️ Back to Settings Menu", SettingsCommandHandler.ShowSettingsMenuCallback)
        ]
    ];

            InlineKeyboardMarkup finalKeyboard = new(keyboardRowList);

            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, sb.ToString(), finalKeyboard, ParseMode.Markdown, cancellationToken);
        }
        #endregion

        #region Language & Privacy Settings Methods (Placeholders - To be fully implemented)
        /// <summary>
        /// Displays language selection options to the user.
        /// </summary>
        /// <summary>
        /// Displays language selection options to the user.
        /// </summary>
        private async Task ShowLanguageSettingsAsync(long telegramUserId, long chatId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            // ✅ FIX: The method now accepts 'telegramUserId' and fetches the User entity itself.
            Domain.Entities.User? userEntity = await _userRepository.GetByTelegramIdAsync(telegramUserId.ToString(), cancellationToken);
            if (userEntity == null)
            {
                _logger.LogWarning("Cannot show language settings, user not found for Telegram ID {TelegramUserId}", telegramUserId);
                await EditMessageOrSendNewAsync(chatId, messageIdToEdit, "Your user profile could not be found. Please use /start first.", null, cancellationToken: cancellationToken);
                return;
            }

            _logger.LogInformation("UserID {SystemUserId}: Displaying language settings. Current language: {CurrentLang}", userEntity.Id, userEntity.PreferredLanguage);

            string text = TelegramMessageFormatter.Bold("🌐 Language Settings", escapePlainText: false) + "\n\n" +
                       $"Your current language is: {TelegramMessageFormatter.Bold(userEntity.PreferredLanguage.ToUpperInvariant())}\n" +
                       "Select your preferred language for the bot interface:";

            // Build the keyboard dynamically
            InlineKeyboardMarkup? keyboard = MarkupBuilder.CreateInlineKeyboard(
                new[]
                {
            InlineKeyboardButton.WithCallbackData(
                $"{(userEntity.PreferredLanguage == "en" ? "🔹 " : "")}🇬🇧 English",
                $"{SelectLanguagePrefix}en"
            )
                },
                new[]
                {
            InlineKeyboardButton.WithCallbackData(
                "⬅️ Back to Settings Menu",
                SettingsCommandHandler.ShowSettingsMenuCallback
            )
                }
            );

            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, text, keyboard, ParseMode.MarkdownV2, cancellationToken);
        }

        /// <summary>
        /// Handles the user's language selection and updates it in the database.
        /// </summary>
        /// <summary>
        /// Handles the user's language selection and updates it in the database.
        /// </summary>
        private async Task HandleSelectLanguageAsync(long telegramUserId, long chatId, int messageIdToEdit, string callbackData, string originalCallbackQueryId, CancellationToken cancellationToken)
        {
            Domain.Entities.User? userEntity = await _userRepository.GetByTelegramIdAsync(telegramUserId.ToString(), cancellationToken);
            if (userEntity == null) { /* ... handle not found ... */ return; }

            string langCode = callbackData[SelectLanguagePrefix.Length..].ToLowerInvariant();

            List<string> supportedLanguages = ["en"];
            if (!supportedLanguages.Contains(langCode))
            {
                // ✅ FIX: Use named arguments
                await AnswerCallbackQuerySilentAsync(
                    callbackQueryId: originalCallbackQueryId,
                    cancellationToken: cancellationToken,
                    text: "Selected language is not supported.",
                    showAlert: true);
                await ShowLanguageSettingsAsync(telegramUserId, chatId, messageIdToEdit, cancellationToken);
                return;
            }

            if (userEntity.PreferredLanguage == langCode)
            {
                // ✅ FIX: Use named arguments
                await AnswerCallbackQuerySilentAsync(
                    callbackQueryId: originalCallbackQueryId,
                    cancellationToken: cancellationToken,
                    text: $"Language is already set to {langCode.ToUpperInvariant()}.");
                await ShowLanguageSettingsAsync(telegramUserId, chatId, messageIdToEdit, cancellationToken);
                return;
            }

            userEntity.PreferredLanguage = langCode;
            userEntity.UpdatedAt = DateTime.UtcNow;

            try
            {
                _ = await _appDbContext.SaveChangesAsync(cancellationToken);

                await AnswerCallbackQuerySilentAsync(
                    callbackQueryId: originalCallbackQueryId,
                    cancellationToken: cancellationToken,
                    text: $"Language preferences updated to {langCode.ToUpperInvariant()}.");

                await ShowLanguageSettingsAsync(telegramUserId, chatId, messageIdToEdit, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save preferred language '{LangCode}' for UserID {SystemUserId}.", langCode, userEntity.Id);
                // ✅ FIX: Use named arguments
                await AnswerCallbackQuerySilentAsync(
                    callbackQueryId: originalCallbackQueryId,
                    cancellationToken: cancellationToken,
                    text: "Error saving language preference. Please try again.",
                    showAlert: true);
                await ShowLanguageSettingsAsync(telegramUserId, chatId, messageIdToEdit, cancellationToken);
            }
        }





        /// <summary>
        /// Displays privacy-related information and options.
        /// </summary>
        private async Task ShowPrivacySettingsAsync(long telegramUserId, long chatId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            // ✅ FIX: The method now accepts 'telegramUserId' and fetches the User entity itself.
            Domain.Entities.User? userEntity = await _userRepository.GetByTelegramIdAsync(telegramUserId.ToString(), cancellationToken);
            if (userEntity == null)
            {
                _logger.LogWarning("Cannot show privacy settings, user not found for Telegram ID {TelegramUserId}", telegramUserId);
                await EditMessageOrSendNewAsync(chatId, messageIdToEdit, "Your user profile could not be found. Please use /start first.", null, cancellationToken: cancellationToken);
                return;
            }

            _logger.LogInformation("UserID {SystemUserId} (TelegramID: {TelegramId}): Displaying privacy settings and policy information.", userEntity.Id, userEntity.TelegramId);

            // TODO: Replace with your actual Privacy Policy URL and Support Contact information.
            string privacyPolicyUrl = "https://your-forex-bot-domain.com/privacy-policy";
            string supportContactCommand = "/support";
            string supportEmail = "support@your-forex-bot-domain.com";

            // Constructing the message text using TelegramMessageFormatter for consistent styling.
            StringBuilder textBuilder = new();
            _ = textBuilder.AppendLine(TelegramMessageFormatter.Bold("🔒 Privacy & Data Management", escapePlainText: false));
            _ = textBuilder.AppendLine(); // Blank line for readability
            _ = textBuilder.AppendLine("We are committed to protecting your privacy and handling your data responsibly.");
            _ = textBuilder.AppendLine("You can review our full privacy policy to understand how we collect, use, and protect your information.");
            _ = textBuilder.AppendLine();
            _ = textBuilder.AppendLine(TelegramMessageFormatter.Link("📜 Read our Full Privacy Policy", privacyPolicyUrl, escapeLinkText: false));
            _ = textBuilder.AppendLine();
            _ = textBuilder.AppendLine(TelegramMessageFormatter.Bold("Data Requests:", escapePlainText: false));
            _ = textBuilder.AppendLine("If you wish to request access to your data, or request data deletion, please contact our support team.");
            _ = textBuilder.AppendLine($"You can reach us via the {TelegramMessageFormatter.Code(supportContactCommand)} command or by emailing us at {TelegramMessageFormatter.Link(supportEmail, $"mailto:{supportEmail}", escapeLinkText: false)}.");
            _ = textBuilder.AppendLine();
            _ = textBuilder.AppendLine(TelegramMessageFormatter.Italic("Note: Data deletion requests will be processed according to our data retention policy and applicable regulations.", escapePlainText: true));

            // Constructing the inline keyboard
            List<List<InlineKeyboardButton>> keyboardRows =
            [
        // First row: Link to the privacy policy
        [
            InlineKeyboardButton.WithUrl("📜 View Privacy Policy Online", privacyPolicyUrl)
        ],
        // Last row: Back to the main settings menu
        [
            InlineKeyboardButton.WithCallbackData("⬅️ Back to Settings Menu", SettingsCommandHandler.ShowSettingsMenuCallback)
        ]
    ];

            InlineKeyboardMarkup finalKeyboard = new(keyboardRows);

            await EditMessageOrSendNewAsync(chatId, messageIdToEdit, textBuilder.ToString(), finalKeyboard, ParseMode.Markdown, cancellationToken);
        }
        #endregion

        #region Menu Display & Helper Methods
        /// <summary>
        /// Re-displays the main settings menu by editing the previous message.
        /// This version includes robust validation and error handling.
        /// </summary>
        private async Task ReshowSettingsMenuAsync(long chatId, int messageIdToEdit, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Attempting to reshow main settings menu for ChatID {ChatId}, MessageID {MessageIdToEdit}", chatId, messageIdToEdit);

            try
            {
                // Uses the static method from SettingsCommandHandler to get consistent menu markup
                (string settingsMenuText, InlineKeyboardMarkup settingsKeyboard) = SettingsCommandHandler.GetSettingsMenuMarkup();

                // 1. VALIDATION: Ensure the generated markup is valid before attempting to use it.
                // This prevents passing null or invalid data to the Telegram API.
                if (string.IsNullOrWhiteSpace(settingsMenuText) || settingsKeyboard is null)
                {
                    // This is a critical developer error. The menu content generator failed.
                    _logger.LogCritical(
                        "Failed to generate settings menu markup in SettingsCommandHandler.GetSettingsMenuMarkup(). " +
                        "Text or Keyboard was null/empty. Cannot reshow menu for ChatID {ChatId}.",
                        chatId);
                    // We exit gracefully without crashing the handler.
                    // Consider answering the callback query with an error message here if this becomes a recurring issue.
                    return;
                }

                // 2. API CALL: The call is now safely inside the try block.
                await EditMessageOrSendNewAsync(
                    chatId,
                    messageIdToEdit,
                    settingsMenuText,
                    settingsKeyboard,
                    ParseMode.MarkdownV2,
                    cancellationToken
                ).ConfigureAwait(false);

                _logger.LogInformation("Successfully reshown settings menu for ChatID {ChatId}", chatId);
            }
            catch (Exception ex)
            {
                // 3. EXCEPTION HANDLING: Catch any exception during the process.
                // This could be an ApiRequestException from Telegram (e.g., message not found, bot blocked)
                // or any other runtime error. This ensures the handler does not crash.
                _logger.LogError(
                    ex,
                    "An unexpected error occurred while trying to reshow the settings menu for ChatID {ChatId} and MessageID {MessageIdToEdit}.",
                    chatId,
                    messageIdToEdit);

                // Although the operation failed, the calling method will still proceed to answer the callback query,
                // preventing the user from seeing a frozen "loading" state on the button.
            }
        }
        /// <summary>
        /// Answers a callback query, either silently or with a toast/alert notification.
        /// This method includes enhanced, specific error handling for common Telegram API exceptions.
        /// </summary>
        /// <param name="callbackQueryId">The unique identifier for the callback query to be answered.</param>
        /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
        /// <param name="text">The text to show in the notification. Can be null for a silent answer.</param>
        /// <param name="showAlert">If true, an alert will be shown to the user instead of a toast notification.</param>
        private async Task AnswerCallbackQuerySilentAsync(string callbackQueryId, CancellationToken cancellationToken, string? text = null, bool showAlert = false)
        {
            try
            {
                await _botClient.AnswerCallbackQuery(
                    callbackQueryId: callbackQueryId,
                    text: text,
                    showAlert: showAlert,
                    cancellationToken: cancellationToken
                ).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    _logger.LogDebug("Answered CallbackQueryID: {CallbackQueryId} with text: '{Text}', ShowAlert: {ShowAlert}", callbackQueryId, text, showAlert);
                }
                else
                {
                    _logger.LogDebug("Answered CallbackQueryID: {CallbackQueryId} silently.", callbackQueryId);
                }
            }
            // Specific Catch: The query is too old or has already been answered. This is a common, non-critical scenario.
            catch (ApiRequestException apiEx) when (apiEx.ErrorCode == 400 && (apiEx.Message.Contains("query is too old", StringComparison.OrdinalIgnoreCase) || apiEx.Message.Contains("QUERY_ID_INVALID", StringComparison.OrdinalIgnoreCase)))
            {
                // This is a common occurrence if the user double-clicks, or the network is slow causing a delayed response.
                // We log this as a warning because it's expected behavior and not a critical bot failure.
                _logger.LogWarning(
                    "Attempted to answer callback query {CallbackQueryId}, but it was too old, invalid, or already answered. This is often a non-critical issue. Telegram API Error: {ApiErrorMessage}",
                    callbackQueryId,
                    apiEx.Message);
            }
            // Specific Catch: Being rate-limited by Telegram (Error 429).
            catch (ApiRequestException apiEx) when (apiEx.ErrorCode == 429)
            {
                // This is a critical warning. It indicates the bot is sending too many requests and is being throttled.
                // The `Retry-After` header (if present) gives a hint on how long to wait.
                var retryAfter = apiEx.Parameters?.RetryAfter;
                _logger.LogWarning(
                    apiEx,
                    "Rate limit hit (429 Too Many Requests) while answering callback query {CallbackQueryId}. Telegram suggests waiting for {RetryAfter} seconds. Consider adjusting bot's request frequency. Telegram API Error: {ApiErrorMessage}",
                    callbackQueryId,
                    retryAfter.HasValue ? retryAfter.Value.ToString() : "an unspecified duration",
                    apiEx.Message);
                // Optional: For a resilient system, you could add `await Task.Delay(TimeSpan.FromSeconds(retryAfter ?? 5), cancellationToken)`
                // here if a retry mechanism is implemented for this specific action.
            }
            // Specific Catch: The text for the toast notification is invalid (e.g., malformed HTML/Markdown).
            catch (ApiRequestException apiEx) when (apiEx.ErrorCode == 400 && apiEx.Message.Contains("MESSAGE_TEXT_INVALID", StringComparison.OrdinalIgnoreCase))
            {
                // This indicates a developer error in the text being sent. It's crucial to log the problematic text.
                _logger.LogError(
                    apiEx,
                    "Invalid message text format detected while answering callback query {CallbackQueryId}. The text was likely malformed. Text sent: '{Text}'. Telegram API Error: {ApiErrorMessage}",
                    callbackQueryId,
                    text, // Log the problematic text for easy debugging.
                    apiEx.Message);
                // A potential mitigation could be to retry with a generic, safe message, but logging the developer error is the primary action.
            }
            // General Catch for other Telegram API errors.
            catch (ApiRequestException apiEx)
            {
                // This catches any other API-related errors that weren't handled by the specific cases above.
                // Logging the ErrorCode and full message is essential for diagnosing new or unexpected API issues.
                _logger.LogError(
                    apiEx,
                    "An unhandled Telegram API error occurred while answering callback query {CallbackQueryId}. Error Code: {ErrorCode}. Telegram API Error: {ApiErrorMessage}",
                    callbackQueryId,
                    apiEx.ErrorCode,
                    apiEx.Message);
            }
            // Catch-all for any other non-API exceptions (e.g., network issues, null references, TaskCanceledException).
            catch (Exception ex)
            {
                // This is the final safety net. It could be a network failure, a bug in our code, or a cancellation.
                // We log this as critical because it represents an unexpected failure in the application's code or its environment.
                _logger.LogCritical(
                    ex,
                    "A critical, non-API error occurred while trying to answer CallbackQueryID {CallbackQueryId}. Text sent: '{Text}'",
                    callbackQueryId,
                    text);
            }
        }

        /// <summary>
        /// Intelligently edits a message, providing a robust and clean user experience.
        /// This method distinguishes between different types of Telegram API failures:
        /// <list type="bullet">
        ///     <item>
        ///         <term>Message Not Modified</term>
        ///         <description>
        ///             If the message content and keyboard are already identical to the new content,
        ///             the operation is considered a "silent success" and is gracefully ignored to prevent chat clutter.
        ///         </description>
        ///     </item>
        ///     <item>
        ///         <term>Message Not Found / Too Old</term>
        ///         <description>
        ///             If the original message is too old to be edited or has been deleted, this method
        ///             will send a brand new message with the intended content as a fallback.
        ///         </description>
        ///     </item>
        ///     <item>
        ///         <term>Chat Not Found / User Blocked</term>
        ///         <description>
        ///             If the bot is blocked by the user, the operation fails silently without throwing an exception,
        ///             as no further communication is possible.
        ///         </description>
        ///     </item>
        ///     <item>
        ///         <term>Other Unexpected Errors</term>
        ///         <description>
        ///             For any other exceptions, an error is logged, and the method will not throw,
        ///             preventing crashes in the calling handler.
        ///         </description>
        ///     </item>
        /// </list>
        /// </summary>
        /// <param name="chatId">The ID of the chat where the message exists.</param>
        /// <param name="messageId">The ID of the message to edit.</param>
        /// <param name="text">The new text content for the message.</param>
        /// <param name="replyMarkup">The new inline keyboard markup.</param>
        /// <param name="parseMode">The parse mode for the message text (e.g., MarkdownV2).</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <remarks>
        /// It is highly recommended to call <c>AnswerCallbackQueryAsync</c> *before* calling this method
        /// to provide immediate feedback to the user and remove the loading spinner from the clicked button.
        /// </remarks>
        private async Task EditMessageOrSendNewAsync(
   long chatId,
   int messageId,
   string text,
   InlineKeyboardMarkup? replyMarkup,
   ParseMode parseMode = ParseMode.None, // Changed from nullable ParseMode? to non-nullable ParseMode with a default value  
   CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug("Attempting to edit message. ChatID: {ChatId}, MessageID: {MessageId}", chatId, messageId);

                // We use a discard '_' because the return value (the edited Message) is not needed here.  
                _ = await _botClient.EditMessageText(
                    chatId: chatId,
                    messageId: messageId,
                    text: text,
                    parseMode: parseMode, // FIX: Use the provided parseMode parameter  
                    replyMarkup: replyMarkup,
                    cancellationToken: cancellationToken
                );

                _logger.LogInformation("Message edited successfully. ChatID: {ChatId}, MessageID: {MessageId}", chatId, messageId);
            }
            catch (ApiRequestException apiEx) when (apiEx.ErrorCode == 400 && apiEx.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
            {
                // --- GRACEFUL IGNORE ---  
                // This is not an error. It's a confirmation from Telegram that the content is already  
                // what we want it to be. We log it for debugging but take no further action.  
                _logger.LogInformation(
                    "Ignoring 'message is not modified' API response for MessageID {MessageId}. The content was already up-to-date.",
                    messageId);
            }
            catch (ApiRequestException apiEx) when (apiEx.ErrorCode == 400 && (
                apiEx.Message.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase) ||
                apiEx.Message.Contains("query is too old", StringComparison.OrdinalIgnoreCase)
            ))
            {
                // --- RESILIENT FALLBACK ---  
                // The original message is gone or too old. Send a new one instead.  
                _logger.LogWarning(apiEx,
                    "Could not edit message (MessageID: {MessageId}) because it was not found or too old. Sending a new message as a fallback.",
                    messageId);

                await _messageSender.SendTextMessageAsync(chatId, text, parseMode, replyMarkup, cancellationToken);
            }
            catch (ApiRequestException apiEx) when (apiEx.ErrorCode == 403 || apiEx.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase))
            {
                // --- SILENT FAILURE (Permanent) ---  
                // The user has blocked the bot, or the bot was kicked from the chat.  
                // There is nothing we can do, so we log it and stop. We don't re-throw.  
                _logger.LogWarning(apiEx,
                    "Could not edit or send message to ChatID {ChatId} because the bot was blocked or the chat was not found. This is a permanent failure for this user.",
                    chatId);
            }
            catch (Exception ex)
            {
                // --- UNEXPECTED ERROR ---  
                // Catch-all for other issues (e.g., network problems not caught by Polly, other API errors).  
                // Log as a critical error for investigation, but don't crash the command handler.  
                _logger.LogError(ex,
                    "An unexpected error occurred while attempting to edit message (MessageID: {MessageId}, ChatID: {ChatId}). No fallback message will be sent.",
                    messageId, chatId);
                // We deliberately do not send a new message here, as the nature of the error is unknown.  
            }
        }


        #endregion
    }
}