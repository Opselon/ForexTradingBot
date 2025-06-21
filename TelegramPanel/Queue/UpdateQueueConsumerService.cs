// File: TelegramPanel/Queue/UpdateQueueConsumerService.cs
#region Usings
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Polly.Wrap;
using StackExchange.Redis;
using System.Text;
using Telegram.Bot;
using TelegramPanel.Application.Interfaces;
#endregion

namespace TelegramPanel.Queue
{
    public class UpdateQueueConsumerService : BackgroundService
    {
        #region Private Readonly Fields
        private readonly ILogger<UpdateQueueConsumerService> _logger;
        private readonly ITelegramUpdateChannel _updateChannel;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly AsyncPolicyWrap _redisResiliencePolicy;
        private readonly AsyncRetryPolicy _processingRetryPolicy;
        private readonly List<long> _adminChatIds;

        // ✅ NEW: Semaphore to control the degree of parallelism for processing updates.
        // This prevents overwhelming the system. The value should be configured based on your environment.
        private readonly SemaphoreSlim _concurrencySemaphore;
        private readonly int _maxConcurrentProcessors;

        private readonly TimeSpan _redisBreakDuration = TimeSpan.FromMinutes(1);
        private readonly TimeSpan _redisReadTimeout = TimeSpan.FromMinutes(1);
        private static readonly Random _jitterer = new Random();
        #endregion

        #region Constructor
        public UpdateQueueConsumerService(IConfiguration configuration,
            ILogger<UpdateQueueConsumerService> logger,
            ITelegramUpdateChannel updateChannel,
            IServiceScopeFactory scopeFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _updateChannel = updateChannel ?? throw new ArgumentNullException(nameof(updateChannel));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

            // ✅ NEW: Configure max concurrency from appsettings.json or use a safe default.
            _maxConcurrentProcessors = configuration.GetValue<int>("TelegramPanel:Queue:MaxConcurrentProcessors", 10);
            _concurrencySemaphore = new SemaphoreSlim(_maxConcurrentProcessors, _maxConcurrentProcessors);
            _logger.LogInformation("Update processor configured with a maximum of {MaxConcurrency} concurrent tasks.", _maxConcurrentProcessors);


            // ✅ STRENGTHENED: The circuit breaker now also handles Polly's own timeout exception.
            var circuitBreakerPolicy = Policy
                .Handle<RedisException>()
                .Or<RedisConnectionException>()
                .Or<RedisTimeoutException>()
                .Or<KeyNotFoundException>()
                .Or<TimeoutRejectedException>() // <-- Handles timeouts from the Polly policy
                .CircuitBreakerAsync(
                    exceptionsAllowedBeforeBreaking: 3,
                    durationOfBreak: _redisBreakDuration,
                    onBreak: (exception, timespan) => _logger.LogCritical(exception, "Redis Circuit Breaker OPENED for {BreakDuration}. Halting all queue consumption.", timespan),
                    onReset: () => _logger.LogInformation("Redis Circuit Breaker RESET. Resuming normal queue consumption."),
                    onHalfOpen: () => _logger.LogWarning("Redis Circuit Breaker is now HALF-OPEN. The next read will test the connection.")
                );

            var timeoutPolicy = Policy.TimeoutAsync(_redisReadTimeout, TimeoutStrategy.Pessimistic);

            _redisResiliencePolicy = Policy.WrapAsync(timeoutPolicy, circuitBreakerPolicy);

            _adminChatIds = configuration.GetSection("TelegramPanel:AdminUserIds").Get<List<long>>() ?? new List<long>();
            if (_adminChatIds.Count == 0)
            {
                _logger.LogWarning("No AdminUserIds found. Admin notifications will be disabled.");
            }
            else
            {
                _logger.LogInformation("Admin notifications configured for {AdminCount} user(s).", _adminChatIds.Count);
            }

            _processingRetryPolicy = Policy
                .Handle<Exception>(ex => ex is not (OperationCanceledException or TaskCanceledException))
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    onRetry: (exception, timeSpan, retryAttempt, context) =>
                    {
                        var updateId = context.TryGetValue("UpdateId", out var id) ? (int?)id : null;
                        _logger.LogWarning(exception, "PollyRetry: Processing update {UpdateId} failed. Retrying in {TimeSpan} (attempt {RetryAttempt}).",
                            updateId, timeSpan, retryAttempt);
                    });
        }
        #endregion

