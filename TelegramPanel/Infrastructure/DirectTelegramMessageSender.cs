// File: TelegramPanel/Infrastructure/DirectTelegramMessageSender.cs

using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TelegramPanel.Infrastructure
{
    /// <summary>
    /// Defines a contract for sending messages directly to the Telegram API,
    /// bypassing any background job queue. This is for operations that need an immediate response,
    /// such as getting the MessageId of a sent message.
    /// </summary>
    public interface IDirectMessageSender
    {
        /// <summary>
        /// Sends a text message directly and returns the created Message object.
        /// </summary>
        /// <returns>The sent <see cref="Message"/> object, or null if sending fails after retries.</returns>
        Task<Message?> SendTextMessageAsync(
            long chatId,
            string text,
            ParseMode? parseMode = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes a message directly.
        /// </summary>
        Task DeleteMessageAsync(
            long chatId,
            int messageId,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Implements IDirectMessageSender by making direct calls to the ITelegramBotClient,
    /// wrapped in a Polly retry policy for resilience.
    /// </summary>
    public class DirectTelegramMessageSender : IDirectMessageSender
    {
        private readonly ITelegramBotClient _botClient;
        private readonly ILogger<DirectTelegramMessageSender> _logger;
        private readonly AsyncRetryPolicy<Message?> _sendRetryPolicy;
        private readonly AsyncRetryPolicy _deleteRetryPolicy;

        // NOTE: We inject the same policy logic as your ActualTelegramMessageActions
        // to ensure consistent behavior for API calls.
        public DirectTelegramMessageSender(ITelegramBotClient botClient, ILogger<DirectTelegramMessageSender> logger)
        {
            _botClient = botClient;
            _logger = logger;

            // Policy for sending messages (returns a Message object)
            _sendRetryPolicy = Policy<Message?>
                .Handle<ApiRequestException>(apiEx => apiEx.ErrorCode is not 403 and not 400) // Simplified retry logic for direct sends
                .Or<Exception>(ex => ex is not OperationCanceledException)
                .WaitAndRetryAsync(3, retry => TimeSpan.FromSeconds(Math.Pow(2, retry)),
                    onRetry: (outcome, timespan, attempt, context) =>
                    {
                        _logger.LogWarning(outcome.Exception,
                            "DirectSend: Retrying to send message for ChatId {ChatId}. Attempt {Attempt}. Delay: {TimeSpan}",
                            context["ChatId"], attempt, timespan);
                    });

            // Policy for deleting messages (void return)
            _deleteRetryPolicy = Policy
                .Handle<ApiRequestException>(apiEx => apiEx.ErrorCode != 403 && apiEx.ErrorCode != 400 && !apiEx.Message.Contains("message to delete not found"))
                .Or<Exception>(ex => ex is not OperationCanceledException)
                .WaitAndRetryAsync(3, retry => TimeSpan.FromSeconds(Math.Pow(2, retry)),
                    onRetry: (exception, timespan, attempt, context) =>
                    {
                        _logger.LogWarning(exception,
                            "DirectDelete: Retrying to delete message {MessageId} for ChatId {ChatId}. Attempt {Attempt}. Delay: {TimeSpan}",
                            context["MessageId"], context["ChatId"], attempt, timespan);
                    });
        }

        public async Task<Message?> SendTextMessageAsync(long chatId, string text, ParseMode? parseMode = null, CancellationToken cancellationToken = default)
        {
            Context context = new($"DirectSend_{chatId}", new Dictionary<string, object> { { "ChatId", chatId } });

            // Use the policy that returns a Message
            return await _sendRetryPolicy.ExecuteAsync(async (ctx, ct) =>
                await _botClient.SendMessage(
                    chatId: chatId,
                    text: text,
                    parseMode: parseMode ?? ParseMode.Markdown,
                    cancellationToken: ct),
                context,
                cancellationToken);
        }

        public async Task DeleteMessageAsync(long chatId, int messageId, CancellationToken cancellationToken = default)
        {
            Context context = new($"DirectDelete_{chatId}_{messageId}", new Dictionary<string, object> { { "ChatId", chatId }, { "MessageId", messageId } });

            try
            {
                // Use the void policy
                await _deleteRetryPolicy.ExecuteAsync(async (ctx, ct) =>
                    await _botClient.DeleteMessage(
                        chatId: chatId,
                        messageId: messageId,
                        cancellationToken: ct),
                    context,
                    cancellationToken);
            }
            catch (ApiRequestException apiEx) when (apiEx.Message.Contains("message to delete not found"))
            {
                _logger.LogInformation("Message {MessageId} in chat {ChatId} was already deleted, ignoring.", messageId, chatId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete message {MessageId} in chat {ChatId} after all retries.", messageId, chatId);
            }
        }
    }
}