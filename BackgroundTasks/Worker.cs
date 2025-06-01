using Microsoft.Extensions.Options; // Essential for configurable options

// Define configuration options for the worker service.
// This allows external configuration (e.g., appsettings.json) to control behavior.
public class WorkerOptions
{
    // Constants for sensible default values, avoiding direct "0" or arbitrary numbers
    // These defaults are meaningful and descriptive.
    public const int DefaultProcessingIntervalMilliseconds = 5_000; // Process every 5 seconds
    public const int DefaultMaxRetries = 3;                       // Allow up to 3 retries
    public const int DefaultRetryDelayMilliseconds = 1_000;       // Initial retry delay of 1 second
    public const int DefaultConsecutiveFailureThreshold = 5;      // Open circuit after 5 consecutive failures
    public const int DefaultCircuitBreakerResetMilliseconds = 60_000; // Circuit stays open for 1 minute

    // Properties with default values, which can be overridden by configuration.
    public int ProcessingIntervalMilliseconds { get; set; } = DefaultProcessingIntervalMilliseconds;
    public int MaxRetries { get; set; } = DefaultMaxRetries;
    public int RetryDelayMilliseconds { get; set; } = DefaultRetryDelayMilliseconds;
    public int ConsecutiveFailureThreshold { get; set; } = DefaultConsecutiveFailureThreshold;
    public int CircuitBreakerResetMilliseconds { get; set; } = DefaultCircuitBreakerResetMilliseconds;

    // A flag to enable or disable the worker entirely through configuration.
    public bool IsEnabled { get; set; } = true;
}

// Defines the contract for the actual work the background service performs.
// This separation of concerns makes the processing logic testable and reusable.
public interface ITaskProcessor
{
    // The core method that executes the specific business logic.
    // It takes a CancellationToken to respect service shutdown requests.
    Task ProcessAsync(CancellationToken cancellationToken);
}

// A concrete implementation of the ITaskProcessor for demonstration.
// In a real application, this would contain your domain-specific tasks.
public class ExampleTaskProcessor : ITaskProcessor
{
    private readonly ILogger<ExampleTaskProcessor> _logger;
    private int _invocationCounter = 0; // Tracks invocations for simulated behavior

    public ExampleTaskProcessor(ILogger<ExampleTaskProcessor> logger)
    {
        _logger = logger;
    }

    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        _invocationCounter++;
        // Immediately check for cancellation to ensure responsiveness.
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation("Processing task initiated. Invocation number: {InvocationCounter}", _invocationCounter);

        // Simulate some asynchronous work with a delay.
        // Using TimeSpan for clarity, avoiding raw numbers.
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);

        // Simulate transient failures for retry mechanism demonstration.
        // For example, fail on every 7th invocation.
        if (_invocationCounter % 7 == 0)
        {
            _logger.LogWarning("Simulating a transient failure for invocation number: {InvocationCounter}", _invocationCounter);
            throw new InvalidOperationException("Simulated transient processing error.");
        }
        // Simulate more persistent failures for circuit breaker demonstration.
        // For example, fail on every 13th invocation.
        else if (_invocationCounter % 13 == 0)
        {
            _logger.LogError("Simulating a persistent failure for invocation number: {InvocationCounter}. This might open the circuit.", _invocationCounter);
            throw new ApplicationException("Simulated persistent processing error.");
        }

        _logger.LogInformation("Task processing completed successfully. Invocation number: {InvocationCounter}", _invocationCounter);
    }
}

namespace BackgroundTasks
{
    // The main BackgroundService class, now significantly upgraded for resilience and configurability.
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly WorkerOptions _options;
        private readonly ITaskProcessor _taskProcessor;

        // Circuit Breaker state variables.
        private int _consecutiveFailures;
        private DateTimeOffset _circuitOpenedTime;
        private bool _isCircuitOpen;

