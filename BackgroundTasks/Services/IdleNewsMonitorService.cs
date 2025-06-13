// File: BackgroundTasks/Services/IdleNewsMonitorService.cs

using Application.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly;
using StackExchange.Redis;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BackgroundTasks.Services
{
    public class IdleNewsMonitorService : BackgroundService, IIdleNewsMonitorService
    {
        private readonly ILogger<IdleNewsMonitorService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IDatabase _redisDb;
        private readonly AsyncCircuitBreakerPolicy _circuitBreakerPolicy; // ✅ NEW
        private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(10); // Run less frequently
        private const string NewsQueueKey = "system_news_queue";

        public IdleNewsMonitorService(
            ILogger<IdleNewsMonitorService> logger,
            IServiceProvider serviceProvider,
            IConnectionMultiplexer redisConnection)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _redisDb = redisConnection.GetDatabase();

            // ✅ NEW: Define a circuit breaker policy.
            // If 2 consecutive exceptions occur, the circuit will "break" for 5 minutes.
            _circuitBreakerPolicy = Policy
                .Handle<RedisException>() // Handle any Redis-related exception
                .CircuitBreakerAsync(
                    exceptionsAllowedBeforeBreaking: 2,
                    durationOfBreak: TimeSpan.FromMinutes(5),
                    onBreak: (exception, timespan) =>
                    {
                        _logger.LogCritical(exception, "IdleNewsMonitorService Circuit Breaker opened for {BreakDuration}. No queue processing will occur.", timespan);
                    },
                    onReset: () => _logger.LogInformation("IdleNewsMonitorService Circuit Breaker reset. Resuming normal operation."),
                    onHalfOpen: () => _logger.LogWarning("IdleNewsMonitorService Circuit Breaker is now half-open. The next operation will determine its state.")
                );
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Idle News Monitor Service (Queue Drainer) is starting.");
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); // Initial delay

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // ✅ NEW: Execute all logic within the circuit breaker
                    await _circuitBreakerPolicy.ExecuteAsync(async () =>
                    {
                        // ✅ REVISED LOGIC: Simple, robust queue length check.
                        var queueLength = await _redisDb.ListLengthAsync(NewsQueueKey);
                        if (queueLength > 0)
                        {
                            _logger.LogInformation("Idle Monitor found {QueueLength} items in the system news queue. Processing one.", queueLength);
                            await ProcessNewsItemFromQueue(stoppingToken);
                        }
                        else
                        {
                            _logger.LogTrace("System news queue is empty. Nothing to process.");
                        }
                    });
                }
                catch (BrokenCircuitException)
                {
                    // This exception is thrown when we try to execute while the circuit is open.
                    // We log it at a lower level because the onBreak delegate already logged the critical error.
                    _logger.LogWarning("Skipping idle check because the Redis circuit is open.");
                }
                catch (Exception ex)
                {
                    // Catch any other unexpected errors.
                    _logger.LogError(ex, "An unhandled error occurred in the Idle News Monitor service loop.");
                }

                await Task.Delay(CheckInterval, stoppingToken);
            }
        }

        private async Task ProcessNewsItemFromQueue(CancellationToken stoppingToken)
        {
            // This internal logic remains largely the same.
            var newsJson = await _redisDb.ListRightPopAsync(NewsQueueKey);
            if (!newsJson.HasValue) return;

            var newsItem = JsonSerializer.Deserialize<SystemNewsItem>(newsJson);
            if (newsItem == null) return;

            _logger.LogInformation("Processing news item '{Title}' from queue.", newsItem.Title);

            using var scope = _serviceProvider.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<INotificationDispatchService>();
            await dispatcher.DispatchNewsNotificationAsync(newsItem.Id, stoppingToken);
        }
    }

    // A simple DTO for news items in the queue
    public class SystemNewsItem
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; }
        public string Message { get; set; }
    }
}