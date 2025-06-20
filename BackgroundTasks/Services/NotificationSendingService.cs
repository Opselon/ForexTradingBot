// File: BackgroundTasks/Services/NotificationSendingService.cs

#region Usings
// Standard .NET & NuGet
// Project specific
using Application.Common.Interfaces;
using Application.DTOs.Notifications;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums; // For UserLevel enum
using Hangfire;
using Microsoft.Data.SqlClient;
using Polly;
using Polly.Retry;
using Shared.Extensions;
using StackExchange.Redis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Telegram.Bot;

// Telegram.Bot
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Formatters; // For TelegramMessageFormatter
using TelegramPanel.Infrastructure;
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions;
#endregion

namespace BackgroundTasks.Services
{
    /// <summary>
    /// Implements the Hangfire job for sending notifications using a cache-first strategy
    /// with a focus on responsible, non-spam communication.
    /// </summary>
    public class NotificationSendingService : INotificationSendingService
    {
        #region Dependencies & Configuration
        private readonly ILogger<NotificationSendingService> _logger;
        private readonly ITelegramMessageSender _telegramMessageSender;
        private readonly IUserService _userService;
        private readonly INewsItemRepository _newsItemRepository;
        private readonly IDatabase _redisDb;
        private readonly INotificationRateLimiter _rateLimiter;
        private readonly AsyncRetryPolicy _telegramApiRetryPolicy;
        private readonly ITelegramBotClient _botClient;
        private readonly int _maxRetries = 5;
        private readonly TimeSpan _baseDelay = TimeSpan.FromSeconds(2); // Base delay for
        private const int PerMessageSendDelayMs = 100;
        #endregion

        #region Constructor


        /// <summary>
        /// Initializes a new instance of the <see cref="NotificationSendingService"/> class.
        /// This constructor is responsible for injecting all necessary external dependencies
        /// and setting up resilient policies, particularly a Polly retry policy, for handling
        /// transient errors during interactions with the Telegram Bot API.
        /// </summary>
        /// <param name="logger">The logging instance used for recording operational events, diagnostics, and errors within the service.</param>
        /// <param name="botClient">The low-level Telegram bot client, providing direct access to Telegram Bot API methods (e.g., SendPhoto, SendMessage).</param>
        /// <param name="telegramMessageSender">An abstraction for sending various types of Telegram messages, likely wrapping <paramref name="botClient"/> with additional logic.</param>
        /// <param name="userService">The service responsible for managing and retrieving user-related information, essential for fetching user profiles (e.g., user level for rate limiting) and marking users as unreachable.</param>
        /// <param name="newsItemRepository">The data repository for accessing <see cref="NewsItem"/> data from the persistence layer, used to retrieve full news article details.</param>
        /// <param name="redisConnection">The Redis connection multiplexer, providing access to the Redis database for fetching cached user lists used in notification dispatch.</param>
        /// <param name="rateLimiter">The service responsible for enforcing notification rate limits based on user activity and subscription levels, preventing abuse and ensuring fair usage.</param>
        /// <returns>
        /// A new instance of <see cref="NotificationSendingService"/>, ready to handle notification dispatch requests.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown if any of the following required dependencies are <c>null</c>:
        /// <list type="bullet">
        ///     <item><description><paramref name="logger"/></description></item>
        ///     <item><description><paramref name="botClient"/></description></item>
        ///     <item><description><paramref name="telegramMessageSender"/></description></item>
        ///     <item><description><paramref name="userService"/></description></item>
        ///     <item><description><paramref name="newsItemRepository"/></description></item>
        ///     <item><description><paramref name="redisConnection"/></description></item>
        ///     <item><description><paramref name="rateLimiter"/></description></item>
        /// </list>
        /// </exception>
        public NotificationSendingService(
            ILogger<NotificationSendingService> logger,
             ITelegramBotClient botClient,
            ITelegramMessageSender telegramMessageSender,
            IUserService userService,
            INewsItemRepository newsItemRepository,
            IConnectionMultiplexer redisConnection,
            INotificationRateLimiter rateLimiter)
        {
            _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _telegramMessageSender = telegramMessageSender ?? throw new ArgumentNullException(nameof(telegramMessageSender));
            _userService = userService ?? throw new ArgumentNullException(nameof(userService));
            _newsItemRepository = newsItemRepository ?? throw new ArgumentNullException(nameof(newsItemRepository));
            _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));

            if (redisConnection == null)
            {
                throw new ArgumentNullException(nameof(redisConnection));
            }

            _redisDb = redisConnection.GetDatabase();