        // Constructor for dependency injection. All required services are injected.
        public Worker(ILogger<Worker> logger, IOptions<WorkerOptions> options, ITaskProcessor taskProcessor)
        {
            // Robust null checks for injected dependencies.
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _taskProcessor = taskProcessor ?? throw new ArgumentNullException(nameof(taskProcessor));

            // Initialize circuit breaker state.
            _consecutiveFailures = 0;
            _circuitOpenedTime = DateTimeOffset.MinValue; // Use MinValue for initial state
            _isCircuitOpen = false;

            // Log the worker's configuration for transparency and debugging.
            _logger.LogInformation("Worker initialized with options: Interval={Interval}ms, MaxRetries={MaxRetries}, RetryDelay={RetryDelay}ms, ConsecutiveFailureThreshold={FailureThreshold}, CircuitReset={ResetTime}ms",
                                   _options.ProcessingIntervalMilliseconds, _options.MaxRetries, _options.RetryDelayMilliseconds, _options.ConsecutiveFailureThreshold, _options.CircuitBreakerResetMilliseconds);
        }

        // The core execution method for the background service.
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Check if the worker is disabled via configuration.
            if (!_options.IsEnabled)
            {
                _logger.LogWarning("Worker is disabled via configuration. Skipping execution.");
                return;
            }

            _logger.LogInformation("Worker service starting execution.");

            // Main loop that continuously runs until cancellation is requested.
            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogDebug("Worker loop iteration initiated.");

                // --- Circuit Breaker Logic ---
                if (_isCircuitOpen)
                {
                    _logger.LogWarning("Circuit is open. Skipping processing for cool-down period.");
                    // Check if enough time has passed to try closing the circuit (half-open state).
                    if (DateTimeOffset.UtcNow >= _circuitOpenedTime.Add(TimeSpan.FromMilliseconds(_options.CircuitBreakerResetMilliseconds)))
                    {
                        _logger.LogInformation("Circuit breaker cool-down period elapsed. Attempting to transition to half-open state.");
                        _isCircuitOpen = false; // Move to half-open: next attempt will be made.
                    }
                    else
                    {
                        // Wait for the next interval, respecting cancellation, if circuit is still open.
                        await Task.Delay(TimeSpan.FromMilliseconds(_options.ProcessingIntervalMilliseconds), stoppingToken).SuppressCancellation();
                        continue; // Skip the processing logic for this iteration.
                    }
                }

                try
                {
                    // Execute the core processing logic, including retries.
                    await ExecuteProcessingWithRetries(stoppingToken);

                    // If processing succeeds, reset circuit breaker and consecutive failure count.
                    _consecutiveFailures = 0;
                    _isCircuitOpen = false;
                    _logger.LogDebug("Processing cycle completed successfully. Consecutive failures reset.");
                }
                catch (OperationCanceledException)
                {
                    // This exception is expected during graceful shutdown, so log as information.
                    _logger.LogInformation("Worker operation cancelled. Initiating graceful shutdown.");
                }
                catch (Exception ex)
                {
                    // Catch any unhandled exceptions during the main processing cycle.
                    _logger.LogError(ex, "An unhandled error occurred during processing cycle. This might be a critical issue.");

                    // Apply circuit breaker logic on failure.
                    _consecutiveFailures++;
                    _logger.LogWarning("Processing failed. Current consecutive failures: {ConsecutiveFailures}", _consecutiveFailures);

                    if (_consecutiveFailures >= _options.ConsecutiveFailureThreshold)
                    {
                        _isCircuitOpen = true;
                        _circuitOpenedTime = DateTimeOffset.UtcNow;
                        _logger.LogError("Circuit breaker opened due to {ConsecutiveFailures} consecutive failures. Will re-attempt after {ResetTime}ms.",
                                         _consecutiveFailures, _options.CircuitBreakerResetMilliseconds);
                    }
                }

