// File: Infrastructure/Services/RedisNotificationRateLimiter.cs

using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Threading.Tasks;

public class RedisNotificationRateLimiter : INotificationRateLimiter
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisNotificationRateLimiter> _logger;

    // This Lua script is atomic and highly efficient.
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

    /// <summary>
    /// Atomically checks if a user is over their notification limit using a fixed time window.
    /// </summary>
    public async Task<bool> IsUserOverLimitAsync(long telegramUserId, int limit, TimeSpan period)
    {
        var db = _redis.GetDatabase();

        // ✅ IMPROVEMENT: Create a key that is unique for the user AND the current calendar hour.
        // This makes the count automatically reset at the start of the next hour.
        // Example key for user 123 at 3:45 PM on Oct 27, 2023 -> "notif_limit:123:2023-10-27-15"
        string timeWindow = DateTime.UtcNow.ToString("yyyy-MM-dd-HH");
        string key = $"notif_limit:{telegramUserId}:{timeWindow}";

        try
        {
            var result = await db.ScriptEvaluateAsync(
                RateLimiterScript,
                new RedisKey[] { key },
                // The expiration is set to be longer than the window to prevent premature expiry.
                // For a 1-hour window, expiring after 2 hours is very safe.
                new RedisValue[] { (long)period.TotalSeconds + 3600 }
            );

            if (result.IsNull)
            {
                _logger.LogWarning("Redis script returned null for key {Key}. Allowing notification as a failsafe.", key);
                return false;
            }

            long currentCount = (long)result;

            if (currentCount > limit)
            {
                _logger.LogInformation("User {UserId} is over the limit for time window {TimeWindow}. Count: {Count}, Limit: {Limit}",
                    telegramUserId, timeWindow, currentCount, limit);
                return true; // Is over limit
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not contact Redis for rate limiting. Allowing notification to pass as a failsafe.");
            return false;
        }

        return false; // Not over limit
    }
}