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

namespace TelegramPanel.Infrastructure
{
    // =========================================================================
    // 1. اینترفیس برای سرویسی که واقعاً با API تلگرام صحبت می‌کند
    //    این اینترفیس توسط ActualTelegramMessageActions پیاده‌سازی می‌شود
    //    و جاب‌های Hangfire این متدها را فراخوانی می‌کنند.
    // =========================================================================
    public interface IActualTelegramMessageActions
    {
        Task EditMessageCaptionInTelegramAsync(long chatId, int messageId, string caption, ParseMode? parseMode, InlineKeyboardMarkup? replyMarkup, CancellationToken cancellationToken);
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
        public async Task EditMessageCaptionInTelegramAsync(long chatId, int messageId, string caption, ParseMode? parseMode, InlineKeyboardMarkup? replyMarkup, CancellationToken cancellationToken)
        {
            string sanitizedLogCaption = SanitizeSensitiveData(caption);
            _logger.LogDebug("Hangfire Job (ActualSend): Editing message caption. ChatID: {ChatId}, MessageID: {MessageId}, Caption (Sanitized): '{SanitizedLogCaption}'", chatId, messageId, sanitizedLogCaption);

            var pollyContext = new Polly.Context($"EditCaption_{chatId}_{messageId}", new Dictionary<string, object>
        {
            { "ChatId", chatId },
            { "MessageId", messageId },
            { "MessagePreview", sanitizedLogCaption }
        });

            try
            {
                await _telegramApiRetryPolicy.ExecuteAsync(async (context, ct) =>
                {
                    // The critical change: call EditMessageCaptionAsync
                    await _botClient.EditMessageCaption(
                        chatId: new ChatId(chatId),
                        messageId: messageId,
                        caption: caption,
                        parseMode: parseMode ?? ParseMode.Markdown,
                        replyMarkup: replyMarkup,
                        cancellationToken: ct);
                }, pollyContext, cancellationToken);

                _logger.LogInformation("Hangfire Job (ActualSend): Successfully edited message caption for ChatID {ChatId}, MessageID: {MessageId}", chatId, messageId);
            }
            catch (ApiRequestException apiEx) when (apiEx.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Hangfire Job (ActualSend): Message caption not modified for ChatID {ChatId}, MessageID {MessageId}. Operation skipped.", chatId, messageId);
            }
            catch (ApiRequestException apiEx) when (
                (apiEx.ErrorCode == 400 && (apiEx.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase) || apiEx.Message.Contains("USER_DEACTIVATED", StringComparison.OrdinalIgnoreCase))) ||
                (apiEx.ErrorCode == 403 && apiEx.Message.Contains("bot was blocked by the user", StringComparison.OrdinalIgnoreCase))
            )
            {
                _logger.LogWarning(apiEx, "Hangfire Job (ActualSend): Telegram API reported chat not found or user deactivated/blocked (Code: {ApiErrorCode}) for ChatID {ChatId} while editing caption. Attempting user removal.", apiEx.ErrorCode, chatId);
                // Here you would add the logic to mark the user as unreachable.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hangfire Job (ActualSend): Error editing message caption for ChatID {ChatId}, MessageID: {MessageId} after retries. Caption (Sanitized): '{SanitizedLogCaption}'", chatId, messageId, sanitizedLogCaption);
                throw; // Re-throw to let Hangfire handle the failure.
            }
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
            var sanitizedTextForSending = EscapeTelegramMarkdownV2(text); // Use the robust regex version
            var sanitizedTextForLogging = SanitizeSensitiveData(text);

            _logger.LogDebug("Job (UltimateEdit): Preparing to edit. Chat: {ChatId}, Msg: {MessageId}", chatId, messageId);

