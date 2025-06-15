// FILE: TelegramPanel/Application/CommandHandlers/Admin/BroadcastInitiationHandler.cs
// ... (This version is now correct because ITelegramStateMachine and UserConversationState are fixed)
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
    public class BroadcastInitiationHandler : ITelegramCallbackQueryHandler
    {
        private readonly ILogger<BroadcastInitiationHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly ITelegramStateMachine _stateMachine;
        private readonly TelegramPanelSettings _settings;
        private const string AdminBroadcastCallback = "admin_broadcast";

        public BroadcastInitiationHandler(ILogger<BroadcastInitiationHandler> logger, ITelegramMessageSender messageSender, ITelegramStateMachine stateMachine, IOptions<TelegramPanelSettings> settingsOptions)
        {
            _logger = logger; _messageSender = messageSender; _stateMachine = stateMachine; _settings = settingsOptions.Value;
        }

        public bool CanHandle(Update update)
        {
            return update.Type == UpdateType.CallbackQuery &&
                   update.CallbackQuery?.Data == AdminBroadcastCallback &&
                   update.CallbackQuery.From != null &&
                   _settings.AdminUserIds.Contains(update.CallbackQuery.From.Id);
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            var callbackQuery = update.CallbackQuery!;
            await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, "Enter broadcast mode...", showAlert: false, cancellationToken: cancellationToken);
            await _stateMachine.SetStateAsync(callbackQuery.From.Id, "WaitingForBroadcastMessage", update, cancellationToken);
        }
    }
}