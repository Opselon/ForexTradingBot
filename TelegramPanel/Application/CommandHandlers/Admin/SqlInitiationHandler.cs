// --- START OF FILE: SqlInitiationHandler.cs ---
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Settings;

namespace TelegramPanel.Application.CommandHandlers.Admin
{
    public class SqlInitiationHandler : ITelegramCallbackQueryHandler
    {
        private readonly ITelegramStateMachine _stateMachine;
        private readonly TelegramPanelSettings _settings;
        private const string ExecuteSqlCallback = "admin_execute_sql";

        public SqlInitiationHandler(ITelegramStateMachine stateMachine, IOptions<TelegramPanelSettings> settingsOptions)
        {
            _stateMachine = stateMachine;
            _settings = settingsOptions.Value;
        }

        public bool CanHandle(Update update)
        {
            return update.Type == UpdateType.CallbackQuery &&
            update.CallbackQuery?.Data == ExecuteSqlCallback &&
            _settings.AdminUserIds.Contains(update.CallbackQuery.From.Id);
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            await _stateMachine.SetStateAsync(update.CallbackQuery!.From.Id, "WaitingForSqlQuery", update, cancellationToken);
        }
    }
}