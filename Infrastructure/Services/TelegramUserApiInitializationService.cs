using Application.Common.Interfaces; // فرض بر این است که ITelegramUserApiClient اینجا تعریف شده
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly; // افزودن using برای استفاده از Polly
using System;

namespace Infrastructure.Services
{
    public class TelegramUserApiInitializationService : BackgroundService
    {
        private readonly ILogger<TelegramUserApiInitializationService> _logger;
        private readonly ITelegramUserApiClient _userApiClient;

        // Configuration constants (can be moved to IConfiguration if needed)
        private const int MaxConnectionRetries = 5; // Keep retry attempts as is
        private const int InitialRetryDelayMilliseconds = 500; // Reduced initial delay for potential speed increase
        private const double RetryBackoffFactor = 2.0; // Standard exponential factor
        private const int MaxRetryDelayMilliseconds = 60000; // Max delay of 60 seconds (1 minute)

        public TelegramUserApiInitializationService(
           ILogger<TelegramUserApiInitializationService> logger,
           ITelegramUserApiClient userApiClient)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userApiClient = userApiClient ?? throw new ArgumentNullException(nameof(userApiClient));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {

            _logger.LogInformation("Telegram User API Initialization Service is starting.");

            var retryPolicy = Policy
                .Handle<Exception>(ex => !(ex is OperationCanceledException && stoppingToken.IsCancellationRequested)) // Retry on any exception EXCEPT cancellation due to stopping token
                .WaitAndRetryAsync(
                    MaxConnectionRetries,
                    retryAttempt =>
                    {
                        // Exponential backoff with jitter
                        var delay = TimeSpan.FromMilliseconds(
                            Math.Min(
                                InitialRetryDelayMilliseconds * Math.Pow(RetryBackoffFactor, retryAttempt - 1), // Exponential increase
                                MaxRetryDelayMilliseconds // Cap the maximum delay
                            )
                        );
                        var jitter = TimeSpan.FromMilliseconds(new Random().Next(0, (int)(delay.TotalMilliseconds * 0.1))); // Reduced random jitter up to 10% of the delay
                        var finalDelay = delay + jitter;

                        // Log the calculated delay before waiting
                        _logger.LogDebug("Attempt {AttemptNumber}: Calculated retry delay of {RetryDelay}ms.", retryAttempt, (int)finalDelay.TotalMilliseconds);
                        return finalDelay;
                    },
                    onRetryAsync: async (exception, timespan, retryAttempt, context) =>
                    {
                        // Log the specific exception during each retry attempt, after the delay
                        _logger.LogError(exception, "Retry attempt {AttemptNumber} failed for Telegram User API client initialization. Retrying in {Timespan}.", retryAttempt, timespan);
                        // No additional logic needed here for this specific case, but available for more complex scenarios.
                        // Consider if any state needs to be reset or logged after a failed attempt but before the retry
                    }
                );

            // Execute the initialization with the retry policy
            var policyResult = await retryPolicy.ExecuteAndCaptureAsync(async (ct) =>
            {
                _logger.LogInformation("Attempting to connect and login to Telegram User API...");
                await _userApiClient.ConnectAndLoginAsync(ct); // Pass the cancellation token

            }, stoppingToken); // Pass the main stopping token to the policy

            if (policyResult.Outcome == OutcomeType.Successful)
            {
                _logger.LogInformation("Telegram User API client initialized successfully.");
            }
            else
            {
                // The policy failed after all retries
                if (policyResult.FinalException is OperationCanceledException && stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Telegram User API client initialization was canceled by the application stopping token after failed attempts.");
                }
                else
                {
                    _logger.LogCritical(policyResult.FinalException,
                       "Exhausted all {MaxAttempts} connection attempts. Telegram User API client could not be initialized. Final Exception: {ExceptionType}.",
                       MaxConnectionRetries + 1, policyResult.FinalException?.GetType()?.Name ?? "Unknown");
                    // Depending on the application's critical path, you might re-throw the exception here
                    // if initialization is absolutely required for the application to function.
                    // throw policyResult.FinalException;
                }
            }
        }
    }
}