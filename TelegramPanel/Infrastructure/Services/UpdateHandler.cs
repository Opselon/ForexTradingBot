using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using TelegramPanel.Application.Interfaces;

namespace TelegramPanel.Infrastructure.Services
{
    public class UpdateHandler : Telegram.Bot.Polling.IUpdateHandler
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

        public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            try
            {
                if (update.CallbackQuery != null)
                {
                    await _callbackQueryHandler.HandleAsync(update, cancellationToken);
                }
                else if (update.Message != null)
                {
                    _logger.LogInformation("Received message: {MessageType} from chat {ChatId}", update.Message.Type, update.Message.Chat.Id);

                    // Example of handling a message with multiple concurrent asynchronous operations
                    var processingTask = ProcessMessageTextAsync(update.Message, cancellationToken);
                    var analyticsTask = NotifyAnalyticsAsync(update.Message, cancellationToken);

                    await Task.WhenAll(processingTask, analyticsTask);

                    _logger.LogInformation("Finished processing message {MessageId}", update.Message.MessageId);
                }
                else
                {
                    _logger.LogWarning("Received unsupported update type: {UpdateType}", update.Type);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling update {UpdateId}", update.Id);
            }
        }

        public Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, HandleErrorSource source, CancellationToken cancellationToken)
        {
            var errorMessage = exception switch
            {
                ApiRequestException apiRequestException
                    => $"Telegram API Error (Source: {source}):\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
                _ => $"Polling Error (Source: {source}): {exception}"
            };

            _logger.LogError(exception, errorMessage);
            return Task.CompletedTask;
        }

        // Helper methods for demonstrating concurrent message processing
        private async Task ProcessMessageTextAsync(Message message, CancellationToken cancellationToken)
        {
            // Simulate asynchronous work for processing message text
            _logger.LogInformation("Starting to process text for message {MessageId}", message.MessageId);
            await Task.Delay(100, cancellationToken); // Simulate I/O bound operation
            // TODO: Replace with actual message text processing logic
            _logger.LogInformation("Finished processing text for message {MessageId}", message.MessageId);
        }

        private async Task NotifyAnalyticsAsync(Message message, CancellationToken cancellationToken)
        {
            // Simulate asynchronous work for sending analytics
            _logger.LogInformation("Starting to notify analytics for message {MessageId}", message.MessageId);
            await Task.Delay(150, cancellationToken); // Simulate I/O bound operation
            // TODO: Replace with actual analytics notification logic
            _logger.LogInformation("Finished notifying analytics for message {MessageId}", message.MessageId);
        }
    }
}