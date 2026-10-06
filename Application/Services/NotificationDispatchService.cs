// File: Application/Services/NotificationDispatchService.cs

#region Usings
using Application.Common.Interfaces;
using Application.Interfaces;
using Domain.Entities;
using Hangfire;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using StackExchange.Redis;

// using Shared.Extensions; // Assuming TruncateWithEllipsis is a local method
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
                        .Where(id => id.HasValue).Select(id => id!.Value).ToList();

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
        [JobDisplayName("Dispatch Coordinator for News: {0}")]
        [AutomaticRetry(Attempts = 0)] // Orchestrator failure shouldn't trigger automatic retries for the whole batch.
        public async Task DispatchNewsNotificationAsync(Guid newsItemId, CancellationToken cancellationToken = default)
        {
            // --- GLOBAL ORCHESTRATOR LOCK ---
            const string globalOrchestrationLockKey = "global-dispatch-orchestration-lock";
            TimeSpan globalOrchestrationLockExpiry = TimeSpan.FromMinutes(15);
            bool globalLockAcquired = false; // Declared here to be accessible by catch/finally
                                             // --- END GLOBAL ORCHESTRATOR LOCK ---

            // --- ITEM-SPECIFIC LOCK VARIABLES ---
            string? itemLockKey = null; // Declare as nullable and outside the try for safe access
            bool itemLockAcquired = false;
            // --- END ITEM-SPECIFIC LOCK VARIABLES ---

            try
            {
                // Acquire the GLOBAL lock first.
                globalLockAcquired = await _jobScheduler.TryAcquireLockAsync(globalOrchestrationLockKey, globalOrchestrationLockExpiry);

                if (!globalLockAcquired)
                {
                    _logger.LogWarning("Dispatch Coordination for NewsItem {NewsItemId} skipped: Global dispatch lock is held by another process. This might indicate a long-running dispatch or a lock not being released.", newsItemId);
                    return;
                }

                _logger.LogInformation("Global dispatch lock acquired for NewsItem {NewsItemId}. Proceeding with orchestration.", newsItemId);


                // --- ORIGINAL IDEMPOTENCY FIX FOR THIS SPECIFIC NEWS ITEM ---
                itemLockKey = $"dispatch-lock:{newsItemId}"; // Assign the value
                TimeSpan itemLockExpiry = TimeSpan.FromHours(1);
                itemLockAcquired = await _redisDb.StringSetAsync(itemLockKey, "locked", itemLockExpiry, When.NotExists); // Attempt to acquire lock

                if (!itemLockAcquired)
                {
                    _logger.LogWarning("Dispatch for NewsItem {NewsItemId} skipped: another dispatch for THIS news item is already in progress.", newsItemId);
                    // If the item lock is already acquired, release the global lock before returning.
                    await _jobScheduler.ReleaseLockAsync(globalOrchestrationLockKey); // Safe because globalLockAcquired is true here
                    return; // EXIT
                }
                // --- END ORIGINAL IDEMPOTENCY FIX ---

                const int chunkSize = 500;
                _logger.LogInformation("Starting Dispatch Coordination for NewsItem {NewsItemId}.", newsItemId);

                if (_redisCircuitBreaker.CircuitState == CircuitState.Open)
                {
                    _logger.LogWarning("Dispatch for NewsItem {NewsItemId} skipped: Redis circuit breaker is open.", newsItemId);
                    // If a critical failure occurs (like circuit breaker open), ensure locks are released
                    // The finally block will handle this, but explicitly releasing here on early exit is safer.
                    // NOTE: 'itemLockKey' is assigned by now if we reached here.
                    if (itemLockKey != null)
                    {
                        await _jobScheduler.ReleaseLockAsync(itemLockKey);
                    }

                    if (globalLockAcquired)
                    {
                        await _jobScheduler.ReleaseLockAsync(globalOrchestrationLockKey);
                    }

                    return;
                }

                NewsItem? newsItem = await _newsItemRepository.GetByIdAsync(newsItemId, cancellationToken);
                if (newsItem == null)
                {
                    _logger.LogWarning("NewsItem {Id} not found. Cannot dispatch.", newsItemId);
                    // Ensure locks are released on early exit due to null newsItem
                    if (itemLockKey != null)
                    {
                        await _jobScheduler.ReleaseLockAsync(itemLockKey);
                    }

                    if (globalLockAcquired)
                    {
                        await _jobScheduler.ReleaseLockAsync(globalOrchestrationLockKey);
                    }

                    return;
                }

                // Fetch all potentially eligible users
                IEnumerable<User> targetUsers = await _userRepository.GetUsersForNewsNotificationAsync(
                    newsItem.AssociatedSignalCategoryId, newsItem.IsVipOnly, cancellationToken);

                List<long> uniqueTelegramIds = targetUsers
                    .Select(u => long.TryParse(u.TelegramId, out long id) ? (long?)id : null)
                    .Where(id => id.HasValue)
                    .Select(id => id!.Value)
                    .Distinct()
                    .ToList();

                if (!uniqueTelegramIds.Any())
                {
                    _logger.LogInformation("No unique, eligible users found for NewsItem {NewsItemId}.", newsItemId);
                    // Ensure locks are released on early exit
                    if (itemLockKey != null)
                    {
                        await _jobScheduler.ReleaseLockAsync(itemLockKey);
                    }

                    if (globalLockAcquired)
                    {
                        await _jobScheduler.ReleaseLockAsync(globalOrchestrationLockKey);
                    }

                    return;
                }

                // --- REVISED FIX: MORE ROBUST USER FILTERING ---
                List<long> eligibleUsersForThisDispatch = [];
                const int globalNotificationLimit = 5;
                TimeSpan globalNotificationPeriod = TimeSpan.FromMinutes(15);

                _logger.LogInformation("Starting EXPLICIT rate limit check in orchestrator for {UserCount} unique users for NewsItem {NewsItemId}.", uniqueTelegramIds.Count, newsItemId);

                foreach (long userId in uniqueTelegramIds)
                {
                    bool isAlreadyOverLimit = await _rateLimiter.IsUserOverLimitAsync(userId, globalNotificationLimit, globalNotificationPeriod);

                    if (isAlreadyOverLimit)
                    {
                        _logger.LogInformation("DISPATCH FILTER SKIP: User {UserId} has reached their global notification limit ({Limit}/{Period}). Not enqueueing job for NewsItem {NewsItemId}.",
                             userId, globalNotificationLimit, globalNotificationPeriod, newsItemId);
                    }
                    else
                    {
                        eligibleUsersForThisDispatch.Add(userId);
                        _logger.LogInformation("DISPATCH FILTER PASS: User {UserId} is eligible to receive notification for NewsItem {NewsItemId}.", newsItemId, userId);
                    }
                }

                if (!eligibleUsersForThisDispatch.Any())
                {
                    _logger.LogInformation("No eligible users remaining for NewsItem {NewsItemId} after rate limit filtering.", newsItemId);
                    // Ensure locks are released on early exit
                    if (itemLockKey != null)
                    {
                        await _jobScheduler.ReleaseLockAsync(itemLockKey);
                    }

                    if (globalLockAcquired)
                    {
                        await _jobScheduler.ReleaseLockAsync(globalOrchestrationLockKey);
                    }

                    return;
                }
                // --- END REVISED FIX ---

                // Cache the FILTERED list of eligible user IDs for this specific news item dispatch.
                string userListCacheKey = $"dispatch:users:{newsItemId}";
                await _redisCircuitBreaker.ExecuteAsync(async () =>
                {
                    string serializedUserIds = JsonSerializer.Serialize(eligibleUsersForThisDispatch);
                    _ = await _redisDb.StringSetAsync(userListCacheKey, serializedUserIds, TimeSpan.FromHours(24));
                });
                _logger.LogInformation("Cached {Count} UNIQUE eligible user IDs to Redis for NewsItem {NewsItemId} after rate limit filtering.", eligibleUsersForThisDispatch.Count, newsItemId);

                // Now, proceed with chunking and enqueuing based on the FILTERED list.
                int totalUsers = eligibleUsersForThisDispatch.Count;
                int chunks = (int)Math.Ceiling((double)totalUsers / chunkSize);

                for (int i = 0; i < chunks; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int chunkStartIndex = i * chunkSize;

                    int itemsInThisChunk = Math.Min(chunkSize, totalUsers - chunkStartIndex);

                    _ = _jobScheduler.Enqueue<INotificationDispatchService>(
                        service => service.ProcessNotificationChunkAsync(newsItemId, userListCacheKey, chunkStartIndex, itemsInThisChunk, CancellationToken.None));
                }

                _logger.LogInformation("Dispatch Coordination Complete for NewsItem {NewsItemId}. Enqueued {ChunkCount} manager jobs to process {TotalUserCount} eligible unique users (after rate limit filtering).", newsItemId, chunks, totalUsers);
            }
            catch (BrokenCircuitException)
            {
                _logger.LogWarning("Dispatch for NewsItem {NewsItemId} failed: Redis circuit is open.", newsItemId);
                // Release locks on error
                if (itemLockKey != null)
                {
                    await _jobScheduler.ReleaseLockAsync(itemLockKey); // itemLockKey IS defined by now
                }

                if (globalLockAcquired)
                {
                    await _jobScheduler.ReleaseLockAsync(globalOrchestrationLockKey);
                }
            }
            catch (RedisException redisEx)
            {
                _logger.LogError(redisEx, "A Redis error occurred during dispatch orchestration for NewsItem {NewsItemId}.", newsItemId);
                // Release locks on Redis errors
                if (itemLockKey != null)
                {
                    await _jobScheduler.ReleaseLockAsync(itemLockKey);
                }

                if (globalLockAcquired)
                {
                    await _jobScheduler.ReleaseLockAsync(globalOrchestrationLockKey);
                }
            }
            catch (OperationCanceledException)
            {
                // If cancelled, ensure locks are released if acquired.
                if (globalLockAcquired)
                {
                    await _jobScheduler.ReleaseLockAsync(globalOrchestrationLockKey);
                }

                if (itemLockKey != null)
                {
                    await _jobScheduler.ReleaseLockAsync(itemLockKey);
                }

                _logger.LogWarning("Dispatch orchestration for NewsItem {NewsItemId} was cancelled.", newsItemId);
                throw; // Re-throw to signal cancellation
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "A critical error occurred during dispatch orchestration for NewsItem {NewsItemId}.", newsItemId);
                // Release locks on other critical errors
                if (globalLockAcquired)
                {
                    await _jobScheduler.ReleaseLockAsync(globalOrchestrationLockKey);
                }

                if (itemLockKey != null)
                {
                    await _jobScheduler.ReleaseLockAsync(itemLockKey);
                }

                throw; // Re-throw the exception
            }
            finally
            {
                // ========================== FINALLY BLOCK FOR LOCK RELEASE =========================
                // This is the primary safety net. Ensure locks are released if acquired.
                if (globalLockAcquired)
                {
                    await _jobScheduler.ReleaseLockAsync(globalOrchestrationLockKey);
                    _logger.LogInformation("Global dispatch lock released for NewsItem {NewsItemId}.", newsItemId);
                }
                // Ensure the item-specific lock is also released if it was acquired.
                // ReleaseLockAsync should handle cases where itemLockKey is null or the key doesn't exist.
                if (itemLockKey != null)
                {
                    await _jobScheduler.ReleaseLockAsync(itemLockKey);
                }
                // =========================================================================
            }
        }

        [JobDisplayName("Process Dispatch Chunk: News {0}, StartIndex {1}")]
        [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
        public Task ProcessNotificationChunkAsync(Guid newsItemId, string userListCacheKey, int chunkStartIndex, int chunkSize, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Processing dispatch chunk for News {NewsItemId} starting at index {StartIndex} for {ChunkSize} users.", newsItemId, chunkStartIndex, chunkSize);

            // ==========================================================================================
            // == THE FIX: Introduce a small delay between each job's scheduled start time.            ==
            // == This rate is ~25 jobs/sec, safely under the typical Telegram API limit of 30/sec.     ==
            // ==========================================================================================
            const int delayBetweenJobsMs = 40; // 1000ms / 25 jobs = 40ms per job.

            // The loop will still be very fast, it just *schedules* jobs for the near future.
            for (int i = 0; i < chunkSize; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Calculate the delay for THIS specific job.
                // The first job (i=0) has 0 delay.
                // The second job (i=1) is scheduled for 40ms in the future.
                // The third job (i=2) is scheduled for 80ms in the future, and so on.
                TimeSpan scheduledDelay = TimeSpan.FromMilliseconds(i * delayBetweenJobsMs);

                int currentUserIndex = chunkStartIndex + i;

                // --- Use Schedule instead of Enqueue ---
                _ = _jobScheduler.Schedule<INotificationSendingService>(
                    service => service.ProcessNotificationFromCacheAsync(newsItemId, userListCacheKey, currentUserIndex, JobCancellationToken.Null),
                    scheduledDelay
                );
            }

            // This manager job still completes in milliseconds.
            _logger.LogInformation(
                "Chunk processing complete for News {NewsItemId} starting at {StartIndex}. Scheduled {ChunkSize} worker jobs with a staggered start.",
                newsItemId, chunkStartIndex, chunkSize);

            return Task.CompletedTask;
        }

        #endregion
    }
}