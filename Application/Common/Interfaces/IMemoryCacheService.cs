// -----------------
// NEW FILE
// -----------------
namespace Application.Common.Interfaces
{
    /// <summary>
    /// Defines a contract for a simple in-memory caching service.
    /// This allows for abstracting the caching mechanism (e.g., MemoryCache, Redis).
    /// </summary>
    /// <typeparam name="T">The type of the object to be cached.</typeparam>
    public interface IMemoryCacheService<T> where T : class
    {
        /// <summary>
        /// Tries to get a value from the cache.
        /// </summary>
        /// <param name="key">The unique key for the cached item.</param>
        /// <param name="value">The retrieved value, if found.</param>
        /// <returns>True if the item was found in the cache; otherwise, false.</returns>
        bool TryGetValue(string key, out T? value);

        /// <summary>
        /// Sets a value in the cache with a specified expiration.
        /// </summary>
        /// <param name="key">The unique key for the item.</param>
        /// <param name="value">The object to cache.</param>
        /// <param name="absoluteExpirationRelativeToNow">The sliding expiration timespan for the item.</param>
        void Set(string key, T value, TimeSpan absoluteExpirationRelativeToNow);
    }
}