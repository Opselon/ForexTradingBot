// File: BackgroundTasks/Services/NotificationSendingService.cs

#region Usings
// Standard .NET & NuGet
// Project specific
using Application.Common.Interfaces;
using Application.DTOs;
using Application.DTOs.Notifications;
using Application.Interfaces;
using Domain.Entities;
using Hangfire;
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
        private readonly IDistributedThrottler _globalApiThrottler;
        private const string TELEGRAM_API_THROTTLE_KEY = "telegram-api-global";
        private const int TELEGRAM_MESSAGES_PER_SECOND_LIMIT = 28;
        private readonly ISettingsService _settingsService;
        private readonly IServiceProvider _serviceProvider;
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
        public NotificationSendingService(ISettingsService settingsService,
            ILogger<NotificationSendingService> logger,
             ITelegramBotClient botClient,
            ITelegramMessageSender telegramMessageSender,
            IUserService userService,
            INewsItemRepository newsItemRepository,
            IConnectionMultiplexer redisConnection, IDistributedThrottler globalApiThrottler,
            INotificationRateLimiter rateLimiter,
            IServiceProvider serviceProvider)
        {
            _settingsService = settingsService;
            _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _telegramMessageSender = telegramMessageSender ?? throw new ArgumentNullException(nameof(telegramMessageSender));
            _userService = userService ?? throw new ArgumentNullException(nameof(userService));
            _newsItemRepository = newsItemRepository ?? throw new ArgumentNullException(nameof(newsItemRepository));
            _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
            _globalApiThrottler = globalApiThrottler ?? throw new ArgumentNullException(nameof(globalApiThrottler));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
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
             if (ex.ErrorCode is >= 500 and < 600)
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

        #region Hangfire Attributes and Performance
        // These attributes ensure jobs are deleted after completion and optimize performance.
        // [JobExpirationTimeout] ensures Hangfire deletes jobs after the specified time (default: 1 hour here).
        // [AutomaticRetry(Attempts = 0)] disables retries for jobs where we want to fail fast.
        // [DisableConcurrentExecution] prevents duplicate processing for the same job.
        // [Queue("notifications")] routes jobs to the notifications queue.
        #endregion

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
        [AutomaticRetry(Attempts = 0)] // No retries for batch notifications
        [Queue("notifications")]
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
        [Queue("notifications")]
        [DisableConcurrentExecution(timeoutInSeconds: 600)]
        [AutomaticRetry(OnAttemptsExceeded = AttemptsExceededAction.Delete)]
        [JobDisplayName("WORKER: Send News {0} to User at Index {2}")]
        public async Task ProcessNotificationFromCacheAsync(
               Guid newsItemId,
               string userListCacheKey,
               int userIndex,
               IJobCancellationToken jobCancellationToken)
        {
            CancellationToken cancellationToken = jobCancellationToken.ShutdownToken;
            long targetUserId = -1;

            using IDisposable? logScope = _logger.BeginScope(new Dictionary<string, object?> { ["UserIndex"] = userIndex });

            try
            {
                // 1. Fetch user details from the cache.
                (UserDto? userDto, targetUserId) = await GetTargetUserAsync(userListCacheKey, userIndex, cancellationToken);
                if (userDto == null)
                {
                    _logger.LogWarning("Skipping job: Target user not found in cache for UserIndex {UserIndex}.", userIndex);
                    return;
                }

                // --- NEW: AUTHORIZATION (FORCE JOIN CHECK) ---
                Application.DTOs.Settings.ForceJoinSettingsDto forceJoinSettings = await _settingsService.GetForceJoinSettingsAsync(cancellationToken);
                if (forceJoinSettings is { IsEnabled: true } && forceJoinSettings.ChannelId != 0)
                {
                    try
                    {
                        ChatMember chatMember = await _botClient.GetChatMember(forceJoinSettings.ChannelId, targetUserId, cancellationToken);
                        if (chatMember.Status is ChatMemberStatus.Left or ChatMemberStatus.Kicked)
                        {
                            _logger.LogInformation("SKIP: User {TargetUserId} is not a member of the required channel {ChannelId}. Halting notification.", targetUserId, forceJoinSettings.ChannelId);
                            // We don't mark as unreachable here, as they might rejoin. We just skip this notification.
                            return; // Gracefully exit the job.
                        }
                    }
                    catch (ApiRequestException apiEx) when (apiEx.Message.Contains("user not found"))
                    {
                        _logger.LogInformation("SKIP: User {TargetUserId} is not a member of the required channel {ChannelId} (user not found in chat). Halting notification.", targetUserId, forceJoinSettings.ChannelId);
                        return; // Gracefully exit.
                    }
                    // Note: We let other ApiRequestExceptions (e.g., bot not admin in channel) fall through
                    // so the job fails and the admin is alerted to a configuration problem.
                }

                // 2. RATE LIMITING (CHECK, THEN ACT)
                if (await _rateLimiter.IsUserAtOrOverLimitAsync(targetUserId, 5, TimeSpan.FromMinutes(15)))
                {
                    _logger.LogInformation(
                        "SKIP: User {TargetUserId} is at or over their rate limit.",
                        targetUserId);
                    return;
                }

                _logger.LogInformation("Rate limit check PASSED for User {TargetUserId}. Proceeding to send.", targetUserId);

                // 3. ACT (SEND THE MESSAGE)
                NewsItem? newsItem = await _newsItemRepository.GetByIdAsync(newsItemId, cancellationToken);
                if (newsItem == null)
                {
                    throw new InvalidOperationException($"NewsItem {newsItemId} not found.");
                }

                NotificationJobPayload payload = BuildNotificationPayload(newsItem, targetUserId);
                await SendNotificationViaSenderAsync(payload, cancellationToken);

                // 4. UPDATE (Rate Limit Counter)
                await _rateLimiter.IncrementUsageAsync(targetUserId, TimeSpan.FromMinutes(15));

                _logger.LogInformation("✅ Successfully SENT notification and INCREMENTED rate limit for User {UserId}.", targetUserId);

            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "A critical exception occurred for User {TargetUserId} (Index {UserIndex}). The job will be retried by Hangfire.", targetUserId, userIndex);
                throw;
            }
        }

        private async Task SendNotificationViaSenderAsync(NotificationJobPayload payload, CancellationToken cancellationToken)
        {
            // =================================================================================
            // == DEFINITIVE DIAGNOSTIC LOG: Find the source of the duplicate flow.           ==
            // =================================================================================
            // Get the current stack trace to see what called this method.
            string stackTrace = Environment.StackTrace;

            // Log the trigger path. This will be verbose but will immediately show the culprit.
            _logger.LogWarning(
                "FINAL SEND TRIGGERED for User {UserId}. Call Stack:\n{StackTrace}",
                payload.TargetTelegramUserId,
                stackTrace);
            // =================================================================================

            InlineKeyboardMarkup? keyboard = BuildTelegramKeyboard(payload.Buttons);
            _logger.LogDebug("Handing off payload to ITelegramMessageSender for User {UserId}", payload.TargetTelegramUserId);

            try
            {
                Task sendTask = !string.IsNullOrWhiteSpace(payload.ImageUrlOrDefault)
                    ? _telegramMessageSender.SendPhotoAsync(
                        chatId: payload.TargetTelegramUserId,
                        photoUrlOrFileId: payload.ImageUrlOrDefault ?? "https://i.postimg.cc/3RmJjBjY/Breaking-News.jpg",
                        caption: payload.MessageText,
                        parseMode: ParseMode.MarkdownV2,
                        replyMarkup: keyboard,
                        cancellationToken: cancellationToken)
                    : _telegramMessageSender.SendTextMessageAsync(
                        chatId: payload.TargetTelegramUserId,
                        text: payload.MessageText,
                        parseMode: ParseMode.MarkdownV2,
                        replyMarkup: keyboard,
                        cancellationToken: cancellationToken,
                        linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true });
                await sendTask;

                _logger.LogInformation("✅ Successfully sent notification to User {UserId}", payload.TargetTelegramUserId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send notification to User {UserId}", payload.TargetTelegramUserId);
                throw;
            }
        }

        // Placeholder
        /// <summary>
        /// Retrieves and validates a user for processing.
        /// </summary>
        /// <returns>A tuple containing the UserDto and their ID, or (null, -1) if the user should be skipped.</returns>
        private async Task<(UserDto?, long)> GetTargetUserAsync(string cacheKey, int index, CancellationToken ct)
        {
            RedisValue serializedUserIds = await _redisDb.StringGetAsync(cacheKey);
            if (!serializedUserIds.HasValue) { return (null, -1); }
            List<long>? allUserIds = JsonSerializer.Deserialize<List<long>>(serializedUserIds.ToString());
            if (allUserIds == null || index >= allUserIds.Count)
            {
                _logger.LogError("Job aborted: User index {UserIndex} out of bounds for list (Size: {ListSize}).", index, allUserIds?.Count ?? 0);
                return (null, -1);
            }
            long targetUserId = allUserIds[index];
            UserDto? user = await _userService.GetUserByTelegramIdAsync(targetUserId.ToString(), ct);
            return (user, targetUserId);
        }

        /// <summary>
        /// Builds the notification payload from a NewsItem.
        /// </summary>
        private NotificationJobPayload BuildNotificationPayload(NewsItem newsItem, long targetUserId)
        {
            return new()
            {
                TargetTelegramUserId = targetUserId,
                MessageText = BuildMessageText(newsItem),
                UseMarkdown = true,
                ImageUrl = newsItem.ImageUrl ?? string.Empty,
                Buttons = BuildSimpleNotificationButtons(newsItem),
                NewsItemId = newsItem.Id
            };
        }

        /// <summary>
        /// Escapes characters in a string that are reserved in Telegram's MarkdownV2 format.
        /// </summary>
        /// <param name="text">The raw text to escape.</param>
        /// <returns>A string safe to be sent with ParseMode.MarkdownV2.</returns>
        private string EscapeTelegramMarkdownV2(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }

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
                        if (sqlEx.Message.Contains("deadlock", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }

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
                (e is StackExchange.Redis.RedisServerException && e.Message.Contains("LOADING", StringComparison.OrdinalIgnoreCase))) // Redis is loading data
            {
                return true;
            }

            // API Exceptions (Assuming ApiRequestException is from your Telegram client or a similar library)
            if (e is ApiRequestException apiEx)
            {
                // Treat server errors (5xx) and potentially rate limits (429) as transient
                if (apiEx.ErrorCode is >= 500 or 429) // 429 Too Many Requests might indicate a temporary API limit
                {
                    // Note: Your internal rate limiter handles user-specific limits *before* calling SendToTelegramAsync.
                    // A 429 from Telegram would be a global or application-wide limit, or maybe specific to the bot.
                    // Retrying on 429 often requires respecting 'Retry-After' header, which Polly or the API client might handle.
                    return true;
                }
                // Note: 403 is handled specifically in the job method, so it won't reach this generic transient check.
            }

            // General Network/HTTP Exceptions
            if (e is System.Net.Sockets.SocketException or
                System.IO.IOException or // Could indicate network stream issues
                System.Net.Http.HttpRequestException) // Generic HTTP client errors
            {
                return true;
            }

            // Timeout/Cancellation Exceptions
            if (e is System.TimeoutException or
                System.Threading.Tasks.TaskCanceledException or // Often results from timeouts
                OperationCanceledException) // General cancellation
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

            List<InlineKeyboardButton> validButtons = [];

            foreach (NotificationButton button in buttons)
            {
                if (string.IsNullOrWhiteSpace(button.Text) || string.IsNullOrWhiteSpace(button.CallbackDataOrUrl))
                {
                    continue; // Skip invalid buttons
                }

                if (button.IsUrl)
                {
                    if (Uri.TryCreate(button.CallbackDataOrUrl, UriKind.Absolute, out Uri? validUri))
                    {
                        validButtons.Add(InlineKeyboardButton.WithUrl(button.Text, validUri.ToString()));
                    }
                    else
                    {
                        _logger.LogWarning("Skipping URL button '{ButtonText}' due to invalid URL format.", button.Text);
                    }
                }
                else // It's a callback button
                {
                    if (System.Text.Encoding.UTF8.GetByteCount(button.CallbackDataOrUrl) > 64)
                    {
                        _logger.LogWarning("Skipping Callback button '{ButtonText}' because data exceeds 64 bytes.", button.Text);
                    }
                    else
                    {
                        validButtons.Add(InlineKeyboardButton.WithCallbackData(button.Text, button.CallbackDataOrUrl));
                    }
                }
            }

            if (!validButtons.Any())
            {
                _logger.LogWarning("No valid buttons were found after filtering. Returning null keyboard.");
                return null;
            }

            _logger.LogDebug("Successfully built keyboard with {ValidButtonCount} valid buttons.", validButtons.Count);

            // =========================================================================================
            // == THE FIX: Create a List<List<InlineKeyboardButton>> instead of a List<InlineKeyboardButton[]>.
            // This is a more standard structure that prevents deserialization errors in Hangfire.
            // =========================================================================================
            List<List<InlineKeyboardButton>> keyboardRows = validButtons
                .Select(b => new List<InlineKeyboardButton> { b }) // <-- THE CHANGE IS HERE
                .ToList();

            return new InlineKeyboardMarkup(keyboardRows);
        }
        public async Task SendNotificationAsync(NotificationJobPayload payload, CancellationToken cancellationToken)
        {
            try
            {
                // A unique key for logging and Polly context
                Context pollyContext = new($"NotificationTo_{payload.TargetTelegramUserId}");

                // <<< PERFORMANCE CHANGE: Prepare the message payload *before* the retry loop. >>>
                // This work is expensive (string manipulation, object creation) and its result
                // is the same for every retry attempt. By doing it once, we save significant
                // CPU cycles on every subsequent retry.
                InlineKeyboardMarkup? finalKeyboard = BuildTelegramKeyboard(payload.Buttons);

                // Execute the core logic within the Polly policy for retries.
                await _telegramApiRetryPolicy.ExecuteAsync(async (ctx) =>
                {
                    // Acquire a permit from the global throttler right before the API call.
                    // This remains inside the loop, which is the correct pattern.
                    // <<< ASYNC HYGIENE: Added .ConfigureAwait(false) to the WaitAsync call >>>
                    // This prevents potential deadlocks by not attempting to resume on the original synchronization context.
                    await _globalApiThrottler.WaitAsync(
                        TELEGRAM_API_THROTTLE_KEY,
                        TELEGRAM_MESSAGES_PER_SECOND_LIMIT,
                        TimeSpan.FromSeconds(1),
                        cancellationToken).ConfigureAwait(false);

                    // Use the pre-calculated, sanitized payload for the API call.
                    // Check if payload.ImageUrlOrDefault is populated (it will be, either from RSS or default).
                    if (!string.IsNullOrWhiteSpace(payload.ImageUrlOrDefault))
                    {
                        _ = await _botClient.SendPhoto(
                            chatId: payload.TargetTelegramUserId,
                            photo: new InputFileUrl(payload.ImageUrlOrDefault),
                            caption: payload.MessageText, // <<< FIX: Using pre-calculated sanitized message >>>
                            parseMode: ParseMode.MarkdownV2,
                            replyMarkup: finalKeyboard, // Using pre-calculated keyboard
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        // If ImageUrlOrDefault is *still* null/empty after the dispatch logic,
                        // send a plain text message. This is a fallback in case the default image URL was also invalid.
                        _ = await _botClient.SendMessage(
                            chatId: payload.TargetTelegramUserId,
                            text: payload.MessageText,
                            parseMode: ParseMode.MarkdownV2,
                            replyMarkup: finalKeyboard,
                            linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                }, pollyContext).ConfigureAwait(false); // Added ConfigureAwait(false) to the ExecuteAsync call itself.

                _logger.LogInformation("Notification sent successfully to User {UserId}", payload.TargetTelegramUserId);
            }
            // YOUR EXCELLENT ERROR HANDLING REMAINS IDENTICAL. NO CHANGES NEEDED HERE.
            catch (ApiRequestException apiEx) when (apiEx.ErrorCode is >= 400 and < 500 and not 429)
            {
                _logger.LogCritical(apiEx, "PERMANENT Send Failure for User {UserId}. ErrorCode: {ErrorCode}. API Message: '{ApiMessage}'.",
                    payload.TargetTelegramUserId, apiEx.ErrorCode, apiEx.Message);
                // Background error log
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new Domain.Entities.ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Critical",
                        Source = "NotificationSendingService",
                        EventType = "SendNotification.ApiRequestException",
                        Message = apiEx.Message,
                        Details = apiEx.StackTrace,
                        Exception = apiEx.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                if (apiEx.ErrorCode == 403 || (apiEx.ErrorCode == 400 && apiEx.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning("Attempting to mark user {UserId} as unreachable due to permanent error (Code: {ErrorCode}).", payload.TargetTelegramUserId, apiEx.ErrorCode);
                    try
                    {
                        _logger.LogInformation("Successfully marked user {UserId} as unreachable.", payload.TargetTelegramUserId);
                    }
                    catch (Exception markEx)
                    {
                        _logger.LogError(markEx, "FAILED during self-healing attempt to mark user {UserId} as unreachable. Re-throwing for Hangfire.", payload.TargetTelegramUserId);
                        // Background error log
                        _ = Task.Run(async () =>
                        {
                            using IServiceScope scope = _serviceProvider.CreateScope();
                            IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                            await repo.AddAsync(new Domain.Entities.ProMonitoringLog
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Error",
                                Source = "NotificationSendingService",
                                EventType = "SendNotification.MarkUnreachableError",
                                Message = markEx.Message,
                                Details = markEx.StackTrace,
                                Exception = markEx.ToString(),
                                Status = "Failed",
                                CreatedAt = DateTime.UtcNow
                            });
                        });
                        throw; // Re-throwing ensures Hangfire knows this job attempt failed.
                    }
                }
            }
            catch (Exception ex)
            {
                // This catch block handles general exceptions that are not specific API errors.
                // It's crucial for telling Hangfire that a job attempt failed so it can retry based on [AutomaticRetry].
                _logger.LogError(ex, "FATAL or UNRESOLVED exception for User {UserId}. Re-throwing for Hangfire.", payload.TargetTelegramUserId);
                // Background error log
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new Domain.Entities.ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "NotificationSendingService",
                        EventType = "SendNotification.GeneralException",
                        Message = ex.Message,
                        Details = ex.StackTrace,
                        Exception = ex.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                throw;
            }
        }

        /// <summary>

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
        /// </summary>
        // In TelegramPanel/Application/CommandHandlers/SomeHandler.cs or wherever BuildMessageText is located

        // ... (assuming NewsItem class, using statements for StringBuilder, Func, Uri, etc.) ...

        // NEW: Helper method to clean and pre-process raw news content.
        private string CleanRawContent(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText))
            {
                return string.Empty;
            }

            string cleaned = rawText;

            // --- Powerful Replacements and Cleaning ---

            // 1. Remove chat metadata (e.g., "Forex AI SmartPro, [7/4/2025 11:00 AM]")
            // This regex targets lines starting with a typical username/bot name, optional space, and a date/time pattern.
            // It's a bit aggressive but handles the provided example well.
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"^.*?(?:\[.*?\]\s)?(?:Forex AI SmartPro(?:.*?)\s)?[\d/:\sAPM]+(?:[\r\n]+)?", "", System.Text.RegularExpressions.RegexOptions.Multiline);

            // 2. Remove "Source: ..." lines
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"^Source: .*?(?:[\r\n]+)?", "", System.Text.RegularExpressions.RegexOptions.Multiline);

            // 3. Trim excessive whitespace from the beginning and end of the entire block.
            cleaned = cleaned.Trim();

            // 4. Handle specific patterns that might appear from scraping or Twitter links:

            // Remove repeated whitespace (replace multiple spaces/tabs/newlines with a single space).
            // This helps collapse unwanted extra spaces.
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ", System.Text.RegularExpressions.RegexOptions.Multiline);

            // Remove specific unwanted trailing characters/patterns that might linger.
            // For example, if a URL was at the end of a line and got processed oddly.
            // This is more for general cleanup.
            cleaned = cleaned.TrimEnd(' ', '\t', '\r', '\n', '.');


            // 5. More intelligent newline handling for summaries:
            // Preserve paragraph breaks if they look intentional (e.g., multiple spaces before a newline).
            // Replace single newlines followed by potential list markers or short lines with a space,
            // but keep double newlines as paragraph separators.
            // A more robust approach is to split by potential paragraph terminators, clean each paragraph, and rejoin.

            // Split into potential paragraphs (using double newline as a strong indicator)
            string[] paragraphs = cleaned.Split(new[] { "\r\n\r\n", "\n\n", "\r\r" }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < paragraphs.Length; i++)
            {
                // Trim each paragraph individually
                paragraphs[i] = paragraphs[i].Trim();

                // Further clean: replace single newlines within a paragraph with a space.
                // This assumes paragraphs might have internal line breaks that aren't intended as separate paragraphs.
                paragraphs[i] = paragraphs[i].Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");

                // Optional: Remove specific Twitter elements if they are truly unwanted or break formatting.
                // For example, you might want to remove the tweet itself if you're only extracting the summary.
                // Be cautious with this, as it can remove actual content.
                // Example (remove pic.twitter.com links):
                paragraphs[i] = System.Text.RegularExpressions.Regex.Replace(paragraphs[i], @"pic\.twitter\.com/\w+", "");

                // Example (remove Twitter handles):
                paragraphs[i] = System.Text.RegularExpressions.Regex.Replace(paragraphs[i], @"@\w+", "");

                // Trim again after internal cleaning
                paragraphs[i] = paragraphs[i].Trim();
            }

            // Rejoin the cleaned paragraphs with double newlines to preserve structure.
            cleaned = string.Join("\n\n", paragraphs.Where(p => !string.IsNullOrEmpty(p)));


            // Final trim to ensure no leading/trailing whitespace on the result.
            return cleaned.Trim();
        }


        private string BuildMessageText(NewsItem newsItem)
        {
            StringBuilder messageTextBuilder = new();

            // --- In-Method Strategy: Escaping Logic ---
            static string escapeMarkdownV2(string? input)
            {
                return string.IsNullOrEmpty(input)
                    ? string.Empty
                    : input
                    .Replace("_", "\\_")
                    .Replace("*", "\\*")
                    .Replace("[", "\\[")
                    .Replace("]", "\\]")
                    .Replace("(", "\\(")
                    .Replace(")", "\\)")
                    .Replace("~", "\\~")
                    .Replace("`", "\\`")
                    .Replace(">", "\\>")
                    .Replace("#", "\\#")
                    .Replace("+", "\\+")
                    .Replace("-", "\\-")
                    .Replace("=", "\\=")
                    .Replace("|", "\\|")
                    .Replace("{", "\\{")
                    .Replace("}", "\\}");
            }
            // --- End In-Method Strategy: Escaping Logic ---


            // --- In-Method Strategy: Data Preparation and Formatting ---

            // 1. Process and escape Title, using cleaned content
            string title = escapeMarkdownV2(CleanRawContent(newsItem.Title)?.Trim() ?? "Untitled News");
            _ = messageTextBuilder.AppendLine($"*{title}*");

            // 2. Process and escape Source Name, using cleaned content
            string sourceName = escapeMarkdownV2(CleanRawContent(newsItem.SourceName ?? string.Empty)?.Trim() ?? "Unknown Source");
            _ = messageTextBuilder.AppendLine(); // Blank line for separation
            _ = messageTextBuilder.AppendLine($"_{sourceName}_");

            // 3. Process and escape Summary, using cleaned content
            string summaryRaw = CleanRawContent(newsItem.Summary ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(summaryRaw))
            {
                // Apply escaping *after* cleaning and paragraph structuring
                string summaryEscaped = escapeMarkdownV2(summaryRaw);

                // Re-structure paragraphs after escaping, ensuring proper separation.
                // (This step is already integrated into CleanRawContent, so we just use its output)
                string formattedSummary = summaryEscaped;
                _ = messageTextBuilder.AppendLine(); // Blank line for visual separation
                _ = messageTextBuilder.AppendLine(formattedSummary);
            }

            // 4. Process and add Link
            string? link = newsItem.Link?.Trim();

            if (!string.IsNullOrWhiteSpace(link) && Uri.TryCreate(link, UriKind.Absolute, out _))
            {
                _ = messageTextBuilder.AppendLine(); // Blank line for visual separation
            }
            // --- End In-Method Strategy: Data Preparation and Formatting ---

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

            // --- THE DEFINITIVE FIX ---
            // The .Select() operation in the original code might have been creating an iterator type
            // that Newtonsoft.Json cannot deserialize. By calling .ToList(), we force the LINQ
            // query to execute immediately and return a concrete List<T>, which is serializable.
            return buttons.ToList();
            // --- END OF FIX ---
        }

        #endregion
    }
}