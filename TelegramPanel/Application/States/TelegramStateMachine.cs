// FILE TO EDIT: TelegramPanel/Application/Services/TelegramStateMachine.cs

using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using TelegramPanel.Application.Interfaces;
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions;

namespace TelegramPanel.Application.States
{
    public class TelegramStateMachine : ITelegramStateMachine
    {
        /// <summary>
        /// Service for managing user conversation states in a persistent storage.
        /// </summary>
        private readonly IUserConversationStateService _stateService;

        /// <summary>
        /// Collection of all available states that can be transitioned to.
        /// </summary>
        private readonly IEnumerable<ITelegramState> _availableStates;

        /// <summary>
        /// Service for sending messages to Telegram users, used for sending entry messages when transitioning states.
        /// </summary>
        private readonly ITelegramMessageSender _messageSender;

        /// <summary>
        /// Logger for logging state transitions and errors within the state machine.
        /// </summary>
        private readonly ILogger<TelegramStateMachine> _logger;


        /// <summary>
        /// Initializes a new instance of the <see cref="TelegramStateMachine"/> class.
        /// </summary>
        /// <param name="stateService"></param>
        /// <param name="availableStates"></param>
        /// <param name="messageSender"></param>
        /// <param name="logger"></param>
        public TelegramStateMachine(
            IUserConversationStateService stateService,
            IEnumerable<ITelegramState> availableStates, // DI will provide all registered states here
            ITelegramMessageSender messageSender,
            ILogger<TelegramStateMachine> logger)
        {
            _stateService = stateService;
            _availableStates = availableStates;
            _messageSender = messageSender;
            _logger = logger;
        }


        /// <summary>
        /// Retrieves the current state for a user based on their user ID.
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<ITelegramState?> GetCurrentStateAsync(long userId, CancellationToken cancellationToken = default)
        {
            UserConversationState? userConvState = await _stateService.GetAsync(userId, cancellationToken);
            return userConvState == null || string.IsNullOrWhiteSpace(userConvState.CurrentStateName)
                ? null
                : _availableStates.FirstOrDefault(s => s.Name.Equals(userConvState.CurrentStateName, StringComparison.OrdinalIgnoreCase));
        }

        public async Task ProcessUpdateInCurrentStateAsync(long userId, Update update, CancellationToken cancellationToken = default)
        {
            ITelegramState? currentState = await GetCurrentStateAsync(userId, cancellationToken);
            if (currentState != null)
            {
                _logger.LogDebug("Processing update for UserID {UserId} in state {StateName}", userId, currentState.Name);
                string? nextStateName = await currentState.ProcessUpdateAsync(update, cancellationToken);

                // If the state changes (i.e., returns a new state name or null)
                if (nextStateName != currentState.Name)
                {
                    // This will now correctly call the simplified SetStateAsync
                    await SetStateAsync(userId, nextStateName, update, cancellationToken);
                }
            }
        }

        /// <summary>
        /// Sets the current state for a user, clearing any existing state data and sending an entry message if applicable.
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="stateName"></param>
        /// <param name="triggerUpdate"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task SetStateAsync(long userId, string? stateName, Update? triggerUpdate = null, CancellationToken cancellationToken = default)
        {
            UserConversationState userConvState = await _stateService.GetAsync(userId, cancellationToken) ?? new UserConversationState();

            if (string.IsNullOrWhiteSpace(stateName))
            {
                // If the new state name is null/empty, clear the state.
                await ClearStateAsync(userId, cancellationToken);
                return;
            }

            // Verify the state exists before setting it.
            ITelegramState? newState = _availableStates.FirstOrDefault(s => s.Name.Equals(stateName, StringComparison.OrdinalIgnoreCase));
            if (newState == null)
            {
                _logger.LogError("Attempted to set unknown state '{StateName}' for UserID {UserId}. Clearing state as a safeguard.", stateName, userId);
                await ClearStateAsync(userId, cancellationToken);
                return;
            }

            _logger.LogInformation("Setting state for UserID {UserId} to {StateName}", userId, stateName);

            // Set the new state name and clear any old state data.
            userConvState.CurrentStateName = stateName;
            userConvState.StateData.Clear();

            // Persist the new state.
            await _stateService.SetAsync(userId, userConvState, cancellationToken);

            // The logic to send the entry message has been REMOVED.
            // This is now the sole responsibility of the calling handler,
            // which in this case is InitiateKeywordSearchAsync.
        }

        /// <summary>
        /// Retrieves a state by its name.
        /// </summary>
        /// <param name="stateName"></param>
        /// <returns></returns>
        public ITelegramState? GetState(string stateName)
        {
            return _availableStates.FirstOrDefault(s => s.Name.Equals(stateName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Clears the current state for a user, effectively resetting their conversation state.
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task ClearStateAsync(long userId, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Clearing state for UserID {UserId}", userId);
            await _stateService.ClearAsync(userId, cancellationToken);
        }

    }
}