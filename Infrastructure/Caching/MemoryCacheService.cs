// -----------------
// NEW FILE
// -----------------
using Application.Common.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace Infrastructure.Caching
{
    /// <summary>
    /// Implements the IMemoryCacheService using the built-in .NET IMemoryCache.
    /// </summary>
    /// <typeparam name="T">The type of the object being cached.</typeparam>
    public class MemoryCacheService<T> : IMemoryCacheService<T> where T : class
    {
        private readonly IMemoryCache _memoryCache;

        public MemoryCacheService(IMemoryCache memoryCache)
        {
            _memoryCache = memoryCache;
        }

        public bool TryGetValue(string key, out T? value)
        {
            return _memoryCache.TryGetValue(key, out value);
        }

        public void Set(string key, T value, TimeSpan absoluteExpirationRelativeToNow)
        {
            MemoryCacheEntryOptions cacheEntryOptions = new()
            {
                AbsoluteExpirationRelativeToNow = absoluteExpirationRelativeToNow
            };
            _ = _memoryCache.Set(key, value, cacheEntryOptions);
        }
    }
}