            _telegramApiRetryPolicy = Policy
         .Handle<ApiRequestException>(ex =>
         {
             // Handle Rate Limit Errors (429)
             if (ex.ErrorCode == 429)
             {
                 _logger.LogDebug("Detected Telegram API Rate Limit error (429).");
                 return true;
             }

             // Handle Flood Control Errors (often returned as 400 Bad Request)
             // Telegram API documentation primarily states 429 for flood, but it's common
             // to see 400 with a flood message. Uncomment if you observe this behavior.
             /*
             if (ex.ErrorCode == 400 && ex.Message.Contains("flood", StringComparison.OrdinalIgnoreCase))
             {
                 _logger.LogDebug("Detected Telegram API Flood Control error (400 with flood message).");
                 return true;
             }
             */

             // Handle Telegram Server-side Errors (5xx)
             if (ex.ErrorCode >= 500 && ex.ErrorCode < 600)
             {
                 _logger.LogDebug("Detected Telegram API Server Error ({ErrorCode}).", ex.ErrorCode);
                 return true;
             }

             // Do NOT retry for other errors like 401 (Unauthorized), 404 (Not Found), etc.
             return false;
         })
         .Or<HttpRequestException>() // Handles general network-related errors
         .Or<TaskCanceledException>() // Handles timeouts if a cancellation token is used or a timeout policy expires
         .WaitAndRetryAsync(
             _maxRetries, // Total number of retries
             retryAttempt =>
             {
                 // Exponential backoff with jitter
                 // Formula: baseDelay * 2^retryAttempt + random_jitter
                 double delaySeconds = _baseDelay.TotalSeconds * Math.Pow(2, retryAttempt);
                 // Add a random jitter (e.g., up to 1 second) to spread out retries
                 double jitter = Random.Shared.NextDouble() * 1.0; // Use Random.Shared for efficiency
                 return TimeSpan.FromSeconds(delaySeconds + jitter);
             },
             onRetry: (exception, timespan, retryCount, context) =>
             {
                 // Log the retry attempt for better visibility
                 string errorMessage = exception switch
                 {
                     ApiRequestException apiEx => $"Telegram API ErrorCode {apiEx.ErrorCode}: {apiEx.Message}",
                     HttpRequestException reqEx => $"HTTP Request Error: {reqEx.Message}",
                     TaskCanceledException => "Task Canceled (potential timeout)",
                     _ => "Unknown Error"
                 };

                 _logger.LogWarning(
                     exception, // Pass the exception for detailed logging
                     "Telegram API operation failed: {ErrorMessage}. Retrying attempt {RetryCount}/{MaxRetries} after {Timespan}...",
                     errorMessage,
                     retryCount,
                     _maxRetries,
                     timespan
                 );
             }
         );
        }
        #endregion



        /// <summary>
        /// **[DEPRECATED AND NON-FUNCTIONAL]**
        /// This method previously orchestrated the sending of batch notifications to a list of Telegram users.
        /// It has been explicitly marked as obsolete and is no longer used or supported.
        /// Its functionality has been entirely superseded by a more efficient and robust "cache-first dispatch pattern,"
        /// which improves performance and scalability for managing user notifications.
        /// </summary>
        /// <remarks>
        /// Due to being marked with `[Obsolete(..., true)]`, attempting to call this method in new code will result in a **compilation error**.
        /// If, for any reason, older compiled code were to invoke this method at runtime, it would:
        /// <list type="bullet">
        ///     <item><description>Log a warning message indicating that an obsolete method was called.</description></item>
        ///     <item><description>Immediately return a completed <see cref="Task"/> without performing any notification dispatch or actual work.</description></item>
        /// </list>
        /// This ensures that while the old signature remains for runtime compatibility, it is actively discouraged and functionally inert.
        /// </remarks>
        /// <param name="targetTelegramUserIdList">This parameter is ignored; no users will receive notifications from this method.</param>
        /// <param name="messageText">This parameter is ignored; no message will be sent.</param>
        /// <param name="imageUrl">This parameter is ignored; no image will be used.</param>
        /// <param name="buttons">This parameter is ignored; no buttons will be generated.</param>
        /// <param name="newsItemId">This parameter is ignored; no specific news item is processed.</param>
        /// <param name="newsItemSignalCategoryId">This parameter is ignored.</param>
        /// <param name="newsItemSignalCategoryName">This parameter is ignored.</param>
        /// <param name="jobCancellationToken">This parameter is ignored; no asynchronous work is performed that could be cancelled.</param>
        /// <returns>
        /// A <see cref="Task"/> that is always already completed (<see cref="Task.CompletedTask"/>).
        /// The returned task signifies that the method has finished its (non-)operation immediately,
        /// without performing any actual asynchronous work or notification dispatch due to its deprecated status.
        /// </returns>
        [Obsolete("This method is deprecated. Use the cache-first dispatch pattern instead.", true)]
        public Task SendBatchNotificationAsync(List<long> targetTelegramUserIdList, string messageText, string? imageUrl, List<NotificationButton> buttons, Guid newsItemId, Guid? newsItemSignalCategoryId, string? newsItemSignalCategoryName, CancellationToken jobCancellationToken)
        {
            _logger.LogWarning("Obsolete method SendBatchNotificationAsync was called and will do nothing.");
            // This method now does nothing and completes immediately.
            return Task.CompletedTask;
        }

        /// <summary>
        /// Processes and sends a consolidated batch notification containing multiple news items to a single specified Telegram user.
        /// This Hangfire background job serves as a key delivery mechanism for aggregated AI-analyzed news.
        /// It first retrieves the full details of all specified news items (which have likely been previously processed and enriched by AI),
        /// then constructs a single, formatted MarkdownV2 message summarizing these items. Finally, it dispatches this consolidated message
        /// to the target user via the Telegram API, incorporating robust error handling and graceful failure scenarios.
        /// </summary>
        /// <param name="targetUserId">The Telegram user ID to whom the consolidated batch notification should be sent. This user is the recipient of the AI's aggregated news insights.</param>
        /// <param name="newsItemIds">A <see cref="List{Guid}"/> representing the unique identifiers of the news items to be included in this batch notification. These IDs link back to the AI-processed news data.</param>
        /// <returns>
        /// A <see cref="Task"/> that represents the asynchronous batch notification operation. The task's completion status directly reflects the Hangfire job's outcome:
        /// <list type="bullet">
        ///     <item><description>
        ///         **Completes Successfully (Hangfire 'Succeeded'):**
        ///         If the consolidated message is sent successfully to the user.
        ///         Also, if the input <paramref name="newsItemIds"/> list is empty or if no valid news items are found for the provided IDs after database lookup (these scenarios are treated as successful "no-op" or gracefully handled stops, meaning no critical failure occurred that prevents the job from finishing its designated task).
        ///     </description></item>
        ///     <item><description>
        ///         **Re-throws an Exception (Hangfire 'Failed'):**
        ///         If a critical, unhandled error occurs during the job's execution (e.g., persistent database issues when fetching news items, an unrecoverable exception during message construction, or an unhandled error during the final message send operation). Re-throwing ensures Hangfire marks the job as 'Failed', signaling a need for manual inspection or automated retry management at the Hangfire level.
        ///     </description></item>
        /// </list>
        /// </returns>
        [AutomaticRetry(Attempts = 0)]
        [JobDisplayName("Send Batch of {1.Count} News Items to User: {0}")]
        public async Task ProcessBatchNotificationForUserAsync(long targetUserId, List<Guid> newsItemIds)
        {
            try
            {
                if (newsItemIds == null || !newsItemIds.Any())
                {
                    _logger.LogWarning("Job for user {UserId} received an empty list of news IDs. Nothing to process.", targetUserId);
                    return; // Gracefully stop if no news items are provided.
                }

                // 1. Fetch all news item entities from the database.
                // Iterates through provided IDs and fetches corresponding NewsItem objects.
                List<NewsItem> newsItems = [];
                foreach (Guid id in newsItemIds)
                {
                    NewsItem? item = await _newsItemRepository.GetByIdAsync(id, CancellationToken.None);
                    if (item != null)
                    {
                        newsItems.Add(item);
                    }
                    else
                    {
                        _logger.LogWarning("News item with ID {NewsItemId} not found for batch send to user {UserId}.", id, targetUserId);
                    }
                }

                if (!newsItems.Any())
                {
                    _logger.LogWarning("No valid news items found for batch send to user {UserId} after fetching from DB. Aborting job.", targetUserId);
                    return; // Gracefully stop if none of the provided IDs resolve to valid news items.
                }

                // 2. Build ONE consolidated message from all the news items.
                // Formats a summary message, including titles and truncated summaries of the first few items.
                StringBuilder messageBuilder = new();
                _ = messageBuilder.AppendLine($"*{newsItems.Count} new updates for you:*");
                _ = messageBuilder.AppendLine();

                // To keep the message from getting too long, we'll only show details for the first few items.
                foreach (NewsItem? item in newsItems.Take(5))
                {
                    _ = messageBuilder.AppendLine($"▫️ *{TelegramMessageFormatter.EscapeMarkdownV2(item.Title)}*");
                    if (!string.IsNullOrWhiteSpace(item.Summary))
                    {
                        string summary = TelegramMessageFormatter.EscapeMarkdownV2(item.Summary.Truncate(120));
                        _ = messageBuilder.AppendLine($"  _{summary}_");
                    }
                    _ = messageBuilder.AppendLine();
                }

                if (newsItems.Count > 5)
                {
                    _ = messageBuilder.AppendLine($"...and {newsItems.Count - 5} more articles.");
                }

                // 3. Prepare the final payload for the sender method.
                // Creates a payload object containing the target user, the consolidated message, and no specific buttons for batch.
                NotificationJobPayload payload = new()
                {
                    TargetTelegramUserId = targetUserId,
                    MessageText = messageBuilder.ToString(),
                    Buttons = [] // Batch notifications usually don't have specific buttons.
                };

                // 4. Call the final "sender" helper to send the message to the Telegram API.
                // This delegates to another private method responsible for actual Telegram API interaction, including retries and self-healing.
                await ProcessSingleNotification(payload, CancellationToken.None);

                _logger.LogInformation("Successfully sent a batch of {Count} news items to user {UserId}", newsItems.Count, targetUserId);
            }
            catch (Exception ex)
            {
                // Catch any unhandled exceptions, log them as critical, and re-throw.
                // Re-throwing is crucial for Hangfire to mark the job as 'Failed', allowing for manual inspection and re-queuing if necessary.
                _logger.LogCritical(ex, "Failed to process batch notification for user {UserId}", targetUserId);
                throw;
            }
        }

        /// <summary>
        /// The primary Hangfire background job method responsible for processing and dispatching a single news notification
        /// to a specific Telegram user. This job implements multiple crucial safeguards and business rules to ensure
        /// responsible and resilient notification delivery in our AI analysis program.
        /// <br/><br/>
        /// **Workflow:**
        /// <list type="number">
        ///     <item><description>Retrieves the target user's Telegram ID from a pre-populated Redis cache list, identified by a cache key and an index.</description></item>
        ///     <item><description>Fetches the user's full profile to determine their subscription level (e.g., Free, Bronze, Platinum).</description></item>
        ///     <item><description>Applies dynamic rate limits based on the user's subscription level, skipping the notification if the user has exceeded their hourly quota.</description></item>
        ///     <item><description>Retrieves the full details of the news article (identified by <paramref name="newsItemId"/>) from the database.</description></item>
        ///     <item><description>Constructs the complete notification payload, including formatted message text, image URL, and interactive buttons.</description></item>
        ///     <item><description>Dispatches the final notification to the Telegram Bot API via a dedicated sender method, which incorporates its own retry logic.</description></item>
        /// </list>
        /// <br/>
        /// **Resilience and Self-Healing:**
        /// The method is designed to be highly resilient against various runtime conditions:
        /// <list type="bullet">
        ///     <item><description>Gracefully handles scenarios where the cached user list or the specific user ID might be missing or invalid.</description></item>
        ///     <item><description>Skips notifications for users who no longer exist in the database or have blocked the bot, logging these events appropriately.</description></item>
        ///     <item><description>Includes a critical self-healing mechanism: upon detecting a permanent "bot blocked" or "chat not found" error from Telegram (HTTP 403), it automatically marks the user as unreachable in the database to prevent future dispatch attempts to that user.</description></item>
        ///     <item><description>Logs and re-throws any other unexpected critical exceptions (e.g., database connectivity issues, serialization errors) to ensure Hangfire marks the job as 'Failed' for review and potential manual intervention.</description></item>
        /// </list>
        /// <br/>
        /// **AI Analysis Impact:**
        /// The logs generated by this method (e.g., successful dispatches, rate limit skips, user blocks, critical failures) are crucial for future AI analysis. They provide rich data for:
        /// <list type="bullet">
        ///     <item><description>Evaluating the effectiveness of AI-driven signal delivery.</description></item>
        ///     <item><description>Analyzing user engagement and potential churn based on notification delivery outcomes.</description></item>
        ///     <item><description>Optimizing AI-driven content relevance by correlating delivery success with content characteristics.</description></item>
        ///     <item><description>Detecting anomalies in system behavior or external API performance.</description></item>
        /// </list>
        /// </summary>
        /// <param name="newsItemId">The unique identifier of the news article (processed by AI) to be sent as part of this notification.</param>
        /// <param name="userListCacheKey">The Redis cache key pointing to a serialized list of Telegram user IDs for this specific notification batch. This ensures efficient access to target users.</param>
        /// <param name="userIndex">The zero-based index within the cached user list, specifying the exact target user for this individual job instance.</param>
        /// <returns>
        /// A <see cref="Task"/> that represents the asynchronous background job operation.
        /// <list type="bullet">
        ///     <item><description>The task completes successfully (and the Hangfire job is marked 'Succeeded') if:
        ///         <list type="circle">
        ///             <item><description>The notification is successfully sent to the user.</description></item>
        ///             <item><description>The job is gracefully stopped due to missing cache data, an invalid user index, the user no longer existing, or the user being over their rate limit (these are considered successful business rule enforcements, not errors).</description></item>
        ///             <item><description>A permanent Telegram API error (like 403 'bot blocked') occurs, and the self-healing mechanism successfully marks the user as unreachable.</description></item>
        ///         </list>
        ///     </description></item>
        ///     <item><description>The task re-throws an <see cref="Exception"/> (causing the Hangfire job to be marked 'Failed') if a critical, unhandled error occurs (e.g., database issues fetching the news item, Redis connectivity problems, or other unexpected internal exceptions that prevent completion and are not part of a graceful exit or self-healing process).</description></item>
        /// </list>
        /// </returns>
        [DisableConcurrentExecution(timeoutInSeconds: 600)]
        [JobDisplayName("Process RSS Notification: News {0}, UserIndex {2}")]
        [AutomaticRetry(Attempts = 3)]
        public async Task ProcessNotificationFromCacheAsync(Guid newsItemId, string userListCacheKey, int userIndex)
        {
            const int freeUserRssHourlyLimit = 20;
            const int vipUserRssHourlyLimit = 100;
            TimeSpan rssLimitPeriod = TimeSpan.FromMinutes(15);
            long targetUserId = -1;

            // --- THE FIX: Declare 'payload' here with a default value. ---
            // This makes it accessible in both the 'try' and 'catch' blocks.
            NotificationJobPayload? payload = null;

            using IDisposable? logScope = _logger.BeginScope(new Dictionary<string, object?> { ["NewsItemId"] = newsItemId, ["UserIndex"] = userIndex, ["CacheKey"] = userListCacheKey });

            try
            {
                RedisValue serializedUserIds = await _redisDb.StringGetAsync(userListCacheKey);
                if (!serializedUserIds.HasValue || serializedUserIds.IsNullOrEmpty)
                {
                    _logger.LogError("Job aborted: Cache key {CacheKey} not found or empty.", userListCacheKey);
                    return;
                }
                List<long>? allUserIds = JsonSerializer.Deserialize<List<long>>(serializedUserIds.ToString());
                if (allUserIds == null || userIndex >= allUserIds.Count)
                {
                    _logger.LogError("Job aborted: User index {UserIndex} is out of bounds for the list (Size: {ListSize}).", userIndex, allUserIds?.Count ?? 0);
                    return;
                }
                targetUserId = allUserIds[userIndex];
                _logger.LogInformation("Processing job for UserID: {TargetUserId}", targetUserId);

                Application.DTOs.UserDto? userDto = await _userService.GetUserByTelegramIdAsync(targetUserId.ToString(), CancellationToken.None);
                if (userDto == null)
                {
                    _logger.LogWarning("Job skipped: User {TargetUserId} no longer exists.", targetUserId);
                    return;
                }

                int applicableLimit = (userDto.Level is UserLevel.Platinum or UserLevel.Bronze) ? vipUserRssHourlyLimit : freeUserRssHourlyLimit;
                if (await _rateLimiter.IsUserAtOrOverLimitAsync(targetUserId, applicableLimit, rssLimitPeriod))
                {
                    _logger.LogInformation("SKIP: User {TargetUserId} has met the rate limit of {Limit}.", targetUserId, applicableLimit);
                    return;
                }

                NewsItem? newsItem = await _newsItemRepository.GetByIdAsync(newsItemId, CancellationToken.None);
                if (newsItem == null)
                {
                    _logger.LogError("Job failed: NewsItem {NewsItemId} not found.", newsItemId);
                    throw new InvalidOperationException($"NewsItem {newsItemId} not found.");
                }

                // --- Now we assign the value to the already-declared variable ---
                payload = new()
                {
                    TargetTelegramUserId = targetUserId,
                    MessageText = EscapeTelegramMarkdownV2(BuildMessageText(newsItem)),
                    UseMarkdown = true,
                    ImageUrl = newsItem.ImageUrl ?? "https://i.postimg.cc/3RmJjBjY/Breaking-News.jpg",
                    Buttons = BuildSimpleNotificationButtons(newsItem),
                    NewsItemId = newsItemId
                };

                await SendToTelegramAsync(payload, CancellationToken.None);

                await _rateLimiter.IncrementUsageAsync(targetUserId, rssLimitPeriod);
                _logger.LogTrace("Rate limit counter incremented for user {UserId}.", targetUserId);
            }
            catch (ApiRequestException apiEx)
            {
                // Now 'payload' is accessible here.
                var sanitizedMessageForLog = EscapeAndTruncate(payload?.MessageText ?? "[payload was not created]");

                if (apiEx.ErrorCode >= 400 && apiEx.ErrorCode < 500)
                {
                    _logger.LogCritical(apiEx,
                        "PERMANENT Telegram API failure for User {TargetUserId}. ErrorCode: {ErrorCode}. API Message: '{ApiErrorMessage}'. Job will NOT be retried. Offending Content (Sanitized): '{SanitizedContent}'",
                        targetUserId,
                        apiEx.ErrorCode,
                        apiEx.Message,
                        sanitizedMessageForLog);
                }
                else
                {
                    _logger.LogError(apiEx,
                        "TRANSIENT Telegram API failure for User {TargetUserId}. ErrorCode: {ErrorCode}. API Message: '{ApiErrorMessage}'. Hangfire will retry. Offending Content (Sanitized): '{SanitizedContent}'",
                        targetUserId,
                        apiEx.ErrorCode,
                        apiEx.Message,
                        sanitizedMessageForLog);
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "FATAL non-API failure during job for User {TargetUserId}. Hangfire will retry.", targetUserId);
                throw;
            }
        }

        private string EscapeAndTruncate(string text, int maxLength = 200)
        {
            if (string.IsNullOrEmpty(text)) return "[empty]";
            // Basic sanitization: remove newlines and truncate for clean logging.
            var sanitized = text.Replace("\n", " ").Replace("\r", "");
            return sanitized.Length <= maxLength ? sanitized : sanitized.Substring(0, maxLength) + "...";
        }


        /// <summary>
        /// Escapes characters in a string that are reserved in Telegram's MarkdownV2 format.
        /// </summary>
        /// <param name="text">The raw text to escape.</param>
        /// <returns>A string safe to be sent with ParseMode.MarkdownV2.</returns>
        private string EscapeTelegramMarkdownV2(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            // This regex pattern matches any single character that is one of the special characters
            // required to be escaped for MarkdownV2.
            const string markdownV2Pattern = @"([_\[\]()~`>#\+\-=\|{}\.!\*])";

            // The replacement string "$1" is a backreference to the captured group (the character itself).
            // We prepend it with a literal backslash (`\\`) to perform the escape.
            return Regex.Replace(text, markdownV2Pattern, @"\$1");
        }


        #region Private Helpers
        /// <summary>
        /// Determines if a given exception represents a transient error that is potentially
        /// recoverable by retrying the operation. This is used to decide whether Hangfire
        /// should retry a job or if the error requires investigation/manual intervention.
        /// </summary>
        /// <param name="e">The exception to evaluate.</param>
        /// <returns><c>true</c> if the exception is likely transient; otherwise, <c>false</c>.</returns>
        private bool IsTransientException(Exception e)
        {
            // --- Check the exception itself ---

            // Database Exceptions (Specific transient error codes often used by cloud databases like Azure SQL)
            // *** CHANGE HERE: Use Microsoft.Data.SqlClient.SqlException ***
            if (e is Microsoft.Data.SqlClient.SqlException sqlEx)
            {
                // Common SQL transient error codes (e.g., from Azure SQL Database)
                switch (sqlEx.Number)
                {
                    case 4060: // Cannot open database requested by the login. The login failed.
                    case 40197: // The service encountered an error processing your request. Please try again.
                    case 10928: // Resource ID: %d. The %s limit for the database is %d and has been reached.
                    case 10929: // Resource ID: %d. The %s minimum guarantee is %d, maximum limit is %d and the current usage for the database is %d.
                    case 10053: // A transport-level error has occurred when receiving results from the server.
                    case 10054: // A transport-level error has occurred when sending the request to the server.
                    case 10060: // A network-related or instance-specific error occurred while establishing a connection to SQL Server.
                    case 40143: // The service has encountered an error processing your request. Please try again.
                    case 233: // The client was unable to establish a connection because of an error during the prelogin process.
                    case 64: // A specified network name is no longer available.
                    case 20: // SQL Server General Network error.
                        return true;
                    default:
                        // Check if it's a deadlock error (can sometimes be transient and retryable)
                        if (sqlEx.Message.Contains("deadlock", StringComparison.OrdinalIgnoreCase)) return true;
                        break;
                }
            }

            // Other common database driver exceptions
            if (e is Npgsql.NpgsqlException) // MySQL driver exception
            {
                // Without specific error code knowledge, treat DB driver exceptions as potentially transient
                // unless they are clearly configuration or data errors. This is a judgment call.
                // A more robust approach would be checking specific driver error codes if available and known to be transient.
                // For simplicity here, we'll lean towards true for general DB driver exceptions.
                return true;
            }

            // Redis Exceptions (StackExchange.Redis specific)
            if (e is StackExchange.Redis.RedisConnectionException ||
                e is StackExchange.Redis.RedisTimeoutException ||
                e is StackExchange.Redis.RedisServerException && e.Message.Contains("LOADING", StringComparison.OrdinalIgnoreCase)) // Redis is loading data
            {
                return true;
            }

            // API Exceptions (Assuming ApiRequestException is from your Telegram client or a similar library)
            if (e is ApiRequestException apiEx)
            {
                // Treat server errors (5xx) and potentially rate limits (429) as transient
                if (apiEx.ErrorCode >= 500 || apiEx.ErrorCode == 429) // 429 Too Many Requests might indicate a temporary API limit
                {
                    // Note: Your internal rate limiter handles user-specific limits *before* calling SendToTelegramAsync.
                    // A 429 from Telegram would be a global or application-wide limit, or maybe specific to the bot.
                    // Retrying on 429 often requires respecting 'Retry-After' header, which Polly or the API client might handle.
                    return true;
                }
                // Note: 403 is handled specifically in the job method, so it won't reach this generic transient check.
            }

            // General Network/HTTP Exceptions
            if (e is System.Net.Sockets.SocketException ||
                e is System.IO.IOException || // Could indicate network stream issues
                e is System.Net.Http.HttpRequestException) // Generic HTTP client errors
            {
                return true;
            }

            // Timeout/Cancellation Exceptions
            if (e is System.TimeoutException ||
                e is System.Threading.Tasks.TaskCanceledException || // Often results from timeouts
                e is OperationCanceledException) // General cancellation
            {
                // Filter out intentional cancellations if needed, but for background jobs
                // cancellation often implies a timeout or shutdown, which might be transient.
                return true;
            }

            // --- Check Inner Exceptions ---
            // Sometimes the true transient cause is wrapped inside another exception
            if (e.InnerException != null)
            {
                // Recursively check the inner exception
                if (IsTransientException(e.InnerException))
                {
                    return true;
                }
            }

            // --- Default: Not considered transient ---
            // If none of the above conditions are met, assume it's a non-transient error
            // (e.g., ArgumentNullException, InvalidOperationException, configuration errors,
            // data errors like malformed input, permissions errors other than 403, etc.)
            return false;
        }

        /// <summary>
        /// Converts a list of custom <see cref="NotificationButton"/> objects (which define generic button properties)
        /// into a Telegram-specific <see cref="InlineKeyboardMarkup"/>. This keyboard is used to add interactive buttons
        /// directly to Telegram messages, enabling users to respond to or act upon AI-generated content or signals.
        /// This helper method dynamically creates either a URL button (linking to external content) or a callback data button
        /// (triggering an internal bot action) based on the button's defined properties.
        /// </summary>
        /// <param name="buttons">
        /// The list of <see cref="NotificationButton"/> objects to be converted. This list represents the interactive elements
        /// that accompany an AI-generated news or signal notification. It can be <c>null</c> or empty if no buttons are desired.
        /// </param>
        /// <returns>
        /// An <see cref="InlineKeyboardMarkup"/> object:
        /// <list type="bullet">
        ///     <item><description>
        ///         A populated <see cref="InlineKeyboardMarkup"/> instance, ready for direct use with the Telegram Bot API,
        ///         if the input <paramref name="buttons"/> list is not <c>null</c> and contains at least one button.
        ///         Each button within the Telegram keyboard will be correctly configured as either a URL link or a callback data trigger.
        ///     </description></item>
        ///     <item><description>
        ///         <c>null</c> if the input <paramref name="buttons"/> list is <c>null</c> or empty, indicating that no interactive keyboard should be attached to the message.
        ///     </description></item>
        /// </list>
        /// </returns>
        private InlineKeyboardMarkup? BuildTelegramKeyboard(List<NotificationButton>? buttons)
        {
            if (buttons == null || !buttons.Any())
            {
                return null;
            }

            _logger.LogTrace("Building Telegram keyboard with {ButtonCount} initial buttons.", buttons.Count);

            var validButtons = new List<InlineKeyboardButton>();

            foreach (var button in buttons)
            {
                // V2 UPGRADE: Validate button text. Skip if empty.
                if (string.IsNullOrWhiteSpace(button.Text))
                {
                    _logger.LogWarning("Skipping button with empty text.");
                    continue;
                }

                // V2 UPGRADE: Validate URL/Callback data. Skip if empty.
                if (string.IsNullOrWhiteSpace(button.CallbackDataOrUrl))
                {
                    _logger.LogWarning("Skipping button '{ButtonText}' due to empty URL or CallbackData.", button.Text);
                    continue;
                }

                if (button.IsUrl)
                {
                    // V2 UPGRADE: Validate the URL.
                    if (Uri.TryCreate(button.CallbackDataOrUrl, UriKind.Absolute, out var validUri))
                    {
                        validButtons.Add(InlineKeyboardButton.WithUrl(button.Text, validUri.ToString()));
                    }
                    else
                    {
                        _logger.LogWarning("Skipping URL button '{ButtonText}' due to invalid URL format: '{InvalidUrl}'",
                            button.Text, button.CallbackDataOrUrl);
                    }
                }
                else // It's a callback button
                {
                    // V2 UPGRADE: Validate callback data length (Telegram limit is 1-64 bytes).
                    if (System.Text.Encoding.UTF8.GetByteCount(button.CallbackDataOrUrl) > 64)
                    {
                        _logger.LogWarning("Skipping Callback button '{ButtonText}' because its data is longer than the 64-byte Telegram limit.", button.Text);
                    }
                    else
                    {
                        validButtons.Add(InlineKeyboardButton.WithCallbackData(button.Text, button.CallbackDataOrUrl));
                    }
                }
            }

            // Only return a keyboard if we have at least one valid button after filtering.
            if (validButtons.Any())
            {
                _logger.LogDebug("Successfully built keyboard with {ValidButtonCount} valid buttons.", validButtons.Count);
                // Telegram keyboards are a list of lists (rows of buttons).
                // For simplicity, we'll put each button on its own row.
                return new InlineKeyboardMarkup(validButtons.Select(b => new[] { b }));
            }

            _logger.LogWarning("No valid buttons were found after filtering. Returning null keyboard.");
            return null;
        }

        /// <summary>
        /// The "last mile" sender responsible for dispatching the notification payload to Telegram.
        /// This V3 "God Mode" version centralizes all API error handling, deciding whether an error
        /// is permanent (and should be swallowed) or transient (and should be retried by Polly).
        /// </summary>
        private async Task SendToTelegramAsync(NotificationJobPayload payload, CancellationToken cancellationToken)
        {
            // --- V3 UPGRADE: Delay is now inside the try-catch for better context in case of cancellation ---
            try
            {
                if (PerMessageSendDelayMs > 0)
                {
                    await Task.Delay(PerMessageSendDelayMs, cancellationToken);
                }

                Context pollyContext = new($"NotificationTo_{payload.TargetTelegramUserId}");
                InlineKeyboardMarkup? finalKeyboard = BuildTelegramKeyboard(payload.Buttons);
                var sanitizedMessageForSending = EscapeTelegramMarkdownV2(payload.MessageText);

                await _telegramApiRetryPolicy.ExecuteAsync(async (ctx) =>
                {
                    if (!string.IsNullOrWhiteSpace(payload.ImageUrlOrDefault))
                    {
                        await _botClient.SendPhoto(
                            chatId: payload.TargetTelegramUserId,
                            photo: InputFile.FromUri(payload.ImageUrlOrDefault),
                            caption: sanitizedMessageForSending,
                            parseMode: ParseMode.MarkdownV2,
                            replyMarkup: finalKeyboard,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await _botClient.SendMessage(
                            chatId: payload.TargetTelegramUserId,
                            text: sanitizedMessageForSending,
                            parseMode: ParseMode.MarkdownV2,
                            replyMarkup: finalKeyboard,
                            linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                }, pollyContext);

                _logger.LogInformation("Notification sent successfully to User {UserId}", payload.TargetTelegramUserId);
            }
            catch (ApiRequestException apiEx)
            {
                // --- V3 UPGRADE: CENTRALIZED API EXCEPTION HANDLING ---
                // This block now handles ALL ApiRequestExceptions after Polly gives up.

                if (apiEx.ErrorCode >= 400 && apiEx.ErrorCode < 500)
                {
                    // This is a PERMANENT client-side error (Bad Request, Forbidden, Not Found).
                    // We log it as a critical failure and DO NOT re-throw. This stops the Hangfire loop.
                    _logger.LogCritical(apiEx, "PERMANENT Send Failure for User {UserId}. ErrorCode: {ErrorCode}. API Message: '{ApiMessage}'. Job will terminate.",
                        payload.TargetTelegramUserId, apiEx.ErrorCode, apiEx.Message);

                    // Optional: Self-healing for user-specific permanent errors.
                    if (apiEx.ErrorCode == 403 || (apiEx.ErrorCode == 400 && apiEx.Message.Contains("chat not found")))
                    {
                        _logger.LogWarning("Attempting to mark user {UserId} as unreachable due to permanent error.", payload.TargetTelegramUserId);
                        try
                        {
                            await _userService.MarkUserAsUnreachableAsync(payload.TargetTelegramUserId.ToString(), "ApiErrorOnSend", cancellationToken);
                        }
                        catch (Exception markEx)
                        {
                            _logger.LogError(markEx, "Failed during self-healing attempt to mark user {UserId} as unreachable.", payload.TargetTelegramUserId);
                        }
                    }
                }
                else // This is likely a 5xx server-side error that Polly couldn't fix.
                {
                    // We treat this as a transient failure and re-throw so Hangfire can try the whole job later.
                    _logger.LogError(apiEx, "TRANSIENT Send Failure for User {UserId} after all retries. ErrorCode: {ErrorCode}. Re-throwing for Hangfire.",
                        payload.TargetTelegramUserId, apiEx.ErrorCode);
                    throw;
                }
            }
            catch (Exception ex)
            {
                // This catches non-API exceptions (e.g., TaskCanceledException, network issues before Polly).
                _logger.LogError(ex, "FATAL unhandled exception during notification send to User {UserId}. Re-throwing for Hangfire.", payload.TargetTelegramUserId);
                throw;
            }
        }
        /// <summary>
        /// Processes and sends a single notification (typically an AI-generated message or signal) to a specified Telegram user.
        /// This method serves as a critical final step in the delivery pipeline. It leverages a Polly retry policy for
        /// resilience against transient Telegram API errors, ensuring that temporary network glitches or API rate limits
        /// do not prevent message delivery.
        /// <br/><br/>
        /// It incorporates vital self-healing logic: upon detecting a permanent "user blocked" or "chat not found" error
        /// from the Telegram API (HTTP 403), it proactively marks the affected user as unreachable in the database.
        /// This prevents future wasteful dispatch attempts to disengaged users, optimizing system resources.
        /// <br/><br/>
        /// **Contribution to AI Analysis:**
        /// The logs generated by this method (successful sends, permanent failures with self-healing, and critical unhandled errors)
        /// are highly valuable for future AI analysis. They provide direct feedback on the deliverability of AI-generated content,
        /// enabling:
        /// <list type="bullet">
        ///     <item><description>Measuring the real-world reach and impact of AI signals.</description></item>
        ///     <item><description>Identifying user churn or disengagement patterns based on "bot blocked" events.</description></item>
        ///     <item><description>Assessing the overall reliability and performance of the notification infrastructure.</description></item>
        ///     <item><description>Informing AI models about user deliverability status to potentially prioritize signals for active users or adapt content for different user segments.</description></item>
        /// </list>
        /// All unhandled exceptions are re-thrown to ensure that the calling Hangfire job is marked as 'Failed',
        /// which is crucial for monitoring, alerting, and manual inspection of critical pipeline issues.
        /// </summary>
        /// <param name="payload">The <see cref="NotificationJobPayload"/> containing the target user's Telegram ID and the MarkdownV2-formatted message text to be sent (which originates from AI analysis).</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to monitor for cancellation requests. This allows for graceful termination of the send operation if requested externally.</param>
        /// <returns>
        /// A <see cref="Task"/> that represents the asynchronous operation of sending the notification.
        /// The task completes successfully (without re-throwing an exception from this method) upon:
        /// <list type="bullet">
        ///     <item><description>Successful message delivery to the Telegram API (including handling of transient retries).</description></item>
        ///     <item><description>Successful handling of a permanent Telegram API error (e.g., bot blocked) where the self-healing mechanism completes successfully.</description></item>
        /// </list>
        /// The task will fail by **re-throwing an exception** (causing the parent Hangfire job to be marked 'Failed') if:
        /// <list type="bullet">
        ///     <item><description>A permanent Telegram API error occurs, but the subsequent attempt to mark the user as unreachable in the database also fails.</description></item>
        ///     <item><description>Any other unexpected and unhandled exception occurs during the notification process (e.g., database connection issues, internal logic errors).</description></item>
        /// </list>
        /// </returns>
        private async Task ProcessSingleNotification(NotificationJobPayload payload, CancellationToken cancellationToken)
        {
            using IDisposable? logScope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["TargetTelegramUserId"] = payload.TargetTelegramUserId
            });

            try
            {
                Context pollyContext = new($"NotificationTo_{payload.TargetTelegramUserId}");

                await _telegramApiRetryPolicy.ExecuteAsync(async (ctx) =>
                {
                    // For simplicity, we assume no buttons for batch messages.
                    // This can be expanded later.
                    InlineKeyboardMarkup? finalKeyboard = null;

                    await _telegramMessageSender.SendTextMessageAsync(
                        payload.TargetTelegramUserId,
                        payload.MessageText,
                        ParseMode.MarkdownV2,
                        finalKeyboard,
                        cancellationToken,
                        new LinkPreviewOptions { IsDisabled = true });
                }, pollyContext);

                _logger.LogInformation("Message sent successfully via Telegram API.");
            }
            catch (ApiRequestException apiEx) when (apiEx.ErrorCode == 403 || apiEx.Message.Contains("chat not found"))
            {
                _logger.LogWarning(apiEx, "Permanent Send Failure: Bot blocked or user deleted chat.");
                try
                {
                    // Self-healing logic: Mark the user as unreachable in your database.
                    await _userService.MarkUserAsUnreachableAsync(payload.TargetTelegramUserId.ToString(), "BotBlockedOrChatNotFound", CancellationToken.None);
                }
                catch (Exception markEx)
                {
                    _logger.LogError(markEx, "A non-critical error occurred while marking user as unreachable.");
                }
                throw; // Re-throw to fail the job.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unhandled exception occurred during the final notification send.");
                throw;
            }
        }




        /// <summary>
        /// Constructs a human-readable message string from a <see cref="NewsItem"/> object,
        /// specifically formatted for display within Telegram chats using MarkdownV2 syntax.
        /// This method is crucial for presenting the AI-analyzed news content to the end-user in an engaging and readable format.
        /// It ensures that all dynamic text (title, source, summary) is safely escaped to prevent Markdown formatting issues or injection vulnerabilities.
        /// </summary>
        /// <param name="newsItem">The <see cref="NewsItem"/> object containing the AI-processed news content (title, source, summary, link) to be formatted into a Telegram message.</param>
        /// <returns>
        /// A <see cref="string"/> representing the fully formatted Telegram message. This string is:
        /// <list type="bullet">
        ///     <item><description>Ready for direct use with the Telegram Bot API's MarkdownV2 <see cref="ParseMode"/>.</description></item>
        ///     <item><description>Composed of a bold title, an italicized source name, an optional summary, and an optional "Read Full Article" link if a valid URL is present.</description></item>
        ///     <item><description>Trimmed of leading/trailing whitespace.</description></item>
        ///     <item><description>Guaranteed to have dynamic content correctly escaped for MarkdownV2.</description></item>
        /// </list>
        /// The quality of this output directly impacts the user's experience with the AI-provided news.
        /// </returns>
        private string BuildMessageText(NewsItem newsItem)
        {
            var messageTextBuilder = new StringBuilder();

            // Use a local helper or a central formatter. The key is that the *implementation* is correct.
            string title = EscapeTelegramMarkdownV2(newsItem.Title?.Trim() ?? "Untitled News");
            string sourceName = EscapeTelegramMarkdownV2(newsItem.SourceName?.Trim() ?? "Unknown Source");
            string summary = EscapeTelegramMarkdownV2(newsItem.Summary?.Trim() ?? string.Empty);
            string? link = newsItem.Link?.Trim();

            messageTextBuilder.AppendLine($"*{title}*");
            messageTextBuilder.AppendLine($"_Source: {sourceName}_");

            if (!string.IsNullOrWhiteSpace(summary))
            {
                messageTextBuilder.Append($"\n{summary}");
            }

            if (!string.IsNullOrWhiteSpace(link) && Uri.TryCreate(link, UriKind.Absolute, out _))
            {
                // --- THE CRITICAL FIX ---
                // We must also escape the URL itself before placing it inside the parentheses.
                // This prevents characters like `(` or `)` in the URL from breaking the Markdown.
                var escapedLink = EscapeTelegramMarkdownV2(link);
                messageTextBuilder.Append($"\n\n[Read Full Article]({escapedLink})");
            }

            return messageTextBuilder.ToString().Trim();
        }



        /// <summary>
        /// Constructs a list of simple notification buttons for a given news item.
        /// This method is part of the final payload preparation for Telegram notifications,
        /// allowing users to easily access the full article after receiving an AI-analyanalyzed summary.
        /// Currently, it generates a "Read More" URL button if the news item's external link is valid and present.
        /// </summary>
        /// <param name="newsItem">The <see cref="NewsItem"/> object from which to extract button information, specifically its external link (which might be an AI-identified source link).</param>
        /// <returns>
        /// A <see cref="List{NotificationButton}"/> containing:
        /// <list type="bullet">
        ///     <item><description>
        ///         A single <see cref="NotificationButton"/> with the text "Read More" and its <c>IsUrl</c> property set to <c>true</c>,
        ///         if the <paramref name="newsItem"/> has a non-empty and well-formed absolute URL in its <see cref="NewsItem.Link"/> property.
        ///         This button directly links to the original article source.
        ///     </description></item>
        ///     <item><description>
        ///         An empty <see cref="List{NotificationButton}"/> if the <paramref name="newsItem"/>'s link is missing, empty, or not a valid absolute URL.
        ///         In this case, no interactive buttons will be added to the Telegram message.
        ///     </description></item>
        /// </list>
        /// This list is then used to construct the Telegram-specific inline keyboard.
        /// </returns>
        private List<NotificationButton> BuildSimpleNotificationButtons(NewsItem newsItem)
        {
            List<NotificationButton> buttons = [];
            if (!string.IsNullOrWhiteSpace(newsItem.Link) && Uri.TryCreate(newsItem.Link, UriKind.Absolute, out _))
            {
                buttons.Add(new NotificationButton { Text = "Read More", CallbackDataOrUrl = newsItem.Link, IsUrl = true });
            }
            return buttons;
        }

        /// <summary>
        /// This method is intended to asynchronously send a notification based on the provided payload.
        /// It serves as a placeholder for future implementation within the notification dispatch process
        /// and is currently not functional. Its purpose would be to encapsulate the final sending logic
        /// for AI-generated alerts or news to a specific user.
        /// </summary>
        /// <remarks>
        /// As of the current implementation, this method is explicitly not implemented and will throw a
        /// <see cref="NotImplementedException"/> if invoked. This signifies that the functionality
        /// for directly sending a notification using this signature is pending development.
        /// </remarks>
        /// <param name="payload">The <see cref="NotificationJobPayload"/> containing all necessary details for the notification (e.g., target user, message content from AI analysis).</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to observe for cancellation requests during the asynchronous operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that represents the asynchronous operation of sending the notification.
        /// <para>
        /// **Note:** This method is currently not implemented and will throw a <see cref="NotImplementedException"/> when called.
        /// It is a placeholder for future AI analysis communication capabilities.
        /// </para>
        /// </returns>
        /// <exception cref="NotImplementedException">
        /// This exception is explicitly thrown every time this method is called, as its implementation is pending.
        /// </exception>
        public Task SendNotificationAsync(NotificationJobPayload payload, CancellationToken cancellationToken)
        {
            // Log a warning or error if this method is called, as it's not meant to be active yet.
            // For AI analysis, calling this would indicate a potential issue in the dispatch flow.
            // _logger.LogError("SendNotificationAsync is called but not implemented."); // Example logging, but current code throws immediately.
            throw new NotImplementedException();
        }
        #endregion
    }
}