                // Wait for the next processing interval.
                // Using Task.Delay for efficient, asynchronous waiting, respecting cancellation.
                try
                {
                    _logger.LogDebug("Worker waiting for next interval of {Interval}ms.", _options.ProcessingIntervalMilliseconds);
                    await Task.Delay(TimeSpan.FromMilliseconds(_options.ProcessingIntervalMilliseconds), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Worker delay cancelled. Exiting loop for graceful shutdown.");
                    // This exception is expected when cancellation is requested while in Task.Delay.
                    // Break the loop to exit ExecuteAsync cleanly.
                    break;
                }
            }

            _logger.LogInformation("Worker service stopping execution.");
        }

        // Private method encapsulating the retry logic for the task processor.
        private async Task ExecuteProcessingWithRetries(CancellationToken stoppingToken)
        {
            int currentRetryAttempt = 0;
            // Loop through retry attempts.
            while (currentRetryAttempt <= _options.MaxRetries)
            {
                // Check for cancellation before each attempt.
                stoppingToken.ThrowIfCancellationRequested();

                try
                {
                    _logger.LogInformation("Attempting to process task (Attempt {AttemptNumber} of {TotalAttempts}).", currentRetryAttempt + 1, _options.MaxRetries + 1);
                    await _taskProcessor.ProcessAsync(stoppingToken); // Execute the actual work.
                    _logger.LogInformation("Task processing attempt successful.");
                    return; // Task succeeded, exit the retry loop.
                }
                catch (OperationCanceledException)
                {
                    // Propagate cancellation immediately if it occurs during an attempt.
                    _logger.LogWarning("Task processing cancelled during retry attempt {AttemptNumber}.", currentRetryAttempt + 1);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Task processing failed on attempt {AttemptNumber}. Error: {ErrorMessage}", currentRetryAttempt + 1, ex.Message);

                    if (currentRetryAttempt < _options.MaxRetries)
                    {
                        currentRetryAttempt++;
                        // Implement a simple exponential backoff for retry delay.
                        // This uses currentRetryAttempt to increase delay for subsequent retries.
                        int currentRetryDelay = _options.RetryDelayMilliseconds * currentRetryAttempt;
                        _logger.LogWarning("Retrying in {RetryDelay}ms...", currentRetryDelay);
                        await Task.Delay(TimeSpan.FromMilliseconds(currentRetryDelay), stoppingToken);
                    }
                    else
                    {
                        // All retry attempts exhausted. Re-throw the exception.
                        _logger.LogError(ex, "All {MaxRetries} retry attempts failed for task processing.", _options.MaxRetries);
                        throw;
                    }
                }
            }
        }
    }
}

// Extension methods for IServiceCollection to simplify worker registration in Program.cs.
public static class WorkerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the robust background worker service and its dependencies.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add the services to.</param>
    /// <param name="configureOptions">An action to configure the <see cref="WorkerOptions"/>.</param>
    /// <returns>The <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddStrongWorker(this IServiceCollection services, Action<WorkerOptions> configureOptions)
    {
        // Null checks for method arguments.
        if (services == null) throw new ArgumentNullException(nameof(services));
        if (configureOptions == null) throw new ArgumentNullException(nameof(configureOptions));

        // Configure the WorkerOptions using the provided action.
        services.Configure(configureOptions);
        // Register the task processor as a transient service.
        services.AddTransient<ITaskProcessor, ExampleTaskProcessor>();
        // Register the Worker as a hosted service.
        services.AddHostedService<BackgroundTasks.Worker>();

        return services;
    }
}

// Helper static class for Task extensions.
internal static class TaskExtensions
{
    /// <summary>
    /// Awaits a task and suppresses <see cref="OperationCanceledException"/> if it occurs.
    /// Useful for delays or operations that are expected to be cancelled during shutdown.
    /// </summary>
    /// <param name="task">The task to await.</param>
    public static async Task SuppressCancellation(this Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Suppress the exception, as it's an expected and handled behavior during shutdown.
        }
    }
}