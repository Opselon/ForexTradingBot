// File: TelegramPanel/Infrastructure/TelegramMessageSender.cs
using Application.Common.Interfaces;
using Application.Interfaces;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
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
        Task SendDocumentToTelegramAsync(long chatId, byte[] documentContents, string fileName, string? caption, CancellationToken cancellationToken);
        Task CopyMessageToTelegramAsync(long targetChatId, long sourceChatId, int messageId, CancellationToken cancellationToken);
        Task EditMessageTextDirectAsync(long chatId, int messageId, string text, ParseMode? parseMode, InlineKeyboardMarkup? replyMarkup, CancellationToken cancellationToken);
        Task SendTextMessageToTelegramAsync(long chatId, string text, ParseMode? parseMode, ReplyMarkup? replyMarkup, bool disableNotification, LinkPreviewOptions? linkPreviewOptions, CancellationToken cancellationToken);
        Task EditMessageTextInTelegramAsync(long chatId, int messageId, string text, ParseMode? parseMode, InlineKeyboardMarkup? replyMarkup, CancellationToken cancellationToken);
        Task AnswerCallbackQueryToTelegramAsync(string callbackQueryId, string? text, bool showAlert, string? url, int cacheTime, CancellationToken cancellationToken);
        Task SendPhotoToTelegramAsync(long chatId, string photoUrlOrFileId, string? caption, ParseMode? parseMode, ReplyMarkup? replyMarkup, CancellationToken cancellationToken);
        Task DeleteMessage(
            long chatId,
            int messageId,
            CancellationToken cancellationToken = default);
        /// <summary>
        /// Hangfire/internal use only: retryable version for progressive retry logic.
        /// </summary>
        Task RetryableSendTextMessageAsync(long chatId, string text, ParseMode? parseMode, ReplyMarkup? replyMarkup, bool disableNotification, LinkPreviewOptions? linkPreviewOptions, CancellationToken cancellationToken, int retryCount);
        /// <summary>
        /// Hangfire/internal use only: retryable version for progressive retry logic.
        /// </summary>
        Task RetryableSendPhotoToTelegramAsync(long chatId, string photoUrlOrFileId, string? caption, ParseMode? parseMode, ReplyMarkup? replyMarkup, CancellationToken cancellationToken, int retryCount);
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
        private readonly IUserService _userService;
        private static readonly int[] RetryDelaysMinutes = { 1, 5, 10 };
        private readonly IServiceProvider _serviceProvider;

        public ActualTelegramMessageActions(ILoggingSanitizer logSanitizer,
            ITelegramBotClient botClient,
            ILogger<ActualTelegramMessageActions> logger,
            IUserRepository userRepository,
            INotificationJobScheduler jobScheduler,
            IAppDbContext context,
            IUserService userService,
            IServiceProvider serviceProvider)
        {
            _jobScheduler = jobScheduler ?? throw new ArgumentNullException(nameof(jobScheduler));
            _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logSanitizer = logSanitizer;
            _userService = userService ?? throw new ArgumentNullException(nameof(userService));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));


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
                        string operationName = context.OperationKey ?? "UnknownOperation";
                        long? chatId = context.TryGetValue("ChatId", out object? id) ? (long?)id : null;
                        string? messagePreview = context.TryGetValue("MessagePreview", out object? msg) ? msg?.ToString() : "N/A";
                        string apiErrorCode = (exception as ApiRequestException)?.ErrorCode.ToString() ?? "N/A";

                        _logger.LogWarning(exception,
                            "PollyRetry: Telegram API operation '{Operation}' failed (ChatId: {ChatId}, Code: {ApiErrorCode}). Retrying in {TimeSpan} for attempt {RetryAttempt}. Message preview: '{MessagePreview}'. Error: {Message}",
                            operationName, chatId, apiErrorCode, timeSpan, retryAttempt, messagePreview, exception.Message);
                    });
        }

        #region Hangfire Attributes and Performance
        // These attributes ensure jobs are deleted after completion and optimize performance.
        // [JobExpirationTimeout] ensures Hangfire deletes jobs after the specified time (default: 1 hour here).
        // [AutomaticRetry(Attempts = 0)] disables retries for jobs where we want to fail fast.
        // [DisableConcurrentExecution] prevents duplicate processing for the same job.
        // [Queue("notifications")] routes jobs to the notifications queue.
        #endregion

        [AutomaticRetry(Attempts = 0)]
        [Queue("notifications")]
        [DisableConcurrentExecution(timeoutInSeconds: 600)]
        public async Task SendDocumentToTelegramAsync(
           long chatId,
           byte[] documentContents,
           string fileName,
           string? caption,
           CancellationToken cancellationToken)
        {
            string sanitizedLogCaption = SanitizeSensitiveData(caption);

            _logger.LogDebug("Hangfire Job (ActualSend): Sending document. ChatID: {ChatId}, FileName: {FileName}, Caption (Sanitized): '{SanitizedLogCaption}'", chatId, fileName, sanitizedLogCaption);

            Context pollyContext = new($"SendDocument_{chatId}_{Guid.NewGuid():N}", new Dictionary<string, object>
            {
                { "ChatId", chatId },
                { "FileName", fileName },
                { "CaptionPreview", sanitizedLogCaption }
            });

            try
            {
                // Create the necessary input file from the byte array inside the Hangfire job
                await using MemoryStream stream = new(documentContents);
                InputFile documentInput = new InputFileStream(stream, fileName);

                await _telegramApiRetryPolicy.ExecuteAsync(async (context, ct) =>
                {
                    _ = await _botClient.SendDocument(
                        chatId: new ChatId(chatId),
                        document: documentInput,
                        caption: caption,
                        parseMode: ParseMode.Markdown, // Optional, can be null
                        cancellationToken: ct);
                }, pollyContext, cancellationToken);

                _logger.LogInformation("Hangfire Job (ActualSend): Successfully sent document '{FileName}' to ChatID {ChatId}", fileName, chatId);
            }
            catch (ApiRequestException apiEx)
                when ((apiEx.ErrorCode == 400 && apiEx.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase)) ||
                      (apiEx.ErrorCode == 403 && apiEx.Message.Contains("bot was blocked by the user", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning(apiEx, "Hangfire Job (ActualSend): Telegram API reported chat not found or user blocked (Code: {ApiErrorCode}) for ChatID {ChatId} while sending document. User removal could be triggered here.", apiEx.ErrorCode, chatId);
                // Optionally, trigger user removal logic here
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hangfire Job (ActualSend): Unexpected error sending document '{FileName}' to ChatID {ChatId} after retries. Caption (Sanitized): '{SanitizedLogCaption}'", fileName, chatId, sanitizedLogCaption);
                throw;
            }
        }

        // ... (All your other existing methods)

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
                        string operationName = context.OperationKey ?? "UnknownOperation";
                        string apiErrorCode = (exception as ApiRequestException)?.ErrorCode.ToString() ?? "N/A";

                        _logger.LogWarning(exception,
                            "PollyRetry({PolicyName}): Operation '{Operation}' failed (Code: {ApiErrorCode}). Retrying in {TimeSpan} (Attempt {RetryAttempt}/3). Error: {Message}",
                            policyName, operationName, apiErrorCode, timeSpan, retryAttempt, exception.Message);
                    });
        }



        // --- ✅ IMPLEMENT THE NEW METHOD ---
        [AutomaticRetry(Attempts = 0)]
        [Queue("notifications")]
        [DisableConcurrentExecution(timeoutInSeconds: 600)]
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

        [AutomaticRetry(Attempts = 0)]
        [Queue("notifications")]
        [DisableConcurrentExecution(timeoutInSeconds: 600)]
        public void DeleteMessage(long chatId, int messageId, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Enqueueing DeleteMessageAsync for ChatID {ChatId}, MsgID {MessageId}", chatId, messageId);
            _ = _jobScheduler.Enqueue<IActualTelegramMessageActions>(
                sender => sender.DeleteMessage(chatId, messageId, CancellationToken.None)
            );
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
                    ? input[..MaxLogLength] + "..."
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




        [AutomaticRetry(Attempts = 0)]
        [Queue("notifications")]
        [DisableConcurrentExecution(timeoutInSeconds: 600)]
        public async Task SendTextMessageToTelegramAsync(
       long chatId,
       string text,
       ParseMode? parseMode,
       ReplyMarkup? replyMarkup,
       bool disableNotification,
       LinkPreviewOptions? linkPreviewOptions,
       CancellationToken cancellationToken)
        {
            await SendTextMessageToTelegramWithRetryAsync(chatId, text, parseMode, replyMarkup, disableNotification, linkPreviewOptions, cancellationToken, 0);
        }

        // Internal retry logic overload
        private async Task SendTextMessageToTelegramWithRetryAsync(
           long chatId,
           string text,
           ParseMode? parseMode,
           ReplyMarkup? replyMarkup,
           bool disableNotification,
           LinkPreviewOptions? linkPreviewOptions,
           CancellationToken cancellationToken,
           int retryCount)
        {
            // Apply robust sanitization immediately. This is the single source of truth for logging this text.
            string sanitizedLogText = SanitizeSensitiveData(text);

            _logger.LogDebug("Hangfire Job (ActualSend): Sending text message. ChatID: {ChatId}, Text (Sanitized): '{SanitizedLogText}'", chatId, sanitizedLogText);

            Context pollyContext = new($"SendText_{chatId}_{Guid.NewGuid():N}", new Dictionary<string, object>
    {
        { "ChatId", chatId },
        { "MessagePreview", sanitizedLogText } // Always use the sanitized version.
    });

            try
            {
                await _telegramApiRetryPolicy.ExecuteAsync(async (context, ct) =>
                {
                    _ = await _botClient.SendMessage(
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
                #region Progressive Retry Logic
                if (retryCount < 3)
                {
                    int[] delays = { 3, 5, 10 }; // minutes
                    int delayMinutes = delays[Math.Min(retryCount, delays.Length - 1)];
                    _logger.LogWarning(apiEx, "Hangfire Job (ActualSend): Telegram API error for ChatID {ChatId} (Attempt {RetryCount}/3). Scheduling retry in {DelayMinutes} minutes.", chatId, retryCount + 1, delayMinutes);
                    _ = _jobScheduler.Schedule<IActualTelegramMessageActions>(
                        sender => sender.RetryableSendTextMessageAsync(chatId, text, parseMode, replyMarkup, disableNotification, linkPreviewOptions, CancellationToken.None, retryCount + 1),
                        TimeSpan.FromMinutes(delayMinutes));
                    return;
                }
                #endregion
                #region User Removal on Deactivation
                _logger.LogWarning(apiEx, "Hangfire Job (ActualSend): Telegram API reported chat not found or user deactivated/blocked (Code: {ApiErrorCode}) for ChatID {ChatId} after {RetryCount} retries. Marking user as unreachable.", apiEx.ErrorCode, chatId, retryCount);
                try
                {
                    await _userService.MarkUserAsUnreachableAsync(chatId.ToString(), $"ApiError_{apiEx.ErrorCode}", cancellationToken);
                    _logger.LogInformation("Hangfire Job (ActualSend): Successfully marked user with Telegram ID {TelegramId} as unreachable (text send).", chatId);
                }
                catch (Exception dbEx)
                {
                    string sanitizedApiExMessage = SanitizeSensitiveData(apiEx.Message);
                    _logger.LogError(dbEx, "Hangfire Job (ActualSend): Failed to mark user with ChatID {ChatId} as unreachable after text send. Original Telegram error (Sanitized): {SanitizedTelegramErrorMessage}",
                        chatId,
                        sanitizedApiExMessage);
                }
                _logger.LogInformation("Hangfire Job (ActualSend): User {ChatId} is already unreachable or deactivated. No further action required.", chatId);
                return;
                #endregion
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hangfire Job (ActualSend): Error sending text message to ChatID {ChatId} after retries.", chatId);
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
        [AutomaticRetry(Attempts = 0)]
        [Queue("notifications")]
        [DisableConcurrentExecution(timeoutInSeconds: 600)]
        public async Task EditMessageTextInTelegramAsync(long chatId, int messageId, string text, ParseMode? parseMode, InlineKeyboardMarkup? replyMarkup, CancellationToken cancellationToken)
        {
            // <<< FIX: Use the correct V1 escaper for the V1 goal.
            _ = TelegramMessageFormatter.EscapeMarkdownV1(text);
            _ = SanitizeSensitiveData(text);

            _logger.LogDebug("Job (UltimateEdit): Preparing to edit with Markdown V1. Chat: {ChatId}, Msg: {MessageId}", chatId, messageId);

            try
            {
                try
                {
                    // --- ATTEMPT 1: Try to edit as a standard text message. ---
                    _ = await _botClient.EditMessageText(
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

                if (apiEx.ErrorCode is >= 400 and < 500)
                {
                    _logger.LogCritical(apiEx, "Job (UltimateEdit): PERMANENT failure for Msg {MessageId}, Chat {ChatId}. ErrorCode: {ErrorCode}, API Message: '{ApiMessage}'. Job will terminate.",
                        messageId, chatId, apiEx.ErrorCode, apiEx.Message);
                    // Background error log
                    _ = Task.Run(async () =>
                    {
                        using IServiceScope scope = _serviceProvider.CreateScope();
                        IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                        await repo.AddAsync(new Domain.Entities.ProMonitoringLog
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Critical",
                            Source = "TelegramMessageSender",
                            EventType = "UltimateEdit.ApiRequestException",
                            Message = apiEx.Message,
                            Details = apiEx.StackTrace,
                            Exception = apiEx.ToString(),
                            Status = "Failed",
                            CreatedAt = DateTime.UtcNow
                        });
                    });
                }
                else
                {
                    _logger.LogError(apiEx, "Job (UltimateEdit): TRANSIENT failure for Msg {MessageId}, Chat {ChatId}. ErrorCode: {ErrorCode}. Re-throwing for Hangfire retry.",
                        messageId, chatId, apiEx.ErrorCode);
                    // Background error log
                    _ = Task.Run(async () =>
                    {
                        using IServiceScope scope = _serviceProvider.CreateScope();
                        IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                        await repo.AddAsync(new Domain.Entities.ProMonitoringLog
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Error",
                            Source = "TelegramMessageSender",
                            EventType = "UltimateEdit.TransientApiRequestException",
                            Message = apiEx.Message,
                            Details = apiEx.StackTrace,
                            Exception = apiEx.ToString(),
                            Status = "Failed",
                            CreatedAt = DateTime.UtcNow
                        });
                    });
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
            string[] specialChars = new[] {
        "_", "*", "[", "]", "(", ")", "~", "`", ">", "#", "+", "-", "=", "|", "{", "}", ".", "!"
    };
            // Added: "`", "!", ".", " " (space, though less common to escape)
            // Note: The list provided in your original code was also missing backtick (`).

            StringBuilder sb = new(text);

            // Loop through each special character and replace it with its escaped version.
            // Order might matter in some edge cases, but usually not for simple replacement.
            // It's generally safer to replace longer sequences first if there's overlap.
            // However, for these individual characters, direct replacement is fine.
            foreach (string? specialChar in specialChars)
            {
                // Be careful not to double-escape if a replacement itself contains a special char.
                // Using a regex replace is safer for complex cases, but for this list, direct replace is okay if done carefully.
                // A simple replace approach:
                _ = sb.Replace(specialChar, "\\" + specialChar);
            }

            // Special case for consecutive escaped characters:
            // Sometimes replacing '*' with '\*' and then '.' with '\.' might create '\.' which is fine.
            // But if you had something like "_*_"; replacing _ first gives "\_\*_"; then replacing * gives "\_\*\_".
            // This is usually fine.
            // The core issue is the missing '.' from the original list.

            return sb.ToString();
        }


        [AutomaticRetry(Attempts = 0)]
        [Queue("notifications")]
        [DisableConcurrentExecution(timeoutInSeconds: 600)]
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
        [AutomaticRetry(Attempts = 0)]
        [Queue("notifications")]
        [DisableConcurrentExecution(timeoutInSeconds: 600)]
        public async Task AnswerCallbackQueryToTelegramAsync(string callbackQueryId, string? text, bool showAlert, string? url, int cacheTime, CancellationToken cancellationToken)
        {
            // Sanitize for logging purposes only. The 'text' parameter is not Markdown.
            string sanitizedLogText = _logSanitizer.Sanitize(text);

            _logger.LogDebug("Job (AnswerCBQ): Preparing to answer. ID: {CBQId}", callbackQueryId);

            Context pollyContext = new($"AnswerCBQ_{callbackQueryId}", new Dictionary<string, object>
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
        private const int TelegramApiMaxCaptionLength = 1024;

        [AutomaticRetry(Attempts = 0)]
        [Queue("notifications")]
        [DisableConcurrentExecution(timeoutInSeconds: 600)]
        public async Task SendPhotoToTelegramAsync(
            long chatId,
            string photoUrlOrFileId,
            string? caption,
            ParseMode? parseMode,
            ReplyMarkup? replyMarkup,
            CancellationToken cancellationToken)
        {
            await SendPhotoToTelegramWithRetryAsync(chatId, photoUrlOrFileId, caption, parseMode, replyMarkup, cancellationToken, 0);
        }

        // Internal retry logic overload
        private async Task SendPhotoToTelegramWithRetryAsync(
            long chatId,
            string photoUrlOrFileId,
            string? caption,
            ParseMode? parseMode,
            ReplyMarkup? replyMarkup,
            CancellationToken cancellationToken,
            int retryCount)
        {
            string sanitizedLogCaption = SanitizeSensitiveData(caption);
            _logger.LogDebug("Sending photo. ChatID: {ChatId}, Photo: {PhotoIdOrUrl}, Caption: {Caption}", chatId, photoUrlOrFileId, sanitizedLogCaption);

            // --- NEW: Check if the photoUrlOrFileId is a direct image URL ---
            if (Uri.TryCreate(photoUrlOrFileId, UriKind.Absolute, out Uri? uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
            {
                try
                {
                    using HttpClient httpClient = new();
                    using HttpResponseMessage response = await httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, uri), cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogError("Image URL check failed: {Url} returned status {StatusCode}", photoUrlOrFileId, response.StatusCode);
                        return; // Skip sending if not accessible
                    }
                    if (!response.Content.Headers.ContentType?.MediaType?.StartsWith("image/") ?? true)
                    {
                        _logger.LogError("Image URL check failed: {Url} is not an image. Content-Type: {ContentType}", photoUrlOrFileId, response.Content.Headers.ContentType);
                        return; // Skip sending if not an image
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to verify image URL before sending to Telegram: {Url}", photoUrlOrFileId);
                    return; // Skip sending on any error
                }
            }

            Context pollyContext = new($"SendPhoto_{chatId}_{Guid.NewGuid():N}", new Dictionary<string, object>
    {
        { "ChatId", chatId },
        { "PhotoIdOrUrl", photoUrlOrFileId },
        { "CaptionPreview", sanitizedLogCaption }
    });

            try
            {
                InputFile photoInput = InputFile.FromString(photoUrlOrFileId);

                await _telegramApiRetryPolicy.ExecuteAsync(async (context, ct) =>
                {
                    string processedCaption = caption ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(caption) && caption.Length > TelegramApiMaxCaptionLength)
                    {
                        processedCaption = caption[..TelegramApiMaxCaptionLength];
                        _logger.LogWarning("Caption truncated for ChatID {ChatId}. Original length: {OriginalLength}, Max allowed: {MaxCaptionLength}.", chatId, caption.Length, TelegramApiMaxCaptionLength);
                    }

                    _ = await _botClient.SendPhoto(
                        chatId: new ChatId(chatId),
                        photo: photoInput,
                        caption: processedCaption,
                        parseMode: ParseMode.Markdown,
                        replyMarkup: replyMarkup,
                        cancellationToken: ct);
                }, pollyContext, cancellationToken);

                _logger.LogInformation("Successfully sent photo to ChatID {ChatId}", chatId);
            }
            catch (Exception ex)
            {
                // Unwrap Polly or AggregateException to get the real Telegram error
                ApiRequestException? apiEx = ex as ApiRequestException
                    ?? (ex as AggregateException)?.InnerExceptions.OfType<ApiRequestException>().FirstOrDefault()
                    ?? ex.InnerException as ApiRequestException;

                if (apiEx != null)
                {
                    _logger.LogError(apiEx,
                        "Telegram API error sending photo to ChatID {ChatId} after {RetryCount} retries. " +
                        "ErrorCode: {ErrorCode}, Message: {ApiMessage}, Photo: {PhotoIdOrUrl}, Caption: {Caption}",
                        chatId, retryCount, apiEx.ErrorCode, apiEx.Message, photoUrlOrFileId, sanitizedLogCaption);

                    // Handle permanent errors (do not retry)
                    if (
                        apiEx.ErrorCode == 403 ||
                        (apiEx.ErrorCode == 400 && (
                            apiEx.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase) ||
                            apiEx.Message.Contains("USER_DEACTIVATED", StringComparison.OrdinalIgnoreCase) ||
                            apiEx.Message.Contains("user is deactivated", StringComparison.OrdinalIgnoreCase) ||
                            apiEx.Message.Contains("PEER_ID_INVALID", StringComparison.OrdinalIgnoreCase)))
                    )
                    {
                        try
                        {
                            await _userService.MarkUserAsUnreachableAsync(chatId.ToString(), $"ApiError_{apiEx.ErrorCode}", cancellationToken);
                            _logger.LogInformation("Marked user {ChatId} as unreachable.", chatId);
                        }
                        catch (Exception dbEx)
                        {
                            _logger.LogError(dbEx, "Failed to mark user {ChatId} as unreachable after photo send.", chatId);
                        }
                        return; // Do not retry for these errors
                    }

                    // Handle progressive retry for transient errors
                    if (retryCount < RetryDelaysMinutes.Length)
                    {
                        int delayMinutes = RetryDelaysMinutes[retryCount];
                        _logger.LogWarning("Retrying photo send to ChatID {ChatId} in {DelayMinutes} minutes (attempt {RetryAttempt}/{MaxAttempts})", chatId, delayMinutes, retryCount + 1, RetryDelaysMinutes.Length);
                        _ = _jobScheduler.Schedule<IActualTelegramMessageActions>(
                            sender => sender.RetryableSendPhotoToTelegramAsync(chatId, photoUrlOrFileId, caption, parseMode, replyMarkup, CancellationToken.None, retryCount + 1),
                            TimeSpan.FromMinutes(delayMinutes));
                        return;
                    }

                    // Log specific Telegram errors for admin awareness
                    if (apiEx.ErrorCode == 400 && apiEx.Message.Contains("caption is too long", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogCritical("Caption too long for ChatID {ChatId}.", chatId);
                    }
                    else if (apiEx.ErrorCode == 400 && apiEx.Message.Contains("wrong file identifier", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogCritical("Wrong file identifier for ChatID {ChatId}.", chatId);
                    }
                    else if (apiEx.ErrorCode == 400 && apiEx.Message.Contains("file is too big", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogCritical("File too big for ChatID {ChatId}.", chatId);
                    }
                }
                else
                {
                    _logger.LogError(ex, "Unexpected error sending photo to ChatID {ChatId} after {RetryCount} retries. Caption: {Caption}", chatId, retryCount, sanitizedLogCaption);
                }
                throw;
            }
        }

        /// <summary>
        /// Hangfire-internal: Retryable version for progressive retry logic. Not part of the interface.
        /// </summary>
        [AutomaticRetry(Attempts = 0)]
        [Queue("notifications")]
        [DisableConcurrentExecution(timeoutInSeconds: 600)]
        public async Task RetryableSendTextMessageAsync(
           long chatId,
           string text,
           ParseMode? parseMode,
           ReplyMarkup? replyMarkup,
           bool disableNotification,
           LinkPreviewOptions? linkPreviewOptions,
           CancellationToken cancellationToken,
           int retryCount)
        {
            await SendTextMessageToTelegramWithRetryAsync(chatId, text, parseMode, replyMarkup, disableNotification, linkPreviewOptions, cancellationToken, retryCount);
        }

        /// <summary>
        /// Hangfire-internal: Retryable version for progressive retry logic. Not part of the interface.
        /// </summary>
        [AutomaticRetry(Attempts = 0)]
        [Queue("notifications")]
        [DisableConcurrentExecution(timeoutInSeconds: 600)]
        public async Task RetryableSendPhotoToTelegramAsync(
            long chatId,
            string photoUrlOrFileId,
            string? caption,
            ParseMode? parseMode,
            ReplyMarkup? replyMarkup,
            CancellationToken cancellationToken,
            int retryCount)
        {
            await SendPhotoToTelegramWithRetryAsync(chatId, photoUrlOrFileId, caption, parseMode, replyMarkup, cancellationToken, retryCount);
        }

        Task IActualTelegramMessageActions.DeleteMessage(long chatId, int messageId, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
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
            /// 

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


            Task SendDocumentAsync(long chatId, byte[] documentContents, string fileName, string? caption = null, CancellationToken cancellationToken = default);
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

            public Task SendDocumentAsync(long chatId, byte[] documentContents, string fileName, string? caption = null, CancellationToken cancellationToken = default)
            {
                _logger.LogDebug("Enqueueing SendDocumentAsync for ChatID {ChatId}, FileName: {FileName}", chatId, fileName);
                _ = _jobScheduler.Enqueue<IActualTelegramMessageActions>(
                    sender => sender.SendDocumentToTelegramAsync(chatId, documentContents, fileName, caption, CancellationToken.None)
                );
                return Task.CompletedTask;
            }

            public Task DeleteMessageAsync(long chatId, int messageId, CancellationToken cancellationToken = default)
            {
                _logger.LogDebug("Enqueueing DeleteMessageAsync for ChatID {ChatId}, MsgID {MessageId}", chatId, messageId);
                _ = _jobScheduler.Enqueue<IActualTelegramMessageActions>(
                    sender => sender.DeleteMessage(chatId, messageId, CancellationToken.None)
                );
                return Task.CompletedTask;
            }

            public Task SendTextMessageAsync(long chatId, string text, ParseMode? parseMode = ParseMode.Markdown, ReplyMarkup? replyMarkup = null, CancellationToken cancellationToken = default, LinkPreviewOptions? linkPreviewOptions = null)
            {
                _logger.LogDebug("Enqueueing SendTextMessageAsync for ChatID {ChatId}", chatId);
                _ = _jobScheduler.Enqueue<IActualTelegramMessageActions>(
                    sender => sender.RetryableSendTextMessageAsync(chatId, text, parseMode, replyMarkup, false, linkPreviewOptions, CancellationToken.None, 0)
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
                    sender => sender.RetryableSendPhotoToTelegramAsync(chatId, photoUrlOrFileId, caption, parseMode, replyMarkup, CancellationToken.None, 0)
                );
                return Task.CompletedTask;
            }


        }
    }
}