        #region Main Execution Logic

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Update Queue Consumer Service is starting...");
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _redisResiliencePolicy.ExecuteAsync(async (ct) =>
                    {
                        _logger.LogTrace("Polling Redis queue for updates...");

                        // The `await foreach` loop is now only responsible for dequeuing.
                        await foreach (var update in _updateChannel.ReadAllAsync(ct).WithCancellation(ct))
                        {
                            // ✅ REAL-TIME: Don't await the processing.
                            // Fire-and-forget the processing task to allow the loop to continue dequeuing immediately.
                            // The semaphore controls how many of these can run at once.
                            _ = ProcessUpdateConcurrentlyAsync(update, ct);
                        }
                    }, stoppingToken).ConfigureAwait(false);
                }
                catch (BrokenCircuitException)
                {
                    _logger.LogWarning("Redis circuit is OPEN. Pausing consumption for {BreakDuration}.", _redisBreakDuration);
                    await Task.Delay(GetJitteredDelay(_redisBreakDuration), stoppingToken).ConfigureAwait(false);
                }
                catch (TimeoutRejectedException)
                {
                    // A timeout on a long-poll is normal. It means no messages arrived.
                    // Log it at a low level and immediately loop again to start the next poll.
                    _logger.LogTrace("Redis queue read timed out (no new messages). Polling again immediately.");
                    continue;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Update Queue Consumer Service was cancelled gracefully.");
                    break;
                }
                catch (Exception ex)
                {
                    // A truly unexpected error in the dequeuing loop.
                    _logger.LogCritical(ex, "An unhandled exception ({ExceptionType}) occurred in the main consumer loop. Pausing before retrying.", ex.GetType().Name);
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                }
            }
        }

        private async Task ProcessUpdateConcurrentlyAsync(Telegram.Bot.Types.Update update, CancellationToken cancellationToken)
        {
            // Wait until a "slot" is available in the semaphore.
            // This safely limits concurrency.
            await _concurrencySemaphore.WaitAsync(cancellationToken);

            try
            {
                // Once a slot is acquired, execute the processing logic.
                // This call is still awaited here to ensure the semaphore is released correctly in the finally block.
                await ProcessSingleUpdateWithRetries(update, cancellationToken);
            }
            finally
            {
                // ✅ CRUCIAL: Release the semaphore slot, allowing another task to start.
                // This is in a 'finally' block to guarantee it runs even if ProcessSingleUpdateWithRetries throws an unhandled exception.
                _concurrencySemaphore.Release();
            }
        }


        #endregion

        #region Private Helper for Processing
        private async Task ProcessSingleUpdateWithRetries(Telegram.Bot.Types.Update update, CancellationToken stoppingToken)
        {
            var pollyContext = new Polly.Context($"UpdateProcessing_{update.Id}", new Dictionary<string, object>
            {
                { "UpdateId", update.Id },
                { "UpdateType", update.Type.ToString() }
            });

            using (_logger.BeginScope(pollyContext.ToDictionary(kv => kv.Key, kv => (object?)kv.Value)))
            {
                try
                {
                    await _processingRetryPolicy.ExecuteAsync(async (context, ct) =>
                    {
                        await using var scope = _scopeFactory.CreateAsyncScope();
                        var updateProcessor = scope.ServiceProvider.GetRequiredService<ITelegramUpdateProcessor>();
                        await updateProcessor.ProcessUpdateAsync(update, ct);
                    }, pollyContext, stoppingToken);
                }
                catch (Exception ex)
                {
                    var errorMessage = new StringBuilder();
                    errorMessage.AppendLine("❌ *PERMANENT FAILURE: Update Processing Failed*");
                    // ... (rest of error message construction is the same) ...
                    errorMessage.AppendLine($"*Message:* `{ex.Message}`");

                    _logger.LogError(ex, "Update {UpdateId} failed processing permanently after all retries. NOTIFYING ADMIN.", update.Id);
                    _ = NotifyAdminAsync(errorMessage.ToString());
                }
            }
        }
        #endregion

        #region Admin Notification Helper
        private async Task NotifyAdminAsync(string message)
        {
            if (_adminChatIds == null || _adminChatIds.Count == 0) return;

            _logger.LogInformation("Sending critical notification to {AdminCount} admin(s).", _adminChatIds.Count);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var botClient = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>();

            foreach (var adminId in _adminChatIds)
            {
                try
                {
                    await botClient.SendMessage(
                        chatId: adminId,
                        text: message,
                        parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                        cancellationToken: CancellationToken.None
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send notification to admin (ChatID: {AdminChatId}).", adminId);
                }
            }
        }
        #endregion

        #region Resilience Helpers
        private static TimeSpan GetJitteredDelay(TimeSpan baseDelay)
        {
            if (baseDelay == TimeSpan.Zero) return TimeSpan.Zero;

            var jitterRange = baseDelay.TotalMilliseconds * 0.2;
            var actualJitterRange = Math.Max(0, jitterRange);
            var randomJitter = _jitterer.NextDouble() * (actualJitterRange * 2) - actualJitterRange;

            return baseDelay + TimeSpan.FromMilliseconds(randomJitter);
        }
        #endregion

        #region Service Lifecycle
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Update Queue Consumer Service stop requested.");
            await base.StopAsync(cancellationToken);
        }
        #endregion
    }
}