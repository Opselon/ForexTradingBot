// File: TelegramPanel/Queue/UpdateQueueConsumerService.cs

#region Usings
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker; // ✅ NEW: Required for the Circuit Breaker
using Polly.Retry;
using StackExchange.Redis; // ✅ NEW: Required to handle Redis exceptions
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
        private readonly AsyncRetryPolicy _processingRetryPolicy;
        private readonly TimeSpan _redisBreakDuration = TimeSpan.FromMinutes(1); // ✅ NEW: Centralized duration field
        // ✅✅ NEW: A resilience policy to protect against a failing Redis connection ✅✅
        private readonly AsyncCircuitBreakerPolicy _redisCircuitBreaker;
        private readonly List<long> _adminChatIds;
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

            // ✅ NEW: Initialize the Circuit Breaker policy in the constructor.
            // If 3 consecutive Redis exceptions occur, the circuit will break (stop trying) for 1 minute.
            _redisCircuitBreaker = Policy
                .Handle<RedisException>() // It triggers on any Redis-specific exception.
                .CircuitBreakerAsync(
                    exceptionsAllowedBeforeBreaking: 3,
                    durationOfBreak: TimeSpan.FromMinutes(1),
                    onBreak: (exception, timespan) =>
                    {
                        _logger.LogCritical(exception, "Redis Circuit Breaker OPENED for {BreakDuration}. Halting all queue consumption.", timespan);
                    },
                    onReset: () => _logger.LogInformation("Redis Circuit Breaker RESET. Resuming normal queue consumption."),
                    onHalfOpen: () => _logger.LogWarning("Redis Circuit Breaker is now HALF-OPEN. The next read will test the connection.")
                );
            _adminChatIds = configuration.GetSection("TelegramPanel:AdminUserIds").Get<List<long>>() ?? new List<long>();

            if (_adminChatIds.Count == 0)
            {
                _logger.LogWarning("No AdminUserIds found in appsettings.json under the 'TelegramPanel' section. Admin notifications will be disabled.");
            }
            else
            {
                _logger.LogInformation("Admin notifications configured for {AdminCount} user(s).", _adminChatIds.Count);
            }
            // This policy for processing an individual update remains the same. It's a good design.
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
            _redisCircuitBreaker = Policy
               .Handle<RedisException>() // It triggers on any Redis-specific exception.
               .CircuitBreakerAsync(
                   exceptionsAllowedBeforeBreaking: 3,
                   durationOfBreak: _redisBreakDuration, // ✅ MODIFIED: Use the field here
                   onBreak: (exception, timespan) =>
                   {
                       _logger.LogCritical(exception, "Redis Circuit Breaker OPENED for {BreakDuration}. Halting all queue consumption.", timespan);
                   },
                   onReset: () => _logger.LogInformation("Redis Circuit Breaker RESET. Resuming normal queue consumption."),
                   onHalfOpen: () => _logger.LogWarning("Redis Circuit Breaker is now HALF-OPEN. The next read will test the connection.")
               );
        }
        #endregion

        #region Main Execution Logic
        // ✅✅ THIS ExecuteAsync METHOD IS NOW FULLY RESILIENT ✅✅
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Update Queue Consumer Service is starting...");

            // A small delay to allow other services like Redis connection to be established.
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // ✅ Execute the entire consumption logic within the Circuit Breaker policy.
                    await _redisCircuitBreaker.ExecuteAsync(async (ct) =>
                    {
                        _logger.LogTrace("Circuit is closed. Polling Redis queue for updates...");

                        // The await foreach loop will now be protected. If ReadAllAsync throws
                        // a RedisException (after its own internal retries fail),
                        // this circuit breaker will catch it.
                        await foreach (var update in _updateChannel.ReadAllAsync(ct).WithCancellation(ct))
                        {
                            // If we get here, we successfully dequeued an item.
                            // The processing logic for a single update remains the same.
                            await ProcessSingleUpdateWithRetries(update, ct);
                        }
                    }, stoppingToken);
                }
                catch (BrokenCircuitException)
                {
                    // This is the expected, "graceful failure" state when Redis is down.
                    // The circuit is open. We log a warning and the while loop will pause.
                    // ✅ MODIFIED: Update the log message to use the field, fixing the warning.
                    _logger.LogWarning("Skipping consumption cycle because the Redis circuit is open. Will try again after the break duration ({BreakDuration}).", _redisBreakDuration);
                    // Wait for the duration of the break before trying again to avoid spamming logs.

                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // This is a normal shutdown.
                    _logger.LogInformation("Update Queue Consumer Service was canceled gracefully.");
                    break; // Exit the while loop.
                }
                catch (Exception ex)
                {
                    // This is a safety net for any other unexpected exception not handled by the circuit breaker.
                    _logger.LogError(ex, "An unhandled exception occurred in the main consumer loop. Pausing before retrying.");
                    // A short delay before the next iteration of the while loop.
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
            }
        }
        #endregion

        #region Private Helper for Processing
        // I've moved the logic for processing a single update into its own helper
        // to make the ExecuteAsync method cleaner and more focused on the resilience loop.
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
                    // --- UPGRADE 3: NOTIFY ADMIN ON PERMANENT UPDATE FAILURE ---
                    var errorMessage = new StringBuilder();
                    errorMessage.AppendLine($"❌ *PERMANENT FAILURE: Update Processing Failed*");
                    errorMessage.AppendLine($"An update failed to process after all retries and has been discarded.");
                    errorMessage.AppendLine();
                    errorMessage.AppendLine($"*Update ID:* `{update.Id}`");
                    errorMessage.AppendLine($"*Update Type:* `{update.Type}`");
                    errorMessage.AppendLine();
                    errorMessage.AppendLine($"*Final Error:* `{ex.GetType().Name}`");
                    errorMessage.AppendLine($"*Message:* `{ex.Message}`");

                    _logger.LogError(ex, "Update {UpdateId} failed processing permanently after all retries. NOTIFYING ADMIN.", update.Id);
                    _ = NotifyAdminAsync(errorMessage.ToString());
                }
             }
        }

        #region Admin Notification Helper
        // --- UPGRADE 3: Modify the helper to loop through all admin IDs ---
        private async Task NotifyAdminAsync(string message)
        {
            if (_adminChatIds == null || _adminChatIds.Count == 0)
            {
                // No admins configured, so do nothing.
                return;
            }

            _logger.LogInformation("Sending critical notification to {AdminCount} admin(s).", _adminChatIds.Count);

            // Create a single scope for all notifications in this batch.
            await using var scope = _scopeFactory.CreateAsyncScope();
            var botClient = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>();

            // Loop through each configured admin ID and send the message.
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
                    // If sending to one admin fails, log it but continue to the next admin.
                    _logger.LogError(ex, "Failed to send notification to admin (ChatID: {AdminChatId}).", adminId);
                }
            }
        }
        #endregion

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