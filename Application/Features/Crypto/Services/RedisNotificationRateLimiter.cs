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

        string key = $"notif_limit:{telegramUserId}";

        try
        {
            // =========================================================================
            //  FINAL CORRECTED SCRIPT EVALUATION CALL
            //  We are now passing the raw script string, which matches the expected signature.
            // =========================================================================
            var result = await db.ScriptEvaluateAsync(
                RateLimiterScript, // The raw Lua script string
                new RedisKey[] { key },
                new RedisValue[] { (long)period.TotalSeconds }
            );
            // =========================================================================

            if (result.IsNull)
            {
                _logger.LogWarning("Redis script evaluation returned null for key {Key}. Allowing notification to pass as failsafe.", key);
                return false;
            }

            long currentCount = (long)result;

            if (currentCount > limit)
            {
                _logger.LogInformation("User {UserId} is over the limit. Current count: {Count}, Limit: {Limit}", telegramUserId, currentCount, limit);
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