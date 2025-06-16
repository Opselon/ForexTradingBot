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
                    // ...and EXCEPT for when the application is explicitly trying to shut down.
                    !(ex is OperationCanceledException && stoppingToken.IsCancellationRequested)
                )
                .WaitAndRetryAsync(
                    MaxConnectionRetries,
                    retryAttempt =>
                    {
                        var delay = TimeSpan.FromMilliseconds(InitialRetryDelayMilliseconds * Math.Pow(RetryBackoffFactor, retryAttempt - 1));
                        var jitter = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 0.25 * (new Random().NextDouble() - 0.5));
                        var finalDelay = delay + jitter;
                        return TimeSpan.FromMilliseconds(Math.Min(finalDelay.TotalMilliseconds, MaxRetryDelayMilliseconds));
                    },
                    onRetryAsync: (exception, timespan, retryAttempt, context) =>
                    {
                        _logger.LogWarning(exception, "Telegram User API client initialization failed (Attempt {AttemptNumber}/{MaxRetries}). Retrying in {Timespan:F1} seconds.",
                            retryAttempt, MaxConnectionRetries, timespan.TotalSeconds);
                        return Task.CompletedTask;
                    }
                );

            var policyResult = await retryPolicy.ExecuteAndCaptureAsync(async (ct) =>
            {
                _logger.LogInformation("Attempting to connect and login to Telegram User API...");
                try
                {
                    await _userApiClient.ConnectAndLoginAsync(ct);
                }
                // THIS IS THE CRITICAL LOGIC
                catch (RpcException rpcEx)
                {
                    _logger.LogError(rpcEx, "An RPC exception occurred while attempting to connect and login to Telegram User API. Code: {Code}, Message: {Message}", rpcEx.Code, rpcEx.Message);
                    throw; // Re-throw the exception to allow Polly to handle it.  
                }
                // Any other exception (SocketException, another RpcException, etc.) will NOT be caught here.
                // It will be caught by Polly's .Handle<Exception> clause, which will trigger a retry.

            }, stoppingToken);

            // Handle the final outcome with specific and actionable logging.
            if (policyResult.Outcome == OutcomeType.Successful)
            {
                _logger.LogInformation("✅ Telegram User API client initialization completed successfully.");
            }
            else
            {
                if (policyResult.FinalException is PermanentApiCredentialException)
                {
                    _logger.LogCritical(policyResult.FinalException,
                        "CRITICAL: Telegram User API client failed to initialize due to INVALID CREDENTIALS. The application will run in a degraded state. **MANUAL INTERVENTION REQUIRED TO FIX API_ID/API_HASH in your configuration.**");
                }
                else if (policyResult.FinalException is OperationCanceledException && stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Telegram User API client initialization was canceled by the application stopping token.");
                }
                else
                {
                    _logger.LogCritical(policyResult.FinalException,
                       "CRITICAL: Exhausted all {MaxAttempts} connection attempts. Telegram User API client could not be initialized. The application will run in a degraded state. Check network connectivity and Telegram status.",
                       MaxConnectionRetries);
                }
            }
        }
        }
}