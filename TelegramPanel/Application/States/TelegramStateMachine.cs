using Microsoft.Extensions.DependencyInjection; // برای IServiceProvider
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Types;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Application.States; // برای IUserConversationStateService
using TelegramPanel.Infrastructure;   // برای ITelegramMessageSender

namespace TelegramPanel.Application.Services // ✅ Namespace صحیح
{
    public class TelegramStateMachine : ITelegramStateMachine
    {
        private readonly IUserConversationStateService _stateService;
        private readonly IServiceProvider _serviceProvider; // برای resolve کردن ITelegramState ها
        private readonly ITelegramMessageSender _messageSender;
        private readonly ILogger<TelegramStateMachine> _logger;
        private readonly IEnumerable<ITelegramState> _availableStates; // تمام State های رجیستر شده

        public TelegramStateMachine(
            IUserConversationStateService stateService,
            IServiceProvider serviceProvider,
            ITelegramMessageSender messageSender,
            ILogger<TelegramStateMachine> logger,
            IEnumerable<ITelegramState> availableStates) // تزریق تمام ITelegramState ها
        {
            _stateService = stateService ?? throw new ArgumentNullException(nameof(stateService));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _availableStates = availableStates ?? throw new ArgumentNullException(nameof(availableStates));
        }

        public async Task<ITelegramState?> GetCurrentStateAsync(long userId, CancellationToken cancellationToken = default)
        {
            var userConvState = await _stateService.GetAsync(userId, cancellationToken);
            if (userConvState == null || string.IsNullOrWhiteSpace(userConvState.CurrentStateName))
            {
                return null;
            }

            // پیدا کردن پیاده‌سازی ITelegramState بر اساس نام
            return _availableStates.FirstOrDefault(s => s.Name.Equals(userConvState.CurrentStateName, StringComparison.OrdinalIgnoreCase));
        }

        public async Task SetStateAsync(long userId, string? stateName, Update? triggerUpdate = null, CancellationToken cancellationToken = default)
        {
            var userConvState = await _stateService.GetAsync(userId, cancellationToken) ?? new UserConversationState();

            if (string.IsNullOrWhiteSpace(stateName))
            {
                _logger.LogInformation("Clearing state for UserID {UserId}", userId);
                userConvState.CurrentStateName = null;
                userConvState.StateData.Clear(); // پاک کردن داده‌های وضعیت قبلی
            }
            else
            {
                var newState = _availableStates.FirstOrDefault(s => s.Name.Equals(stateName, StringComparison.OrdinalIgnoreCase));
                if (newState == null)
                {
                    _logger.LogError("Attempted to set unknown state '{StateName}' for UserID {UserId}", stateName, userId);
                    // throw new ArgumentException($"State '{stateName}' not found.");
                    // یا به یک وضعیت پیش‌فرض برگردید یا خطا را به کاربر اطلاع دهید
                    await ClearStateAsync(userId, cancellationToken); // پاک کردن وضعیت نامعتبر
                    var chatId = triggerUpdate?.Message?.Chat?.Id ?? triggerUpdate?.CallbackQuery?.Message?.Chat?.Id;
                    if (chatId.HasValue)
                    {
                        await _messageSender.SendTextMessageAsync(chatId.Value, "An internal error occurred with conversation flow. Please try again.", cancellationToken: cancellationToken);
                    }
                    return;
                }

                _logger.LogInformation("Setting state for UserID {UserId} to {StateName}", userId, stateName);
                userConvState.CurrentStateName = stateName;
                userConvState.StateData.Clear(); // پاک کردن داده‌های وضعیت قبلی هنگام ورود به وضعیت جدید

                // ارسال پیام ورودی وضعیت جدید
                var entryMessage = await newState.GetEntryMessageAsync(userId, triggerUpdate, cancellationToken);
                var chatIdForEntry = triggerUpdate?.Message?.Chat?.Id ?? triggerUpdate?.CallbackQuery?.Message?.Chat?.Id;

                if (!string.IsNullOrWhiteSpace(entryMessage) && chatIdForEntry.HasValue)
                {
                    await _messageSender.SendTextMessageAsync(chatIdForEntry.Value, entryMessage, cancellationToken: cancellationToken);
                }
            }
            await _stateService.SetAsync(userId, userConvState, cancellationToken);
        }

        public async Task ProcessUpdateInCurrentStateAsync(long userId, Update update, CancellationToken cancellationToken = default)
        {
            var currentState = await GetCurrentStateAsync(userId, cancellationToken);
            if (currentState == null)
            {
                _logger.LogWarning("ProcessUpdateInCurrentStateAsync called for UserID {UserId} but no current state found.", userId);
                // این نباید اتفاق بیفتد اگر منطق RouteToHandlerAsync صحیح باشد
                return;
            }

            _logger.LogDebug("Processing update for UserID {UserId} in state {StateName}", userId, currentState.Name);
            var nextStateName = await currentState.ProcessUpdateAsync(update, cancellationToken);

            if (nextStateName != currentState.Name) // اگر وضعیت تغییر کرده یا null شده (پایان مکالمه)
            {
                await SetStateAsync(userId, nextStateName, update, cancellationToken); // triggerUpdate را پاس می‌دهیم برای ارسال پیام ورودی وضعیت جدید
            }
        }

        public async Task ClearStateAsync(long userId, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Clearing state for UserID {UserId}", userId);
            await _stateService.ClearAsync(userId, cancellationToken);
        }
    }
}