            try
            {
                try
                {
                    // --- ATTEMPT 1: Try to edit as a standard text message. ---
                    await _botClient.EditMessageText(
                        chatId: new ChatId(chatId),
                        messageId: messageId,
                        text: sanitizedTextForSending,
                        parseMode: parseMode ?? ParseMode.MarkdownV2,
                        replyMarkup: replyMarkup,
                        cancellationToken: cancellationToken);

                    _logger.LogInformation("Job (UltimateEdit): Successfully edited message {MessageId} as TEXT.", messageId);
                    return; // Success! Exit the method.
                }
                catch (ApiRequestException textEditEx)
                    when (textEditEx.ErrorCode == 400 && textEditEx.Message.Contains("there is no text in the message to edit", StringComparison.OrdinalIgnoreCase))
                {
                    // This is not a true error, but a signal to try the next method.
                    _logger.LogWarning("Job (UltimateEdit): Failed to edit Msg {MessageId} as text. Switching to edit as CAPTION.", messageId);
                }

                // --- ATTEMPT 2: If we reach here, the first attempt failed correctly. Now, try to edit the caption. ---
                await _botClient.EditMessageCaption(
                    chatId: new ChatId(chatId),
                    messageId: messageId,
                    caption: sanitizedTextForSending,
                    replyMarkup: replyMarkup,
                    cancellationToken: cancellationToken);

                _logger.LogInformation("Job (UltimateEdit): Successfully edited message {MessageId} as CAPTION.", messageId);
            }
            // --- V3 UPGRADE: CENTRALIZED, ROBUST ERROR HANDLING ---
            catch (ApiRequestException apiEx)
            {
                // This single block now catches any ApiRequestException from EITHER of the attempts above.

                if (apiEx.ErrorCode == 400 && apiEx.Message.Contains("message is not modified"))
                {
                    _logger.LogDebug("Job (UltimateEdit): Message {MessageId} was not modified (content was identical).", messageId);
                    return; // This is a success, not an error.
                }

                if (apiEx.ErrorCode >= 400 && apiEx.ErrorCode < 500)
                {
                    // This is a PERMANENT client-side error (Bad Request, User Blocked, Message not found, etc.).
                    // We log it critically and DO NOT re-throw to stop the Hangfire retry loop.
                    _logger.LogCritical(apiEx, "Job (UltimateEdit): PERMANENT failure for Msg {MessageId}, Chat {ChatId}. ErrorCode: {ErrorCode}, API Message: '{ApiMessage}'. Job will terminate.",
                        messageId, chatId, apiEx.ErrorCode, apiEx.Message);

                    if (apiEx.ErrorCode == 403 || (apiEx.ErrorCode == 400 && apiEx.Message.Contains("chat not found")))
                    {
                        // Optional self-healing
                        // await _userService.MarkUserAsUnreachableAsync(chatId.ToString());
                    }
                }
                else // This is a transient server-side error (5xx)
                {
                    _logger.LogError(apiEx, "Job (UltimateEdit): TRANSIENT failure for Msg {MessageId}, Chat {ChatId}. ErrorCode: {ErrorCode}. Re-throwing for Hangfire retry.",
                        messageId, chatId, apiEx.ErrorCode);
                    throw;
                }
            }
            catch (Exception ex)
            {
                // The final safety net for non-API exceptions.
                _logger.LogError(ex, "Job (UltimateEdit): CRITICAL unhandled error for Msg {MessageId}, Chat {ChatId}. All attempts failed.", messageId, chatId);
                throw; // Re-throw to let Hangfire handle the failure.
            }
        }

        // Ensure you are using the robust, regex-based version of this helper
        private string EscapeTelegramMarkdownV2(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            const string markdownV2Pattern = @"([_\[\]()~`>#\+\-=\|{}\.!\*])";
            return Regex.Replace(text, markdownV2Pattern, @"\$1");
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

            // --- THIS IS THE NEWLY ADDED METHOD ---
            /// <summary>
            /// Enqueues a job to edit the caption of an existing message that contains media (e.g., a photo).
            /// </summary>
            Task EditMessageCaptionAsync(
                long chatId,
                int messageId,
                string caption,
                ParseMode? parseMode = null,
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

            public Task EditMessageCaptionAsync(long chatId, int messageId, string caption, ParseMode? parseMode = null, InlineKeyboardMarkup? replyMarkup = null, CancellationToken cancellationToken = default)
            {
                _logger.LogDebug("Enqueueing EditMessageCaptionAsync for ChatID {ChatId}, MsgID {MessageId}", chatId, messageId);

                // Create a background job that calls the *actual* implementation for editing a caption.
                _jobScheduler.Enqueue<IActualTelegramMessageActions>(
                    sender => sender.EditMessageCaptionInTelegramAsync(chatId, messageId, caption, parseMode, replyMarkup, CancellationToken.None)
                );

                // Return immediately, as the job is now in Hangfire's queue.
                return Task.CompletedTask;
            }
        }
    }
}
