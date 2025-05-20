using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Application.States; // برای IUserConversationStateService
using TelegramPanel.Infrastructure;   // برای ITelegramMessageSender

namespace TelegramPanel.Application.Services // ✅ Namespace صحیح
{
    /// <summary>
    /// Manages the conversation state değişiklikleri for Telegram users.
    /// It orchestrates transitions between different <see cref="ITelegramState"/> implementations.
    /// </summary>
    public class TelegramStateMachine : ITelegramStateMachine
    {
        #region Fields

        private readonly IUserConversationStateService _stateService;
        private readonly IServiceProvider _serviceProvider; // Used to resolve ITelegramState instances
        private readonly ITelegramMessageSender _messageSender;
        private readonly ILogger<TelegramStateMachine> _logger;
        private readonly IEnumerable<ITelegramState> _availableStates; // All registered ITelegramState implementations

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="TelegramStateMachine"/> class.
        /// </summary>
        /// <param name="stateService">Service for managing user conversation state persistence.</param>
        /// <param name="serviceProvider">Service provider for resolving dependencies, specifically ITelegramState instances.</param>
        /// <param name="messageSender">Service for sending messages to Telegram.</param>
        /// <param name="logger">Logger for logging state machine activities.</param>
        /// <param name="availableStates">An enumerable collection of all available ITelegramState implementations, injected by DI.</param>
        /// <exception cref="ArgumentNullException">Thrown if any of the constructor arguments are null.</exception>
        public TelegramStateMachine(
            IUserConversationStateService stateService,
            IServiceProvider serviceProvider,
            ITelegramMessageSender messageSender,
            ILogger<TelegramStateMachine> logger,
            IEnumerable<ITelegramState> availableStates) // Injection of all ITelegramState implementations
        {
            _stateService = stateService ?? throw new ArgumentNullException(nameof(stateService));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider)); // ServiceProvider can be used to dynamically resolve states if they are not all injected directly.
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _availableStates = availableStates ?? throw new ArgumentNullException(nameof(availableStates)); // All states are expected to be registered and injected.
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Retrieves the current <see cref="ITelegramState"/> for a given user.
        /// </summary>
        /// <param name="userId">The ID of the user.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>The current <see cref="ITelegramState"/> if set; otherwise, null.</returns>
        public async Task<ITelegramState?> GetCurrentStateAsync(long userId, CancellationToken cancellationToken = default)
        {
            var userConvState = await _stateService.GetAsync(userId, cancellationToken);
            if (userConvState == null || string.IsNullOrWhiteSpace(userConvState.CurrentStateName))
            {
                // No state is currently set for the user.
                return null;
            }

            // Find the ITelegramState implementation based on the stored state name.
            return _availableStates.FirstOrDefault(s => s.Name.Equals(userConvState.CurrentStateName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Sets the conversation state for a given user.
        /// If a state name is provided, it transitions the user to that state.
        /// If stateName is null or whitespace, it clears the user's current state.
        /// </summary>
        /// <param name="userId">The ID of the user.</param>
        /// <param name="stateName">The name of the state to transition to, or null to clear the state.</param>
        /// <param name="triggerUpdate">The Telegram update that triggered this state change, used to send entry messages.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        public async Task SetStateAsync(long userId, string? stateName, Update? triggerUpdate = null, CancellationToken cancellationToken = default)
        {
            // Retrieve the current conversation state or create a new one if it doesn't exist.
            var userConvState = await _stateService.GetAsync(userId, cancellationToken) ?? new UserConversationState();

            if (string.IsNullOrWhiteSpace(stateName))
            {
                _logger.LogInformation("Clearing state for UserID {UserId}", userId);
                userConvState.CurrentStateName = null;
                userConvState.StateData.Clear(); // Clear any data associated with the previous state.
            }
            else
            {
                // Find the new state implementation from the available states.
                var newState = _availableStates.FirstOrDefault(s => s.Name.Equals(stateName, StringComparison.OrdinalIgnoreCase));
                if (newState == null)
                {
                    _logger.LogError("Attempted to set unknown state '{StateName}' for UserID {UserId}", stateName, userId);
                    // If an unknown state is requested, clear the current state and notify the user.
                    // This prevents the conversation from getting stuck in an invalid state.
                    await ClearStateAsync(userId, cancellationToken); // Clear the invalid state attempt.
                    var chatId = triggerUpdate?.Message?.Chat?.Id ?? triggerUpdate?.CallbackQuery?.Message?.Chat?.Id;
                    if (chatId.HasValue)
                    {
                        // Inform the user about the internal error.
                        await _messageSender.SendTextMessageAsync(chatId.Value, "An internal error occurred with conversation flow. Please try again.", cancellationToken: cancellationToken);
                    }
                    return; // Exit early as the state transition failed.
                }

                _logger.LogInformation("Setting state for UserID {UserId} to {StateName}", userId, stateName);
                userConvState.CurrentStateName = stateName;
                userConvState.StateData.Clear(); // Clear data from the previous state when entering a new one.

                // Send the entry message for the new state, if any.
                var entryMessage = await newState.GetEntryMessageAsync(userId, triggerUpdate, cancellationToken);
                var chatIdForEntry = triggerUpdate?.Message?.Chat?.Id ?? triggerUpdate?.CallbackQuery?.Message?.Chat?.Id;

                if (!string.IsNullOrWhiteSpace(entryMessage) && chatIdForEntry.HasValue)
                {
                    await _messageSender.SendTextMessageAsync(chatIdForEntry.Value, entryMessage, cancellationToken: cancellationToken);
                }
            }
            // Persist the updated conversation state.
            await _stateService.SetAsync(userId, userConvState, cancellationToken);
        }

        /// <summary>
        /// Processes a Telegram update within the user's current conversation state.
        /// </summary>
        /// <param name="userId">The ID of the user.</param>
        /// <param name="update">The Telegram update to process.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        public async Task ProcessUpdateInCurrentStateAsync(long userId, Update update, CancellationToken cancellationToken = default)
        {
            var currentState = await GetCurrentStateAsync(userId, cancellationToken);
            if (currentState == null)
            {
                _logger.LogWarning("ProcessUpdateInCurrentStateAsync called for UserID {UserId} but no current state found.", userId);
                // This scenario should ideally be handled before reaching here,
                // e.g., by routing to a default handler or state if no state is set.
                return;
            }

            _logger.LogDebug("Processing update for UserID {UserId} in state {StateName}", userId, currentState.Name);
            // Delegate processing to the current state and get the name of the next state.
            var nextStateName = await currentState.ProcessUpdateAsync(update, cancellationToken);

            // If the state has changed (or cleared, indicated by null nextStateName),
            // update the state using SetStateAsync.
            if (nextStateName != currentState.Name) // Covers transitions to a new state or to no state (null).
            {
                // Pass the original update as triggerUpdate to allow the new state's entry message to be sent.
                await SetStateAsync(userId, nextStateName, update, cancellationToken);
            }
        }

        /// <summary>
        /// Clears the conversation state for a given user.
        /// </summary>
        /// <param name="userId">The ID of the user whose state should be cleared.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        public async Task ClearStateAsync(long userId, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Clearing state for UserID {UserId}", userId);
            await _stateService.ClearAsync(userId, cancellationToken);
        }

        #endregion
    }
}