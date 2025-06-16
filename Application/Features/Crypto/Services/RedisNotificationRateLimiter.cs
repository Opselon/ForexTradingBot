// File: Infrastructure/Services/RedisNotificationRateLimiter.cs

using Application.Common.Interfaces; // You will need to add the using for INotificationRateLimiter
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using StackExchange.Redis;

public class RedisNotificationRateLimiter : INotificationRateLimiter
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisNotificationRateLimiter> _logger;
    private readonly AsyncRetryPolicy _redisRetryPolicy;

    #region Lua Scripts

    /// <summary>
    /// Lua script that checks the current count and increments it if below the limit.
    /// This is the original, combined-action script.
    /// </summary>
    private const string RateLimiterLuaScript =
        @"-- KEYS[1]: The unique key for the user's rate limit sorted set
          -- ARGV[1]: The current unix timestamp (in milliseconds)
          -- ARGV[2]: The time window for the limit (in milliseconds)
          -- ARGV[3]: The maximum number of requests allowed in the window

          local clear_before = tonumber(ARGV[1]) - tonumber(ARGV[2])
          redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', clear_before)
          local current_count = redis.call('ZCARD', KEYS[1])
          if current_count >= tonumber(ARGV[3]) then
              return current_count 
          end
          local new_member = ARGV[1]
          redis.call('ZADD', KEYS[1], new_member, new_member)
          redis.call('EXPIRE', KEYS[1], math.ceil(tonumber(ARGV[2]) / 1000) + 60)
          return current_count + 1";

    /// <summary>
    /// A Lua script for Redis that ONLY checks the current rate limit count without incrementing it.
    /// It cleans expired entries and returns the current number of requests in the window.
    /// </summary>
    private const string CheckRateLimitLuaScript =
        @"-- KEYS[1]: The unique key for the user's rate limit sorted set
          -- ARGV[1]: The current unix timestamp (in milliseconds)
          -- ARGV[2]: The time window for the limit (in milliseconds)

          local clear_before = tonumber(ARGV[1]) - tonumber(ARGV[2])
          redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', clear_before)
          local current_count = redis.call('ZCARD', KEYS[1])
          return current_count";

    /// <summary>
    /// A Lua script for Redis that ONLY increments the rate limit counter by adding a new timestamp.
    /// It does not check the limit, just performs the increment and sets the key expiration.
    /// </summary>
    private const string IncrementRateLimitLuaScript =
        @"-- KEYS[1]: The unique key for the user's rate limit sorted set
          -- ARGV[1]: The current unix timestamp (in milliseconds)
          -- ARGV[2]: The time window for the limit (in milliseconds)

          local new_member = ARGV[1]
          redis.call('ZADD', KEYS[1], new_member, new_member)
          redis.call('EXPIRE', KEYS[1], math.ceil(tonumber(ARGV[2]) / 1000) + 60)
          return 1";

    #endregion

    public RedisNotificationRateLimiter(IConnectionMultiplexer redis, ILogger<RedisNotificationRateLimiter> logger)
    {
        _redis = redis;
        _logger = logger;

        _redisRetryPolicy = Policy
            .Handle<RedisConnectionException>()
            .Or<RedisTimeoutException>()
            .WaitAndRetryAsync(
                retryCount: 2,
                sleepDurationProvider: retryAttempt => TimeSpan.FromMilliseconds(100 * retryAttempt),
                onRetry: (exception, timeSpan, retryCount, context) =>
                {
                    _logger.LogWarning(exception, "A transient Redis error occurred while rate limiting. Retrying in {TimeSpan}s. Attempt {RetryCount}/2.", timeSpan.TotalSeconds, retryCount);
                });
    }

    private record RateLimitParams(string Key, long Now, long Window, int Limit);

    #region New Methods for Two-Step Rate Limiting

    /// <summary>
    /// Asynchronously checks if a user has reached or exceeded their notification limit for a given period,
    /// **without** incrementing their usage count. This method is the "check" part of a two-step rate-limiting
    /// process, allowing the system to query the user's status before attempting an operation.
    /// </summary>
    /// <param name="telegramUserId">The unique Telegram user ID to check.</param>
    /// <param name="limit">The maximum number of notifications allowed. The check will return true if the current count is greater than or equal to this value.</param>
    /// <param name="period">The time window for the rate limit (e.g., 1 hour).</param>
    /// <returns>
    /// A <see cref="Task{bool}"/> that completes with:
    /// <list type="bullet">
    ///     <item><description><c>true</c> if the user's current notification count is at or over the specified limit.</description></item>
    ///     <item><description><c>false</c> if the user is still under their limit or if a Redis error occurs (fail-open).</description></item>
    /// </list>
    /// </returns>
    public async Task<bool> IsUserAtOrOverLimitAsync(long telegramUserId, int limit, TimeSpan period)
    {
        IDatabase db = _redis.GetDatabase();
        string key = $"notif_limit:v2:{telegramUserId}";
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long window = (long)period.TotalMilliseconds;

        try
        {
            RedisResult result = await _redisRetryPolicy.ExecuteAsync(async () =>
            {
                return await db.ScriptEvaluateAsync(
                    CheckRateLimitLuaScript,
                    new RedisKey[] { key },
                    new RedisValue[] { now, window }
                );
            });

            long currentCount = (long)result;
            bool isAtOrOverLimit = currentCount >= limit;

            if (isAtOrOverLimit)
            {
                _logger.LogTrace("Rate limit CHECK for User {UserId}: AT or OVER limit. Count: {CurrentCount}/{Limit}.", telegramUserId, currentCount, limit);
            }

            return isAtOrOverLimit;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not contact Redis for rate limit check for User {UserId}. Allowing request to pass as failsafe.", telegramUserId);
            return false; // Fail-open: if Redis is down, don't block notifications.
        }
    }

    /// <summary>
    /// Asynchronously increments a user's notification usage count for a given period. This method
    /// should be called **after** a notification has been successfully sent to ensure the user's
    /// quota is accurately consumed. It is the "increment" part of a two-step rate-limiting process.
    /// </summary>
    /// <param name="telegramUserId">The unique Telegram user ID whose usage to increment.</param>
    /// <param name="period">The time window for the rate limit. This is used to set the key's expiration.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation of incrementing the user's count in Redis.</returns>
    public async Task IncrementUsageAsync(long telegramUserId, TimeSpan period)
    {
        IDatabase db = _redis.GetDatabase();
        string key = $"notif_limit:v2:{telegramUserId}";
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long window = (long)period.TotalMilliseconds;

        try
        {
            await _redisRetryPolicy.ExecuteAsync(async () =>
            {
                await db.ScriptEvaluateAsync(
                    IncrementRateLimitLuaScript,
                    new RedisKey[] { key },
                    new RedisValue[] { now, window }
                );
            });
            _logger.LogTrace("Successfully incremented rate limit usage for User {UserId}.", telegramUserId);
        }
        catch (Exception ex)
        {
            // Log the error but do not throw. Failing to increment is not a critical failure for the user,
            // though it is a system integrity issue that needs to be monitored.
            _logger.LogError(ex, "Failed to increment rate limit usage for User {UserId} after retries. Check Redis connectivity.", telegramUserId);
        }
    }

    #endregion

    /// <summary>
    /// Asynchronously checks if a specific user has exceeded a predefined notification rate limit and increments the count if they have not.
    /// This method performs both actions atomically and is suitable for scenarios where the check-and-increment must happen in one step.
    /// </summary>
    /// <remarks>
    /// This method is retained for compatibility or for use cases where the two-step process is not needed.
    /// For ensuring users receive an exact number of successful notifications, prefer using <see cref="IsUserAtOrOverLimitAsync"/>
    /// followed by <see cref="IncrementUsageAsync"/> upon success.
    /// </remarks>
    public async Task<bool> IsUserOverLimitAsync(long telegramUserId, int limit, TimeSpan period)
    {
        IDatabase db = _redis.GetDatabase();
        var parameters = new RateLimitParams(
            Key: $"notif_limit:v2:{telegramUserId}",
            Now: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Window: (long)period.TotalMilliseconds,
            Limit: limit
        );

        try
        {
            RedisResult result = await _redisRetryPolicy.ExecuteAsync(async () =>
            {
                return await db.ScriptEvaluateAsync(
                    RateLimiterLuaScript,
                    new RedisKey[] { parameters.Key },
                    new RedisValue[] { parameters.Now, parameters.Window, parameters.Limit }
                );
            });

            long currentCount = (long)result;
            bool isOverLimit = currentCount > limit;

            if (isOverLimit)
            {
                _logger.LogWarning("Rate limit VIOLATED for User {UserId}. Count: {CurrentCount}/{Limit} per {Period}.",
                    telegramUserId, currentCount, limit, period);
            }
            else
            {
                _logger.LogInformation("Rate limit check PASSED for User {UserId}. Count: {CurrentCount}/{Limit} per {Period}.",
                    telegramUserId, currentCount, limit, period);
            }

            return isOverLimit;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not contact Redis for rate limiting after all retries for User {UserId}. Allowing notification to pass as a failsafe.", telegramUserId);
            return false;
        }
    }
}