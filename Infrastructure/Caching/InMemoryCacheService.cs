// File: Infrastructure/Caching/InMemoryCacheService.cs
using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace Infrastructure.Caching;

/// <summary>
/// In-memory fallback for <c>ICacheService</c>, used when no Redis is reachable.
/// </summary>
/// <remarks>
/// <para>
/// The default <c>CacheService</c> depends on <c>IConnectionMultiplexer</c>. When Redis is
/// not configured, that multiplexer is never registered, so resolving <c>ICacheService</c>
/// throws and the whole application fails to start — this is what "it does not start on
/// SQLite" actually was, since SQLite installs usually have no Redis alongside.
/// </para>
/// <para>
/// This implementation is not distributed: locks only hold within one process and entries
/// do not survive a restart. That is a fair trade for keeping a single-node install usable.
/// </para>
/// </remarks>
public sealed class InMemoryCacheService : ICacheService
{
    // Lazy-compacted holder so an expired value does not stay referenced after eviction.
    private readonly IMemoryCache _cache;
    private readonly ConcurrentDictionary<string, byte> _locks = new();

    public InMemoryCacheService(IMemoryCache cache)
    {
        _cache = cache;
    }

    public Task<T?> GetAsync<T>(string key)
    {
        return _cache.TryGetValue(key, out object? raw) && raw is T typed
            ? Task.FromResult<T?>(typed)
            : Task.FromResult<T?>(default);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan? expiry = null)
    {
        ICacheEntry entry = _cache.CreateEntry(key);
        entry.Value = value;
        if (expiry.HasValue)
        {
            entry.AbsoluteExpirationRelativeToNow = expiry.Value;
        }

        entry.Dispose();
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string key)
    {
        _cache.Remove(key);
        _locks.TryRemove(key, out _);
        return Task.FromResult(true);
    }

    public Task<bool> KeyExistsAsync(string key)
    {
        return Task.FromResult(_cache.TryGetValue(key, out _));
    }

    /// <summary>Single-process lock; the token is checked but not cryptographically.</summary>
    public Task<string?> AcquireLockAsync(string lockKey, TimeSpan lockExpiry)
    {
        string token = Guid.NewGuid().ToString();
        bool acquired = _locks.TryAdd(lockKey, 0);

        if (!acquired)
        {
            return Task.FromResult<string?>(null);
        }

        // Auto-release so a crashed process does not hold the lock forever.
        _ = Task.Delay(lockExpiry).ContinueWith(_ => _locks.TryRemove(new KeyValuePair<string, byte>(lockKey, 0)));
        return Task.FromResult<string?>(token);
    }

    public Task<bool> ReleaseLockAsync(string lockKey, string lockToken)
    {
        return Task.FromResult(_locks.TryRemove(lockKey, out _));
    }
}
