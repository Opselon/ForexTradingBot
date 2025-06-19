// In Infrastructure/Services/CacheService.cs
using Application.Common.Interfaces;
using StackExchange.Redis;
using System.Text.Json;

/// <summary>
/// A Redis-based implementation of the cache service, providing distributed caching,
/// existence checks, and distributed locking capabilities.
/// </summary>
public class CacheService : ICacheService
{
    private readonly IDatabase _redisDb;

    public CacheService(IConnectionMultiplexer redis)
    {
        _redisDb = redis.GetDatabase();
    }

    public async Task<T?> GetAsync<T>(string key)
    {
        RedisValue redisValue = await _redisDb.StringGetAsync(key);
        return redisValue.HasValue ? JsonSerializer.Deserialize<T>(redisValue!) : default;
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null)
    {
        string jsonValue = JsonSerializer.Serialize(value);
        await _redisDb.StringSetAsync(key, jsonValue, expiry);
    }

    public Task<bool> RemoveAsync(string key)
    {
        return _redisDb.KeyDeleteAsync(key);
    }

    // --- NEW IMPLEMENTATIONS FOR THE UPGRADE ---

    /// <inheritdoc />
    public Task<bool> KeyExistsAsync(string key)
    {
        return _redisDb.KeyExistsAsync(key);
    }

    /// <inheritdoc />
    public async Task<string?> AcquireLockAsync(string lockKey, TimeSpan lockExpiry)
    {
        // The lock token must be unique to this specific lock attempt.
        // This ensures that we don't accidentally release a lock held by another process.
        string lockToken = Guid.NewGuid().ToString();

        // LockTakeAsync attempts to acquire the lock. It returns true if successful.
        bool lockAcquired = await _redisDb.LockTakeAsync(lockKey, lockToken, lockExpiry);

        if (lockAcquired)
        {
            // Return the unique token so the caller can use it to release the lock.
            return lockToken;
        }

        // Return null if the lock could not be acquired.
        return null;
    }

    /// <inheritdoc />
    public Task<bool> ReleaseLockAsync(string lockKey, string lockToken)
    {
        // Only release the lock if the provided token matches the one stored in Redis.
        // This prevents one process from releasing a lock held by another.
        return _redisDb.LockReleaseAsync(lockKey, lockToken);
    }
}