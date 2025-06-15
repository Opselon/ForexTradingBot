// File: Infrastructure/Services/RssFetchingCoordinatorService.cs
#region Usings
using Application.Common.Interfaces; // For IRssSourceRepository, IRssReaderService
using Domain.Entities;               // For RssSource
using Hangfire;                      // For JobDisplayName, AutomaticRetry
using Microsoft.Extensions.Logging;
using Polly;                         // For Polly policies
using Polly.Retry;                   // For Retry policies
using System.Net;                    // For HttpStatusCode (for analyzing permanent errors)
#endregion

namespace Infrastructure.Services
{
    /// <summary>
    /// Service for coordinating and managing the fetching and processing of RSS feeds.
    /// This service retrieves active RSS feeds from the repository and passes each one for processing to the <see cref="IRssReaderService"/>.
    /// Each individual feed fetch operation is protected by a Polly policy for enhanced resilience against transient errors.
    /// </summary>
    public class RssFetchingCoordinatorService : IRssFetchingCoordinatorService
    {
        private readonly IRssSourceRepository _rssSourceRepository;
        private readonly IRssReaderService _rssReaderService;
        private readonly ILogger<RssFetchingCoordinatorService> _logger;
        private readonly AsyncRetryPolicy _coordinatorRetryPolicy; // This policy is for the coordinator's processing of individual feeds

        // Level 5: Limit concurrency to avoid overloading the VPS. Configurable constant.
        private const int MaxConcurrentFeedFetches = 4; // Adjust based on VPS cores and I/O capacity (e.g., 2x-4x cores)

        /// <summary>
        /// Constructor for <see cref="RssFetchingCoordinatorService"/>.
        /// </summary>
        /// <param name="rssSourceRepository">Repository for accessing RSS sources.</param>
        /// <param name="rssReaderService">Service for reading and processing RSS feeds.</param>
        /// <param name="logger">Logger for logging information and errors.</param>
        public RssFetchingCoordinatorService(
            IRssSourceRepository rssSourceRepository,
            IRssReaderService rssReaderService,
            ILogger<RssFetchingCoordinatorService> logger)
        {
            _rssSourceRepository = rssSourceRepository ?? throw new ArgumentNullException(nameof(rssSourceRepository));
            _rssReaderService = rssReaderService ?? throw new ArgumentNullException(nameof(rssReaderService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Level 3/4: Initialize Polly policy for retrying transient errors at the coordinator level.
            // This policy specifically handles exceptions that bubble up from `_rssReaderService.FetchAndProcessFeedAsync`.
            // It will *not* retry `OperationCanceledException` (intended cancellations) or
            // `HttpRequestException` for permanent HTTP status codes (400-405, 410, 422).
            _coordinatorRetryPolicy = Policy
                .Handle<Exception>(ex =>
                {
                    if (ex is OperationCanceledException or TaskCanceledException)
                    {
                        return false; // Don't retry if it's an explicit cancellation.
                    }

                    // Level 3: Check for specific HttpRequestException types (Permanent HTTP errors).
                    // This relies on HttpRequestException containing StatusCode for the propagation
                    // or parsing the message. We assume RssReaderService already uses these status codes.
                    if (ex is HttpRequestException httpEx)
                    {
                        // In `RssReaderService.IsPermanentHttpError` we check these, so we'll do the same here.
                        // Assuming httpEx.StatusCode is populated when relevant.
                        if (IsPermanentHttpErrorStatusCode(httpEx.StatusCode))
                        {
                            _logger.LogWarning(httpEx, "Polly Coordinator: Encountered permanent HTTP error ({StatusCode}) from reader for RSS feed. Not retrying.", httpEx.StatusCode);
                            return false; // Do NOT retry permanent HTTP errors
                        }
                    }

                    // Otherwise, log and retry other exceptions (considered transient for coordinator level)
                    return true;
                })
                .WaitAndRetryAsync(
                    retryCount: 2, // Fewer retries here, as the reader service has its own policies.
                    sleepDurationProvider: retryAttempt =>
                    {
                        TimeSpan delay = TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)) + TimeSpan.FromMilliseconds(new Random().Next(0, 500)); // Exponential backoff with jitter
                        _logger.LogWarning(
                            "Polly Coordinator: Retrying a single RSS feed fetch. Attempt {RetryAttempt} of 2. Delaying for {TimeSpanSeconds:F1} seconds...",
                            retryAttempt, delay.TotalSeconds);
                        return delay;
                    },
                    onRetryAsync: (exception, timeSpan, retryAttempt, context) =>
                    {
                        // Level 2: Enhanced structured logging for retries.
                        // Attempt to extract source info from Polly context, if available.
                        string sourceInfo = context.TryGetValue("RssSourceName", out object? name) ? $" (Source: {name})" : "";
                        string sourceId = context.TryGetValue("RssSourceId", out object? id) ? $" (ID: {id})" : "";

                        _logger.LogWarning(exception,
                            "Polly Coordinator: Transient error encountered while processing RSS feed{SourceInfo}{SourceId}. Retrying in {TimeSpanSeconds:F1}s for attempt {RetryAttempt}/2. Error: {ErrorMessage}",
                            sourceInfo, sourceId, timeSpan.TotalSeconds, retryAttempt, exception.Message);
                        return Task.CompletedTask;
                    });
        }

