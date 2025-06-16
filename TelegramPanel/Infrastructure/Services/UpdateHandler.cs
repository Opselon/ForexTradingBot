using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling; // Required for IUpdateHandler
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums; // Required for UpdateType
using TelegramPanel.Application.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TelegramPanel.Infrastructure.Services
{
    public class UpdateHandler : IUpdateHandler
    {
        private readonly ILogger<UpdateHandler> _logger;
        private readonly ITelegramCallbackQueryHandler _callbackQueryHandler;

        public UpdateHandler(
            ILogger<UpdateHandler> logger,
            ITelegramCallbackQueryHandler callbackQueryHandler)
        {
            _logger = logger;
            _callbackQueryHandler = callbackQueryHandler;
        }

        /// <summary>
        /// This is the main entry point for all incoming updates. Its only job is to
        /// dispatch the update to the correct specialized handler.
        /// It does NOT contain a top-level try-catch, as the Polling Receiver is responsible
        //  for that, and will pass any unhandled exceptions to HandleErrorAsync.
        /// </summary>
        public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            // Use a logging scope to correlate all logs for a single update.
            using var logScope = _logger.BeginScope("Processing Update {UpdateId}", update.Id);

            // 1. STRUCTURED DISPATCHING: Use a switch expression for cleaner, more maintainable routing.
            await (update.Type switch
            {
                UpdateType.Message => HandleMessageAsync(botClient, update.Message!, cancellationToken),
                UpdateType.CallbackQuery => HandleCallbackQueryAsync(botClient, update.CallbackQuery!, cancellationToken),
                // To add support for more update types, simply add a new case and handler method.
                // e.g., UpdateType.InlineQuery => HandleInlineQueryAsync(botClient, update.InlineQuery!, cancellationToken),
                _ => HandleUnknownUpdateAsync(update)
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// This is the global error handler for any unhandled exceptions from HandleUpdateAsync.
        /// It's responsible for logging errors in a structured way.
        /// </summary>
        public Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, HandleErrorSource source, CancellationToken cancellationToken)
        {
            // 2. ENHANCED ERROR LOGGING: Provide specific logging for different exception types.
            var errorMessage = exception switch
            {
                // This is an expected exception when the bot is shutting down. Log as Information.
                OperationCanceledException or TaskCanceledException => "Operation was canceled by user or host.",

                // This handles specific Telegram API errors (e.g., bot blocked, chat not found).
                ApiRequestException apiRequestException =>
                    $"Telegram API Error (Source: {source}): [{apiRequestException.ErrorCode}] - {apiRequestException.Message}",

                // This is the fallback for all other unexpected exceptions.
                _ => $"Polling Error (Source: {source}): {exception.Message}"
            };

            // Log with the appropriate severity level.
            switch (exception)
            {
                case OperationCanceledException or TaskCanceledException:
                    _logger.LogInformation(errorMessage);
                    break;
                default:
                    _logger.LogError(exception, errorMessage);
                    break;
            }

            return Task.CompletedTask;
        }

        #region Private Update Handlers

        private async Task HandleMessageAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Received {MessageType} message in Chat {ChatId}", message.Type, message.Chat.Id);

            // TODO: Here you would typically route to a command handler, state machine, etc.
            // For now, we'll process the helper tasks as in your example.

            var processingTask = ProcessMessageTextAsync(message, cancellationToken);
            var analyticsTask = NotifyAnalyticsAsync(message, cancellationToken);

            // 3. ROBUST CONCURRENT TASK HANDLING
            await HandleConcurrentTasksAsync(processingTask, analyticsTask).ConfigureAwait(false);

            _logger.LogInformation("Finished processing MessageId {MessageId}", message.MessageId);
        }

        private async Task HandleCallbackQueryAsync(ITelegramBotClient botClient, CallbackQuery callbackQuery, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Received CallbackQuery with data '{CallbackData}' from User {UserId}", callbackQuery.Data, callbackQuery.From.Id);
            // This is a call to an external service/handler, so it's wise to have
            // a specific try-catch here if you want to provide user-facing feedback on failure.
            try
            {
                await _callbackQueryHandler.HandleAsync(new Update { CallbackQuery = callbackQuery }, cancellationToken);
            }
            catch (Exception ex)
            {
                // This specific catch block allows us to provide feedback directly to the user
                // who pressed the button, which is a better user experience.
                _logger.LogError(ex, "Handler for CallbackQuery {CallbackQueryId} failed.", callbackQuery.Id);
                await botClient.AnswerCallbackQuery(
                    callbackQueryId: callbackQuery.Id,
                    text: "An error occurred while processing this action.",
                    showAlert: true,
                    cancellationToken: CancellationToken.None // Use a fresh token for this emergency answer
                ).ConfigureAwait(false);
            }
        }

        private Task HandleUnknownUpdateAsync(Update update)
        {
            _logger.LogWarning("Received unhandled update type: {UpdateType}", update.Type);
            return Task.CompletedTask;
        }

        /// <summary>
        /// A robust helper to await multiple tasks and log all exceptions without swallowing any.
        /// </summary>
        private async Task HandleConcurrentTasksAsync(params Task[] tasks)
        {
            var whenAllTask = Task.WhenAll(tasks);
            try
            {
                await whenAllTask;
            }
            catch
            {
                var allExceptions = tasks
                    .Where(t => t.IsFaulted && t.Exception != null)
                    .SelectMany(t => t.Exception!.InnerExceptions);

                // Re-throw an AggregateException that contains ALL failures, not just the first one.
                // This will be caught by the polling receiver and sent to HandleErrorAsync.
                throw new AggregateException(allExceptions);
            }
        }
        #endregion

        #region Your Original Helper Methods
        private async Task ProcessMessageTextAsync(Message message, CancellationToken cancellationToken)
        {
            // Simulate asynchronous work for processing message text
            _logger.LogInformation("Starting to process text for message {MessageId}", message.MessageId);
            await Task.Delay(100, cancellationToken); // Simulate I/O bound operation
            _logger.LogInformation("Finished processing text for message {MessageId}", message.MessageId);
        }

        private async Task NotifyAnalyticsAsync(Message message, CancellationToken cancellationToken)
        {
            // Simulate asynchronous work for sending analytics
            _logger.LogInformation("Starting to notify analytics for message {MessageId}", message.MessageId);
            await Task.Delay(150, cancellationToken); // Simulate I/O bound operation
            _logger.LogInformation("Finished notifying analytics for message {MessageId}", message.MessageId);
        }
        #endregion
    }
}