// FILE TO EDIT: TelegramPanel/Application/Services/TelegramStateMachine.cs

using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Application.States;
using TelegramPanel.Infrastructure;

namespace TelegramPanel.Application.Services
{
    public class TelegramStateMachine : ITelegramStateMachine
    {
        private readonly IUserConversationStateService _stateService;
        private readonly IEnumerable<ITelegramState> _availableStates;
        private readonly ITelegramMessageSender _messageSender;
        private readonly ILogger<TelegramStateMachine> _logger;

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

        public async Task<ITelegramState?> GetCurrentStateAsync(long userId, CancellationToken cancellationToken = default)
        {
            var userConvState = await _stateService.GetAsync(userId, cancellationToken);
            if (userConvState == null || string.IsNullOrWhiteSpace(userConvState.CurrentStateName))
            {
                return null;
            }
            return _availableStates.FirstOrDefault(s => s.Name.Equals(userConvState.CurrentStateName, StringComparison.OrdinalIgnoreCase));
        }

        public async Task SetStateAsync(long userId, string? stateName, Update? triggerUpdate = null, CancellationToken cancellationToken = default)
        {
            var userConvState = await _stateService.GetAsync(userId, cancellationToken) ?? new UserConversationState();

            if (string.IsNullOrWhiteSpace(stateName))
            {
                await ClearStateAsync(userId, cancellationToken);
                return;
            }

            var newState = _availableStates.FirstOrDefault(s => s.Name.Equals(stateName, StringComparison.OrdinalIgnoreCase));
            if (newState == null)
            {
                _logger.LogError("Attempted to set unknown state '{StateName}' for UserID {UserId}", stateName, userId);
                await ClearStateAsync(userId, cancellationToken);
                return;
            }

            _logger.LogInformation("Setting state for UserID {UserId} to {StateName}", userId, stateName);
            userConvState.CurrentStateName = stateName;
            userConvState.StateData.Clear();

            await _stateService.SetAsync(userId, userConvState, cancellationToken);

            var entryMessage = await newState.GetEntryMessageAsync(userId, triggerUpdate, cancellationToken);
            var chatIdForEntry = triggerUpdate?.Message?.Chat?.Id ?? triggerUpdate?.CallbackQuery?.Message?.Chat?.Id;
            if (!string.IsNullOrWhiteSpace(entryMessage) && chatIdForEntry.HasValue)
            {
                await _messageSender.SendTextMessageAsync(chatIdForEntry.Value, entryMessage, cancellationToken: cancellationToken);
            }
        }

        public async Task ProcessUpdateInCurrentStateAsync(long userId, Update update, CancellationToken cancellationToken = default)
        {
            var currentState = await GetCurrentStateAsync(userId, cancellationToken);
            if (currentState != null)
            {
                _logger.LogDebug("Processing update for UserID {UserId} in state {StateName}", userId, currentState.Name);
                var nextStateName = await currentState.ProcessUpdateAsync(update, cancellationToken);
                if (nextStateName != currentState.Name)
                {
                    await SetStateAsync(userId, nextStateName, update, cancellationToken);
                }
            }
        }

        public async Task ClearStateAsync(long userId, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Clearing state for UserID {UserId}", userId);
            await _stateService.ClearAsync(userId, cancellationToken);
        }
    }
}