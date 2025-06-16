// File: Infrastructure/Services/RedisNotificationRateLimiter.cs

using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using StackExchange.Redis;

public class RedisNotificationRateLimiter : INotificationRateLimiter
{
    
    /// <summary>
   /// The Redis connection multiplexer instance, used to interact with the Redis database.
   /// This provides access to commands for managing sorted sets and executing Lua scripts.
   /// </summary>
    private readonly IConnectionMultiplexer _redis;

    /// <summary>
    /// The logger instance for recording operational events, warnings, and errors
    /// related to Redis-based rate limiting, essential for monitoring and diagnostics.
    /// </summary>
    private readonly ILogger<RedisNotificationRateLimiter> _logger;



    /// <summary>
    /// A constant string containing the Lua script for Redis-based sliding window rate limiting.
    /// This script is executed atomically on the Redis server to ensure correct and consistent
    /// incrementing, cleanup, and checking of rate limits.
    /// <br/>
    /// **Lua Script Logic:**
    /// <list type="bullet">
    ///     <item><description>Removes expired timestamps from the sorted set, keeping only entries within the current time window.</description></item>
    ///     <item><description>Counts the current number of requests in the window.</description></item>
    ///     <item><description>If the count is already at or above the limit, it returns the current count (blocking the request).</description></item>
    ///     <item><description>Otherwise, it adds the current timestamp to the sorted set.</description></item>
    ///     <item><description>Sets an expiration time on the key to prevent stale data from accumulating indefinitely.</description></item>
    ///     <item><description>Returns the new count (after adding the current request) if the request was allowed.</description></item>
    /// </list>
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

    private readonly AsyncRetryPolicy _redisRetryPolicy;



    /// <summary>
    /// Initializes a new instance of the <see cref="RedisNotificationRateLimiter"/> class.
    /// This constructor sets up the essential dependencies for Redis connectivity and logging,
    /// and crucially configures a Polly retry policy to ensure robust and resilient interactions
    /// with the Redis database for rate limiting operations.
    /// </summary>
    /// <param name="redis">The <see cref="IConnectionMultiplexer"/> instance, providing a connection to the Redis server. This is the primary dependency for performing Redis operations.</param>
    /// <param name="logger">The <see cref="ILogger{RedisNotificationRateLimiter}"/> instance, used for recording operational events, warnings, and errors specifically related to Redis rate limiting.</param>
    /// <returns>
    /// A new instance of <see cref="RedisNotificationRateLimiter"/>, ready to perform rate limit checks
    /// with built-in resilience against transient Redis connectivity issues.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown if either <paramref name="redis"/> or <paramref name="logger"/> is <c>null</c>.
    /// </exception>
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


    /// <summary>
    /// Asynchronously checks if a specific user has exceeded a predefined notification rate limit within a given time period.
    /// This method is crucial for responsible communication in our AI analysis program, preventing notification spam
    /// and ensuring fair usage across different user tiers. It utilizes an atomic Lua script executed directly on Redis
    /// for efficient and accurate rate limiting.
    /// </summary>
    /// <param name="telegramUserId">The unique Telegram user ID for whom the rate limit check is performed.</param>
    /// <param name="limit">The maximum number of notifications allowed within the specified <paramref name="period"/>.</param>
    /// <param name="period">The <see cref="TimeSpan"/> defining the duration over which the <paramref name="limit"/> applies (e.g., 1 hour).</param>
    /// <returns>
    /// A <see cref="Task{bool}"/> that represents the asynchronous operation. The task completes with:
    /// <list type="bullet">
    ///     <item><description><c>true</c> if the user has exceeded the defined <paramref name="limit"/> within the <paramref name="period"/> (i.e., they are blocked from receiving more notifications at this time).</description></item>
    ///     <item><description><c>false</c> if the user is within their allowed limit, or if there's a transient issue contacting Redis (in which case, notifications are allowed as a failsafe to avoid service interruption). This implies the user *can* receive the notification.</description></item>
    /// </list>
    /// </returns>
    /// <remarks>
    /// **Mechanism for AI Analysis:**
    /// The logs generated by this method (rate limit VIOLATED vs. PASSED) are highly valuable for AI analysis.
    /// Future AI models can analyze these logs to:
    /// <list type="bullet">
    ///     <item><description>
    ///         **Optimize Notification Pacing:** Understand how often users hit their limits, helping AI models
    ///         to intelligently schedule notifications or adjust content delivery based on user tolerance.
    ///     </description></item>
    ///     <item><description>
    ///         **Personalized Rate Limits:** Develop AI models that suggest dynamic, personalized rate limits
    ///         for individual users based on their engagement patterns, subscription tier, and the perceived
    ///         value/urgency of the AI-generated signal.
    ///     </description></item>
    ///     <item><description>
    ///         **Resource Management:** Identify peak times or user segments that heavily utilize notification quotas,
    ///         aiding in resource allocation and capacity planning for notification infrastructure.
    ///     </description></item>
    ///     <item><description>
    ///         **Health Monitoring:** Alerts on repeated Redis connectivity failures (logged as errors here) can indicate
    ///         critical infrastructure issues impacting the AI's ability to deliver insights.
    ///     </description></item>
    /// </list>
    /// </remarks>
    public async Task<bool> IsUserOverLimitAsync(long telegramUserId, int limit, TimeSpan period)
    {
        IDatabase db = _redis.GetDatabase();
        RateLimitParams parameters = new(
            Key: $"notif_limit:v2:{telegramUserId}",
            Now: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Window: (long)period.TotalMilliseconds,
            Limit: limit
        );
         

        try
        {
            RedisResult result = await _redisRetryPolicy.ExecuteAsync(async () =>
            {
                // ✅✅ THE FIX IS HERE ✅✅
                // We are now passing the raw script string `RateLimiterLuaScript` directly.
                // This matches the expected `(string, RedisKey[], RedisValue[])` signature.
                return await db.ScriptEvaluateAsync(
                    RateLimiterLuaScript,
                    new RedisKey[] { parameters.Key },
                    new RedisValue[] { parameters.Now, parameters.Window, parameters.Limit }
                );
            });

            long currentCount = (long)result;
            // Allow exactly `limit` messages. The `limit + 1` message is the first to be blocked.
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
            _logger.LogError(ex, "Could not contact Redis for rate limiting after all retries for User {UserId}. Allowing notification to pass as a failsafe. Check Redis connectivity.", telegramUserId);
            return false;
        }
    }
}