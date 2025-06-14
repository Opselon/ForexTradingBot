// File: Infrastructure/Services/RedisNotificationRateLimiter.cs

using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using StackExchange.Redis;

public class RedisNotificationRateLimiter : INotificationRateLimiter
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisNotificationRateLimiter> _logger;

    // This is the raw Lua script string. It is sent with every request,
    // which is highly reliable and resolves the compiler error.
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