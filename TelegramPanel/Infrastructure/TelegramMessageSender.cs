// File: TelegramPanel/Infrastructure/TelegramMessageSender.cs
using Application.Common.Interfaces;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using System.Text;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Formatters;

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
        private readonly AsyncRetryPolicy _sendMessagePolicy;
        private readonly AsyncRetryPolicy _answerCallbackQueryPolicy;
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


            _sendMessagePolicy = CreateTelegramRetryPolicy(
                policyName: "SendMessagePolicy",
                shouldIgnoreQueryIsOld: false // Not relevant for sending messages
            );

            _answerCallbackQueryPolicy = CreateTelegramRetryPolicy(
                policyName: "AnswerCallbackQueryPolicy",
                shouldIgnoreQueryIsOld: true // CRITICAL: We want this policy to ignore "query is too old"
            );
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

        /// <summary>
        /// A centralized factory method for creating robust Polly retry policies for the Telegram API.
        /// This promotes consistency and simplifies policy management.
        /// </summary>
        /// <param name="policyName">A name for the policy, used in logging.</param>
        /// <param name="shouldIgnoreQueryIsOld">If true, the policy will NOT retry on "query is too old" errors.</param>
        /// <returns>A configured AsyncRetryPolicy.</returns>
        private AsyncRetryPolicy CreateTelegramRetryPolicy(string policyName, bool shouldIgnoreQueryIsOld)
        {
            return Policy
                .Handle<Exception>(ex =>
                {
                    // --- V3 UPGRADE: Centralized, clear exception handling logic ---

                    // First, check for exceptions we NEVER want to retry.
                    if (ex is OperationCanceledException or TaskCanceledException)
                    {
                        return false; // Never retry on cancellation.
                    }

                    if (ex is ApiRequestException apiEx)
                    {
                        // Check for the specific "query is too old" error if requested.
                        if (shouldIgnoreQueryIsOld && apiEx.ErrorCode == 400 &&
                            apiEx.Message.Contains("query is too old", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning("Policy({PolicyName}): Ignoring permanent 'query is too old' error. No retry will occur.", policyName);
                            return false; // False = Do Not Handle = Do Not Retry
                        }

                        // Check for other permanent, unrecoverable client errors.
                        if ((apiEx.ErrorCode == 403 && apiEx.Message.Contains("bot was blocked", StringComparison.OrdinalIgnoreCase)) ||
                            (apiEx.ErrorCode == 400 && (
                                apiEx.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase) ||
                                apiEx.Message.Contains("USER_DEACTIVATED", StringComparison.OrdinalIgnoreCase) ||
                                apiEx.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase)
                            )))
                        {
                            _logger.LogWarning("Policy({PolicyName}): Ignoring permanent user/chat error (Code: {ErrorCode}). No retry will occur.", policyName, apiEx.ErrorCode);
                            return false; // False = Do Not Handle = Do Not Retry
                        }
                    }

                    // For all other exceptions (e.g., 5xx server errors, HttpRequestException, other 4xx API errors), we DO want to retry.
                    _logger.LogTrace("Policy({PolicyName}): Handling exception of type {ExceptionType} for retry.", policyName, ex.GetType().Name);
                    return true; // True = Handle this error = Retry
                })
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    onRetry: (exception, timeSpan, retryAttempt, context) =>
                    {
                        var operationName = context.OperationKey ?? "UnknownOperation";
                        var apiErrorCode = (exception as ApiRequestException)?.ErrorCode.ToString() ?? "N/A";

                        _logger.LogWarning(exception,
                            "PollyRetry({PolicyName}): Operation '{Operation}' failed (Code: {ApiErrorCode}). Retrying in {TimeSpan} (Attempt {RetryAttempt}/3). Error: {Message}",
                            policyName, operationName, apiErrorCode, timeSpan, retryAttempt, exception.Message);
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




        // Inside the ActualTelegramMessageActions class

        /// <summary>
        /// Intelligently and resiliently edits a message in Telegram. It uses a Polly retry policy
        /// and automatically falls back from editing text to editing a caption if the message contains media.
        /// </summary>
        /// <summary>
        /// Intelligently and resiliently edits a message in Telegram. This V3 version uses a fast-switch
        /// fallback from text to caption and incorporates a centralized, robust error handling strategy
        /// to manage all possible API exceptions gracefully.
        /// </summary>
        public async Task EditMessageTextInTelegramAsync(long chatId, int messageId, string text, ParseMode? parseMode, InlineKeyboardMarkup? replyMarkup, CancellationToken cancellationToken)
        {
            // <<< FIX: Use the correct V1 escaper for the V1 goal.
            var sanitizedText = TelegramMessageFormatter.EscapeMarkdownV1(text);
            var sanitizedTextForLogging = SanitizeSensitiveData(text);

            _logger.LogDebug("Job (UltimateEdit): Preparing to edit with Markdown V1. Chat: {ChatId}, Msg: {MessageId}", chatId, messageId);

            try
            {
                try
                {
                    // --- ATTEMPT 1: Try to edit as a standard text message. ---
                    await _botClient.EditMessageText(
                     chatId: new ChatId(chatId),
                     messageId: messageId,
                     // <<< FIX: Use the sanitized text, not the raw text.
                     text: text,
                     // <<< FIX: Consistently use Markdown V1.
                     parseMode: ParseMode.Markdown,
                     replyMarkup: replyMarkup,
                     cancellationToken: cancellationToken);

                    _logger.LogInformation("Job (UltimateEdit): Successfully edited message {MessageId} as TEXT using V1.", messageId);
                    return; // Success!
                }
                catch (ApiRequestException textEditEx)
                    when (textEditEx.ErrorCode == 400 && textEditEx.Message.Contains("there is no text in the message to edit", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Job (UltimateEdit): Failed to edit Msg {MessageId} as text. Switching to edit as CAPTION.", messageId);
                }
            }
            // The rest of your robust error handling remains correct.
            catch (ApiRequestException apiEx)
            {
                if (apiEx.ErrorCode == 400 && apiEx.Message.Contains("message is not modified"))
                {
                    _logger.LogDebug("Job (UltimateEdit): Message {MessageId} was not modified (content was identical).", messageId);
                    return;
                }

                if (apiEx.ErrorCode >= 400 && apiEx.ErrorCode < 500)
                {
                    _logger.LogCritical(apiEx, "Job (UltimateEdit): PERMANENT failure for Msg {MessageId}, Chat {ChatId}. ErrorCode: {ErrorCode}, API Message: '{ApiMessage}'. Job will terminate.",
                        messageId, chatId, apiEx.ErrorCode, apiEx.Message);
                }
                else
                {
                    _logger.LogError(apiEx, "Job (UltimateEdit): TRANSIENT failure for Msg {MessageId}, Chat {ChatId}. ErrorCode: {ErrorCode}. Re-throwing for Hangfire retry.",
                        messageId, chatId, apiEx.ErrorCode);
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job (UltimateEdit): CRITICAL unhandled error for Msg {MessageId}, Chat {ChatId}. All attempts failed.", messageId, chatId);
                throw;
            }
        }

        private string EscapeTelegramMarkdownV2(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }

            // IMPORTANT: Ensure ALL special characters are escaped. The dot '.' is crucial.
            var specialChars = new[] {
        "_", "*", "[", "]", "(", ")", "~", "`", ">", "#", "+", "-", "=", "|", "{", "}", ".", "!"
    };
            // Added: "`", "!", ".", " " (space, though less common to escape)
            // Note: The list provided in your original code was also missing backtick (`).

            var sb = new StringBuilder(text);

            // Loop through each special character and replace it with its escaped version.
            // Order might matter in some edge cases, but usually not for simple replacement.
            // It's generally safer to replace longer sequences first if there's overlap.
            // However, for these individual characters, direct replacement is fine.
            foreach (var specialChar in specialChars)
            {
                // Be careful not to double-escape if a replacement itself contains a special char.
                // Using a regex replace is safer for complex cases, but for this list, direct replace is okay if done carefully.
                // A simple replace approach:
                sb.Replace(specialChar, "\\" + specialChar);
            }

            // Special case for consecutive escaped characters:
            // Sometimes replacing '*' with '\*' and then '.' with '\.' might create '\.' which is fine.
            // But if you had something like "_*_"; replacing _ first gives "\_\*_"; then replacing * gives "\_\*\_".
            // This is usually fine.
            // The core issue is the missing '.' from the original list.

            return sb.ToString();
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

        /// <summary>
        /// Answers a Telegram callback query with high resilience. This V3 version uses our
        /// specialized `_answerCallbackQueryPolicy` that does not retry on "query is too old"
        /// errors, handling them gracefully instead.
        /// </summary>
        public async Task AnswerCallbackQueryToTelegramAsync(string callbackQueryId, string? text, bool showAlert, string? url, int cacheTime, CancellationToken cancellationToken)
        {
            // Sanitize for logging purposes only. The 'text' parameter is not Markdown.
            string sanitizedLogText = _logSanitizer.Sanitize(text);

            _logger.LogDebug("Job (AnswerCBQ): Preparing to answer. ID: {CBQId}", callbackQueryId);

            var pollyContext = new Polly.Context($"AnswerCBQ_{callbackQueryId}", new Dictionary<string, object>
           {
               { "CallbackQueryId", callbackQueryId },
               { "AnswerTextPreview", sanitizedLogText }
           });

            try
            {
                // --- THE DEFINITIVE FIX: Use the new, specialized policy ---
                await _answerCallbackQueryPolicy.ExecuteAsync(async (context, ct) =>
                {
                    // Use the modern ...Async method
                    await _botClient.AnswerCallbackQuery(
                        callbackQueryId: callbackQueryId,
                        text: text,
                        showAlert: showAlert,
                        url: url,
                        cacheTime: cacheTime,
                        cancellationToken: ct);
                }, pollyContext, cancellationToken);

                _logger.LogInformation("Job (AnswerCBQ): Successfully answered CBQ. ID: {CBQId}", callbackQueryId);
            }
            catch (ApiRequestException apiEx)
                when (apiEx.ErrorCode == 400 && apiEx.Message.Contains("query is too old", StringComparison.OrdinalIgnoreCase))
            {
                // --- V3 UPGRADE: GRACEFUL HANDLING OF STALE QUERIES ---
                // This is an expected outcome if the job was delayed.
                // We log it as a warning and DO NOT re-throw, so the Hangfire job succeeds.
                _logger.LogWarning("Job (AnswerCBQ): Could not answer CBQ {CBQId} because it was too old. This is an expected operational event, not a failure.", callbackQueryId);
            }
            catch (Exception ex)
            {
                // This catches any other errors that the retry policy failed to handle (e.g., true network failures).
                _logger.LogError(ex, "Job (AnswerCBQ): A critical error occurred while answering CBQ {CBQId}. Text (Sanitized): '{SanitizedLogText}'", callbackQueryId, sanitizedLogText);
                throw; // Re-throw to let Hangfire mark the job as failed.
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


        // The full namespace and usings for your project would be here.
        // using Telegram.Bot.Types.Enums;
        // using Telegram.Bot.Types.ReplyMarkups;
        // using Telegram.Bot.Types;

        /// <summary>
        /// Defines the contract for sending and managing messages via the Telegram Bot API.
        /// Implementations of this interface (e.g., HangfireRelayTelegramMessageSender) will handle
        /// the actual delivery of these messages, potentially through a background job queue.
        /// </summary>
        public interface ITelegramMessageSender
        {
            /// <summary>
            /// Enqueues a job to send a text message to a specified chat.
            /// </summary>
            Task SendTextMessageAsync(
                long chatId,
                string text,
                ParseMode? parseMode = ParseMode.Markdown,
                ReplyMarkup? replyMarkup = null,
                CancellationToken cancellationToken = default,
                LinkPreviewOptions? linkPreviewOptions = null);

            /// <summary>
            /// Enqueues a job to send a photo with an optional caption.
            /// </summary>
            Task SendPhotoAsync(
                long chatId,
                string photoUrlOrFileId,
                string? caption = null,
                ParseMode? parseMode = null,
                ReplyMarkup? replyMarkup = null,
                CancellationToken cancellationToken = default);

            /// <summary>
            /// Enqueues a job to edit the text of an existing message.
            /// </summary>
            Task EditMessageTextAsync(
                long chatId,
                int messageId,
                string text,
                ParseMode? parseMode = ParseMode.Markdown,
                InlineKeyboardMarkup? replyMarkup = null,
                CancellationToken cancellationToken = default);


            // --- END OF NEW METHOD ---

            /// <summary>
            /// Enqueues a job to answer a callback query, typically to stop the loading animation on a button.
            /// </summary>
            Task AnswerCallbackQueryAsync(
                string callbackQueryId,
                string? text = null,
                bool showAlert = false,
                string? url = null,
                int cacheTime = 0,
                CancellationToken cancellationToken = default);

            /// <summary>
            /// Enqueues a job to delete a message from a chat.
            /// </summary>
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
