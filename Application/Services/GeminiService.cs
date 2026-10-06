using Application.Common.Interfaces;
using Application.Common.Interfaces.Admin;
using Application.Common.Models;
using Domain.Entities;
using Hangfire;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Polly.Wrap;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Application.Services
{
    public class GeminiService : IGeminiService
    {
        // Dependencies
        private readonly ILogger<GeminiService> _logger;
        private readonly HttpClient _httpClient;
        private readonly IServiceProvider _serviceProvider;
        private readonly IMemoryCache _cache;

        #region State Management (In-Class Strategy)
        // Static state to be shared across all instances of this service.
        private static readonly ConcurrentDictionary<int, AiApiConfiguration> _configs = new();
        private static List<int> _orderedConfigIds = [];
        private static readonly SemaphoreSlim _configLock = new(1, 1);
        private const string CONFIG_REFRESH_LOCK_KEY = "GeminiService_ConfigRefreshLock";
        private static readonly TimeSpan CONFIG_CACHE_DURATION = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan CONFIG_FAILURE_COOLDOWN = TimeSpan.FromSeconds(60);
        #endregion

        #region Resilience Policies (In-Class Strategy)
        // A registry to hold a specific circuit breaker FOR EACH config ID.
        private static readonly ConcurrentDictionary<string, AsyncCircuitBreakerPolicy<HttpResponseMessage>> _circuitBreakers = new();
        private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;
        private readonly AsyncTimeoutPolicy<HttpResponseMessage> _timeoutPolicy; // Made generic
        #endregion

        // Idempotency lock per request content
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _requestLocks = new();

        // Quota Constants
        private const int FREE_RPM = 15;
        private const int FREE_TPM = 250_000;
        private const int FREE_RPD = 1_000;

        // JSON options
        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        // Default prompt template for message enhancement
        private const string DEFAULT_PROMPT_TEMPLATE = @"You are an expert financial content enhancer. Your task is to improve the given trading signal message to make it more professional, engaging, and informative while maintaining all the original trading information.

IMPORTANT RULES:
1. Keep ALL original trading data (prices, levels, symbols) exactly as provided
2. Add professional formatting and structure
3. Enhance the language to be more engaging and professional
4. Add relevant trading context or insights if appropriate
5. Use markdown formatting for better presentation
6. Keep the message concise but informative

Original message: {message}

Enhanced message:";

        public GeminiService(
            ILogger<GeminiService> logger,
            IHttpClientFactory httpClientFactory,
            IServiceProvider serviceProvider,
            IMemoryCache memoryCache)
        {
            _logger = logger;
            _httpClient = httpClientFactory.CreateClient("GeminiClient");
            _serviceProvider = serviceProvider;
            _cache = memoryCache;

            // General policies that apply to any call
            _timeoutPolicy = Policy.TimeoutAsync<HttpResponseMessage>(8, TimeoutStrategy.Pessimistic);

            _retryPolicy = Policy<HttpResponseMessage>
                .Handle<HttpRequestException>()
                .OrResult(r => (int)r.StatusCode >= 500 || r.StatusCode == HttpStatusCode.RequestTimeout)
                .WaitAndRetryAsync(2, attempt => TimeSpan.FromMilliseconds(200 * attempt),
                    onRetry: (resp, ts, attempt, ctx) =>
                    {
                        object configId = ctx.GetValueOrDefault("ConfigId", "N/A");
                        _logger.LogWarning(
                            "[Polly] Retrying call for ConfigId {ConfigId}. Attempt {Attempt}. Delay {Delay}ms. Reason: {Reason}",
                            configId, attempt, ts.TotalMilliseconds, resp.Exception?.Message ?? resp.Result.StatusCode.ToString());
                    });
        }

        public async Task<string?> EnhanceMessageAsync(string text, CancellationToken ct, string? apiKeyName = null)
        {
            // First, try to get an immediate result
            try
            {
                string? result = await AttemptImmediateEnhancementAsync(text, apiKeyName, ct);
                if (!string.IsNullOrWhiteSpace(result))
                {
                    _logger.LogInformation("Message enhanced immediately. Text length: {TextLength} -> {ResultLength}",
                        text?.Length ?? 0, result?.Length ?? 0);
                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Immediate enhancement failed, falling back to background job");
                // Background error log
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "GeminiService",
                        EventType = "ImmediateEnhancement",
                        Message = ex.Message,
                        Details = ex.StackTrace,
                        Exception = ex.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
            }

            // Fallback to background job if immediate enhancement fails
            string jobId = Guid.NewGuid().ToString("N");

            // Enqueue the job and return immediately
            string jobIdResult = BackgroundJob.Enqueue(() =>
                ProcessEnhanceMessageJobAsync(text!, jobId, apiKeyName!, ct));

            _logger.LogInformation("EnhanceMessage job enqueued. JobId: {JobId}, HangfireJobId: {HangfireJobId}",
                jobId, jobIdResult);

            // Return a placeholder response - in real implementation, you might want to return the job ID
            // and have the client poll for results or use SignalR for real-time updates
            return await Task.FromResult<string?>($"Job enqueued successfully. JobId: {jobId}");
        }

        public Task<string?> EnhanceMessageAsync(
            string? text,
            ICollection<byte[]>? images, // This parameter will now be effectively ignored by the core logic.
            CancellationToken ct,
            string? apiKeyName = null)
        {
            // For the overload with images, we'll also use Hangfire
            string jobId = Guid.NewGuid().ToString("N");

            string jobIdResult = BackgroundJob.Enqueue(() =>
                ProcessEnhanceMessageJobAsync(text!, jobId, apiKeyName!, ct));

            _logger.LogInformation("EnhanceMessage job enqueued (with images). JobId: {JobId}, HangfireJobId: {HangfireJobId}",
                jobId, jobIdResult);

            return Task.FromResult<string?>($"Job enqueued successfully. JobId: {jobId}");
        }

        /// <summary>
        /// Attempts immediate enhancement without using background jobs
        /// </summary>
        private async Task<string?> AttemptImmediateEnhancementAsync(string text, string? apiKeyName, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            AdminLogger adminLogger = new("ImmediateEnhancement", Guid.NewGuid().ToString("N"));

            try
            {
                adminLogger.Info($"🚀 Attempting immediate enhancement for text length: {text.Length}", null, "IMMEDIATE");

                string contentCacheKey = GenerateContentCacheKey(text, null);
                adminLogger.Info($"Request content hash: {contentCacheKey}", null, "CACHE");

                if (_cache.TryGetValue(contentCacheKey, out string? cachedResult))
                {
                    adminLogger.Success("Fulfilled from cache.", null, "CACHE");
                    return cachedResult;
                }

                int maxAttempts = _configs.Count > 0 ? _configs.Count : 1;
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    AiApiConfiguration? config = await GetNextActiveConfigAsync(apiKeyName, adminLogger, ct);
                    if (config == null)
                    {
                        adminLogger.Failure("No available API configurations found. Halting.", null, "CONFIG");
                        break;
                    }

                    adminLogger.Info($"Attempt {attempt}/{maxAttempts}: Using Config ID {config.Id} (Model: {config.ModelName})", config.Id, "API_CALL");

                    // CHANGE 1: Receive the full ResilientResponse object instead of deconstructing.
                    ResilientResponse<string> response = await AttemptApiCallAsync(config, text, null, adminLogger, ct);

                    // CHANGE 2: Check for success using the 'Success' property.
                    if (response.Success && !string.IsNullOrWhiteSpace(response.Data))
                    {
                        adminLogger.Success($"Successfully received response from Config ID {config.Id}.", config.Id, "API_CALL");
                        _ = _cache.Set(contentCacheKey, response.Data, TimeSpan.FromHours(1));
                        return response.Data; // Return the successful result.
                    }

                    // --- Failure Handling ---
                    // Log the detailed error from our structured response.
                    string reason = response.Error?.Reason ?? "An unknown error occurred";
                    adminLogger.Failure($"Config ID {config.Id} failed. Reason: {reason}", config.Id, "ROTATION");

                    // This key failed, so put it on cooldown and move it to the back of the queue.
                    await ReportFailureAndRotateAsync(config.Id);

                    // CHANGE 3: Be smart about failures. If the error is permanent for the *request*, stop trying.
                    if (response.Type == ResilientResponseType.NonRetryableError)
                    {
                        adminLogger.Failure($"A non-retryable error occurred: '{reason}'. Halting all further attempts.", null, "FINAL");
                        break; // Exit the loop. No other key will fix a bad request.
                    }

                    // If it was a retryable error (like a rate limit or server error), the loop will continue with the next key.
                    await Task.Delay(100, ct);
                }

                adminLogger.Failure("All available API configurations were tried and failed, or a permanent error was hit.", null, "FINAL");
                await NotifyAdminAsync(adminLogger.Render(), ct);
                return null; // Return null as the immediate attempt failed.
            }
            catch (Exception ex)
            {
                adminLogger.Failure($"An unexpected exception occurred during immediate enhancement: {ex.Message}", null, "EXCEPTION");
                _logger.LogError(ex, "Exception during immediate enhancement");
                // Background error log
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "GeminiService",
                        EventType = "AttemptImmediateEnhancement",
                        Message = ex.Message,
                        Details = ex.StackTrace,
                        Exception = ex.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                await NotifyAdminAsync(adminLogger.Render(), ct);
                return null;
            }
        }

        /// <summary>
        /// Hangfire background job method for processing message enhancement
        /// </summary>
        [AutomaticRetry(Attempts = 2, OnAttemptsExceeded = AttemptsExceededAction.Delete)] // Let Hangfire retry on exceptions
        public async Task ProcessEnhanceMessageJobAsync(string? text, string idempotencyKey, string? jobId, CancellationToken ct)
        {
            AdminLogger adminLogger = new("EnhanceMessageJob", jobId ?? string.Empty);
            adminLogger.Info($"🚀 Hangfire job started. IdempotencyKey: {idempotencyKey}", null, "HANGFIRE_JOB");

            // ✅ Behavior Rule 1: Lock with Timeout
            // The lock is acquired to prevent concurrent processing for the same idempotency key.
            SemaphoreSlim requestLock = _requestLocks.GetOrAdd(idempotencyKey, _ => new SemaphoreSlim(1, 1));

            // Use a timeout for acquiring the lock itself, to prevent jobs from getting stuck indefinitely.
            bool lockAcquired = await requestLock.WaitAsync(TimeSpan.FromSeconds(60), ct);

            if (!lockAcquired)
            {
                // Another job is processing this key, and our wait timed out.
                // This is a temporary failure; we should retry later.
                adminLogger.Failure("Failed to acquire idempotency lock within 60s. Job will be retried.", null, "LOCKING");
                await NotifyAdminAsync(adminLogger.Render(), ct);
                throw new RetryableJobException("Idempotency lock timed out. Another process may be running.");
            }

            try
            {
                // First, check if a result already exists from a previous successful run.
                if (_cache.TryGetValue(idempotencyKey, out ResilientResponse<string>? cachedResponse) && cachedResponse!.Success)
                {
                    adminLogger.Success("Fulfilled from cache (Idempotency).", null, "CACHE");
                    await NotifyAdminAsync(adminLogger.Render(), ct);
                    // The job is successful, no need to do anything else.
                    return;
                }

                // Execute the core logic for enhancement.
                ResilientResponse<string> response = await ExecuteResilientEnhancementAsync(text!, adminLogger, ct);

                // ✅ Behavior Rule 2: Telemetry + Metrics
                // The response object itself contains all necessary telemetry data.
                // Sanitize jobId to prevent log forging
                string sanitizedJobId = (jobId ?? string.Empty).Replace("\n", "").Replace("\r", "");
                _logger.LogInformation(
                    "Job {JobId} completed with Type: {ResponseType}. Success: {IsSuccess}. Reason: {Reason}",
                    sanitizedJobId, response.Type, response.Success, response.Error?.Reason ?? "N/A"
                );

                // Cache the final response, regardless of success or failure, to prevent re-computation for non-retryable errors.
                _ = _cache.Set(idempotencyKey, response, TimeSpan.FromHours(1));

                // Now, act based on the response type to guide the Hangfire system.
                switch (response.Type)
                {
                    case ResilientResponseType.Success:
                        adminLogger.Success("Job completed successfully.", null, "FINAL");
                        // Do nothing, let the job complete as successful.
                        break;

                    case ResilientResponseType.RetryableError:
                        adminLogger.Failure($"Job failed with a retryable error: {response.Error!.Reason}. Hangfire will retry.", null, "FINAL");
                        // Throw an exception to trigger Hangfire's automatic retry mechanism.
                        throw new RetryableJobException(response.Error!.Reason);

                    case ResilientResponseType.NonRetryableError:
                        adminLogger.Failure($"Job failed with a non-retryable error: {response.Error!.Reason}. Moved to Failed queue.", null, "FINAL");
                        // ✅ Behavior Rule 3: Fallback Action
                        // For permanent failures, throw a standard exception.
                        // After Hangfire exhausts its retries, it will move the job to the Failed queue (our DeadLetterQueue).
                        throw new InvalidOperationException(response.Error!.Reason);
                }
            }
            finally
            {
                _ = requestLock.Release();
                await NotifyAdminAsync(adminLogger.Render(), ct);
            }
        }

        /// <summary>
        /// The core logic that attempts to get a response by rotating through available API keys.
        /// This method is the heart of the resilient strategy.
        /// </summary>
        private async Task<ResilientResponse<string>> ExecuteResilientEnhancementAsync(string text, AdminLogger adminLogger, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return ResilientResponse<string>.CreateNonRetryableError(400, "Invalid Request: Input text cannot be empty.");
            }

            int maxAttempts = _configs.Count > 0 ? _configs.Count : 1;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                AiApiConfiguration? config = await GetNextActiveConfigAsync(null, adminLogger, ct);

                // ✅ Behavior Rule 3: Fallback Action (Part 1)
                // If we run out of configs to try.
                if (config == null)
                {
                    adminLogger.Failure("All API keys are exhausted or on cooldown.", null, "CONFIG");
                    return ResilientResponse<string>.CreateNonRetryableError(503, "Service Unavailable: All available API keys are exhausted. Job should be moved to a DeadLetterQueue.");
                }

                adminLogger.Info($"Attempt {attempt}/{maxAttempts}: Using Config ID {config.Id} (Model: {config.ModelName})", config.Id, "API_CALL");

                // Attempt the API call for the selected configuration.
                ResilientResponse<string> response = await AttemptApiCallAsync(config, text, null, adminLogger, ct);

                // If the call was successful, we're done.
                if (response.Success)
                {
                    return response;
                }

                // If the error is non-retryable for this KEY (e.g., Invalid Key), we mark it and try the next.
                // If the error is non-retryable for the REQUEST (e.g., Bad Request), we should stop immediately.
                if (response.Type == ResilientResponseType.NonRetryableError && response.Error!.Code == 400)
                {
                    adminLogger.Failure($"Non-retryable request error with Config ID {config.Id}: {response.Error.Reason}. Halting execution.", config.Id, "API_RESPONSE");
                    return response; // Propagate the non-retryable error up.
                }

                // For any other failure (retryable, or non-retryable key-specific), rotate the key and continue the loop.
                adminLogger.Failure($"Config ID {config.Id} failed. Reason: {response.Error!.Reason}. Moving to next.", config.Id, "ROTATION");
                await ReportFailureAndRotateAsync(config.Id);

                await Task.Delay(100, ct); // Small delay before trying the next key.
            }

            // ✅ Behavior Rule 3: Fallback Action (Part 2)
            // If the loop finishes, it means all keys were tried and all of them failed.
            adminLogger.Failure("All available API configurations failed after trying each one.", null, "FINAL");
            return ResilientResponse<string>.CreateNonRetryableError(503, "Service Unavailable: All API keys failed. Job should be moved to a DeadLetterQueue.");
        }

        /// <summary>
        /// Attempts a single API call and translates the HTTP outcome into a structured ResilientResponse.
        /// This method is now responsible for interpreting API results.
        /// </summary>
        /// <summary>
        /// Attempts a single API call and translates the HTTP outcome into a structured ResilientResponse.
        /// This method intelligently parses the Gemini error response body to provide precise failure reasons.
        /// </summary>
        private async Task<ResilientResponse<string>> AttemptApiCallAsync(
            AiApiConfiguration cfg, string text, ICollection<byte[]>? images, AdminLogger adminLogger, CancellationToken ct)
        {
            // Pre-flight checks (Quota, Circuit Breaker) remain the same.
            if (!CheckAndIncrementQuota(cfg.ModelName, text))
            {
                adminLogger.Info($"Quota exceeded for model {cfg.ModelName}; skipping.", cfg.Id, "QUOTA");
                // This is a retryable condition for the job, as another key might have quota.
                return GeminiErrors.ResourceExhausted<string>();
            }

            AsyncCircuitBreakerPolicy<HttpResponseMessage> circuitBreaker = GetOrCreateCircuitBreaker(cfg.Id, adminLogger);
            if (circuitBreaker.CircuitState == CircuitState.Open)
            {
                adminLogger.Info($"Circuit breaker is open for Config ID {cfg.Id}. Skipping.", cfg.Id, "CIRCUIT_BREAKER");
                return GeminiErrors.ServiceUnavailable<string>(); // Circuit breaker open means service is unavailable
            }

            // Policy setup remains the same
            AsyncPolicyWrap<HttpResponseMessage> policyWrap = Policy.WrapAsync(_timeoutPolicy, circuitBreaker, _retryPolicy);
            Context pollyCtx = new($"GeminiCall-{cfg.Id}", new Dictionary<string, object> { { "ConfigId", cfg.Id.ToString() } });

            try
            {
                GeminiRequest request = BuildRequest(cfg.PromptTemplate ?? DEFAULT_PROMPT_TEMPLATE, text);
                string uri = $"https://gemini-proxy.opcelon.workers.dev/v1beta/models/{cfg.ModelName}:generateContent?key={cfg.ApiKey}";


                Stopwatch sw = Stopwatch.StartNew();
                HttpResponseMessage response = await policyWrap.ExecuteAsync(ctx => _httpClient.PostAsJsonAsync(uri, request, _jsonOpts, ct), pollyCtx);
                sw.Stop();

                adminLogger.Info($"HTTP POST returned {(int)response.StatusCode} {response.ReasonPhrase} in {sw.ElapsedMilliseconds}ms.", cfg.Id, "API_CALL");

                // --- SUCCESS PATH ---
                if (response.IsSuccessStatusCode)
                {
                    GeminiResponse? geminiResponse = await response.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken: ct);

                    // Check for safety blocks or recitation issues which appear in a successful response body.
                    if (geminiResponse?.Candidates == null || !geminiResponse.Candidates.Any())
                    {
                        string finishReason = geminiResponse?.PromptFeedback?.BlockReason ?? "UNKNOWN";
                        adminLogger.Failure($"API returned success status but content was blocked. Reason: {finishReason}", cfg.Id, "SAFETY_BLOCK");
                        // Safety blocks are non-retryable for this specific prompt.
                        return ResilientResponse<string>.CreateNonRetryableError(400, $"Content blocked due to safety settings. Reason: {finishReason}", "SAFETY_SETTINGS");
                    }

                    string? responseText = geminiResponse.Candidates.FirstOrDefault()?.Content?.parts?.FirstOrDefault()?.Text?.Trim();
                    if (!string.IsNullOrWhiteSpace(responseText))
                    {
                        return ResilientResponse<string>.CreateSuccess(responseText);
                    }

                    // Success status but empty content is a temporary server-side issue.
                    adminLogger.Failure("API returned success status but content was empty.", cfg.Id, "API_RESPONSE");
                    return GeminiErrors.InternalError<string>(); // Treat as an internal error
                }

                // --- ERROR PATH ---
                string errorContent = await response.Content.ReadAsStringAsync(ct);
                GeminiErrorResponse? geminiError = null;
                try
                {
                    geminiError = JsonSerializer.Deserialize<GeminiErrorResponse>(errorContent, _jsonOpts);
                }
                catch (JsonException)
                {
                    adminLogger.Warning($"Failed to deserialize Gemini error response for status {(int)response.StatusCode}. Content: {errorContent.Truncate(100)}", cfg.Id, "DESERIALIZATION");
                }

                // ✅ Behavior Rule 2 & 4: Map HTTP errors and Google-specific statuses to our response types
                switch (response.StatusCode)
                {
                    case HttpStatusCode.BadRequest: // 400
                        if (geminiError?.Error?.Status == "FAILED_PRECONDITION")
                        {
                            return GeminiErrors.FailedPrecondition<string>();
                        }

                        return GeminiErrors.InvalidArgument<string>(geminiError?.Error?.Message ?? errorContent);

                    case HttpStatusCode.Forbidden: // 403
                        return GeminiErrors.PermissionDenied<string>();

                    case HttpStatusCode.NotFound: // 404
                        return GeminiErrors.NotFound<string>(geminiError?.Error?.Message ?? "The requested model or resource was not found.");

                    case HttpStatusCode.TooManyRequests: // 429
                        return GeminiErrors.ResourceExhausted<string>();

                    case HttpStatusCode.InternalServerError: // 500
                        return GeminiErrors.InternalError<string>();

                    case HttpStatusCode.ServiceUnavailable: // 503
                        return GeminiErrors.ServiceUnavailable<string>();

                    case HttpStatusCode.GatewayTimeout: // 504
                        return GeminiErrors.DeadlineExceeded<string>();

                    default:
                        // Fallback for unexpected status codes
                        adminLogger.Failure($"Unhandled HTTP status code: {(int)response.StatusCode}. Raw content: {errorContent.Truncate(150)}", cfg.Id, "UNHANDLED_ERROR");
                        return (int)response.StatusCode >= 500
                            ? ResilientResponse<string>.CreateRetryableError((int)response.StatusCode, $"Unhandled Server Error: {response.ReasonPhrase}", "UNKNOWN_SERVER_ERROR")
                            : ResilientResponse<string>.CreateNonRetryableError((int)response.StatusCode, $"Unhandled Client Error: {response.ReasonPhrase}", "UNKNOWN_CLIENT_ERROR");
                }
            }
            // --- EXCEPTION PATH ---
            catch (BrokenCircuitException)
            {
                adminLogger.Failure("Circuit breaker tripped and is now open.", cfg.Id, "CIRCUIT_BREAKER");
                return GeminiErrors.ServiceUnavailable<string>();
            }
            catch (TimeoutRejectedException)
            {
                adminLogger.Failure("Request timed out by Polly policy.", cfg.Id, "TIMEOUT");
                return GeminiErrors.DeadlineExceeded<string>(); // A timeout is equivalent to a deadline exceeded
            }
            catch (HttpRequestException ex)
            {
                adminLogger.Failure($"HTTP request exception: {ex.Message}", cfg.Id, "NETWORK");
                // Network errors are typically temporary.
                return ResilientResponse<string>.CreateRetryableError(503, $"Network Error: {ex.Message}", "NETWORK_ERROR");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected exception during API call for Config ID {ConfigId}", cfg.Id);
                adminLogger.Failure($"Unexpected exception: {ex.Message}", cfg.Id, "EXCEPTION");
                // Unexpected errors are safer to be classified as non-retryable to avoid poison messages.
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "GeminiService",
                        EventType = "AttemptApiCallAsync",
                        Message = ex.Message,
                        Details = ex.StackTrace,
                        Exception = ex.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                return ResilientResponse<string>.CreateNonRetryableError(500, $"An unexpected error occurred: {ex.Message}", "UNEXPECTED_EXCEPTION");
            }
        }

        // A simple custom exception to signal Hangfire to retry
        public class RetryableJobException : Exception
        {
            public RetryableJobException(string message) : base(message) { }
        }

        public Task<string?> GetJobResultAsync(string jobId, CancellationToken ct)
        {
            if (_cache.TryGetValue($"JobResult_{jobId}", out string? result))
            {
                return Task.FromResult<string?>(result);
            }

            // If not in cache, check if job is still running
            JobDetailsDto jobState = JobStorage.Current.GetMonitoringApi().JobDetails(jobId);
            return Task.FromResult<string?>(jobState != null ? "JOB_RUNNING" : "JOB_NOT_FOUND");
        }

        /// <summary>
        /// Enqueue a batch of message enhancements
        /// </summary>
        public Task<List<string>> EnhanceMessagesBatchAsync(List<string> texts, CancellationToken ct, string? apiKeyName = null)
        {
            List<string> jobIds = [];

            foreach (string text in texts)
            {
                string jobId = Guid.NewGuid().ToString("N");
                string hangfireJobId = BackgroundJob.Enqueue(() =>
                    ProcessEnhanceMessageJobAsync(text, jobId, apiKeyName!, ct));

                jobIds.Add(jobId);
                _logger.LogInformation("Batch job enqueued. Text: {TextLength} chars, JobId: {JobId}",
                    text?.Length ?? 0, jobId);
            }

            return Task.FromResult(jobIds);
        }

        #region Core Logic Helpers

        private async Task<AiApiConfiguration?> GetNextActiveConfigAsync(string? apiKeyName, AdminLogger adminLogger, CancellationToken ct)
        {
            await RefreshConfigsIfNeededAsync(adminLogger, ct);

            await _configLock.WaitAsync(ct);
            try
            {
                // Find the first ID in our ordered list that is not on cooldown.
                // NOTE: Filtering by 'apiKeyName' was removed because the property 'KeyName' does not exist on your AiApiConfiguration entity.
                // If you add this property to your entity, you can re-enable the commented-out filter.
                int configId = _orderedConfigIds.FirstOrDefault(id =>
                {
                    if (!_configs.TryGetValue(id, out AiApiConfiguration? conf))
                    {
                        return false;
                    }
                    //bool nameMatch = string.IsNullOrEmpty(apiKeyName) || conf.KeyName == apiKeyName; // <-- This line requires 'KeyName' property
                    bool onCooldown = _cache.TryGetValue(GetCooldownCacheKey(id), out _);
                    return !onCooldown; // && nameMatch;
                });

                return configId != 0 && _configs.TryGetValue(configId, out AiApiConfiguration? config) ? config : null;
            }
            finally
            {
                _ = _configLock.Release();
            }
        }

        private async Task ReportFailureAndRotateAsync(int failedConfigId)
        {
            _ = _cache.Set(GetCooldownCacheKey(failedConfigId), true, CONFIG_FAILURE_COOLDOWN);

            await _configLock.WaitAsync();
            try
            {
                if (_orderedConfigIds.Remove(failedConfigId))
                {
                    _orderedConfigIds.Add(failedConfigId);
                }
            }
            finally
            {
                _ = _configLock.Release();
            }
        }

        private async Task RefreshConfigsIfNeededAsync(AdminLogger adminLogger, CancellationToken ct)
        {
            if (!_cache.TryGetValue(CONFIG_REFRESH_LOCK_KEY, out _))
            {
                await _configLock.WaitAsync(ct);
                try
                {
                    if (_cache.TryGetValue(CONFIG_REFRESH_LOCK_KEY, out _))
                    {
                        return;
                    }

                    adminLogger.Info("Config cache expired. Refreshing configurations from database.", null, "CONFIG_REFRESH");

                    await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
                    IAiApiConfigurationRepository repo = scope.ServiceProvider.GetRequiredService<IAiApiConfigurationRepository>();
                    IEnumerable<AiApiConfiguration> allDbConfigs = await repo.GetAllByProviderAndStatusAsync("Gemini", true, ct);

                    List<AiApiConfiguration> freshConfigs = allDbConfigs
                        .Where(c => !string.IsNullOrWhiteSpace(c.ApiKey) && !string.IsNullOrWhiteSpace(c.ModelName))
                        .ToList();

                    _configs.Clear();
                    foreach (AiApiConfiguration? cfg in freshConfigs)
                    {
                        _configs[cfg.Id] = cfg;
                    }

                    List<int> newOrder = _orderedConfigIds.Intersect(_configs.Keys).ToList();
                    List<int> addedKeys = _configs.Keys.Except(newOrder).ToList();
                    newOrder.AddRange(addedKeys);
                    _orderedConfigIds = newOrder;

                    adminLogger.Info($"Refresh complete. Found {_configs.Count} active configs. Order: [{string.Join(",", _orderedConfigIds)}]", null, "CONFIG_REFRESH");
                    _ = _cache.Set(CONFIG_REFRESH_LOCK_KEY, true, CONFIG_CACHE_DURATION);
                }
                finally
                {
                    _ = _configLock.Release();
                }
            }
        }

        private AsyncCircuitBreakerPolicy<HttpResponseMessage> GetOrCreateCircuitBreaker(int configId, AdminLogger adminLogger)
        {
            string key = $"GeminiConfig_{configId}";
            return _circuitBreakers.GetOrAdd(key, _ =>
            {
                adminLogger.Info($"Creating new circuit breaker for Config ID {configId}.", configId, "CIRCUIT_BREAKER");
                return Policy<HttpResponseMessage>
                    .Handle<HttpRequestException>()
                    .OrResult(r => (int)r.StatusCode >= 500)
                    .CircuitBreakerAsync(
                        handledEventsAllowedBeforeBreaking: 3,
                        durationOfBreak: TimeSpan.FromMinutes(2),
                        onBreak: (outcome, ts) => adminLogger.Failure($"Circuit breaker opened for Config ID {configId}. Break duration: {ts.TotalMinutes} minutes.", configId, "CIRCUIT_BREAKER"),
                        onReset: () => adminLogger.Info($"Circuit breaker reset for Config ID {configId}.", configId, "CIRCUIT_BREAKER"),
                        onHalfOpen: () => adminLogger.Info($"Circuit breaker half-open for Config ID {configId}.", configId, "CIRCUIT_BREAKER")
                    );
            });
        }

        private bool CheckAndIncrementQuota(string model, string? text)
        {
            DateTime now = DateTime.UtcNow;
            string minuteKey = $"quota_rpm_{model}_{now:yyyyMMddHHmm}";
            string dayKey = $"quota_rpd_{model}_{now:yyyyMMdd}";
            string tokenKey = $"quota_tpm_{model}_{now:yyyyMMddHHmm}";

            // Check RPM
            int currentRpm = _cache.GetOrCreate(minuteKey, entry => { entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1); return 0; });
            if (currentRpm >= FREE_RPM)
            {
                return false;
            }

            _ = _cache.Set(minuteKey, currentRpm + 1, TimeSpan.FromMinutes(1));

            // Check RPD
            int currentRpd = _cache.GetOrCreate(dayKey, entry => { entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1); return 0; });
            if (currentRpd >= FREE_RPD)
            {
                return false;
            }

            _ = _cache.Set(dayKey, currentRpd + 1, TimeSpan.FromDays(1));

            // Check TPM (rough estimate: 1 token ≈ 4 characters)
            int estimatedTokens = (text?.Length ?? 0) / 4;
            int currentTpm = _cache.GetOrCreate(tokenKey, entry => { entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1); return 0; });
            if (currentTpm + estimatedTokens > FREE_TPM)
            {
                return false;
            }

            _ = _cache.Set(tokenKey, currentTpm + estimatedTokens, TimeSpan.FromMinutes(1));

            return true;
        }

        private static string GenerateContentCacheKey(string? text, ICollection<byte[]>? images)
        {
            StringBuilder content = new();
            if (!string.IsNullOrWhiteSpace(text))
            {
                _ = content.Append($"text:{text}");
            }
            if (images != null && images.Any())
            {
                foreach (byte[] img in images)
                {
                    using SHA256 sha256 = SHA256.Create();
                    byte[] hash = sha256.ComputeHash(img);
                    _ = content.Append($"img:{Convert.ToBase64String(hash)}");
                }
            }
            using SHA256 finalHash = SHA256.Create();
            byte[] finalBytes = finalHash.ComputeHash(Encoding.UTF8.GetBytes(content.ToString()));
            return $"gemini_content_{Convert.ToBase64String(finalBytes)}";
        }


        #endregion

        private string GetCooldownCacheKey(int configId)
        {
            return $"GeminiConfig_Cooldown_{configId}";
        }

        private async Task NotifyAdminAsync(string message, CancellationToken ct)
        {
            try
            {
                await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
                var notificationService = scope.ServiceProvider.GetService<INotificationToAdminService>();
                if (notificationService != null)
                {
                    await notificationService.SendNotificationAsync(message, ct);
                }
                else
                {
                    _logger.LogWarning("Admin notification service is not registered. Cannot send admin notification.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send admin notification");
            }
        }

        private GeminiRequest BuildRequest(string promptTemplate, string? text)
        {
            List<Part> parts = [];

            string processedText;
            if (!string.IsNullOrWhiteSpace(text))
            {
                // Apply the template to the provided text.
                processedText = promptTemplate.Replace("{message}", text);
            }
            else
            {
                // If text is null or empty, use the template with an empty message,
                // or just the template itself if it doesn't require a message.
                processedText = promptTemplate.Replace("{message}", "").Trim();
            }

            // Add the single text part.
            if (!string.IsNullOrWhiteSpace(processedText))
            {
                parts.Add(new Part(Text: processedText, InlineData: null));
            }
            return new GeminiRequest([new(parts)]);
        }

        // Place these records within the GeminiService class or in a dedicated file like 'GeminiModels.cs'

        #region Request Models

        /// <summary>
        /// The top-level request payload sent to the Gemini API.
        /// </summary>
        /// <param name="contents">A list of content blocks, typically just one for a single prompt.</param>
        public record GeminiRequest(
            [property: JsonPropertyName("contents")] List<Content> contents
        );

        /// <summary>
        /// Represents a single block of content in a request, containing various parts.
        /// </summary>
        /// <param name="parts">The different parts of the content, such as text or image data.</param>
        public record Content(
            [property: JsonPropertyName("parts")] List<Part> parts
        );

        /// <summary>
        /// A single part of a prompt, which can be either text or inline data (like a base64-encoded image).
        /// </summary>
        /// <param name="Text">The text content of the part.</param>
        /// <param name="InlineData">The inline data content of the part.</param>
        public record Part(
            [property: JsonPropertyName("text")] string? Text,
            [property: JsonPropertyName("inline_data")] InlineData? InlineData
        );

        /// <summary>
        /// Represents inline data, such as an image, sent as part of a prompt.
        /// </summary>
        /// <param name="MimeType">The MIME type of the data (e.g., "image/jpeg").</param>
        /// <param name="Data">The base64-encoded data.</param>
        public record InlineData(
            [property: JsonPropertyName("mime_type")] string MimeType,
            [property: JsonPropertyName("data")] string Data
        );

        #endregion

        #region Success & Feedback Response Models

        /// <summary>
        /// The top-level response from the Gemini API for a successful request.
        /// </summary>
        /// <param name="Candidates">A list of generated candidate responses from the model.</param>
        /// <param name="PromptFeedback">Feedback regarding the prompt, including any blocking reasons.</param>
        public record GeminiResponse(
            [property: JsonPropertyName("candidates")] List<Candidate>? Candidates,
            [property: JsonPropertyName("promptFeedback")] PromptFeedback? PromptFeedback
        );

        /// <summary>
        /// A single candidate response generated by the model.
        /// </summary>
        /// <param name="Content">The content of the response.</param>
        /// <param name="FinishReason">The reason the model stopped generating text.</param>
        /// <param name="SafetyRatings">A list of safety ratings for the candidate response.</param>
        public record Candidate(
            [property: JsonPropertyName("content")] Content? Content,
            [property: JsonPropertyName("finishReason")] string? FinishReason,
            [property: JsonPropertyName("safetyRatings")] List<SafetyRating>? SafetyRatings
        );

        /// <summary>
        /// Contains feedback for the prompt sent in the request, especially if it was blocked.
        /// </summary>
        /// <param name="BlockReason">The reason the prompt was blocked, if applicable.</param>
        /// <param name="SafetyRatings">Safety ratings associated with the prompt itself.</param>
        public record PromptFeedback(
            [property: JsonPropertyName("blockReason")] string? BlockReason,
            [property: JsonPropertyName("safetyRatings")] List<SafetyRating>? SafetyRatings
        );

        /// <summary>
        /// Represents the safety rating for a piece of content.
        /// </summary>
        /// <param name="Category">The safety category (e.g., "HARM_CATEGORY_HARASSMENT").</param>
        /// <param name="Probability">The likelihood of the content falling into this category (e.g., "NEGLIGIBLE").</param>
        public record SafetyRating(
            [property: JsonPropertyName("category")] string Category,
            [property: JsonPropertyName("probability")] string Probability
        );

        #endregion

        #region Error Response Models

        /// <summary>
        /// The top-level structure for a standard error response from the Gemini API.
        /// This is crucial for parsing detailed error information when the HTTP status is not 200 OK.
        /// </summary>
        /// <param name="Error">The detailed error payload.</param>
        private record GeminiErrorResponse(
            [property: JsonPropertyName("error")] GeminiErrorPayload Error
        );

        /// <summary>
        /// The detailed error payload containing the specific reason for the failure.
        /// </summary>
        /// <param name="Code">The HTTP status code.</param>
        /// <param name="Message">A developer-facing error message.</param>
        /// <param name="Status">A Google-specific status code (e.g., "INVALID_ARGUMENT", "RESOURCE_EXHAUSTED").</param>
        private record GeminiErrorPayload(
            [property: JsonPropertyName("code")] int Code,
            [property: JsonPropertyName("message")] string Message,
            [property: JsonPropertyName("status")] string Status
        );

        #endregion

        private class AdminLogger
        {
            private readonly string _operationName;
            private readonly Stopwatch _totalStopwatch;
            private readonly List<LogEntry> _entries = [];
            private string? _finalStatus;
            private readonly DateTime _startTime;
            private int _successCount = 0;
            private int _failureCount = 0;
            private int _infoCount = 0;
            private int _warningCount = 0; // Added for warnings
            private readonly Dictionary<int, int> _configUsageCount = [];

            public string CorrelationId { get; }

            public AdminLogger(string operationName, string correlationId)
            {
                _operationName = operationName;
                CorrelationId = correlationId;
                _totalStopwatch = Stopwatch.StartNew();
                _startTime = DateTime.UtcNow;
                Info($"🚀 Operation '{_operationName}' Started", null, "START");
            }

            public void Info(string message, int? configId = null, string? category = null)
            {
                _entries.Add(new LogEntry(message, _totalStopwatch.Elapsed, configId, "INFO", category));
                _infoCount++;
                if (configId.HasValue)
                {
                    _configUsageCount[configId.Value] = _configUsageCount.GetValueOrDefault(configId.Value) + 1;
                }
            }

            public void Success(string message, int? configId = null, string? category = null)
            {
                _entries.Add(new LogEntry(message, _totalStopwatch.Elapsed, configId, "SUCCESS", category));
                _successCount++;
                if (configId.HasValue)
                {
                    _configUsageCount[configId.Value] = _configUsageCount.GetValueOrDefault(configId.Value) + 1;
                }

                _finalStatus = "✅ SUCCESS";
            }

            public void Failure(string message, int? configId = null, string? category = null)
            {
                _entries.Add(new LogEntry(message, _totalStopwatch.Elapsed, configId, "FAILURE", category));
                _failureCount++;
                if (configId.HasValue)
                {
                    _configUsageCount[configId.Value] = _configUsageCount.GetValueOrDefault(configId.Value) + 1;
                }

                _finalStatus ??= "❌ FAILURE";
            }

            public void Warning(string message, int? configId = null, string? category = null)
            {
                _entries.Add(new LogEntry(message, _totalStopwatch.Elapsed, configId, "WARNING", category));
                _warningCount++; // Increment warning count
                if (configId.HasValue)
                {
                    _configUsageCount[configId.Value] = _configUsageCount.GetValueOrDefault(configId.Value) + 1;
                }
            }

            public string Render()
            {
                _totalStopwatch.Stop();
                StringBuilder sb = new();

                // Header with beautiful styling
                _ = sb.AppendLine("**🌟 GEMINI AI ENHANCEMENT REPORT**");
                _ = sb.AppendLine();
                _ = sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                _ = sb.AppendLine();

                // Operation Summary
                _ = sb.AppendLine($"**📝 Operation:** `{_operationName}`");
                _ = sb.AppendLine($"**🎯 Status:** {GetStatusWithColor(_finalStatus ?? "UNKNOWN")}");
                _ = sb.AppendLine($"**⏱️ Duration:** `{FormatDuration(_totalStopwatch.Elapsed)}`");
                _ = sb.AppendLine($"**🕒 Started:** `{_startTime:yyyy-MM-dd HH:mm:ss} UTC`");
                _ = sb.AppendLine($"**🕐 Ended:** `{DateTime.UtcNow:HH:mm:ss} UTC`");
                _ = sb.AppendLine($"**🔗 Correlation ID:** `{CorrelationId}`");
                _ = sb.AppendLine();

                // Performance Metrics
                _ = sb.AppendLine("**📊 PERFORMANCE METRICS**");
                _ = sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                _ = sb.AppendLine($"**Total Operations:** `{_entries.Count}`");
                _ = sb.AppendLine($"**Success Rate:** `{CalculateSuccessRate()}`");
                _ = sb.AppendLine($"**Average Response Time:** `{CalculateAverageResponseTime()}`");
                _ = sb.AppendLine($"**Configurations Used:** `{_configUsageCount.Count}`");
                _ = sb.AppendLine();

                // Configuration Usage
                if (_configUsageCount.Any())
                {
                    _ = sb.AppendLine("**⚙️ CONFIGURATION USAGE**");
                    _ = sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                    foreach (KeyValuePair<int, int> kvp in _configUsageCount.OrderByDescending(x => x.Value))
                    {
                        _ = sb.AppendLine($"• **Config {kvp.Key}:** `{kvp.Value} operations`");
                    }
                    _ = sb.AppendLine();
                }

                // Detailed Trace
                _ = sb.AppendLine("**🔍 DETAILED TRACE**");
                _ = sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                foreach (IGrouping<string, LogEntry>? group in _entries.GroupBy(e => e.Category ?? "GENERAL").OrderBy(g => g.Key))
                {
                    if (group.Key != "GENERAL")
                    {
                        _ = sb.AppendLine($"**{GetCategoryIcon(group.Key)} {group.Key.ToUpper()}:**");
                    }

                    foreach (LogEntry? entry in group)
                    {
                        string icon = GetStatusIcon(entry.Status);
                        string configInfo = entry.ConfigId.HasValue ? $"`[C{entry.ConfigId}]`" : "";
                        string timestamp = $"`+{entry.Timestamp.TotalMilliseconds:F0}ms`";
                        string message = TruncateMessage(entry.Message, 80);

                        _ = sb.AppendLine($"  {icon} {timestamp} {configInfo} {message}");
                    }
                    _ = sb.AppendLine();
                }

                // Final Analysis
                _ = sb.AppendLine("**📋 ANALYSIS & RECOMMENDATIONS**");
                _ = sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

                if (_successCount > 0)
                {
                    KeyValuePair<int, int> bestConfig = _configUsageCount.OrderByDescending(x => x.Value).FirstOrDefault();
                    _ = sb.AppendLine($"✅ **Success:** AI enhancement completed successfully");
                    _ = sb.AppendLine($"   • **Best Config:** `Config {bestConfig.Key}` ({bestConfig.Value} operations)");
                    _ = sb.AppendLine($"   • **Success Rate:** `{_successCount * 100.0 / _entries.Count:F1}%`");
                }
                else
                {
                    _ = sb.AppendLine("❌ **Failure:** All configurations failed");
                    _ = sb.AppendLine("   • **Check:** API keys, quotas, network connectivity");
                    _ = sb.AppendLine("   • **Verify:** Gemini service configuration");
                }

                if (_failureCount > 0)
                {
                    _ = sb.AppendLine($"⚠️ **Failures Detected:** `{_failureCount} failures`");
                    _ = sb.AppendLine("   • **Review:** Configuration settings and API quotas");
                    _ = sb.AppendLine("   • **Check:** Network connectivity and service status");
                }

                if (_entries.Any(e => e.Status == "FAILURE" && e.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)))
                {
                    _ = sb.AppendLine("⏱️ **Timeout Issues Detected:**");
                    _ = sb.AppendLine("   • **Consider:** Lowering timeout values");
                    _ = sb.AppendLine("   • **Check:** Network speed and API response times");
                }

                if (_configUsageCount.Count > 1)
                {
                    _ = sb.AppendLine("🔄 **Multiple Configs Used:**");
                    _ = sb.AppendLine("   • **Strategy:** Fallback configuration system active");
                    _ = sb.AppendLine("   • **Reliability:** Enhanced through config redundancy");
                }

                _ = sb.AppendLine();
                _ = sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                _ = sb.AppendLine("**📅 Report Generated:** " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"));

                return sb.ToString();
            }

            private string GetStatusWithColor(string status)
            {
                return status switch
                {
                    "SUCCESS" => "✅ **SUCCESS**",
                    "FAILURE" => "❌ **FAILURE**",
                    _ => "❓ **UNKNOWN**"
                };
            }

            private string GetStatusIcon(string status)
            {
                return status switch
                {
                    "SUCCESS" => "✅",
                    "FAILURE" => "❌",
                    "START" => "🚀",
                    "RETRY" => "🔄",
                    "TIMEOUT" => "⏱️",
                    "QUOTA" => "💳",
                    "NETWORK" => "🌐",
                    _ => "➡️"
                };
            }

            private string GetCategoryIcon(string category)
            {
                return category.ToUpper() switch
                {
                    "INITIALIZATION" => "🚀",
                    "ENHANCEMENT" => "✨",
                    "VALIDATION" => "🔍",
                    "NETWORK" => "🌐",
                    "API" => "🔌",
                    "CACHE" => "💾",
                    "ERROR" => "⚠️",
                    "SUCCESS" => "✅",
                    _ => "📋"
                };
            }




            private string FormatDuration(TimeSpan duration)
            {
                return duration.TotalMilliseconds < 1000
                    ? $"{duration.TotalMilliseconds:F0}ms"
                    : duration.TotalSeconds < 60 ? $"{duration.TotalSeconds:F1}s" : $"{duration.TotalMinutes:F1}m {duration.Seconds}s";
            }

            private string CalculateSuccessRate()
            {
                if (_entries.Count == 0)
                {
                    return "0%";
                }

                double successRate = (double)_successCount / _entries.Count * 100;
                return $"{successRate:F1}% ({_successCount}/{_entries.Count})";
            }

            private string CalculateAverageResponseTime()
            {
                if (_entries.Count == 0)
                {
                    return "0ms";
                }

                double avgTime = _entries.Average(e => e.Timestamp.TotalMilliseconds);
                return $"{avgTime:F0}ms";
            }

            private string TruncateMessage(string message, int maxLength)
            {
                return message.Length <= maxLength ? message : message[..maxLength] + "...";
            }

            private record LogEntry(string Message, TimeSpan Timestamp, int? ConfigId, string Status, string? Category);
        }
    }

    public static class StringExtensions
    {
        public static string Truncate(this string value, int maxLength)
        {
            return value.Length <= maxLength ? value : value[..maxLength] + "...";
        }
    }
}