        #region FetchAllActiveFeedsAsync (Public Hangfire Job - Level 5: Parallel.ForEachAsync)
        /// <summary>
        /// Fetches and processes all active RSS feeds asynchronously.
        /// This method is executed as a Hangfire job.
        /// Each feed is processed in parallel with limited concurrency to optimize resource usage.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for cancelling the operation.</param>
        [JobDisplayName("Fetch All Active RSS Feeds - Coordinator")] // Display name for Hangfire dashboard
        [AutomaticRetry(Attempts = 0)] // Polly handles retries at the single-feed processing level
        public async Task FetchAllActiveFeedsAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("[HANGFIRE JOB] Starting: FetchAllActiveFeedsAsync at {UtcNow}", DateTime.UtcNow);

            List<RssSource> activeSources = (await _rssSourceRepository.GetActiveSourcesAsync(cancellationToken).ConfigureAwait(false)).ToList(); // Level 1: ConfigureAwait(false)

            if (!activeSources.Any())
            {
                _logger.LogInformation("[HANGFIRE JOB] No active RSS sources found to fetch.");
                return;
            }
            _logger.LogInformation("[HANGFIRE JOB] Found {Count} active RSS sources to process. Processing with {Concurrency} concurrent fetches.", activeSources.Count(), MaxConcurrentFeedFetches);

            // Level 5: Using Parallel.ForEachAsync for cleaner and more robust structured parallelism.
            // This implicitly manages concurrency using MaxConcurrentFeedFetches.
            await Parallel.ForEachAsync(activeSources,
                new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentFeedFetches, CancellationToken = cancellationToken }, // Level 5: Link ParallelOptions CancellationToken
                async (source, ct) =>
                {
                    // Level 2: Each feed's processing is now self-contained, logging its own context.
                    await ProcessSingleFeedWithLoggingAndRetriesAsync(source, ct).ConfigureAwait(false); // Level 1: ConfigureAwait(false)
                }).ConfigureAwait(false); // Level 1: ConfigureAwait(false)

