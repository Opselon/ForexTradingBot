// Place this in your Infrastructure/Services or a similar folder
// You will need to add the StackExchange.Redis NuGet package.
using StackExchange.Redis;
using System.Threading.Tasks;
using System;
using Microsoft.Extensions.Logging;

public class RedisNotificationRateLimiter : INotificationRateLimiter
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisNotificationRateLimiter> _logger;

    // This is the raw Lua script string. It is sent with every request.
    // It is still highly efficient on the Redis side.
    private const string RateLimiterScript =
        @"local count = redis.call('INCR', KEYS[1])
          if tonumber(count) == 1 then
              redis.call('EXPIRE', KEYS[1], ARGV[1])
          end
          return count";

    public RedisNotificationRateLimiter(IConnectionMultiplexer redis, ILogger<RedisNotificationRateLimiter> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<bool> IsUserOverLimitAsync(long telegramUserId, int limit, TimeSpan period)
    {
        var db = _redis.GetDatabase();

        // ✅✅ IMPROVEMENT: Create a key that is unique for the user AND the current time window.
        // This makes the count automatically reset when the time window passes. For an hourly limit,
        // a key like "notif_limit:12345:2023-10-27-15" will be used. At 16:00, a new key will be used.
        string timeWindow = DateTime.UtcNow.ToString("yyyy-MM-dd-HH");
        string key = $"notif_limit:{telegramUserId}:{timeWindow}";

        try
        {
            var result = await db.ScriptEvaluateAsync(
                RateLimiterScript,
                new RedisKey[] { key },
                // The expiration should be slightly longer than the window to prevent premature expiry.
                // For a 1-hour window, expiring after 2 hours is safe.
                new RedisValue[] { (long)period.TotalSeconds + 3600 }
            );

            if (result.IsNull)
            {
                _logger.LogWarning("Redis script evaluation returned null for key {Key}. Allowing notification to pass.", key);
                return false;
            }

            long currentCount = (long)result;

            if (currentCount > limit)
            {
                _logger.LogInformation("User {UserId} is over the limit for time window {TimeWindow}. Count: {Count}, Limit: {Limit}",
                    telegramUserId, timeWindow, currentCount, limit);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not contact Redis for rate limiting. Allowing notification to pass as a failsafe.");
            return false;
        }

        return false;
    }
}