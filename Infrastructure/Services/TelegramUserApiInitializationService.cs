using Application.Common.Interfaces; // For ITelegramUserApiClient
using Microsoft.Extensions.Hosting; // For BackgroundService
using Microsoft.Extensions.Logging; // For ILogger
using Polly; // For Polly resilience policies
using System.Net.Sockets;
using TL; // For SocketException (common network error)

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


        public class PermanentApiCredentialException : Exception
        {
            public PermanentApiCredentialException(string message, Exception innerException) : base(message, innerException) { }
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

            var retryPolicy = Policy
                // We handle any exception...
                .Handle<Exception>(ex =>
                    // ...EXCEPT for our custom "permanent failure" exception...
                    ex is not PermanentApiCredentialException &&
                    // ...and EXCEPT for when the application is explicitly trying to shut down (OperationCanceledException triggered by stoppingToken).
                    !(ex is OperationCanceledException && stoppingToken.IsCancellationRequested)
                )
                .WaitAndRetryAsync(
                    MaxConnectionRetries, // Maximum number of retry attempts.
                    retryAttempt =>
                    {
                        // Calculate exponential backoff delay with added jitter to prevent "thundering herd" effect.
                        var delay = TimeSpan.FromMilliseconds(InitialRetryDelayMilliseconds * Math.Pow(RetryBackoffFactor, retryAttempt - 1));
                        var jitter = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 0.25 * (new Random().NextDouble() - 0.5));
                        var finalDelay = delay + jitter;
                        return TimeSpan.FromMilliseconds(Math.Min(finalDelay.TotalMilliseconds, MaxRetryDelayMilliseconds));
                    },
                    onRetryAsync: (exception, timespan, retryAttempt, context) =>
                    {
                        // Log a WARNING message for each retry attempt, including the exception details.
                        _logger.LogWarning(exception, "Telegram User API client initialization failed (Attempt {AttemptNumber}/{MaxRetries}). Retrying in {Timespan:F1} seconds.",
                            retryAttempt, MaxConnectionRetries, timespan.TotalSeconds);
                        return Task.CompletedTask;
                    }
                );

            // Execute the connection and login logic within the defined retry policy.
            // ExecuteAndCaptureAsync allows inspecting the final outcome (success or final exception).
            var policyResult = await retryPolicy.ExecuteAndCaptureAsync(async (ct) =>
            {
                _logger.LogInformation("Attempting to connect and login to Telegram User API...");
                try
                {
                    // The core logic to connect and login to the Telegram User API.
                    await _userApiClient.ConnectAndLoginAsync(ct);
                }
                // This specific catch block handles RpcExceptions that are not retried by Polly's main .Handle<Exception>
                // because they might require specific handling or indicate a known Telegram API error.
                catch (RpcException rpcEx)
                {
                    _logger.LogError(rpcEx, "An RPC exception occurred while attempting to connect and login to Telegram User API. Code: {Code}, Message: {Message}", rpcEx.Code, rpcEx.Message);
                    throw; // Re-throw the exception for Polly to capture and handle (either retry or mark as final failure).
                }
                // Any other exception type (including System.NullReferenceException from WTelegram.Client as seen in logs,
                // System.Net.Sockets.SocketException, etc.) will be handled by Polly's initial .Handle<Exception> clause,
                // triggering retries as configured.

            }, stoppingToken); // The stoppingToken allows external cancellation of the entire retry process.

            // Handle the final outcome of the Polly policy execution.
            if (policyResult.Outcome == OutcomeType.Successful)
            {
                // If the connection and login succeeded after all attempts/retries.
                _logger.LogInformation("✅ Telegram User API client initialization completed successfully.");
            }
            else
            {
                // If the operation failed after all retries.
                if (policyResult.FinalException is PermanentApiCredentialException)
                {
                    // This is a CRITICAL failure: The credentials are fundamentally wrong.
                    // This requires manual intervention and prevents the application from properly functioning.
                    _logger.LogCritical(policyResult.FinalException,
                        "CRITICAL: Telegram User API client failed to initialize due to INVALID CREDENTIALS. The application will run in a degraded state. MANUAL INTERVENTION REQUIRED TO FIX API_ID/API_HASH in your configuration.");
                }
                else if (policyResult.FinalException is OperationCanceledException && stoppingToken.IsCancellationRequested)
                {
                    // This is an expected scenario: The application is shutting down, so the service was cancelled.
                    _logger.LogWarning("Telegram User API client initialization was canceled by the application stopping token.");
                }
                else
                {
                    // For any other type of failure after exhausting all retries (e.g., persistent network issues,
                    // unexpected client library errors like NullReferenceException, or general transient RPC errors
                    // that did not resolve), log a WARNING.
                    // This indicates that the Telegram User API features will not be available,
                    // but the application itself is not deemed in a catastrophic, unrecoverable state,
                    // allowing other parts of the system to potentially continue operating.
                    _logger.LogWarning(policyResult.FinalException,
                       "WARNING: Telegram User API client could not be initialized after {MaxAttempts} attempts. The application will run in a degraded state without user API features. Check network connectivity, Telegram status, or client library issues.",
                       MaxConnectionRetries);
                }
            }
        }
    }
}