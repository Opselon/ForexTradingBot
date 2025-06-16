// In a new file, e.g., Infrastructure/Queue/RedisUpdateChannel.cs
using StackExchange.Redis;
using System.Text.Json;
using Polly;
using Polly.Retry;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using TelegramPanel.Queue;
using Telegram.Bot.Types;

public class RedisUpdateChannel : ITelegramUpdateChannel
{
    private readonly ILogger<RedisUpdateChannel> _logger;
    private readonly IDatabase _redisDb;
    private readonly AsyncRetryPolicy _redisRetryPolicy;
    private const string UpdateQueueKey = "queue:telegram:updates";

    public RedisUpdateChannel(IConnectionMultiplexer redis, ILogger<RedisUpdateChannel> logger)
    {
        _logger = logger;
        _redisDb = redis.GetDatabase();

        // Polly policy for handling transient Redis connection issues
        _redisRetryPolicy = Policy
            .Handle<RedisConnectionException>()
            .Or<RedisTimeoutException>()
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                (exception, timeSpan, retryCount, context) =>
                {
                    _logger.LogWarning(exception, "Redis operation failed. Retrying in {TimeSpan}s. Attempt {RetryCount}/3.", timeSpan, retryCount);
                });
    }

    public async ValueTask WriteAsync(Update update, CancellationToken cancellationToken = default)
    {
        await _redisRetryPolicy.ExecuteAsync(async () =>
        {
            try
            {
                // Serialize the Update object to JSON
                var jsonUpdate = JsonSerializer.Serialize(update);

                // LPUSH the JSON string to the head of the Redis List
                await _redisDb.ListLeftPushAsync(UpdateQueueKey, jsonUpdate);

                _logger.LogTrace("Enqueued update {UpdateId} to Redis queue '{QueueKey}'.", update.Id, UpdateQueueKey);
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "Failed to serialize Telegram update {UpdateId}.", update.Id);
                // Do not re-throw; we don't want to retry a serialization error.
            }
        });
    }

    public async IAsyncEnumerable<Update> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting to consume from Redis queue '{QueueKey}'.", UpdateQueueKey);
        while (!cancellationToken.IsCancellationRequested)
        {
            Update? update = null;
            await _redisRetryPolicy.ExecuteAsync(async () =>
            {
                // BRPOP is the blocking pop command. It waits for 5 seconds for an item.
                // This is much more efficient than a tight loop with a manual delay.
                var redisValue = await _redisDb.ListRightPopAsync(UpdateQueueKey);

                if (redisValue.HasValue)
                {
                    try
                    {
                        // Deserialize the JSON string back to an Update object
                        update = JsonSerializer.Deserialize<Update>(redisValue!);
                    }
                    catch (JsonException jsonEx)
                    {
                        _logger.LogError(jsonEx, "Failed to deserialize update from Redis. Value: '{RedisValue}'. Moving to dead-letter queue.", redisValue);
                        // Move malformed message to a dead-letter queue for inspection
                        await _redisDb.ListLeftPushAsync($"{UpdateQueueKey}:deadletter", redisValue);
                    }
                }
            });

            if (update != null)
            {
                // Yield the successfully deserialized update to the consumer
                yield return update;
            }
            else
            {
                // If BRPOP times out (queue is empty), wait a short moment before trying again
                // to prevent a tight loop in case of continuous Redis failures.
                await Task.Delay(100, cancellationToken);
            }
        }
        _logger.LogInformation("Stopped consuming from Redis queue '{QueueKey}'.", UpdateQueueKey);
    }
}