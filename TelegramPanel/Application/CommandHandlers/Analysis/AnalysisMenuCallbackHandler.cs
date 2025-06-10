// File: TelegramPanel/Application/CommandHandlers/Features/Analysis/AnalysisMenuCallbackHandler.cs
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.CommandHandlers.MainMenu;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters;
using TelegramPanel.Infrastructure;
using TelegramPanel.Infrastructure.Helpers;

namespace TelegramPanel.Application.CommandHandlers.Features.Analysis
{
    public class AnalysisMenuCallbackHandler : ITelegramCallbackQueryHandler
    {
        private readonly ILogger<AnalysisMenuCallbackHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly ITelegramStateMachine _stateMachine;

        private const string SearchByKeywordsCallback = "analysis_search_keywords";

        public AnalysisMenuCallbackHandler(
            ILogger<AnalysisMenuCallbackHandler> logger,
            ITelegramMessageSender messageSender,
            ITelegramStateMachine stateMachine)
        {
            _logger = logger;
            _messageSender = messageSender;
            _stateMachine = stateMachine;
        }

        public bool CanHandle(Update update)
        {
            return update.Type == UpdateType.CallbackQuery &&
                   update.CallbackQuery?.Data == SearchByKeywordsCallback;
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            var callbackQuery = update.CallbackQuery!;
            await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);

            var chatId = callbackQuery.Message!.Chat.Id;
            var userId = callbackQuery.From.Id;
            var messageId = callbackQuery.Message.MessageId;

            _logger.LogInformation("User {UserId} is entering news keyword search state.", userId);

            // Set the state first
            string nextStateName = "WaitingForNewsKeywords";
            await _stateMachine.SetStateAsync(userId, nextStateName, update, cancellationToken);

            // Get the state object to retrieve its entry message
            var newState = _stateMachine.GetState(nextStateName);
            if (newState == null)
            {
                _logger.LogError("Could not retrieve state object for '{StateName}'.", nextStateName);
                await _messageSender.SendTextMessageAsync(chatId, "An internal error occurred. Please try again.", cancellationToken: cancellationToken);
                return;
            }

            // Get the entry message from the state itself
            var entryMessage = await newState.GetEntryMessageAsync(chatId, update, cancellationToken);

            var keyboard = MarkupBuilder.CreateInlineKeyboard(
                new[] { InlineKeyboardButton.WithCallbackData("⬅️ Cancel Search", MenuCallbackQueryHandler.BackToMainMenuGeneral) });

            // Edit the existing message to show the prompt from the state
            await _messageSender.EditMessageTextAsync(chatId, messageId, entryMessage!, ParseMode.MarkdownV2, keyboard, cancellationToken);
        }
    }
}