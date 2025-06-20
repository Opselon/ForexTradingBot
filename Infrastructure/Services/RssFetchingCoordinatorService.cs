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
    /// Serves as the central coordinator and orchestrator for fetching and processing RSS feeds within the AI analysis program.
    /// This service is responsible for maintaining a continuous and resilient flow of fresh news data into the system,
    /// which is then consumed and analyzed by the AI models.
    /// <br/><br/>
    /// It identifies all active RSS feed sources, manages concurrent fetching operations to optimize throughput,
    /// and leverages robust retry mechanisms (using Polly) to ensure that transient failures during individual
    /// feed processing attempts do not halt the entire ingestion pipeline.
    /// </summary>
    /// <remarks>
    /// Role for AI Analysis:
    /// This service is foundational for the entire AI analysis program. Its operational health and efficiency directly
    /// dictate the timeliness, completeness, and quality of the raw data available to the AI models.
    /// <list type="bullet">
    ///     <item><description>
    ///         Data Freshness: Ensures AI models always have access to the latest news, critical for real-time signal generation and analysis.
    ///     </description></item>
    ///     <item><description>
    ///         Pipeline Resilience: By handling transient errors and managing concurrency, it guarantees a continuous data supply, minimizing
    ///         disruptions to AI processing even when external RSS sources or network conditions are unstable.
    ///     </description></item>
    ///     <item><description>
    ///         Operational Insight (MLOps): Provides comprehensive logging at various levels (coordinator, per-feed, retry attempts). This telemetry is
    ///         invaluable for MLOps teams to monitor the health of the data ingestion pipeline, diagnose source-specific issues,
    ///         and optimize resource allocation for AI data preparation.
    ///     </description></item>
    ///     <item><description>
    ///         Scalability: The concurrent processing capabilities allow the system to scale its data ingestion efforts
    ///         as the number of monitored RSS sources grows, without linearly increasing processing time.
    ///     </description></item>
    /// </list>
    /// </remarks>
    public class RssFetchingCoordinatorService : IRssFetchingCoordinatorService
    {
        private readonly IRssSourceRepository _rssSourceRepository;
        private readonly IRssReaderService _rssReaderService;
        private readonly ILogger<RssFetchingCoordinatorService> _logger;
        private readonly AsyncRetryPolicy _coordinatorRetryPolicy; // This policy is for the coordinator's processing of individual feeds

        // Level 5: Limit concurrency to avoid overloading the VPS. Configurable constant.
        private const int MaxConcurrentFeedFetches = 4; // Adjust based on VPS cores and I/O capacity (e.g., 2x-4x cores)

        /// <summary>
        /// Initializes a new instance of the <see cref="RssFetchingCoordinatorService"/> class.
        /// This constructor sets up the essential dependencies required for coordinating RSS feed fetching
        /// and configures a robust Polly retry policy specifically for handling transient errors
        /// that may occur during the processing of individual RSS feeds by the underlying reader service.
        /// </summary>
        /// <param name="rssSourceRepository">The repository responsible for accessing and managing RSS source configurations in the database. This is a mandatory dependency.</param>
        /// <param name="rssReaderService">The service responsible for the actual fetching, parsing, and initial processing of individual RSS feeds. This is a mandatory dependency.</param>
        /// <param name="logger">The logger instance used for capturing operational information, warnings, and errors throughout the coordinator's lifecycle. This is a mandatory dependency.</param>
        /// <returns>
        /// A new instance of <see cref="RssFetchingCoordinatorService"/>, fully initialized and ready to orchestrate
        /// the fetching of active RSS feeds. The `_coordinatorRetryPolicy` is configured to enhance the resilience
        /// of the overall data ingestion pipeline.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown if any of the required constructor parameters (<paramref name="rssSourceRepository"/>,
        /// <paramref name="rssReaderService"/>, or <paramref name="logger"/>) are <c>null</c>.
        /// </exception>
        /// <remarks>
        /// **Role for AI Analysis and Data Pipeline Reliability:**
        /// This constructor's configuration of the `_coordinatorRetryPolicy` is vital for the reliability
        /// of the data pipeline that feeds our AI analysis.
        /// <list type="bullet">
        ///     <item><description>
        ///         **Data Flow Consistency:** By retrying transient failures at the coordinator level, it helps
        ///         ensure that data from individual RSS sources continues to flow into the system even when
        ///         faced with temporary external issues (e.g., source server temporary unavailability, network blips).
        ///     </description></item>
        ///     <item><description>
        ///         **Resource Management:** It explicitly avoids retrying permanent errors (like 404 Not Found or explicit cancellations).
        ///         This prevents wasting computational resources on operations that are guaranteed to fail, allowing the system to
        ///         focus on healthy data sources. This distinction is critical for efficient MLOps.
        ///     </description></item>
        ///     <item><description>
        ///         **Operational Insight for AI/MLOps:** The structured logging within the `onRetryAsync` callback provides
        ///         valuable telemetry. AI/MLOps teams can monitor these logs to:
        ///         <list type="circle">
        ///             <item><description>Track the frequency and types of transient errors affecting specific RSS feeds.</description></item>
        ///             <item><description>Identify RSS sources that consistently cause retries, which might indicate
        ///             degrading quality or intermittent issues that need deeper investigation.</description></item>
        ///             <item><description>Analyze the effectiveness of the retry policy itself and potentially
        ///             tune its parameters (retry count, delays) using AI/ML-driven optimization.</description></item>
        ///         </list>
        ///     </description></item>
        /// </list>
        /// </remarks>
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
                        string sourceInfo = context.TryGetValue("RssSourceName", out var name) ? $" (Source: {name})" : "";
                        string sourceId = context.TryGetValue("RssSourceId", out var id) ? $" (ID: {id})" : "";

                        _logger.LogWarning(exception,
                            "Polly Coordinator: Transient error encountered while processing RSS feed{SourceInfo}{SourceId}. Retrying in {TimeSpanSeconds:F1}s for attempt {RetryAttempt}/2. Error: {ErrorMessage}",
                            sourceInfo, sourceId, timeSpan.TotalSeconds, retryAttempt, exception.Message);
                        return Task.CompletedTask;
                    });
        }

        #region FetchAllActiveFeedsAsync (Public Hangfire Job - Level 5: Parallel.ForEachAsync)


        /// <summary>
        /// Orchestrates the asynchronous fetching and processing of all currently active RSS feeds.
        /// This method is designed as a Hangfire background job, serving as the central coordinator
        /// for continuously ingesting fresh news data into the system, which is then utilized by AI analysis models.
        /// It fetches a list of active RSS sources from the repository and processes each feed concurrently
        /// using structured parallelism, optimizing resource usage while ensuring all eligible feeds are attempted.
        /// </summary>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> that allows for external cancellation of the entire fetching operation. If cancelled, the job will stop processing new feeds and attempt to gracefully terminate ongoing fetches.</param>
        /// <returns>
        /// A <see cref="Task"/> that represents the asynchronous operation of fetching and processing all active RSS feeds.
        /// <list type="bullet">
        ///     <item><description>The task completes successfully when all active RSS feeds have been processed (or attempts have been made) according to the configured concurrency, and the job is marked 'Succeeded' in Hangfire.</description></item>
        ///     <item><description>The task will transition to a cancelled state (and the Hangfire job marked 'Cancelled') if the <paramref name="cancellationToken"/> is signaled during the operation.</description></item>
        ///     <item><description>Any individual feed processing failures are handled internally by <c>ProcessSingleFeedWithLoggingAndRetriesAsync</c> without causing this orchestrator job to fail, unless a critical, unhandled exception occurs in the orchestration itself (which would then cause this job to fail in Hangfire).</description></item>
        /// </list>
        /// </returns>
        /// <remarks>
        /// **Role for AI Analysis and Operations:**
        /// This method is a cornerstone of the data ingestion pipeline. Its effective and timely execution
        /// directly impacts the freshness and completeness of data available for AI models.
        /// <list type="bullet">
        ///     <item><description>
        ///         **Data Freshness:** The frequency and success of this job dictate how quickly new information
        ///         is available for AI processing, directly influencing the real-time capability of AI signals.
        ///     </description></item>
        ///     <item><description>
        ///         **Data Completeness:** Ensuring all active feeds are regularly processed minimizes gaps in the data,
        ///         leading to more comprehensive AI analysis.
        ///     </description></item>
        ///     <item><description>
        ///         **Operational Monitoring:** Logs generated by this method (start/end times, number of sources found/processed,
        ///         concurrency levels) are crucial for monitoring the health and throughput of the data pipeline.
        ///     </description></item>
        ///     <item><description>
        ///         **Resource Management:** The `MaxConcurrentFeedFetches` parameter is a critical tuning point;
        ///         AI/ML Ops teams can analyze system resource utilization and feed processing times to optimize this concurrency level.
        ///     </description></item>
        /// </list>
        /// </remarks>
        [JobDisplayName("Fetch All Active RSS Feeds - Coordinator")] // Display name for Hangfire dashboard
        [AutomaticRetry(Attempts = 2)] // It's quick, a simple retry is fine.
        public async Task FetchAllActiveFeedsAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("[HANGFIRE JOB] Starting: FetchAllActiveFeedsAsync at {UtcNow}", DateTime.UtcNow);

            var activeSources = (await _rssSourceRepository.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToList(); // Level 1: ConfigureAwait(false)

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
        /// Processes a single RSS feed asynchronously, incorporating detailed logging and resilient retries
        /// at the coordinator level using a configured Polly policy. This method encapsulates the logic
        /// for fetching, parsing, and storing news items for one specific RSS source, safeguarding the
        /// `_rssReaderService.FetchAndProcessFeedAsync` call against transient errors.
        /// </summary>
        /// <remarks>
        /// This method is a crucial component within the overall RSS feed ingestion pipeline, especially
        /// for ensuring data freshness for downstream AI analysis. Its robust error handling
        /// (logging and not re-throwing exceptions after retries) allows the `FetchAllActiveFeedsAsync`
        /// coordinator job to continue processing other feeds even if a single feed encounters persistent issues.
        /// <br/><br/>
        /// **Role for AI Analysis:**
        /// <list type="bullet">
        ///     <item><description>
        ///         **Data Reliability:** By ensuring individual feed fetches are resilient, this method directly
        ///         contributes to the continuous flow of data for AI models, reducing data gaps.
        ///     </description></item>
        ///     <item><description>
        ///         **Operational Insight:** The detailed logging (using `pollyContext` and logging scopes) provides
        ///         granular visibility into the success rate, retry attempts, and specific error types for each
        ///         individual RSS source. This data is invaluable for AI/ML Ops to monitor feed health, diagnose
        ///         problematic sources (e.g., frequently failing feeds), and inform adaptive scheduling or source
        ///         prioritization based on AI model needs.
        ///     </description></item>
        ///     <item><description>
        ///         **Proactive Issue Detection:** Persistent errors for a specific feed, even if not causing the
        ///         main job to fail, are logged critically here, enabling monitoring systems to alert on data
        ///         ingestion problems for specific AI data streams.
        ///     </description></item>
        /// </list>
        /// </remarks>
        /// <param name="source">The <see cref="RssSource"/> object representing the specific RSS feed to be processed (containing URL, ID, and name).</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to monitor for cancellation requests. This token is propagated from the parallel processing loop and allows for graceful termination of the current feed's fetch operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that represents the asynchronous processing of the single RSS feed.
        /// The task completes when:
        /// <list type="bullet">
        ///     <item><description>The feed has been successfully fetched, processed, and its status updated (including any new news items saved and dispatched).</description></item>
        ///     <item><description>The operation for this feed was explicitly cancelled by the <paramref name="cancellationToken"/>.</description></item>
        ///     <item><description>The feed processing encountered an error that could not be resolved by the `_coordinatorRetryPolicy` (e.g., after exhausting all retries or encountering a non-retryable exception). In this case, the error is logged, but no exception is re-thrown from this method, allowing other concurrent feed processes to continue undisturbed.</description></item>
        /// </list>
        /// </returns>
        private async Task ProcessSingleFeedWithLoggingAndRetriesAsync(RssSource source, CancellationToken cancellationToken)
        {
            // Level 2: Define specific Polly context for this individual feed for granular logging.
            var pollyContext = new Context($"RssFeedFetch_{source.Id}")
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
                    var result = await _coordinatorRetryPolicy.ExecuteAsync(async (ctx, ct) =>
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

        /// <summary>
        /// Determines if a given HTTP status code represents a "permanent" client-side error.
        /// In the context of our AI analysis program, this method is crucial for intelligently handling
        /// responses from external data sources like RSS feeds. It helps differentiate between
        /// transient issues (e.g., network glitches, server overload) that might resolve with retries,
        /// and fundamental problems (e.g., invalid requests, missing resources) that require
        /// investigation or deactivation of the data source.
        /// </summary>
        /// <param name="statusCode">The nullable <see cref="HttpStatusCode"/> to evaluate, typically received from an HTTP response when fetching an RSS feed.</param>
        /// <returns>
        /// <c>true</c> if the <paramref name="statusCode"/> is identified as one of the following permanent client-side error codes:
        /// <list type="bullet">
        ///     <item><description><see cref="HttpStatusCode.BadRequest"/> (400): The server cannot or will not process the request due to an apparent client error (e.g., malformed syntax).</description></item>
        ///     <item><description><see cref="HttpStatusCode.Unauthorized"/> (401): Authentication is required and has failed or has not yet been provided.</description></item>
        ///     <item><description><see cref="HttpStatusCode.Forbidden"/> (403): The server understood the request but refuses to authorize it.</description></item>
        ///     <item><description><see cref="HttpStatusCode.NotFound"/> (404): The requested resource could not be found on the server.</description></item>
        ///     <item><description><see cref="HttpStatusCode.MethodNotAllowed"/> (405): The request method is known by the server but is not supported by the target resource.</description></item>
        ///     <item><description><see cref="HttpStatusCode.Gone"/> (410): The target resource is no longer available at the origin server and no forwarding address is known.</description></item>
        ///     <item><description><see cref="HttpStatusCode.UnprocessableEntity"/> (422): The server understands the content type of the request entity, and the syntax of the request entity is correct, but it was unable to process the contained instructions.</description></item>
        /// </list>
        /// <c>false</c> otherwise. This includes informational (1xx), success (2xx), redirection (3xx), and
        /// server error (5xx) status codes, as well as other client error codes not explicitly listed here,
        /// which are generally considered transient or require different handling (e.g., retries for 5xx errors).
        /// </returns>
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