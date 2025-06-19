public interface ICacheService
{
    Task<T?> GetAsync<T>(string key);
    Task SetAsync<T>(string key, T value, TimeSpan? expiry = null);
    Task<bool> RemoveAsync(string key);

    // --- NEW METHODS FOR THE UPGRADE ---

    /// <summary>
    /// Checks if a key exists in the cache without retrieving its value.
    /// </summary>
    /// <param name="key">The key to check.</param>
    /// <returns>True if the key exists, otherwise false.</returns>
    Task<bool> KeyExistsAsync(string key);

    /// <summary>
    /// Attempts to acquire a distributed lock.
    /// </summary>
    /// <param name="lockKey">The key to use for the lock.</param>
    /// <param name="lockExpiry">How long the lock should be held before it automatically expires.</param>
    /// <returns>A unique lock token if the lock was acquired, otherwise null.</returns>
    Task<string?> AcquireLockAsync(string lockKey, TimeSpan lockExpiry);

    /// <summary>
    /// Releases a previously acquired distributed lock.
    /// </summary>
    /// <param name="lockKey">The key of the lock to release.</param>
    /// <param name="lockToken">The unique token returned by AcquireLockAsync.</param>
    /// <returns>True if the lock was released, otherwise false.</returns>
    Task<bool> ReleaseLockAsync(string lockKey, string lockToken);
}