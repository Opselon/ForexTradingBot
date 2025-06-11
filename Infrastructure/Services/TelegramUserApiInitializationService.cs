using Application.Common.Interfaces; // For ITelegramUserApiClient
using Microsoft.Extensions.Hosting; // For BackgroundService
using Microsoft.Extensions.Logging; // For ILogger
using Polly; // For Polly resilience policies
using System.Net.Sockets; // For SocketException (common network error)

namespace Infrastructure.Services
{
    /// <summary>
    /// A background service responsible for initializing and maintaining the connection
    /// to the Telegram User API client. It employs robust retry mechanisms using Polly
    /// to handle transient connection failures and ensures the client is connected
    /// and logged in throughout the application's lifecycle.
    /// </summary>
    /// <remarks>
    /// This service extends <see cref="BackgroundService"/> which means it runs
    /// in the background, starting with the host and stopping when the host shuts down.
    /// </remarks>
    public class TelegramUserApiInitializationService : BackgroundService
    {
        private readonly ILogger<TelegramUserApiInitializationService> _logger;
        private readonly ITelegramUserApiClient _userApiClient;

        // Configuration constants for connection retry policy.
        // These values should ideally be configurable via IConfiguration.
        private const int MaxConnectionRetries = 5; // Total attempts, including the first one
        private const int InitialRetryDelayMilliseconds = 500; // Starting delay (e.g., 0.5 seconds)
        private const double RetryBackoffFactor = 2.0; // Factor for exponential backoff (e.g., 0.5s, 1s, 2s, 4s, 8s...)
        private const int MaxRetryDelayMilliseconds = 60000; // Cap maximum delay at 60 seconds (1 minute)

        /// <summary>
        /// Initializes a new instance of the <see cref="TelegramUserApiInitializationService"/> class.
        /// </summary>
        /// <param name="logger">The logger for recording service events and errors.</param>
        /// <param name="userApiClient">The Telegram User API client responsible for actual connection and login.</param>
        public TelegramUserApiInitializationService(
           ILogger<TelegramUserApiInitializationService> logger,
           ITelegramUserApiClient userApiClient)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userApiClient = userApiClient ?? throw new ArgumentNullException(nameof(userApiClient));
        }

        /// <summary>
        /// This method is called when the <see cref="IHostedService"/> starts.
        /// It implements the main logic for connecting and logging into the Telegram User API,
        /// including robust retry mechanisms using Polly.
        /// </summary>
        /// <param name="stoppingToken">A <see cref="CancellationToken"/> that signals when the host is shutting down.</param>
        /// <returns>A <see cref="Task"/> that represents the asynchronous operation.</returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Telegram User API Initialization Service is starting.");

            // Define the Polly retry policy for the connection and login attempt.
            var retryPolicy = Policy
                // Handle common transient network/connection exceptions.
                .Handle<SocketException>() // Connection refused, host unreachable, etc.
                .Or<IOException>() // Network stream issues, disconnected
                .Or<HttpRequestException>() // If the Telegram client uses HTTP for its API (e.g., for file downloads, some internal ops)
                .Or<TimeoutException>() // Operation timed out (e.g., client internal timeout)
                                        // Also handle a generic Exception to catch any unexpected errors from the API client,
                                        // but exclude OperationCanceledException if it's due to our stopping token.
                .Or<Exception>(ex => !(ex is OperationCanceledException && stoppingToken.IsCancellationRequested))
                .WaitAndRetryAsync(
                    MaxConnectionRetries, // Max retry attempts
                    retryAttempt =>
                    {
                        // Implement full jitter for exponential backoff.
                        // This prevents multiple clients from retrying at the same time ("thundering herd" problem).
                        var delay = TimeSpan.FromMilliseconds(
                            InitialRetryDelayMilliseconds * Math.Pow(RetryBackoffFactor, retryAttempt - 1)
                        );
                        var random = new Random();
                        var finalDelay = TimeSpan.FromMilliseconds(
                            Math.Min(
                                delay.TotalMilliseconds + (random.NextDouble() * delay.TotalMilliseconds), // Full jitter
                                MaxRetryDelayMilliseconds // Cap the maximum delay
                            )
                        );

                        _logger.LogDebug("Attempt {AttemptNumber}: Calculated retry delay of {RetryDelay}ms.", retryAttempt, (int)finalDelay.TotalMilliseconds);
                        return finalDelay;
                    },
                    onRetryAsync: (exception, timespan, retryAttempt, context) =>
                    {
                        // Log the specific exception during each retry attempt, after the delay has passed.
                        _logger.LogError(exception, "Telegram User API client initialization failed (Attempt {AttemptNumber}/{MaxRetries}). Retrying in {Timespan:F1} seconds.",
                            retryAttempt, MaxConnectionRetries, timespan.TotalSeconds);
                        return Task.CompletedTask; // Required by onRetryAsync delegate
                    }
                );

            // Execute the connection and login logic with the defined retry policy.
            var policyResult = await retryPolicy.ExecuteAndCaptureAsync(async (ct) =>
            {
                // Ensure the stoppingToken is passed through to the underlying connection logic
                // to allow graceful shutdown during a connection attempt.
                _logger.LogInformation("Attempting to connect and login to Telegram User API...");
                await _userApiClient.ConnectAndLoginAsync(ct); // Pass the cancellation token to the actual API call
                _logger.LogInformation("Successfully connected and logged in to Telegram User API.");

            }, stoppingToken); // Pass the main stopping token to the policy's execution.

            // Handle the final outcome of the policy execution.
            if (policyResult.Outcome == OutcomeType.Successful)
            {
                _logger.LogInformation("Telegram User API client initialization completed successfully.");
            }
            else
            {
                // The policy failed after exhausting all retries or was cancelled.
                if (policyResult.FinalException is OperationCanceledException && stoppingToken.IsCancellationRequested)
                {
                    // This is a graceful shutdown scenario.
                    _logger.LogWarning("Telegram User API client initialization was canceled by the application stopping token after failed attempts.");
                }
                else
                {
                    // This is a critical failure after all retries.
                    _logger.LogCritical(policyResult.FinalException,
                       "Exhausted all {MaxAttempts} connection attempts. Telegram User API client could not be initialized. Final Exception: {ExceptionType}.",
                       MaxConnectionRetries + 1, policyResult.FinalException?.GetType()?.Name ?? "Unknown");
                    // Depending on your application's requirements, you might want to:
                    // 1. Re-throw the exception to crash the host if the API client is a critical dependency.
                    //    throw policyResult.FinalException;
                    // 2. Simply log the critical error and allow the host to continue running in a degraded state.
                    //    (Current implementation follows this path)
                }
            }
        }
    }
}