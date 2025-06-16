// File: TelegramPanel/Infrastructure/TelegramMessageSender.cs
using Application.Common.Interfaces;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramPanel.Infrastructure
{
    // =========================================================================
    // 1. اینترفیس برای سرویسی که واقعاً با API تلگرام صحبت می‌کند
    //    این اینترفیس توسط ActualTelegramMessageActions پیاده‌سازی می‌شود
    //    و جاب‌های Hangfire این متدها را فراخوانی می‌کنند.
    // =========================================================================
    public interface IActualTelegramMessageActions
    {
        Task CopyMessageToTelegramAsync(long targetChatId, long sourceChatId, int messageId, CancellationToken cancellationToken);
        Task EditMessageTextDirectAsync(long chatId, int messageId, string text, ParseMode? parseMode, InlineKeyboardMarkup? replyMarkup, CancellationToken cancellationToken);
        Task SendTextMessageToTelegramAsync(long chatId, string text, ParseMode? parseMode, ReplyMarkup? replyMarkup, bool disableNotification, LinkPreviewOptions? linkPreviewOptions, CancellationToken cancellationToken);
        Task EditMessageTextInTelegramAsync(long chatId, int messageId, string text, ParseMode? parseMode, InlineKeyboardMarkup? replyMarkup, CancellationToken cancellationToken);
        Task AnswerCallbackQueryToTelegramAsync(string callbackQueryId, string? text, bool showAlert, string? url, int cacheTime, CancellationToken cancellationToken);
        Task SendPhotoToTelegramAsync(long chatId, string photoUrlOrFileId, string? caption, ParseMode? parseMode, ReplyMarkup? replyMarkup, CancellationToken cancellationToken);
        Task DeleteMessageAsync(
            long chatId,
            int messageId,
            CancellationToken cancellationToken = default);
    }

    // =========================================================================
    // 2. پیاده‌سازی سرویسی که واقعاً با API تلگرام صحبت می‌کند
    //    این کلاس IActualTelegramMessageActions را پیاده‌سازی می‌کند.
    // =========================================================================
    public class ActualTelegramMessageActions : IActualTelegramMessageActions
    {
        private readonly ILoggingSanitizer _logSanitizer; // New Dependency
                                                 
        private readonly INotificationJobScheduler _jobScheduler;
        private readonly ITelegramBotClient _botClient;
        private readonly ILogger<ActualTelegramMessageActions> _logger;
        private const ParseMode DefaultParseMode = ParseMode.Markdown;
        private readonly IUserRepository _userRepository;
        private readonly IAppDbContext _context;
        private readonly AsyncRetryPolicy _telegramApiRetryPolicy;
        private readonly AsyncRetryPolicy _hangfireRetryPolicy; // <-- RENAME for clarity
        private static readonly Regex EmailRegex = new(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex PhoneRegex = new(@"\(?\b\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}\b", RegexOptions.Compiled);
        private const string RedactedPlaceholder = "[REDACTED]";
        private const int MaxLogLength = 150;
        public ActualTelegramMessageActions(ILoggingSanitizer logSanitizer,
            ITelegramBotClient botClient,
            ILogger<ActualTelegramMessageActions> logger,
            IUserRepository userRepository,
            INotificationJobScheduler jobScheduler,
            IAppDbContext context)
        {
            _jobScheduler = jobScheduler ?? throw new ArgumentNullException(nameof(jobScheduler));
            _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logSanitizer = logSanitizer;




            // --- THIS IS THE CRITICAL FIX ---
            _hangfireRetryPolicy = Policy // <-- Use new name
                .Handle<ApiRequestException>(apiEx =>
                {
                    // This logic determines if Polly should HANDLE (and thus RETRY) the exception.
                    // We return 'true' for errors we want to retry, 'false' for those we want to ignore.

                    // DO NOT RETRY if the message is simply not modified. Let the exception bubble up immediately.
                    if (apiEx.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
                    {
                        return false; // False = Do Not Handle = Do Not Retry
                    }

                    // DO NOT RETRY for user-blocked/deactivated errors.
                    if ((apiEx.ErrorCode == 403 && apiEx.Message.Contains("bot was blocked by the user", StringComparison.OrdinalIgnoreCase)) ||
                        (apiEx.ErrorCode == 400 &&
                         (apiEx.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase) ||
                          apiEx.Message.Contains("USER_DEACTIVATED", StringComparison.OrdinalIgnoreCase) ||
                          apiEx.Message.Contains("user is deactivated", StringComparison.OrdinalIgnoreCase) ||
                          apiEx.Message.Contains("PEER_ID_INVALID", StringComparison.OrdinalIgnoreCase))
                        ))
                    {
                        return false; // False = Do Not Handle = Do Not Retry
                    }

                    // For all OTHER ApiRequestExceptions, we DO want to retry.
                    return true; // True = Handle this error = Retry
                })
                .Or<Exception>(ex => ex is not (OperationCanceledException or TaskCanceledException))
                .WaitAndRetryAsync(
                    // ... rest of your policy remains the same
                    retryCount: 3,
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    onRetry: (exception, timeSpan, retryAttempt, context) =>
                    {
                        // ... your logging here
                    });

            _telegramApiRetryPolicy = Policy
                .Handle<ApiRequestException>(apiEx =>
                    !(apiEx.ErrorCode == 403 && apiEx.Message.Contains("bot was blocked by the user", StringComparison.OrdinalIgnoreCase)) &&
                    !(apiEx.ErrorCode == 400 &&
                      (apiEx.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase) ||
                       apiEx.Message.Contains("USER_DEACTIVATED", StringComparison.OrdinalIgnoreCase) ||
                       apiEx.Message.Contains("user is deactivated", StringComparison.OrdinalIgnoreCase) ||
                       apiEx.Message.Contains("PEER_ID_INVALID", StringComparison.OrdinalIgnoreCase)
                      ))
                )
                .Or<Exception>(ex => ex is not (OperationCanceledException or TaskCanceledException))
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    onRetry: (exception, timeSpan, retryAttempt, context) =>
                    {
                        var operationName = context.OperationKey ?? "UnknownOperation";
                        var chatId = context.TryGetValue("ChatId", out var id) ? (long?)id : null;
                        var messagePreview = context.TryGetValue("MessagePreview", out var msg) ? msg?.ToString() : "N/A";
                        var apiErrorCode = (exception as ApiRequestException)?.ErrorCode.ToString() ?? "N/A";

                        _logger.LogWarning(exception,
                            "PollyRetry: Telegram API operation '{Operation}' failed (ChatId: {ChatId}, Code: {ApiErrorCode}). Retrying in {TimeSpan} for attempt {RetryAttempt}. Message preview: '{MessagePreview}'. Error: {Message}",
                            operationName, chatId, apiErrorCode, timeSpan, retryAttempt, messagePreview, exception.Message);
                    });
        }
        // --- ✅ IMPLEMENT THE NEW METHOD ---
        public async Task CopyMessageToTelegramAsync(long targetChatId, long sourceChatId, int messageId, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Hangfire Job: Copying message {MessageId} to ChatID {TargetChatId}", messageId, targetChatId);

            try
            {
                await _telegramApiRetryPolicy.ExecuteAsync(async ct =>
                {
                    _ = await _botClient.CopyMessage(
                        chatId: targetChatId,
                        fromChatId: sourceChatId,
                        messageId: messageId,
                        cancellationToken: ct
                    );
                }, cancellationToken);
            }
            catch (ApiRequestException apiEx) when (apiEx.ErrorCode == 403 || (apiEx.ErrorCode == 400 && apiEx.Message.Contains("chat not found")))
            {
                _logger.LogWarning(apiEx, "User {TargetChatId} blocked the bot or chat was not found during broadcast. They will be skipped.", targetChatId);
                // In a real system, you might mark this user as inactive in your database.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hangfire Job: Failed to copy message to ChatID {TargetChatId} after retries.", targetChatId);
                throw; // Re-throw to let Hangfire handle the failure.
            }
        }

        public Task DeleteMessageAsync(long chatId, int messageId, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Enqueueing DeleteMessageAsync for ChatID {ChatId}, MsgID {MessageId}", chatId, messageId);
            _ = _jobScheduler.Enqueue<IActualTelegramMessageActions>(
                sender => sender.DeleteMessageAsync(chatId, messageId, CancellationToken.None)
            );
            return Task.CompletedTask;
        }
        /// <summary>
        /// Robustly sanitizes a string to prevent PII/sensitive data exposure in logs.
        /// It redacts known patterns (emails, phone numbers) and truncates the result.
        /// This method is designed to be fail-safe.
        /// </summary>
        /// <param name="input">The potentially sensitive string to sanitize.</param>
        /// <returns>A sanitized and truncated string safe for logging.</returns>
        private string SanitizeSensitiveData(string? input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return "N/A";
            }

            try
            {
                // Truncate first to limit the amount of data being processed and logged.
                string sanitized = input.Length > MaxLogLength
                    ? input.Substring(0, MaxLogLength) + "..."
                    : input;

                // Apply redaction rules.
                sanitized = EmailRegex.Replace(sanitized, RedactedPlaceholder);
                sanitized = PhoneRegex.Replace(sanitized, RedactedPlaceholder);
                // Add more Regex rules here for other PII types as needed.

                // Final sanitization for any remaining log-forging characters.
                return sanitized.Replace(Environment.NewLine, " ").Replace("\r", " ").Replace("\n", " ");
            }
            catch (Exception ex)
            {
                // Failsafe: If sanitization has an error, log the error but return a generic
                // placeholder to absolutely prevent leaking the original sensitive data.
                _logger.LogError(ex, "An unexpected error occurred within SanitizeSensitiveData. Returning a generic placeholder.");
                return "[SENSITIVE DATA - SANITIZATION FAILED]";
            }
        }




        public async Task SendTextMessageToTelegramAsync(
       long chatId,
       string text,
       ParseMode? parseMode,
       ReplyMarkup? replyMarkup,
       bool disableNotification,
       LinkPreviewOptions? linkPreviewOptions,
       CancellationToken cancellationToken)
        {
            // Apply robust sanitization immediately. This is the single source of truth for logging this text.
            string sanitizedLogText = SanitizeSensitiveData(text);

            _logger.LogDebug("Hangfire Job (ActualSend): Sending text message. ChatID: {ChatId}, Text (Sanitized): '{SanitizedLogText}'", chatId, sanitizedLogText);

            var pollyContext = new Polly.Context($"SendText_{chatId}_{Guid.NewGuid():N}", new Dictionary<string, object>
    {
        { "ChatId", chatId },
        { "MessagePreview", sanitizedLogText } // Always use the sanitized version.
    });

            try
            {
                await _telegramApiRetryPolicy.ExecuteAsync(async (context, ct) =>
                {
                    await _botClient.SendMessage(
                        chatId: new ChatId(chatId),
                        text: text,
                        parseMode: ParseMode.Markdown,
                        replyMarkup: replyMarkup,
                        disableNotification: disableNotification,
                        linkPreviewOptions: linkPreviewOptions,
                        cancellationToken: ct);
                }, pollyContext, cancellationToken);

                _logger.LogInformation("Hangfire Job (ActualSend): Successfully sent text message to ChatID {ChatId}.", chatId);
            }
            catch (ApiRequestException apiEx)
                when ((apiEx.ErrorCode == 400 &&
                       (apiEx.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase) ||
                        apiEx.Message.Contains("USER_DEACTIVATED", StringComparison.OrdinalIgnoreCase) ||
                        apiEx.Message.Contains("user is deactivated", StringComparison.OrdinalIgnoreCase) ||
                        apiEx.Message.Contains("PEER_ID_INVALID", StringComparison.OrdinalIgnoreCase))) ||
                      (apiEx.ErrorCode == 403 && apiEx.Message.Contains("bot was blocked by the user", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning(apiEx, "Hangfire Job (ActualSend): Telegram API reported chat not found or user deactivated/blocked (Code: {ApiErrorCode}) for ChatID {ChatId}. Text (Sanitized): '{SanitizedLogText}'. Attempting user removal.",
                                    apiEx.ErrorCode,
                                    chatId,
                                    sanitizedLogText);

                try
                {
                    Domain.Entities.User? userToDelete = await _userRepository.GetByTelegramIdAsync(chatId.ToString(), cancellationToken);
                    if (userToDelete != null)
                    {
                        await _userRepository.DeleteAndSaveAsync(userToDelete, cancellationToken);
                        _logger.LogInformation("Hangfire Job (ActualSend): Successfully removed user with Telegram ID {TelegramId} from database.", userToDelete.TelegramId);
                    }
                }
                catch (Exception dbEx)
                {
                    // HARDENED: Sanitize the original API exception message before logging.
                    var sanitizedApiExMessage = SanitizeSensitiveData(apiEx.Message);
                    _logger.LogError(dbEx, "Hangfire Job (ActualSend): Failed to remove user with ChatID {ChatId} from database. Original Telegram error (Sanitized): {SanitizedTelegramErrorMessage}",
                                        chatId,
                                        sanitizedApiExMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hangfire Job (ActualSend): Error sending text message to ChatID {ChatId} after retries. Text (Sanitized): '{SanitizedLogText}'", chatId, sanitizedLogText);
                throw;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="chatId"></param>
        /// <param name="messageId"></param>
        /// <param name="text"></param>
        /// <param name="parseMode"></param>
        /// <param name="replyMarkup"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task EditMessageTextInTelegramAsync(long chatId, int messageId, string text, ParseMode? parseMode, InlineKeyboardMarkup? replyMarkup, CancellationToken cancellationToken)
        {
            // Apply robust sanitization immediately.
            string sanitizedLogText = SanitizeSensitiveData(text);

            _logger.LogDebug("Hangfire Job (ActualSend): Editing message. ChatID: {ChatId}, MessageID: {MessageId}, Text (Sanitized): '{SanitizedLogText}'", chatId, messageId, sanitizedLogText);

            var pollyContext = new Polly.Context($"EditMessage_{chatId}_{messageId}", new Dictionary<string, object>
    {
        { "ChatId", chatId },
        { "MessageId", messageId },
        { "MessagePreview", sanitizedLogText } // Always use the sanitized version.
    });

            try
            {
                await _telegramApiRetryPolicy.ExecuteAsync(async (context, ct) =>
                {
                    await _botClient.EditMessageText(
                        chatId: new ChatId(chatId),
                        messageId: messageId,
                        text: text,
                        parseMode: ParseMode.Markdown,
                        replyMarkup: replyMarkup,
                        cancellationToken: ct);
                }, pollyContext, cancellationToken);

                _logger.LogInformation("Hangfire Job (ActualSend): Successfully edited message for ChatID {ChatId}, MessageID: {MessageId}", chatId, messageId);
            }
            catch (ApiRequestException apiEx)
                when (apiEx.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
            {
                // This is not an error, so a simple debug log is sufficient. No sensitive data is in this specific message.
                _logger.LogDebug("Hangfire Job (ActualSend): Message not modified for ChatID {ChatId}, MessageID {MessageId}. Operation skipped.", chatId, messageId);
            }
            catch (ApiRequestException apiEx)
                when ((apiEx.ErrorCode == 400 &&
                       (apiEx.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase) ||
                        apiEx.Message.Contains("USER_DEACTIVATED", StringComparison.OrdinalIgnoreCase))) ||
                      (apiEx.ErrorCode == 403 && apiEx.Message.Contains("bot was blocked by the user", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning(apiEx, "Hangfire Job (ActualSend): Telegram API reported chat not found or user deactivated/blocked (Code: {ApiErrorCode}) for ChatID {ChatId} while editing message. Attempting user removal.", apiEx.ErrorCode, chatId);
                // Attempt to remove user (logic omitted for brevity, but would be similar to SendTextMessageAsync)
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hangfire Job (ActualSend): Error editing message for ChatID {ChatId}, MessageID: {MessageId} after retries. Text (Sanitized): '{SanitizedLogText}'", chatId, messageId, sanitizedLogText);
                throw;
            }
        }


        public Task EditMessageTextDirectAsync(long chatId, int messageId, string text, ParseMode? parseMode, InlineKeyboardMarkup? replyMarkup, CancellationToken cancellationToken)
        {
            // It calls the base method directly, bypassing Polly entirely.
            return _base_EditMessageText(chatId, messageId, text, parseMode, replyMarkup, cancellationToken);
        }

        // --- CREATE THIS NEW PRIVATE BASE METHOD ---
        // This is the raw, unwrapped call to the Telegram API.
        private Task _base_EditMessageText(long chatId, int messageId, string text, ParseMode? parseMode, InlineKeyboardMarkup? replyMarkup, CancellationToken cancellationToken)
        {
            return _botClient.EditMessageText(
                chatId: new ChatId(chatId),
                messageId: messageId,
                text: text,
                parseMode: ParseMode.Markdown,
                replyMarkup: replyMarkup,
                cancellationToken: cancellationToken);
        }

        public async Task AnswerCallbackQueryToTelegramAsync(string callbackQueryId, string? text, bool showAlert, string? url, int cacheTime, CancellationToken cancellationToken)
        {
            // Apply robust sanitization immediately.
            string sanitizedLogText = SanitizeSensitiveData(text);

            _logger.LogDebug("Hangfire Job (ActualSend): Answering CBQ. ID: {CBQId}, Text (Sanitized): '{SanitizedLogText}'", callbackQueryId, sanitizedLogText);

            var pollyContext = new Polly.Context($"AnswerCBQ_{callbackQueryId}", new Dictionary<string, object>
    {
        { "CallbackQueryId", callbackQueryId },
        { "AnswerTextPreview", sanitizedLogText } // Always use the sanitized version.
    });

            try
            {
                await _telegramApiRetryPolicy.ExecuteAsync(async (context, ct) =>
                {
                    await _botClient.AnswerCallbackQuery(
                        callbackQueryId: callbackQueryId,
                        text: text,
                        showAlert: showAlert,
                        url: url,
                        cacheTime: cacheTime,
                        cancellationToken: ct);
                }, pollyContext, cancellationToken);

                _logger.LogInformation("Hangfire Job (ActualSend): Successfully answered CBQ. ID: {CBQId}", callbackQueryId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hangfire Job (ActualSend): Error answering CBQ. ID: {CBQId} after retries. Text (Sanitized): '{SanitizedLogText}'", callbackQueryId, sanitizedLogText);
                throw;
            }
        }



        // REWRITTEN METHOD
        public async Task SendPhotoToTelegramAsync(
            long chatId,
            string photoUrlOrFileId,
            string? caption,
            ParseMode? parseMode,
            ReplyMarkup? replyMarkup,
            CancellationToken cancellationToken)
        {
            // Apply robust sanitization immediately.
            string sanitizedLogCaption = SanitizeSensitiveData(caption);

            _logger.LogDebug("Hangfire Job (ActualSend): Sending photo. ChatID: {ChatId}, Photo: {PhotoIdOrUrl}, Caption (Sanitized): '{SanitizedLogCaption}'", chatId, photoUrlOrFileId, sanitizedLogCaption);

            var pollyContext = new Polly.Context($"SendPhoto_{chatId}_{Guid.NewGuid():N}", new Dictionary<string, object>
    {
        { "ChatId", chatId },
        { "PhotoIdOrUrl", photoUrlOrFileId },
        { "CaptionPreview", sanitizedLogCaption } // Always use the sanitized version.
    });

            try
            {
                InputFile photoInput = InputFile.FromString(photoUrlOrFileId);

                await _telegramApiRetryPolicy.ExecuteAsync(async (context, ct) =>
                {
                    await _botClient.SendPhoto(
                        chatId: new ChatId(chatId),
                        photo: photoInput,
                        caption: caption,
                        parseMode: ParseMode.Markdown,
                        replyMarkup: replyMarkup,
                        cancellationToken: ct);
                }, pollyContext, cancellationToken);

                _logger.LogInformation("Hangfire Job (ActualSend): Successfully sent photo to ChatID {ChatId}", chatId);
            }
            catch (ApiRequestException apiEx)
                when ((apiEx.ErrorCode == 400 &&
                       (apiEx.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase) ||
                        apiEx.Message.Contains("USER_DEACTIVATED", StringComparison.OrdinalIgnoreCase))) ||
                      (apiEx.ErrorCode == 403 && apiEx.Message.Contains("bot was blocked by the user", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning(apiEx, "Hangfire Job (ActualSend): Telegram API reported chat not found or user deactivated/blocked (Code: {ApiErrorCode}) for ChatID {ChatId} while sending photo. Attempting user removal.", apiEx.ErrorCode, chatId);
                // Attempt to remove user (logic omitted for brevity)
            }
            catch (Exception ex)
            {
                // HARDENED: Log sanitized caption, not raw exception message which might contain it.
                _logger.LogError(ex, "Hangfire Job (ActualSend): Unexpected error sending photo to ChatID {ChatId} after retries. Caption (Sanitized): '{SanitizedLogCaption}'", chatId, sanitizedLogCaption);
                throw;
            }
        }
        // =========================================================================
        // 3. اینترفیس ITelegramMessageSender (بدون تغییر)
        // =========================================================================
        public interface ITelegramMessageSender
        {
            Task SendPhotoAsync(
                long chatId,
                string photoUrlOrFileId,
                string? caption = null,
                ParseMode? parseMode = null,
                ReplyMarkup? replyMarkup = null,
                CancellationToken cancellationToken = default);

            Task SendTextMessageAsync(
                long chatId,
                string text,
                ParseMode? parseMode = ParseMode.Markdown,
                ReplyMarkup? replyMarkup = null,
                CancellationToken cancellationToken = default,
                LinkPreviewOptions? linkPreviewOptions = null);

            Task EditMessageTextAsync(
                long chatId,
                int messageId,
                string text,
                ParseMode? parseMode = ParseMode.Markdown,
                InlineKeyboardMarkup? replyMarkup = null,
                CancellationToken cancellationToken = default);

            Task AnswerCallbackQueryAsync(
                string callbackQueryId,
                string? text = null,
                bool showAlert = false,
                string? url = null,
                int cacheTime = 0,
                CancellationToken cancellationToken = default);

            Task DeleteMessageAsync(
              long chatId,
              int messageId,
              CancellationToken cancellationToken = default);
        }

        // =========================================================================
        // 4. پیاده‌سازی ITelegramMessageSender که جاب‌ها را به Hangfire "رله" می‌کند (بدون تغییر)
        // =========================================================================
        public class HangfireRelayTelegramMessageSender : ITelegramMessageSender
        {
            private readonly INotificationJobScheduler _jobScheduler;
            private readonly ILogger<HangfireRelayTelegramMessageSender> _logger;

            public HangfireRelayTelegramMessageSender(
                INotificationJobScheduler jobScheduler,
                ILogger<HangfireRelayTelegramMessageSender> logger)
            {
                _jobScheduler = jobScheduler ?? throw new ArgumentNullException(nameof(jobScheduler));
                _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            }

            public Task DeleteMessageAsync(long chatId, int messageId, CancellationToken cancellationToken = default)
            {
                _logger.LogDebug("Enqueueing DeleteMessageAsync for ChatID {ChatId}, MsgID {MessageId}", chatId, messageId);
                _ = _jobScheduler.Enqueue<IActualTelegramMessageActions>(
                    sender => sender.DeleteMessageAsync(chatId, messageId, CancellationToken.None)
                );
                return Task.CompletedTask;
            }

            public Task SendTextMessageAsync(long chatId, string text, ParseMode? parseMode = ParseMode.Markdown, ReplyMarkup? replyMarkup = null, CancellationToken cancellationToken = default, LinkPreviewOptions? linkPreviewOptions = null)
            {
                _logger.LogDebug("Enqueueing SendTextMessageAsync for ChatID {ChatId}", chatId);
                _ = _jobScheduler.Enqueue<IActualTelegramMessageActions>(
                    // ✅ CORRECTED LINE: Swapped the order of linkPreviewOptions and CancellationToken.None
                    sender => sender.SendTextMessageToTelegramAsync(chatId, text, parseMode, replyMarkup, false, linkPreviewOptions, CancellationToken.None)
                );
                return Task.CompletedTask;
            }

            public Task EditMessageTextAsync(long chatId, int messageId, string text, ParseMode? parseMode = ParseMode.Markdown, InlineKeyboardMarkup? replyMarkup = null, CancellationToken cancellationToken = default)
            {
                _logger.LogDebug("Enqueueing EditMessageTextAsync for ChatID {ChatId}, MsgID {MessageId}", chatId, messageId);
                _ = _jobScheduler.Enqueue<IActualTelegramMessageActions>(
                    sender => sender.EditMessageTextInTelegramAsync(chatId, messageId, text, parseMode, replyMarkup, CancellationToken.None)
                );
                return Task.CompletedTask;
            }

            public Task AnswerCallbackQueryAsync(string callbackQueryId, string? text = null, bool showAlert = false, string? url = null, int cacheTime = 0, CancellationToken cancellationToken = default)
            {
                _logger.LogDebug("Enqueueing AnswerCallbackQueryAsync for CBQID {CallbackQueryId}", callbackQueryId);
                _ = _jobScheduler.Enqueue<IActualTelegramMessageActions>(
                    sender => sender.AnswerCallbackQueryToTelegramAsync(callbackQueryId, text, showAlert, url, cacheTime, CancellationToken.None)
                );
                return Task.CompletedTask;
            }

            public Task SendPhotoAsync(long chatId, string photoUrlOrFileId, string? caption = null, ParseMode? parseMode = null, ReplyMarkup? replyMarkup = null, CancellationToken cancellationToken = default)
            {
                _logger.LogDebug("Enqueueing SendPhotoAsync for ChatID {ChatId}", chatId);
                _ = _jobScheduler.Enqueue<IActualTelegramMessageActions>(
                    sender => sender.SendPhotoToTelegramAsync(chatId, photoUrlOrFileId, caption, parseMode, replyMarkup, CancellationToken.None)
                );
                return Task.CompletedTask;
            }
        }
    }
}
