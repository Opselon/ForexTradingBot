// File: Application/Services/NotificationDispatchService.cs

#region Usings
using Application.Common.Interfaces;
using Application.DTOs.Notifications;
using Application.Interfaces;
using Domain.Entities;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using StackExchange.Redis;

// using Shared.Extensions; // Assuming TruncateWithEllipsis is a local method
using System.Text;
// using System.Threading.RateLimiting; // Not needed here

// --- ✅ FIX 1: ADD MISSING USINGS ---
using System.Text.Json;
#endregion

namespace Application.Services
{
    public class NotificationDispatchService : INotificationDispatchService
    {
        #region Private Readonly Fields
        private readonly IUserRepository _userRepository;
        private readonly INotificationJobScheduler _jobScheduler;
        private readonly ILogger<NotificationDispatchService> _logger;
        private readonly INewsItemRepository _newsItemRepository;
        private readonly StackExchange.Redis.IDatabase _redisDb;
        private readonly INotificationRateLimiter _rateLimiter;
        private readonly AsyncCircuitBreakerPolicy _redisCircuitBreaker;
        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="NotificationDispatchService"/> class.
        /// This constructor is responsible for injecting all necessary dependencies for orchestrating
        /// notification dispatches and configuring a robust Circuit Breaker pattern with Polly
        /// to enhance resilience against transient (and potentially persistent) Redis connectivity issues.
        /// </summary>
        /// <param name="newsItemRepository">The repository for accessing news item data, used to retrieve details of news items to be dispatched.</param>
        /// <param name="userRepository">The repository for accessing user data, used to identify and retrieve eligible target users for notifications.</param>
        /// <param name="jobScheduler">The Hangfire background job scheduler, used to enqueue individual notification send tasks for asynchronous processing.</param>
        /// <param name="logger">The logger instance for recording operational events, warnings, and errors within the service.</param>
        /// <param name="redisConnection">The Redis connection multiplexer, providing the underlying connection to the Redis database for caching user lists.</param>
        /// <param name="rateLimiter">The service responsible for enforcing notification rate limits, preventing excessive messages to users.</param>
        /// <returns>
        /// A new instance of <see cref="NotificationDispatchService"/>, fully initialized and ready to orchestrate
        /// various types of news notification dispatches with built-in resilience.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown if any of the required constructor parameters (<paramref name="newsItemRepository"/>,
        /// <paramref name="userRepository"/>, <paramref name="jobScheduler"/>, <paramref name="logger"/>,
        /// <paramref name="redisConnection"/>, or <paramref name="rateLimiter"/>) are <c>null</c>.
        /// </exception>
        /// <remarks>
        /// **Role for AI Analysis and MLOps:**
        /// The constructor's setup of the Redis Circuit Breaker policy is especially important for MLOps:
        /// <list type="bullet">
        ///     <item><description>
        ///         **System Stability:** The circuit breaker ensures that if Redis (a critical caching component)
        ///         becomes unhealthy, dispatch operations fail fast rather than accumulating errors or hanging. This
        ///         protects the overall application's stability.
        ///     </description></item>
        ///     <item><description>
        ///         **Observability:** The `onBreak`, `onReset`, and `onHalfOpen` callbacks log significant state changes
        ///         of the Redis connection. This telemetry provides crucial insights for MLOps teams to:
        ///         <list type="circle">
        ///             <item><description>
        ///                 Quickly detect and diagnose Redis connectivity issues affecting notification delivery.
        ///             </description></item>
        ///             <item><description>
        ///                 Monitor the frequency and duration of Redis outages or performance degradation.
        ///             </description></item>
        ///             <item><description>
        ///                 Understand the real-time impact on the AI's ability to communicate results to users.
        ///             </description></item>
        ///         </list>
        ///     </description></item>
        ///     <item><description>
        ///         **Automated Recovery:** While manual intervention might be needed for persistent outages, the half-open state
        ///         allows the system to automatically test and recover when Redis becomes available again,
        ///         reducing the need for human intervention.
        ///     </description></item>
        /// </list>
        /// </remarks>
        public NotificationDispatchService(
       INewsItemRepository newsItemRepository,
       IUserRepository userRepository,
       INotificationJobScheduler jobScheduler,
       ILogger<NotificationDispatchService> logger,
       IConnectionMultiplexer redisConnection,
       INotificationRateLimiter rateLimiter)
        {
            // Parameter validation and assignment for all required dependencies.
            _newsItemRepository = newsItemRepository ?? throw new ArgumentNullException(nameof(newsItemRepository));
            _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            _jobScheduler = jobScheduler ?? throw new ArgumentNullException(nameof(jobScheduler));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));