            _logger.LogInformation("[HANGFIRE JOB] Finished: FetchAllActiveFeedsAsync at {UtcNow}", DateTime.UtcNow);
        }
        #endregion

        #region ProcessSingleFeedWithLoggingAndRetriesAsync (Private Helper - Level 9: Comprehensive Error Reporting)
        /// <summary>
        /// Processes a single RSS feed with detailed logging and coordinator-level retries.
        /// This method uses the Polly policy defined in the constructor to protect the feed reader service call.
        /// </summary>
        /// <param name="source">The RSS source to process.</param>
        /// <param name="cancellationToken">Cancellation token for cancelling the operation.</param>
        private async Task ProcessSingleFeedWithLoggingAndRetriesAsync(RssSource source, CancellationToken cancellationToken)
        {
            // Level 2: Define specific Polly context for this individual feed for granular logging.
            Context pollyContext = new($"RssFeedFetch_{source.Id}")
            {
                ["RssSourceId"] = source.Id.ToString(), // Use ToString() for Guid ID
                ["RssSourceName"] = source.SourceName
            }; // Use ToString() for Guid ID

            // Level 2: Use logging scope to include source-specific context for all logs within this method.
            using (_logger.BeginScope(new Dictionary<string, object?>
            {
                ["RssSourceId"] = source.Id.ToString(), // Use ToString() for Guid ID
                ["RssSourceName"] = source.SourceName,
                ["RssSourceUrl"] = source.Url
            }))
            {
                _logger.LogInformation("Processing RSS source '{SourceName}' (ID: {RssSourceId}) via coordinator. CorrelationId: {CorrelationId}",
                                       source.SourceName, source.Id.ToString(), pollyContext.CorrelationId);

                try
                {
                    // Level 9: Execute FetchAndProcessFeedAsync protected by the coordinator's Polly policy.
                    // Pass Polly's internal cancellation token (`ct`) to the reader service if its contract allowed it.
                    // Assuming FetchAndProcessFeedAsync uses the main CancellationToken and doesn't need context propagation to its underlying policies.
                    Shared.Results.Result<IEnumerable<Application.DTOs.News.NewsItemDto>> result = await _coordinatorRetryPolicy.ExecuteAsync(async (ctx, ct) =>
                    {
                        // Ensure the cancellation token is propagated from Parallel.ForEachAsync's lambda -> Polly -> IRssReaderService
                        return await _rssReaderService.FetchAndProcessFeedAsync(source, ct).ConfigureAwait(false); // Level 1: ConfigureAwait(false)
                    }, pollyContext, cancellationToken).ConfigureAwait(false); // Level 1: ConfigureAwait(false)

                    // Level 9: Analyze the result from FetchAndProcessFeedAsync.
                    if (result.Succeeded)
                    {
                        _logger.LogInformation("Successfully processed RSS source '{SourceName}' (ID: {RssSourceId}). Found {NewItemCount} new items. Message: {ResultMessage}",
                            source.SourceName, source.Id.ToString(), result.Data?.Count() ?? 0, result.SuccessMessage);
                    }
                    else
                    {


                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Level 1: Catch specific cancellation.
                    _logger.LogInformation("RSS feed processing for '{SourceName}' (ID: {RssSourceId}) was explicitly cancelled.", source.SourceName, source.Id.ToString());
                }
                catch (Exception ex)
                {
                    // Level 9: Catch any exceptions that Polly's coordinator policy did NOT handle/retry (e.g., non-retryable errors or after max retries).
                    _logger.LogError(ex, "Critical unhandled error while processing RSS source '{SourceName}' (ID: {RssSourceId}) after all coordinator retries. Error: {ErrorMessage}",
                        source.SourceName, source.Id.ToString(), ex.Message);
                    // Error is logged, not re-thrown, allowing other feeds to proceed.
                }
            }
        }

        // Level 3: Helper method to determine if an HTTP status code indicates a permanent error.
        private bool IsPermanentHttpErrorStatusCode(HttpStatusCode? statusCode)
        {
            return statusCode switch
            {
                HttpStatusCode.BadRequest => true,        // 400
                HttpStatusCode.Unauthorized => true,     // 401
                HttpStatusCode.Forbidden => true,        // 403
                HttpStatusCode.NotFound => true,         // 404
                HttpStatusCode.MethodNotAllowed => true, // 405
                HttpStatusCode.Gone => true,             // 410
                HttpStatusCode.UnprocessableEntity => true, // 422
                _ => false,
            };
        }
        #endregion
    }
}