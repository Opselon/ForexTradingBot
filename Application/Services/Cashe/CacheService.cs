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
        RedisValue redisValue = await _redisDb.StringGetAsync(key);
        return redisValue.HasValue ? JsonSerializer.Deserialize<T>(redisValue!) : default;
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null)
    {
        string jsonValue = JsonSerializer.Serialize(value);
        _ = await _redisDb.StringSetAsync(key, jsonValue, expiry);
    }

    public Task RemoveAsync(string key)
    {
        return _redisDb.KeyDeleteAsync(key);
    }
}