            if (redisConnection == null)
            {
                throw new ArgumentNullException(nameof(redisConnection));
            }

            _redisDb = redisConnection.GetDatabase(); // Obtain a Redis database instance from the multiplexer.

            // Initialize the Circuit Breaker policy using Polly.
            // This policy monitors Redis operations for failures.
            _redisCircuitBreaker = Policy
                .Handle<RedisException>() // The circuit breaker will specifically trigger on any Redis-related exception.
                .CircuitBreakerAsync(
                    exceptionsAllowedBeforeBreaking: 3, // Allow 3 consecutive Redis failures before opening the circuit.
                    durationOfBreak: TimeSpan.FromMinutes(1), // Once open, the circuit will remain open for 1 minute.
                                                              // Callback executed when the circuit transitions from Closed to Open (breaks).
                    onBreak: (exception, timespan) =>
                    {
                        _logger.LogCritical(exception, "Redis Circuit Breaker opened for {BreakDuration}. All dispatch operations will fail fast until the circuit resets. Impact: AI notifications via Redis cache will be temporarily halted.", timespan);
                    },
                    // Callback executed when the circuit transitions from Open to Half-Open (after durationOfBreak).
                    onHalfOpen: () => _logger.LogWarning("Redis Circuit Breaker is now half-open. The next dispatch attempt will test the Redis connection. Monitoring for recovery."),
                    // Callback executed when the circuit transitions from Half-Open to Closed (a successful test request).
                    onReset: () => _logger.LogInformation("Redis Circuit Breaker has been reset. Resuming normal dispatch operations and Redis interactions.")
                );
        }
        #endregion

        #region INotificationDispatchService Implementation




        /// <summary>
        /// Orchestrates the dispatch of a batch of news notifications to multiple eligible users.
        /// This method serves as a high-level coordinator for distributing aggregated AI-analyzed news.
        /// It is responsible for identifying the target audience based on the news items' categories,
        /// applying user-specific batch rate limits, and then enqueuing individual Hangfire jobs
        /// for each eligible user. Each enqueued job will receive a consolidated notification
        /// containing all specified news items.
        /// </summary>
        /// <param name="newsItemIds">A list of <see cref="Guid"/>s representing the unique identifiers of the news items to be included in the batch notification. These IDs typically refer to content previously processed by AI.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to observe for cancellation requests during the dispatch orchestration. This allows for graceful termination of the user enumeration and job enqueueing process.</param>
        /// <returns>
        /// A <see cref="Task"/> that represents the asynchronous dispatch orchestration process.
        /// <list type="bullet">
        ///     <item><description>The task completes immediately (<see cref="Task.CompletedTask"/>) if the input <paramref name="newsItemIds"/> list is <c>null</c> or empty, as there are no news items to dispatch.</description></item>
        ///     <item><description>The task completes asynchronously after all eligible users have had their batch notification jobs successfully enqueued, or after they have been skipped due to rate limits or other business rules.</description></item>
        ///     <item><description>Any critical errors occurring during the orchestration phase (e.g., issues fetching users, rate limiting checks, or enqueueing) are logged to ensure observability, but they are purposefully NOT re-thrown from this method's `Task.Run` block. This design ensures that the dispatch orchestration itself remains robust and does not crash the broader system, allowing other batch dispatches or processes to continue.</description></item>
        ///     <item><description>If the <paramref name="cancellationToken"/> is signalled, the task will enter a cancelled state, and the enqueueing loop will cease.</description></item>
        /// </list>
        /// The successful completion of this task indicates that the batch notification jobs have been submitted to Hangfire, not that the notifications have been fully delivered to users.
        /// </returns>
        public Task DispatchBatchNewsNotificationAsync(List<Guid> newsItemIds, CancellationToken cancellationToken = default)
        {
            return newsItemIds == null || !newsItemIds.Any()
                ? Task.CompletedTask
                : Task.Run(async () =>
            {
                try
                {
                    _logger.LogInformation("Initiating BATCH dispatch for {Count} news items.", newsItemIds.Count);

                    // Fetch all eligible users ONCE. We assume they are all for the same category/Vip status.
                    // If not, you need to group newsItemIds by category first.
                    NewsItem? firstNewsItem = await _newsItemRepository.GetByIdAsync(newsItemIds.First(), cancellationToken);
                    if (firstNewsItem == null)
                    {
                        return; // Cannot determine target users
                    }

                    IEnumerable<User> targetUsers = await _userRepository.GetUsersForNewsNotificationAsync(
                        firstNewsItem.AssociatedSignalCategoryId, firstNewsItem.IsVipOnly, cancellationToken);

                    List<long> validTelegramIds = targetUsers.Select(u => long.TryParse(u.TelegramId, out long id) ? (long?)id : null)
                        .Where(id => id.HasValue).Select(id => id.Value).ToList();

                    if (!validTelegramIds.Any())
                    {
                        _logger.LogInformation("No eligible users found for this batch dispatch.");
                        return;
                    }

                    // Create ONE job per user.
                    foreach (long userId in validTelegramIds)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // Check the user's rate limit for BATCH notifications.
                        // We use a different key to track batch sends vs single sends if needed.
                        if (await _rateLimiter.IsUserOverLimitAsync(userId, 1, TimeSpan.FromHours(1))) // Limit to 1 batch per hour
                        {
                            _logger.LogTrace("User {UserId} is over the batch notification rate limit. Skipping.", userId);
                            continue;
                        }

                        // Enqueue a job with the LIST of news item IDs.
                        _ = _jobScheduler.Enqueue<INotificationSendingService>(
                            service => service.ProcessBatchNotificationForUserAsync(userId, newsItemIds));
                    }
                    _logger.LogInformation("Completed enqueuing batch jobs for {UserCount} users.", validTelegramIds.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "A critical error occurred during BATCH dispatch orchestration.");
                }
            }, cancellationToken);
        }


        /// <summary>
        /// Orchestrates the large-scale dispatch of a news notification by identifying eligible users,
        /// efficiently caching their IDs in Redis, and then enqueueing individual, lightweight Hangfire jobs
        /// for each recipient. This "cache-first dispatch pattern" is designed for scalability and resilience,
        /// especially when dealing with a large user base or transient service disruptions.
        /// </summary>
        /// <remarks>
        /// This method is a crucial part of our AI analysis program's communication pipeline. After AI identifies
        /// a relevant news item or signal, this method ensures that the corresponding notification is prepared
        /// for delivery to all relevant users.
        /// <br/><br/>
        /// Key operational aspects and their relevance for AI analysis:
        /// <list type="bullet">
        ///     <item><description>
        ///         **User Segmentation (AI Input):** It fetches users based on criteria like `AssociatedSignalCategoryId` and `IsVipOnly`,
        ///         which are often determined or informed by AI's user segmentation or content relevance models.
        ///     </description></item>
        ///     <item><description>
        ///         **Redis for Scale (MLOps):** Utilizing Redis to cache user lists optimizes performance for batch notifications.
        ///         Monitoring Redis health and usage here is vital for MLOps.
        ///     </description></item>
        ///     <item><description>
        ///         **Circuit Breaker (Resilience):** The Redis Circuit Breaker provides a critical fail-fast mechanism.
        ///         If Redis becomes unhealthy, the system avoids costly, repeated failed attempts, preserving resources.
        ///         AI/MLOps teams can monitor circuit breaker states to detect and react to infrastructure issues quickly.
        ///     </description></item>
        ///     <item><description>
        ///         **Rate Limiting:** Applies user-specific rate limits (e.g., 15 notifications per hour) before enqueuing,
        ///         preventing spam and maintaining a positive user experience. Logs from this step contribute to user engagement
        ///         analysis, which can feed back into AI models for content pacing.
        ///     </description></item>
        ///     <item><description>
        ///         **Asynchronous Enqueueing:** Distributes the notification load by creating a separate Hangfire job for each user,
        ///         allowing for highly concurrent and fault-tolerant processing downstream.
        ///     </description></item>
        ///     <item><description>
        ///         **Error Handling:** Catches and logs specific infrastructure errors (e.g., Redis issues, circuit breaker trips)
        ///         without necessarily failing the orchestrator itself, but still providing critical observability.
        ///     </description></item>
        /// </list>
        /// </remarks>
        /// <param name="newsItemId">The unique identifier of the news article for which notifications are to be dispatched. This ID references the content previously analyzed by AI.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to monitor for cancellation requests, allowing the orchestration process to be gracefully terminated.</param>
        /// <returns>
        /// A <see cref="Task"/> that represents the asynchronous orchestration process. The task completes when:
        /// <list type="bullet">
        ///     <item><description>
        ///         The eligible users have been identified, their IDs cached in Redis, and corresponding individual notification jobs enqueued in Hangfire.
        ///     </description></item>
        ///     <item><description>
        ///         The operation is aborted gracefully due to reasons like:
        ///         <list type="circle">
        ///             <item><description>The news item not being found.</description></item>
        ///             <item><description>No eligible users being found for the notification criteria.</description></item>
        ///             <item><description>The Redis circuit breaker being open, leading to a fail-fast skip of Redis operations.</description></item>
        ///         </list>
        ///     </description></item>
        ///     <item><description>
        ///         The operation is cancelled via the <paramref name="cancellationToken"/>.
        ///     </description></item>
        /// </list>
        /// This method does *not* return the results of the actual notification sends, as those occur asynchronously in the enqueued Hangfire jobs. Any critical, unhandled errors that occur during the orchestration process will be logged and re-thrown.
        /// </returns>
        public Task DispatchNewsNotificationAsync(Guid newsItemId, CancellationToken cancellationToken = default)
        {
            TimeSpan delayBetweenJobs = TimeSpan.FromMilliseconds(40);

            return Task.Run(async () =>
            {
                // ✅✅ NEW: Check the circuit state BEFORE doing any work ✅✅
                if (_redisCircuitBreaker.CircuitState == CircuitState.Open)
                {
                    _logger.LogWarning("Dispatch for NewsItem {NewsItemId} skipped because the Redis circuit breaker is open.", newsItemId);
                    return; // Fail fast without hitting the database
                }

                try
                {
                    NewsItem? newsItem = await _newsItemRepository.GetByIdAsync(newsItemId, cancellationToken);
                    if (newsItem == null)
                    {
                        _logger.LogWarning("NewsItem {Id} not found. Cannot dispatch.", newsItemId);
                        return;
                    }
                    _logger.LogInformation("Dispatching for NewsItem: {Title}", newsItem.Title);

                    IEnumerable<User> targetUsers = await _userRepository.GetUsersForNewsNotificationAsync(
                        newsItem.AssociatedSignalCategoryId, newsItem.IsVipOnly, cancellationToken);

                    List<long> validTelegramIds = targetUsers
                        .Select(u => long.TryParse(u.TelegramId, out long id) ? (long?)id : null)
                        .Where(id => id.HasValue).Select(id => id.Value).ToList();

                    if (!validTelegramIds.Any())
                    {
                        _logger.LogInformation("No eligible users found for this dispatch.");
                        return;
                    }

                    // ✅✅ NEW: Execute the Redis operation within the Circuit Breaker policy ✅✅
                    string userListCacheKey = $"dispatch:users:{newsItemId}";
                    await _redisCircuitBreaker.ExecuteAsync(async () =>
                    {
                        string serializedUserIds = JsonSerializer.Serialize(validTelegramIds);
                        _ = await _redisDb.StringSetAsync(userListCacheKey, serializedUserIds, TimeSpan.FromHours(24));
                    });

                    _logger.LogInformation("Cached {Count} user IDs to Redis key: {Key}", validTelegramIds.Count, userListCacheKey);

                    // --- Job Enqueueing Loop (largely unchanged) ---
                    int enqueuedCount = 0;
                    for (int i = 0; i < validTelegramIds.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        long userId = validTelegramIds[i];

                        if (await _rateLimiter.IsUserOverLimitAsync(userId, 15, TimeSpan.FromHours(1)))
                        {
                            _logger.LogTrace("User {UserId} is over rate limit. Skipping job enqueue.", userId);
                            continue;
                        }

                        _ = _jobScheduler.Enqueue<INotificationSendingService>(
                            service => service.ProcessNotificationFromCacheAsync(newsItemId, userListCacheKey, i));
                        enqueuedCount++;

                        if (i < validTelegramIds.Count - 1)
                        {
                            await Task.Delay(delayBetweenJobs, cancellationToken);
                        }
                    }
                    _logger.LogInformation("Dispatch orchestration complete. {Count} jobs enqueued.", enqueuedCount);
                }
                catch (BrokenCircuitException) // ✅ NEW: Specific catch
                {
                    _logger.LogWarning("Dispatch for NewsItem {NewsItemId} failed because the Redis circuit is open.", newsItemId);
                }
                catch (RedisException redisEx) // ✅ NEW: Specific catch
                {
                    _logger.LogError(redisEx, "A Redis error occurred during dispatch orchestration for NewsItem {NewsItemId}. The circuit breaker may have tripped.", newsItemId);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Dispatch orchestration was cancelled.");
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "A critical, unhandled error occurred during dispatch orchestration for NewsItem {NewsItemId}.", newsItemId);
                }
            }, cancellationToken);
        }

        #endregion
    }
}