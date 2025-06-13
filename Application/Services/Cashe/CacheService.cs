// In Infrastructure/Services/CacheService.cs
using StackExchange.Redis;
using System.Text.Json;

public class CacheService : ICacheService
{
    private readonly IDatabase _redisDb;

    public CacheService(IConnectionMultiplexer redis)
    {
        _redisDb = redis.GetDatabase();
    }

    public async Task<T?> GetAsync<T>(string key)
    {
        var redisValue = await _redisDb.StringGetAsync(key);
        if (redisValue.HasValue)
        {
            return JsonSerializer.Deserialize<T>(redisValue!);
        }
        return default;
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null)
    {
        var jsonValue = JsonSerializer.Serialize(value);
        await _redisDb.StringSetAsync(key, jsonValue, expiry);
    }

    public Task RemoveAsync(string key)
    {
        return _redisDb.KeyDeleteAsync(key);
    }
}