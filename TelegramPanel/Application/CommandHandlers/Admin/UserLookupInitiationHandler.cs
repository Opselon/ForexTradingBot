// NEW FILE: TelegramPanel/Application/CommandHandlers/Admin/UserLookupInitiationHandler.cs

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Infrastructure;
using TelegramPanel.Settings;
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions;

namespace TelegramPanel.Application.CommandHandlers.Admin
{
    public class UserLookupInitiationHandler : ITelegramCallbackQueryHandler
    {
        private readonly ILogger<UserLookupInitiationHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly ITelegramStateMachine _stateMachine;
        private readonly TelegramPanelSettings _settings;

        private const string AdminUserLookupCallback = "admin_user_lookup";

        public UserLookupInitiationHandler(
            ILogger<UserLookupInitiationHandler> logger,
            ITelegramMessageSender messageSender,
            ITelegramStateMachine stateMachine,
            IOptions<TelegramPanelSettings> settingsOptions)
        {
            _logger = logger;
            _messageSender = messageSender;
            _stateMachine = stateMachine;
            _settings = settingsOptions.Value;
        }

        public bool CanHandle(Update update)
        {
            return update.Type == UpdateType.CallbackQuery &&
                   update.CallbackQuery?.Data == AdminUserLookupCallback &&
                   update.CallbackQuery.From != null &&
                   _settings.AdminUserIds.Contains(update.CallbackQuery.From.Id);
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            var callbackQuery = update.CallbackQuery!;
            _ = callbackQuery.Message!;
            var adminId = callbackQuery.From.Id;

            await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, "Enter lookup mode...", showAlert: false, cancellationToken: cancellationToken);

            // ✅ CORRECTED CALL: We now pass the STATE NAME as a STRING, matching the interface.
            // The "triggerUpdate" is the third parameter.
            await _stateMachine.SetStateAsync(adminId, "WaitingForUserLookupId", update, cancellationToken);

            // ... the rest of the method is the same ...
            _logger.LogInformation("Admin {AdminId} entered user lookup flow.", adminId);
            // ...
        }
    }
}