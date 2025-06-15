// File: Application/Services/NotificationDispatchService.cs

#region Usings
using Application.Common.Interfaces;
using Application.Interfaces;
using Domain.Entities;
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
        public NotificationDispatchService(
             INewsItemRepository newsItemRepository,
             IUserRepository userRepository,
             INotificationJobScheduler jobScheduler,
             ILogger<NotificationDispatchService> logger,
             IConnectionMultiplexer redisConnection,
             INotificationRateLimiter rateLimiter)
        {
            _newsItemRepository = newsItemRepository ?? throw new ArgumentNullException(nameof(newsItemRepository));
            _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            _jobScheduler = jobScheduler ?? throw new ArgumentNullException(nameof(jobScheduler));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));

            if (redisConnection == null)
            {
                throw new ArgumentNullException(nameof(redisConnection));
            }

            _redisDb = redisConnection.GetDatabase();

            // ✅✅ NEW: Initialize the Circuit Breaker policy ✅✅
            // If 3 consecutive Redis operations fail, the circuit will "break" (stop trying) for 1 minute.
            _redisCircuitBreaker = Policy
                .Handle<RedisException>() // It will trigger on any Redis-specific exception
                .CircuitBreakerAsync(
                    exceptionsAllowedBeforeBreaking: 3,
                    durationOfBreak: TimeSpan.FromMinutes(1),
                    onBreak: (exception, timespan) =>
                    {
                        _logger.LogCritical(exception, "Redis Circuit Breaker opened for {BreakDuration}. All dispatch operations will fail fast until the circuit resets.", timespan);
                    },
                    onReset: () => _logger.LogInformation("Redis Circuit Breaker has been reset. Resuming normal dispatch operations."),
                    onHalfOpen: () => _logger.LogWarning("Redis Circuit Breaker is now half-open. The next dispatch will test the connection.")
                );
        }
        #endregion

        #region INotificationDispatchService Implementation




        // In NotificationDispatchService.cs implementation:
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
        /// Fetches users, caches their IDs in Redis, and enqueues a lightweight job for each user.
        /// This method does NOT build the final message content.
        /// </summary>
        /// <summary>
        /// Fetches users, caches their IDs in Redis, and enqueues a lightweight job for each user.
        /// This method does NOT build the final message content.
        /// </summary>
        /// <summary>
        /// Orchestrates a large-scale notification dispatch. It fetches all eligible users,
        //  caches their IDs in Redis using a Circuit Breaker for resilience, and then enqueues
        /// a lightweight job for each user.
        /// </summary>
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