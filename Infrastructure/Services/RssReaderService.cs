// File: Infrastructure/Services/RssReaderService.cs
// Version: 2.0 (Hyper-Verbose Enterprise Edition)
// Last Updated: [Current Date]
// Description: An extremely detailed, robust, and resilient implementation for fetching,
//              processing, and dispatching RSS feed news items. This version prioritizes
//              diagnostics, configurability, and maintainability.

#region Usings

// --- Standard .NET Framework Namespaces ---
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.ServiceModel.Syndication;
using System.Text;
using System.Xml;

// --- Third-party Libraries ---
using AutoMapper;
using Dapper;
using Hangfire;
using HtmlAgilityPack;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

// --- Application-Specific Namespaces ---
using Application.Common.Interfaces;
using Application.DTOs.News;
using Application.Interfaces;
using Domain.Entities;
using Shared.Extensions;
using Shared.Results;

#endregion

namespace Infrastructure.Services
{
    #region Service-Specific Configuration Settings Class

    /// <summary>
    /// Defines the configuration settings for the <see cref="RssReaderService"/>.
    /// This class is designed to be populated from application configuration (e.g., appsettings.json)
    /// and injected via the <see cref="IOptions{TOptions}"/> pattern.
    /// </summary>
    /// <summary>
    /// Represents the configurable settings for the <see cref="RssReaderService"/>.
    /// These settings control various aspects of RSS feed fetching and processing,
    /// including network timeouts, retry behaviors for HTTP and database operations,
    /// and thresholds for automatic RSS source deactivation.
    /// </summary>
    public class RssReaderServiceSettings
    {
        /// <summary>
        /// The default section name under which these settings are expected to be found
        /// in the application's configuration file (e.g., appsettings.json).
        /// </summary>
        public const string ConfigurationSectionName = "RssReaderService";

        /// <summary>
        /// Gets or sets the timeout in seconds for an individual HTTP GET request made to an RSS feed endpoint.
        /// This timeout applies to the entire request, including connection, sending, and receiving headers/content.
        /// </summary>
        /// <value>
        /// The timeout duration in seconds. Defaults to 60 seconds if not specified in configuration.
        /// </value>
        public int HttpClientTimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// Gets or sets the number of times to retry a failed HTTP request to an RSS feed.
        /// This applies to transient errors (e.g., network issues, server-side 5xx errors, 429 Too Many Requests).
        /// </summary>
        /// <value>
        /// The number of retry attempts. Defaults to 3 retries if not specified in configuration.
        /// </value>
        public int HttpRetryCount { get; set; } = 3;

        /// <summary>
        /// Gets or sets the number of times to retry a failed database operation.
        /// This applies to transient database errors (e.g., temporary connection loss, deadlocks).
        /// </summary>
        /// <value>
        /// The number of retry attempts. Defaults to 3 retries if not specified in configuration.
        /// </value>
        public int DbRetryCount { get; set; } = 3;

        /// <summary>
        /// Gets or sets the maximum number of consecutive fetch errors that an RSS source can accumulate
        /// before it is automatically marked as inactive in the database. Inactive sources will no longer
        /// be regularly fetched by the RSS reader.
        /// </summary>
        /// <value>
        /// The maximum error count. Defaults to 10 errors if not specified in configuration.
        /// </value>
        public int MaxFetchErrorsToDeactivate { get; set; } = 10;

        /// <summary>
        /// Gets or sets the default User-Agent string that will be sent with every HTTP request to RSS feed URLs.
        /// It is recommended to use a polite and descriptive User-Agent to identify the service to feed providers.
        /// </summary>
        /// <value>
        /// A string representing the User-Agent. Defaults to "ForexSignalBot/1.0 (RSSFetcher; Compatible; +https://yourbotdomain.com/contact)"
        /// if not specified in configuration.
        /// </value>
        public string UserAgent { get; set; } = "ForexSignalBot/1.0 (RSSFetcher; Compatible; +https://yourbotdomain.com/contact)";
    }

    #endregion

    /// <summary>
    /// Provides a highly robust and resilient implementation for fetching, processing, storing, and dispatching RSS feed data.
    /// This service is designed with enterprise-grade diagnostics and maintainability in mind, featuring:
    /// - Comprehensive resilience using Polly for both HTTP and database operations.
    /// - Extremely detailed and structured logging for complete traceability of every fetch cycle.
    /// - Granular refactoring into single-responsibility methods to enhance clarity and testability.
    /// - Strict adherence to database schemas and asynchronous programming best practices.
    /// - Business logic to exclusively dispatch notifications for news items that contain an image.
    /// </summary>
    /// <summary>
    /// Provides a comprehensive service for fetching, parsing, processing, and storing news items from RSS feeds.
    /// This class orchestrates the entire RSS ingestion pipeline, including:
    /// <list type="bullet">
    ///     <item><description>Making resilient HTTP requests to RSS feed URLs (with retries and timeouts).</description></item>
    ///     <item><description>Parsing XML feed content into structured syndication items.</description></item>
    ///     <item><description>Deduplicating news items against existing records and within the current fetch batch.</description></item>
    ///     <item><description>Cleaning and extracting relevant data (text, images) from feed entries.</description></item>
    ///     <item><description>Persisting new news items to the database within atomic transactions.</description></item>
    ///     <item><description>Orchestrating the dispatch of notifications for newly processed items to a background job system (e.g., Hangfire).</description></item>
    ///     <item><description>Managing the operational status of RSS sources (e.g., tracking errors, deactivating problematic feeds).</description></item>
    /// </list>
    /// The service is designed for resilience, utilizing Polly for transient fault handling in both HTTP and database operations,
    /// and structured logging for traceability and error diagnosis.
    /// </summary>
    public class RssReaderService : IRssReaderService
    {

        /// <summary>
        /// The named client identifier used to retrieve pre-configured <see cref="HttpClient"/> instances
        /// from the <see cref="IHttpClientFactory"/> for RSS feed requests.
        /// </summary>
        public const string HttpClientNamedClient = "RssFeedClient";

        #region Service Dependencies and Configuration Fields

        /// <summary>
        /// Factory for creating configured <see cref="HttpClient"/> instances, ensuring proper pooling and lifetime management.
        /// </summary>
        private readonly IHttpClientFactory _httpClientFactory;

        /// <summary>
        /// AutoMapper instance for mapping between domain entities (like <see cref="NewsItem"/>) and Data Transfer Objects (DTOs)
        /// (like <see cref="NewsItemDto"/>) for various operations, including returning results.
        /// </summary>
        private readonly IMapper _mapper;

        /// <summary>
        /// Logger for capturing detailed diagnostic information, operational events, and errors within the service.
        /// </summary>
        private readonly ILogger<RssReaderService> _logger;

        /// <summary>
        /// Hangfire client used to enqueue background jobs for asynchronous notification dispatch of processed news items.
        /// </summary>
        private readonly IBackgroundJobClient _backgroundJobClient;

        /// <summary>
        /// The database connection string used for all Dapper operations to interact with the underlying data store.
        /// </summary>
        private readonly string _connectionString;

        /// <summary>
        /// The configured settings specific to the <see cref="RssReaderService"/> (e.g., timeouts, error thresholds),
        /// injected via `IOptions` for external configuration.
        /// </summary>
        private readonly RssReaderServiceSettings _settings;

        /// <summary>
        /// Polly policy for resiliently handling transient HTTP errors (e.g., 429 Too Many Requests, network errors, timeouts)
        /// when fetching RSS feeds, implementing retry logic with exponential backoff.
        /// </summary>
        private readonly AsyncRetryPolicy<HttpResponseMessage> _httpRetryPolicy;

        /// <summary>
        /// Polly policy for resiliently handling transient database errors (e.g., connection issues, deadlocks, temporary unavailability)
        /// during data access operations, implementing retry logic.
        /// </summary>
        private readonly AsyncRetryPolicy _dbRetryPolicy;

        #endregion

        #region Database Column Length Constants

        /// <summary>
        /// The maximum allowed length for a news item's title in the database, matching the 'NewsItems.Title' column schema.
        /// Used for truncation before persistence.
        /// </summary>
        private const int NewsTitleMaxLenDb = 500;

        /// <summary>
        /// The maximum allowed length for a news item's unique identifier originating from its RSS source,
        /// matching the 'NewsItems.SourceItemId' database column schema. Used for truncation.
        /// </summary>
        private const int NewsSourceItemIdMaxLenDb = 500;

        /// <summary>
        /// The maximum allowed length for the name of the RSS source, matching the 'NewsItems.SourceName' database column schema.
        /// Used for truncation.
        /// </summary>
        private const int NewsSourceNameMaxLenDb = 150;

        /// <summary>
        /// The maximum allowed length for a news item's primary link (URL), matching the 'NewsItems.Link' database column schema.
        /// Used for truncation.
        /// </summary>
        private const int NewsLinkMaxLenDb = 2083;

        #endregion

        #region Private Nested Types for Internal State Management

        /// <summary>
        /// Enumerates the possible high-level categories of errors that can occur during the RSS fetch pipeline.
        /// This enumeration is crucial for structured error handling, targeted logging, and intelligently determining
        /// whether an <see cref="RssSource"/> should be deactivated or its error count merely incremented.
        /// </summary>
        private enum RssFetchErrorType
        {
            /// <summary>
            /// Indicates no error, implying a successful or "not modified" outcome.
            /// </summary>
            None,
            /// <summary>
            /// Represents a transient HTTP error (e.g., 429 Too Many Requests, 5xx server error, network timeouts)
            /// where a retry might succeed.
            /// </summary>
            TransientHttp,
            /// <summary>
            /// Represents a permanent HTTP error (e.g., 400 Bad Request, 404 Not Found, 403 Forbidden)
            /// indicating a fundamental problem that won't resolve on retry.
            /// </summary>
            PermanentHttp,
            /// <summary>
            /// Indicates an error during the parsing of the RSS feed's XML content, suggesting malformed data.
            /// </summary>
            XmlParsing,
            /// <summary>
            /// Represents an error during database operations (e.g., saving news items, updating source status).
            /// </summary>
            Database,
            /// <summary>
            /// Indicates a general error during the content processing phase (e.g., unhandled issues during HTML cleaning, image extraction).
            /// </summary>
            ContentProcessing,
            /// <summary>
            /// The operation was explicitly cancelled by an external signal.
            /// </summary>
            Cancellation,
            /// <summary>
            /// A generic category for any unexpected or unhandled exception not covered by more specific types.
            /// </summary>
            Unexpected
        }

        /// <summary>
        /// A record to encapsulate the complete, immutable result of a single RSS feed fetch cycle.
        /// This pattern centralizes all possible outcomes (success or various types of failure) and
        /// associated metadata (such as newly dispatched news items, HTTP caching headers, and detailed error information)
        /// into a single, cohesive, and easy-to-pass object throughout the RSS processing pipeline.
        /// </summary>
        /// <param name="IsSuccess">A boolean indicating whether the fetch operation completed successfully (<c>true</c>) or failed (<c>false</c>).</param>
        /// <param name="DispatchedNewsItems">An enumerable collection of <see cref="NewsItemDto"/> objects representing the news items that were successfully processed, saved, and subsequently enqueued for notification dispatch during this fetch cycle. This collection will be empty if <paramref name="IsSuccess"/> is <c>false</c> or if no eligible items were found/dispatched.</param>
        /// <param name="ETag">The ETag (Entity Tag) value retrieved from the HTTP response headers, used for conditional GET requests in subsequent fetches. Can be <c>null</c>.</param>
        /// <param name="LastModifiedHeader">The Last-Modified header value retrieved from the HTTP response headers, also used for conditional GET requests. Can be <c>null</c>.</param>
        /// <param name="ErrorType">An <see cref="RssFetchErrorType"/> enum value categorizing the type of error that occurred if <paramref name="IsSuccess"/> is <c>false</c>. Defaults to <see cref="RssFetchErrorType.None"/> on success.</param>
        /// <param name="ErrorMessage">A human-readable string describing the error that occurred if <paramref name="IsSuccess"/> is <c>false</c>. Can be <c>null</c> on success.</param>
        /// <param name="Exception">The actual <see cref="Exception"/> object that was caught, providing detailed technical insights into the failure. This is typically <c>null</c> on success.</param>
        /// <returns>
        /// An instance of <see cref="RssFetchOutcome"/> representing the comprehensive result of an RSS fetch operation.
        /// </returns>
        private record RssFetchOutcome(
            bool IsSuccess,
            IEnumerable<NewsItemDto> DispatchedNewsItems,
            string? ETag,
            string? LastModifiedHeader,
            RssFetchErrorType ErrorType,
            string? ErrorMessage,
            Exception? Exception = null)
        {
            /// <summary>
            /// Creates a standard success outcome for an RSS fetch operation.
            /// </summary>
            /// <param name="dispatchedNewsItems">The collection of <see cref="NewsItemDto"/> that were successfully processed and enqueued for dispatch.</param>
            /// <param name="etag">The ETag from the successful HTTP response.</param>
            /// <param name="lastModified">The Last-Modified header from the successful HTTP response.</param>
            /// <returns>A new <see cref="RssFetchOutcome"/> instance configured for a successful result.</returns>
            public static RssFetchOutcome Success(IEnumerable<NewsItemDto> dispatchedNewsItems, string? etag, string? lastModified)
                => new(true, dispatchedNewsItems, etag, lastModified, RssFetchErrorType.None, null);

            /// <summary>
            /// Creates a standard failure outcome for an RSS fetch operation.
            /// </summary>
            /// <param name="errorType">The specific type of error that occurred.</param>
            /// <param name="errorMessage">A descriptive message for the error.</param>
            /// <param name="ex">The optional <see cref="Exception"/> object that caused the failure.</param>
            /// <param name="etag">The ETag that was available (or attempted) during the failed HTTP response.</param>
            /// <param name="lastModified">The Last-Modified header that was available (or attempted) during the failed HTTP response.</param>
            /// <returns>A new <see cref="RssFetchOutcome"/> instance configured for a failed result, with an empty <see cref="DispatchedNewsItems"/> collection.</returns>
            public static RssFetchOutcome Failure(RssFetchErrorType errorType, string errorMessage, Exception? ex = null, string? etag = null, string? lastModified = null)
                => new(false, Enumerable.Empty<NewsItemDto>(), etag, lastModified, errorType, errorMessage, ex);
        }

        /// <summary>
        /// A lightweight, immutable record designed to encapsulate all necessary context and data
        /// required for attempting to create a <see cref="NewsItem"/> entity from a <see cref="SyndicationItem"/>.
        /// This context facilitates various validation and deduplication checks during the news item creation process,
        /// ensuring that only unique and valid items are considered for persistence.
        /// </summary>
        /// <param name="SyndicationItem">The specific <see cref="SyndicationItem"/> (raw RSS feed entry) currently being processed.</param>
        /// <param name="RssSource">The <see cref="RssSource"/> from which the <see cref="SyndicationItem"/> originated, providing context like its ID, name, and default category.</param>
        /// <param name="ExistingSourceItemIds">A <see cref="HashSet{T}"/> of `SourceItemId` strings that are already known to exist in the database for the given <see cref="RssSource"/>. This is used for database-level deduplication.</param>
        /// <param name="ProcessedInThisBatch">A <see cref="HashSet{T}"/> of `SourceItemId` strings that have already been successfully processed (or attempted) within the *current* batch of syndication items being read from the feed. This is used for in-memory, intra-batch deduplication.</param>
        /// <returns>
        /// An immutable instance of <see cref="NewsItemCreationContext"/> populated with the provided contextual data.
        /// This record is typically used as an input parameter for methods that attempt to transform <see cref="SyndicationItem"/>s into <see cref="NewsItem"/>s.
        /// </returns>
        private record NewsItemCreationContext(
            SyndicationItem SyndicationItem,
            RssSource RssSource,
            HashSet<string> ExistingSourceItemIds,
            HashSet<string> ProcessedInThisBatch);

        #endregion

        #region Constructor and Dependency Injection

        /// <summary>
        /// Initializes a new instance of the <see cref="RssReaderService"/>, injecting all required dependencies
        /// and configuring resilience policies based on application settings.
        /// </summary>
        /// <param name="httpClientFactory">Factory for creating HttpClient instances.</param>
        /// <param name="configuration">Application configuration to retrieve the connection string.</param>
        /// <param name="settingsOptions">Strongly-typed configuration settings for this service.</param>
        /// <param name="mapper">AutoMapper instance for object-to-object mapping.</param>
        /// <param name="logger">Logger for capturing detailed diagnostic information.</param>
        /// <param name="backgroundJobClient">Hangfire client to enqueue background processing jobs.</param>
        /// <exception cref="ArgumentNullException">Thrown if any injected dependency is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the required database connection string is not found.</exception>
        public RssReaderService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            IOptions<RssReaderServiceSettings> settingsOptions,
            IMapper mapper,
            ILogger<RssReaderService> logger,
            IBackgroundJobClient backgroundJobClient)
        {
            // --- Dependency Validation ---
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            _backgroundJobClient = backgroundJobClient ?? throw new ArgumentNullException(nameof(backgroundJobClient));
            _settings = settingsOptions?.Value ?? throw new ArgumentNullException(nameof(settingsOptions));

            _connectionString = configuration.GetConnectionString("DefaultConnection")
                                ?? throw new InvalidOperationException("The 'DefaultConnection' connection string was not found in the application configuration.");

            _logger.LogInformation("Initializing RssReaderService with UserAgent: {UserAgent}", _settings.UserAgent);

            // --- Polly Policy Configuration ---

            // Fix for CS1929: Ensure the correct Polly namespace is used and the WaitAndRetryAsync method is properly invoked.  
            _httpRetryPolicy = Policy<HttpResponseMessage>
               .Handle<HttpRequestException>()
               .OrResult(response =>
                   response.StatusCode >= HttpStatusCode.InternalServerError ||
                   response.StatusCode == HttpStatusCode.RequestTimeout ||
                   response.StatusCode == HttpStatusCode.TooManyRequests)
               .WaitAndRetryAsync(
                   retryCount: _settings.HttpRetryCount,
                   sleepDurationProvider: retryAttempt =>
                   {
                       var delay = TimeSpan.FromSeconds(Math.Pow(2, retryAttempt));
                       _logger.LogWarning(
                           "Polly HTTP Retry: Attempt {RetryAttempt} of {MaxRetries} failed. Waiting {Delay} before next retry.",
                           retryAttempt, _settings.HttpRetryCount, delay);
                       return delay;
                   });


            _dbRetryPolicy = Policy
                .Handle<DbException>(ex =>
                {
                    if (ex is SqlException sqlEx && (sqlEx.Number == 2627 || sqlEx.Number == 2601))
                    {
                        _logger.LogWarning(sqlEx,
                            "Polly DB Policy: Encountered non-transient SQL error (Unique Constraint/PK Violation, Error {ErrorNumber}). This indicates a data issue, not a transient fault. Will NOT retry.",
                            sqlEx.Number);
                        return false;
                    }
                    _logger.LogWarning(ex, "Polly DB Policy: Encountered a transient database exception. Will retry.");
                    return true;
                })
                .WaitAndRetryAsync(
                    retryCount: _settings.DbRetryCount,
                    sleepDurationProvider: retryAttempt =>
                    {
                        var delay = TimeSpan.FromSeconds(Math.Pow(2, retryAttempt));
                        _logger.LogWarning("Polly DB Retry: Database operation failed on attempt {RetryAttempt} of {MaxRetries}. Waiting {Delay} before next retry.",
                            retryAttempt, _settings.DbRetryCount, delay);
                        return delay;
                    });

            _logger.LogInformation("RssReaderService initialized successfully.");
        }

        /// <summary>
        /// Creates a new, unopened <see cref="SqlConnection"/> instance using the connection string from configuration.
        /// </summary>
        /// <returns>A new <see cref="SqlConnection"/> object.</returns>
        /// <remarks>
        /// This is a simple factory method. Connection management, opening, and closing are handled
        /// within the methods that use it, leveraging .NET's built-in connection pooling.
        /// </remarks>
        private SqlConnection CreateConnection() => new(_connectionString);

        #endregion

        #region Main Service Logic: FetchAndProcessFeedAsync

        /// <summary>
        /// Orchestrates the comprehensive, asynchronous process of fetching, processing, and storing news items from a single RSS feed source.
        /// This method represents the core pipeline for ingesting RSS content into our system. It ensures data integrity and operational
        /// resilience by handling various scenarios, from successful content retrieval to transient network issues and permanent feed errors.
        /// The overall goal is to efficiently identify and store new news items and prepare them for user notification, while
        /// maintaining an accurate status of each RSS source.
        /// </summary>
        /// <param name="rssSource">The <see cref="RssSource"/> object detailing the specific RSS feed to be fetched. This object's state
        /// (e.g., `LastFetchAttemptAt`, `FetchErrorCount`, `ETag`, `LastModifiedHeader`, `IsActive`) will be updated in the database
        /// based on the outcome of this operation.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to allow for external cancellation of the entire fetch operation
        /// at various stages, ensuring graceful termination.</param>
        /// <returns>
        /// A <see cref="Task"/> that represents the asynchronous fetch and process operation. The task yields a <see cref="Result{IEnumerable{NewsItemDto}}"/>,
        /// which encapsulates the final outcome:
        /// <list type="bullet">
        ///     <item><description>
        ///         On Success (`Result.Success`): Indicates that the feed was successfully fetched and processed. The returned value is an
        ///         <see cref="IEnumerable{NewsItemDto}"/> representing all new news items that were successfully extracted,
        ///         persisted to the database, and subsequently enqueued for notification dispatch. This enumerable may be empty if
        ///         no new items were found in the feed, or if items were found but did not meet dispatch criteria (e.g., no image for image-only dispatch).
        ///         The <paramref name="rssSource"/>'s status in the database will reflect a successful fetch (error count reset, last successful fetch time updated).
        ///     </description></item>
        ///     <item><description>
        ///         On Failure (`Result.Failure`): Indicates that the operation encountered an error preventing a full successful run.
        ///         The result contains one or more error messages detailing the cause (e.g., invalid RSS source URL,
        ///         HTTP request failure, XML parsing error, or critical database issues).
        ///         The <paramref name="rssSource"/>'s status will be updated to reflect the error (error count incremented,
        ///         and potentially deactivated if error threshold is reached).
        ///     </description></item>
        /// </list>
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown if the provided <paramref name="rssSource"/> object is <c>null</c>, indicating a programming error.
        /// </exception>
        public async Task<Result<IEnumerable<NewsItemDto>>> FetchAndProcessFeedAsync(RssSource rssSource, CancellationToken cancellationToken = default)
        {
            const string methodName = nameof(FetchAndProcessFeedAsync);
            _logger.LogTrace("Entering {MethodName}", methodName);

            // --- 1. Initial Validation ---
            if (rssSource == null)
            {
                _logger.LogError("{MethodName} was called with a null RssSource object, which is a programming error.", methodName);
                throw new ArgumentNullException(nameof(rssSource));
            }
            if (string.IsNullOrWhiteSpace(rssSource.Url))
            {
                _logger.LogWarning("Validation failed for RssSource '{SourceName}' (ID: {RssSourceId}): URL is null or empty. Skipping fetch.", rssSource.SourceName, rssSource.Id);
                return Result<IEnumerable<NewsItemDto>>.Failure($"RSS source '{rssSource.SourceName}' has an invalid URL.");
            }

            // --- 2. Setup Logging Scope and Initial State ---
            string correlationId = $"RSSFetch_{rssSource.Id}_{Guid.NewGuid():N}";
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["CorrelationId"] = correlationId,
                ["RssSourceId"] = rssSource.Id,
                ["RssSourceName"] = rssSource.SourceName
            });

            _logger.LogInformation("--- Starting new RSS fetch cycle for {SourceName} ---", rssSource.SourceName);
            rssSource.LastFetchAttemptAt = DateTime.UtcNow; // Record the attempt time regardless of outcome.

            // --- 3. Main Execution Pipeline with Exception Handling ---
            HttpResponseMessage? httpResponse = null;
            RssFetchOutcome outcome;
            try
            {
                _logger.LogDebug("Entering HTTP request and processing phase.");
                httpResponse = await ExecuteHttpRequestAsync(rssSource, correlationId, cancellationToken);
                outcome = await ProcessHttpResponseAsync(httpResponse, rssSource, cancellationToken);
                _logger.LogDebug("Exited HTTP request and processing phase.");
            }
            // Specific catch for OperationCanceledException, as it's often a controlled shutdown or timeout.
            catch (OperationCanceledException oce)
            {
                _logger.LogInformation(oce, "RSS fetch operation for '{SourceName}' (ID: {RssSourceId}) was cancelled or timed out. This is a handled outcome.", rssSource.SourceName, rssSource.Id);
                outcome = HandleFetchException(oce, httpResponse); // Let HandleFetchException classify it as Cancellation or TransientHttp (for timeouts).
            }
            // General catch-all for any other unexpected, unclassified errors.
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "A critical, unhandled exception was caught in the main pipeline. The operation will be marked as a failure.");
                outcome = HandleFetchException(ex, httpResponse); // Determine specific outcome based on exception type.
            }
            finally
            {
                httpResponse?.Dispose(); // Ensure the HttpResponseMessage is always disposed to release resources.
                _logger.LogTrace("HTTP response object has been disposed.");
            }

            // --- 4. Final Status Update and Return ---
            _logger.LogInformation("Fetch cycle has concluded. Updating final status of RssSource in the database.");
            var finalResult = await UpdateRssSourceStatusAfterFetchOutcomeAsync(rssSource, outcome, cancellationToken);

            // New logic: If the fetch failed specifically due to a database error, delete the RSS source.
            if (!finalResult.Succeeded && outcome.ErrorType == RssFetchErrorType.Database)
            {
                _logger.LogError("RSS Source '{SourceName}' (ID: {RssSourceId}) encountered a database error during fetch/processing. Attempting to delete this source to prevent further issues.", rssSource.SourceName, rssSource.Id);
                try
                {
                    await DeleteRssSourceAsync(rssSource.Id, cancellationToken);
                    _logger.LogInformation("RSS Source '{SourceName}' (ID: {RssSourceId}) successfully deleted due to database error during fetch cycle.", rssSource.SourceName, rssSource.Id);
                }
                catch (Exception deleteEx)
                {
                    _logger.LogCritical(deleteEx, "CRITICAL FAILURE: Could not delete RSS Source '{SourceName}' (ID: {RssSourceId}) after a database error during fetch cycle. Its state may be problematic.", rssSource.SourceName, rssSource.Id);
                    // Re-throw the delete exception if it's critical, or decide to suppress based on desired behavior.
                    // For this case, we re-throw to make sure the outer Hangfire job fails for inspection.
                    throw new RepositoryException($"Failed to delete RSS source '{rssSource.SourceName}' after database error.", deleteEx);
                }
            }

            _logger.LogTrace("Exiting {MethodName}", methodName);
            return finalResult;
        }
        /// <summary>
        /// Deletes an RSS source from the database by its ID. This is typically invoked
        /// when a specific RSS feed consistently causes critical, unrecoverable database errors
        /// during its processing cycle, indicating a "poison pill" source that needs removal.
        /// The operation is designed to be resilient to transient database issues.
        /// </summary>
        /// <param name="rssSourceId">The unique identifier (GUID) of the RSS source to be deleted.</param>
        /// <param name="cancellationToken">A CancellationToken to monitor for cancellation requests. If cancellation is requested, the database operation and any pending retries will attempt to terminate gracefully.</param>
        /// <returns>
        /// A Task representing the asynchronous delete operation. The task completes when the source is deleted or an error occurs.
        /// </returns>
        /// <exception cref="RepositoryException">
        /// Thrown if the database operation fails after exhausting all configured retry attempts,
        /// wrapping the original database exception for consistent error handling upstream.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// Thrown if the <paramref name="cancellationToken"/> is signaled during the database operation or while retrying.
        /// </exception>
        /// <remarks>
        /// For AI analysis: Removing problematic RSS sources is critical for maintaining data pipeline health.
        /// This method ensures that consistently failing sources (which might provide malformed data or cause
        /// unexpected database behavior) are purged, preventing them from:
        /// <list type="bullet">
        ///     <item><description>Consuming unnecessary processing resources.</description></item>
        ///     <item><description>Introducing corrupt or problematic data into the AI training/inference datasets.</description></item>
        ///     <item><description>Masking other underlying issues by continuously generating errors.</description></item>
        /// </list>
        /// The logging within this method is essential for MLOps to audit when and why sources are being removed.
        /// </remarks>
        private async Task DeleteRssSourceAsync(Guid rssSourceId, CancellationToken cancellationToken)
        {
            const string methodName = nameof(DeleteRssSourceAsync);
            _logger.LogTrace("Entering {MethodName} for RssSourceId: {RssSourceId}", methodName, rssSourceId);

            try
            {
                await _dbRetryPolicy.ExecuteAsync(async (ct) =>
                {
                    await using var connection = CreateConnection();
                    await connection.OpenAsync(ct);
                    var sql = "DELETE FROM RssSources WHERE Id = @Id;";
                    var rowsAffected = await connection.ExecuteAsync(
                        new CommandDefinition(sql, new { Id = rssSourceId }, commandTimeout: 120, cancellationToken: ct)
                    );
                    if (rowsAffected > 0)
                    {
                        _logger.LogInformation("RssSource with ID {RssSourceId} successfully deleted from database. {RowsAffected} rows affected.", rssSourceId, rowsAffected);
                    }
                    else
                    {
                        _logger.LogWarning("Attempted to delete RssSource with ID {RssSourceId}, but no rows were affected. It may not exist.", rssSourceId);
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException oce)
            {
                _logger.LogWarning(oce, "DeleteRssSourceAsync for RssSourceId {RssSourceId} was cancelled.", rssSourceId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting RssSource with ID {RssSourceId} from the database after retries. Original exception: {ErrorMessage}", rssSourceId, ex.Message);
                throw new RepositoryException($"Failed to delete RssSource '{rssSourceId}' from database.", ex);
            }

            _logger.LogTrace("Exiting {MethodName}", methodName);
        }

        /// <summary>
        /// Orchestrates the processing of a received HTTP response from an RSS feed.
        /// This method analyzes the HTTP status code and response headers to determine the next steps:
        /// handling a "Not Modified" response, processing successful feed content, or logging and classifying HTTP errors.
        /// </summary>
        /// <param name="httpResponse">The <see cref="HttpResponseMessage"/> received from the RSS feed server.</param>
        /// <param name="rssSource">The <see cref="RssSource"/> object associated with this fetch operation, used for updating its state (e.g., ETag, last modified).</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to observe for cancellation requests during content parsing and processing.</param>
        /// <returns>
        /// A <see cref="Task{RssFetchOutcome}"/> representing the asynchronous operation. The task completes with:
        /// <list type="bullet">
        ///     <item><description><see cref="RssFetchOutcome.NotModified"/> if the feed content has not changed since the last fetch (HTTP 304).</description></item>
        ///     <item><description><see cref="RssFetchOutcome.Failure"/> (with <see cref="RssFetchErrorType.PermanentHttp"/> or <see cref="RssFetchErrorType.TransientHttp"/>) if an unsuccessful HTTP status code is received, categorizing the error type.</description></item>
        ///     <item><description><see cref="RssFetchOutcome.Success"/> if the feed content was successfully retrieved, parsed, new items filtered, saved, and dispatched.</description></item>
        /// </list>
        /// </returns>
        private async Task<RssFetchOutcome> ProcessHttpResponseAsync(HttpResponseMessage httpResponse, RssSource rssSource, CancellationToken cancellationToken)
        {
            const string methodName = nameof(ProcessHttpResponseAsync);
            _logger.LogTrace("Entering {MethodName} for status code {StatusCode}", methodName, httpResponse.StatusCode);

            if (httpResponse.StatusCode == HttpStatusCode.NotModified)
            {
                return HandleNotModifiedResponse(rssSource, httpResponse);
            }

            if (!httpResponse.IsSuccessStatusCode)
            {
                string httpErrorMsg = $"HTTP request failed with status code {httpResponse.StatusCode} ({httpResponse.ReasonPhrase}).";
                _logger.LogWarning(httpErrorMsg);
                var errorType = IsPermanentHttpError(httpResponse.StatusCode) ? RssFetchErrorType.PermanentHttp : RssFetchErrorType.TransientHttp;
                return RssFetchOutcome.Failure(errorType, httpErrorMsg, new HttpRequestException(httpErrorMsg, null, httpResponse.StatusCode),
                    CleanETag(httpResponse.Headers.ETag?.Tag), GetLastModifiedFromHeaders(httpResponse.Headers));
            }

            _logger.LogInformation("HTTP 2xx response received. Proceeding to parse feed content.");
            SyndicationFeed feed = await ParseFeedContentAsync(httpResponse, cancellationToken);

            _logger.LogInformation("Feed parsed. Proceeding to filter and create news item entities from {ItemCount} syndicated items.", feed.Items.Count());
            List<NewsItem> newNewsEntities = await FilterAndCreateNewsEntitiesAsync(feed.Items, rssSource, cancellationToken);

            _logger.LogInformation("Filtering complete. Proceeding to save {NewItemCount} new items and dispatch notifications.", newNewsEntities.Count);
            var outcome = await SaveAndDispatchAsync(rssSource, newNewsEntities, httpResponse, cancellationToken);

            _logger.LogTrace("Exiting {MethodName}", methodName);
            return outcome;
        }



        /// <summary>
        /// Executes an asynchronous HTTP GET request to fetch the RSS feed from the specified source.
        /// This method is engineered for high performance, security, and resilience, serving as a robust
        /// component in our AI analysis program's data ingestion pipeline.
        /// <br/><br/>
        /// It incorporates several best practices:
        /// <list type="bullet">
        ///     <item><description>
        ///         **Intelligent Resilience (Powerful Shields):** Uses a Polly retry policy (`_httpRetryPolicy`)
        ///         to automatically handle transient network errors, DNS resolution problems, or server-side
        ///         issues (e.g., 5xx status codes, connection timeouts). The `HttpRequestMessage` is
        ///         re-created for each retry to prevent "request already sent" errors.
        ///     </description></item>
        ///     <item><description>
        ///         **Optimized Performance (Faster & Smarter):** Leverages <see cref="IHttpClientFactory"/>
        ///         for efficient client pooling, reduces network overhead with conditional GET headers
        ///         (`If-None-Match`, `If-Modified-Since`), and utilizes `HttpCompletionOption.ResponseHeadersRead`
        ///         to minimize latency by processing headers before the full response body is downloaded.
        ///     </description></item>
        ///     <item><description>
        ///         **Robust Cancellation & Timeouts:** Enforces a request-specific timeout and integrates
        ///         external cancellation tokens, ensuring that hanging requests are prevented and operations
        ///         are responsive to system shutdowns.
        ///     </description></item>
        ///     <item><description>
        ///         **Enhanced Security:** Sets a polite User-Agent header for proper client identification
        ///         and assumes HTTPS for secure communication, preventing data tampering or eavesdropping.
        ///     </description></item>
        ///     <item><description>
        ///         **Scalability for Large Requests (Responses):** While sending a GET request is typically small,
        ///         this method is prepared to efficiently handle potentially large RSS feed responses by
        ///         reading only headers initially, and relying on subsequent streaming for content.
        ///     </description></item>
        /// </list>
        /// </summary>
        /// <param name="rssSource">The <see cref="RssSource"/> object containing the URL (assumed HTTPS for security),
        /// and current ETag/Last-Modified values for conditional GET requests, which optimize bandwidth usage.</param>
        /// <param name="correlationId">A unique identifier used for structured logging and within the Polly context for tracing
        /// the execution flow of individual feed fetches, aiding in diagnostics for AI data pipelines.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel the HTTP request and
        /// the overall operation from an external source (e.g., application shutdown, higher-level timeout).</param>
        /// <returns>
        /// A <see cref="Task{HttpResponseMessage}"/> representing the asynchronous operation. The task completes with:
        /// <list type="bullet">
        ///     <item><description>The <see cref="HttpResponseMessage"/> received from the RSS feed server upon successful completion (which could be a 200 OK, 304 Not Modified, or an error status). This response will then be analyzed by subsequent processing steps.</description></item>
        /// </list>
        /// </returns>
        /// <exception cref="HttpRequestException">
        /// Thrown (after all retries, if applicable) if the HTTP request ultimately fails due to network issues,
        /// DNS resolution problems, or other non-success HTTP status codes that the Polly policy is configured to retry but eventually gives up on.
        /// This indicates a persistent problem with accessing the RSS source.
        /// </exception>
        /// <exception cref="TaskCanceledException">
        /// Thrown if the request is cancelled either by the provided <paramref name="cancellationToken"/>
        /// or due to the internal request timeout specified by `_settings.HttpClientTimeoutSeconds` (a specific type of `OperationCanceledException`).
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// Thrown if the <paramref name="cancellationToken"/> is signalled during the operation,
        /// specifically if it's the primary cancellation source for the `linkedCts` that encompasses the entire HTTP request lifecycle.
        /// </exception>
        private async Task<HttpResponseMessage> ExecuteHttpRequestAsync(RssSource rssSource, string correlationId, CancellationToken cancellationToken)
        {
            const string methodName = nameof(ExecuteHttpRequestAsync);
            _logger.LogTrace("Entering {MethodName} for RssSourceId: {RssSourceId}, URL: {RssSourceUrl}", methodName, rssSource.Id, rssSource.Url);

            // Get an HttpClient instance from the factory. HttpClientFactory ensures proper pooling,
            // lifetime management, and applies pre-configured handlers (like logging, Polly policies if configured in Startup).
            var httpClient = _httpClientFactory.CreateClient("RssFeedClient");

            // Execute the HTTP GET request within the configured Polly retry policy.
            // The lambda passed to ExecuteAsync is re-executed on each retry, ensuring a fresh HttpRequestMessage.
            var response = await _httpRetryPolicy.ExecuteAsync(async (context, ct) =>
            {
                // CRITICAL FIX: Create a NEW HttpRequestMessage instance for EACH attempt (initial call and all retries).
                // HttpRequestMessage is designed for single use.
                using var requestMessage = new HttpRequestMessage(HttpMethod.Get, rssSource.Url);

                // Set a descriptive User-Agent header for polite identification to the RSS feed server.
                requestMessage.Headers.UserAgent.ParseAdd(_settings.UserAgent);

                // Apply conditional GET headers (If-None-Match, If-Modified-Since) to minimize bandwidth
                // by allowing the server to return HTTP 304 Not Modified if content hasn't changed.
                AddConditionalGetHeaders(requestMessage, rssSource);

                // Create a CancellationTokenSource for the specific HTTP request timeout.
                using var requestTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.HttpClientTimeoutSeconds));
                // Link all relevant cancellation tokens: Polly's internal token (`ct`), the request timeout token,
                // and the external method's cancellation token (`cancellationToken`). This ensures cancellation
                // from any source is propagated.
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, requestTimeoutCts.Token, cancellationToken);

                _logger.LogDebug("Polly Execute: Sending HTTP GET request to '{RequestUrl}' with a {Timeout}s timeout. Attempt {RetryAttempt}.",
                                 rssSource.Url, _settings.HttpClientTimeoutSeconds, context.Count);

                // Send the HTTP request. HttpCompletionOption.ResponseHeadersRead is used for performance,
                // as it returns the HttpResponseMessage as soon as headers are received, without waiting
                // for the entire response body to download. The body will be streamed later if needed.
                return await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
            },
            // Pass the Polly Context (including CorrelationId and RssSource details) for enhanced logging within Polly callbacks.
            new Context(correlationId) { ["RssSourceId"] = rssSource.Id.ToString(), ["RssSourceName"] = rssSource.SourceName },
            // Pass the external cancellation token to Polly's ExecuteAsync to allow cancellation of the entire retry sequence.
            cancellationToken).ConfigureAwait(false); // ConfigureAwait(false) is used for library methods to prevent deadlocks and optimize performance.

            _logger.LogTrace("Exiting {MethodName} for RssSourceId: {RssSourceId}", methodName, rssSource.Id);
            return response;
        }

        #endregion

        #region Feed Parsing, Filtering, and Entity Creation

        /// <summary>
        /// Parses the XML content from an HTTP response stream into a <see cref="SyndicationFeed"/> object.
        /// This method configures <see cref="XmlReaderSettings"/> for asynchronous reading, security (ignoring DTDs to prevent XXE attacks),
        /// and efficient whitespace handling. The synchronous <see cref="SyndicationFeed.Load(System.Xml.XmlReader)"/> operation
        /// is wrapped in a <see cref="Task.Run(System.Action)"/> to ensure it runs asynchronously without blocking the calling thread.
        /// </summary>
        /// <param name="response">The <see cref="HttpResponseMessage"/> containing the RSS feed's XML content.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to observe for cancellation requests during stream reading and feed parsing.</param>
        /// <returns>
        /// A <see cref="Task{TResult}"/> that represents the asynchronous parsing operation,
        /// yielding the populated <see cref="SyndicationFeed"/> object upon completion.
        /// </returns>
        /// <exception cref="System.Xml.XmlException">Thrown if the content stream is not valid XML or cannot be parsed into a syndication feed.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the <paramref name="cancellationToken"/> is cancelled during the operation.</exception>
        private async Task<SyndicationFeed> ParseFeedContentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            const string methodName = nameof(ParseFeedContentAsync);
            _logger.LogTrace("Entering {MethodName}", methodName);

            await using var feedStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var readerSettings = new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Ignore, // Security best practice: prevent XXE attacks.
                IgnoreWhitespace = true
            };

            using var xmlReader = XmlReader.Create(feedStream, readerSettings);
            var feed = await Task.Run(() => SyndicationFeed.Load(xmlReader), cancellationToken);

            _logger.LogDebug("Successfully parsed feed content. Feed Title: '{FeedTitle}'", feed.Title?.Text.Truncate(100));
            _logger.LogTrace("Exiting {MethodName}", methodName);
            return feed;
        }

        /// <summary>
        /// Processes a collection of raw <see cref="SyndicationItem"/>s obtained from an RSS feed.
        /// This method performs several crucial steps:
        /// <list type="bullet">
        ///     <item><description>Filters out items that have already been processed and stored from the same <see cref="RssSource"/> (database-level deduplication).</description></item>
        ///     <item><description>Deduplicates items within the current batch based on their source-specific identifiers (in-memory deduplication for the current fetch).</description></item>
        ///     <item><description>Maps the truly new, unique, and valid items to <see cref="NewsItem"/> entities, preparing them for persistence.</description></item>
        /// </list>
        /// Items are processed in descending order of their publication date to prioritize newer content.
        /// </summary>
        /// <param name="syndicationItems">The raw syndicated items obtained from the RSS feed, which are candidates for processing.</param>
        /// <param name="rssSource">The source of the RSS feed, providing essential context for filtering (e.g., source ID for checking existing items) and mapping.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to monitor for cancellation requests during the filtering and creation process, allowing for graceful early exit.</param>
        /// <returns>
        /// A <see cref="Task{TResult}"/> representing the asynchronous operation. Upon completion, the task resolves to a <see cref="List{NewsItem}"/>:
        /// <list type="bullet">
        ///     <item><description>An empty <see cref="List{NewsItem}"/> if the input <paramref name="syndicationItems"/> collection is empty, or if all items are found to be duplicates (either within the current batch or already in the database), or if none of the items are valid for conversion.</description></item>
        ///     <item><description>A <see cref="List{NewsItem}"/> containing only the <see cref="NewsItem"/> entities that are genuinely new, unique, and valid, ready for persistence to the database.</description></item>
        /// </list>
        /// </returns>
        /// <exception cref="OperationCanceledException">Thrown if the <paramref name="cancellationToken"/> is signalled during the operation (e.g., during the iteration through syndication items or database calls for existing IDs).</exception>
        private async Task<List<NewsItem>> FilterAndCreateNewsEntitiesAsync(IEnumerable<SyndicationItem> syndicationItems, RssSource rssSource, CancellationToken cancellationToken)
        {
            const string methodName = nameof(FilterAndCreateNewsEntitiesAsync);
            _logger.LogTrace("Entering {MethodName}", methodName);

            if (!syndicationItems.Any())
            {
                _logger.LogInformation("The parsed feed contained no syndication items to process.");
                return [];
            }

            var creationContext = new NewsItemCreationContext(
                SyndicationItem: null!, // This is a placeholder; it will be set inside the loop.
                RssSource: rssSource,
                ExistingSourceItemIds: await GetExistingSourceItemIdsAsync(rssSource.Id, cancellationToken),
                ProcessedInThisBatch: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            );

            var newNewsEntities = new List<NewsItem>();
            _logger.LogDebug("Beginning iteration through {ItemCount} fetched syndication items.", syndicationItems.Count());

            foreach (var syndicationItem in syndicationItems.OrderByDescending(i => i.PublishDate.UtcDateTime))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var contextWithItem = creationContext with { SyndicationItem = syndicationItem };
                var newsEntity = TryCreateNewsItemEntity(contextWithItem);
                if (newsEntity != null)
                {
                    newNewsEntities.Add(newsEntity);
                }
            }

            _logger.LogInformation("Finished filtering. Original items: {OriginalCount}, New unique items created: {NewCount}.", syndicationItems.Count(), newNewsEntities.Count);
            _logger.LogTrace("Exiting {MethodName}", methodName);
            return newNewsEntities;
        }

        /// <summary>
        /// Asynchronously fetches a set of existing unique source item identifiers (`SourceItemId`) from the database
        /// for a specified RSS source. This operation is fundamental for the RSS processing pipeline
        /// to prevent the re-processing and re-dispatching of news items that have already been imported.
        /// It acts as a critical deduplication step, providing a comprehensive list of known items for efficient lookups.
        /// </summary>
        /// <param name="rssSourceId">The unique identifier (<see cref="Guid"/>) of the RSS source for which to retrieve existing item IDs.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to observe for cancellation requests during the database query.</param>
        /// <returns>
        /// A <see cref="Task{TResult}"/> representing the asynchronous operation. The task yields a <see cref="HashSet{string}"/> upon completion.
        /// <list type="bullet">
        ///     <item><description>
        ///         A <see cref="HashSet{string}"/> containing all `SourceItemId` values associated with the given `rssSourceId`
        ///         that are currently stored in the `NewsItems` table. The <see cref="HashSet{string}"/> is configured for
        ///         case-insensitive comparisons (using <see cref="StringComparer.OrdinalIgnoreCase"/>) to ensure accurate
        ///         duplicate detection across various RSS feed formats.
        ///     </description></item>
        ///     <item><description>
        ///         An empty <see cref="HashSet{string}"/> if no existing items are found for the specified source in the database.
        ///     </description></item>
        /// </list>
        /// The database query is executed within a configured retry policy (`_dbRetryPolicy`) for resilience against transient database errors.
        /// </returns>
        /// <exception cref="OperationCanceledException">
        /// Thrown if the <paramref name="cancellationToken"/> is signaled during the database query or connection establishment.
        /// </exception>
        /// <exception cref="RepositoryException">
        /// Thrown if the database operation fails after exhausting all retries configured in `_dbRetryPolicy`,
        /// wrapping the underlying database exception.
        /// </exception>
        private async Task<HashSet<string>> GetExistingSourceItemIdsAsync(Guid rssSourceId, CancellationToken cancellationToken)
        {
            const string methodName = nameof(GetExistingSourceItemIdsAsync);
            _logger.LogTrace("Entering {MethodName} for RssSourceId: {RssSourceId}", methodName, rssSourceId);

            var ids = await _dbRetryPolicy.ExecuteAsync(async () =>
            {
                await using var connection = CreateConnection();
                var sql = "SELECT SourceItemId FROM NewsItems WHERE RssSourceId = @RssSourceId AND SourceItemId IS NOT NULL;";
                _logger.LogDebug("Executing SQL to fetch existing SourceItemIds.");
                return await connection.QueryAsync<string>(sql, new { RssSourceId = rssSourceId });
            });

            var idSet = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _logger.LogDebug("Fetched {Count} existing SourceItemIds from the database.", idSet.Count);
            _logger.LogTrace("Exiting {MethodName}", methodName);
            return idSet;
        }


        /// <summary>
        /// Attempts to create a single <see cref="NewsItem"/> entity from a <see cref="SyndicationItem"/> within a given creation context.
        /// This method acts as a gatekeeper, performing several crucial validation and deduplication steps:
        /// <list type="bullet">
        ///     <item><description>Determines a stable and unique `SourceItemId` for the incoming syndicated item.</description></item>
        ///     <item><description>Checks if the item (via its `SourceItemId`) is already present in the current processing batch (intra-batch deduplication).</description></item>
        ///     <item><description>Checks if the item (via its `SourceItemId`) already exists in the database for the given RSS source (database-level deduplication).</description></item>
        ///     <item><description>Extracts, cleans, and truncates relevant data (title, link, summary, content, image URL, published date) from the <see cref="SyndicationItem"/>.</description></item>
        ///     <item><description>Assigns associated RSS source and signal category information.</description></item>
        /// </list>
        /// </summary>
        /// <param name="context">A <see cref="NewsItemCreationContext"/> record containing the <see cref="SyndicationItem"/> to process,
        /// the associated <see cref="RssSource"/>, a <see cref="HashSet{T}"/> of already existing `SourceItemId`s from the database,
        /// and a <see cref="HashSet{T}"/> to track items processed within the current batch.</param>
        /// <returns>
        /// A new, fully populated <see cref="NewsItem"/> entity if all validation and deduplication checks pass,
        /// indicating that it is a unique and valid news item to be added to the system.
        /// Returns <c>null</c> if:
        /// <list type="bullet">
        ///     <item><description>A stable `SourceItemId` cannot be determined for the item (e.g., no suitable ID or link, and hash generation fails).</description></item>
        ///     <item><description>The item's `SourceItemId` is already present in the `ProcessedInThisBatch` set (duplicate within the current feed fetch).</description></item>
        ///     <item><description>The item's `SourceItemId` is found in the `ExistingSourceItemIds` set (already exists in the database).</description></item>
        ///     <item><description>Any other internal condition prevents the successful creation of a valid <see cref="NewsItem"/> (though current logic primarily covers the above).</description></item>
        /// </list>
        /// </returns>
        private NewsItem? TryCreateNewsItemEntity(NewsItemCreationContext context)
        {
            var syndicationItem = context.SyndicationItem;
            var rssSource = context.RssSource;

            string? originalLink = syndicationItem.Links.FirstOrDefault(l => l.Uri != null)?.Uri?.ToString();
            string title = syndicationItem.Title?.Text?.Trim() ?? "Untitled News Item";

            string itemSourceId = DetermineSourceItemId(syndicationItem, originalLink, title, rssSource.Id);

            if (string.IsNullOrWhiteSpace(itemSourceId))
            {
                _logger.LogWarning("Skipping item because a stable SourceItemId could not be determined. Title: '{Title}'", title.Truncate(50));
                return null;
            }

            if (!context.ProcessedInThisBatch.Add(itemSourceId))
            {
                _logger.LogTrace("Skipping duplicate item within this fetch batch. SourceItemId: {SourceItemId}", itemSourceId.Truncate(50));
                return null;
            }

            if (context.ExistingSourceItemIds.Contains(itemSourceId))
            {
                _logger.LogTrace("Skipping existing item already found in database. SourceItemId: {SourceItemId}", itemSourceId.Truncate(50));
                return null;
            }

            _logger.LogDebug("Validation passed. Creating new NewsItem entity for SourceItemId: {SourceItemId}", itemSourceId.Truncate(50));
            return new NewsItem
            {
                Id = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow,
                Title = title.Truncate(NewsTitleMaxLenDb),
                Link = (originalLink ?? itemSourceId).Truncate(NewsLinkMaxLenDb),
                Summary = CleanHtmlWithHtmlAgility(syndicationItem.Summary?.Text),
                FullContent = CleanHtmlWithHtmlAgility(syndicationItem.Content is TextSyndicationContent tc ? tc.Text : syndicationItem.Summary?.Text),
                ImageUrl = ExtractImageUrlWithHtmlAgility(syndicationItem, syndicationItem.Summary?.Text, syndicationItem.Content?.ToString()),
                PublishedDate = syndicationItem.PublishDate.UtcDateTime,
                RssSourceId = rssSource.Id,
                SourceName = rssSource.SourceName.Truncate(NewsSourceNameMaxLenDb),
                SourceItemId = itemSourceId.Truncate(NewsSourceItemIdMaxLenDb),
                IsVipOnly = false,
                AssociatedSignalCategoryId = rssSource.DefaultSignalCategoryId
            };
        }

        #endregion

        #region Database Interaction and Notification Dispatch (REWRITTEN)

        /// <summary>
        /// Orchestrates the final stages of the RSS feed processing pipeline: persisting newly identified
        /// news items to the database and then initiating the notification dispatch process for them.
        /// This method ensures data integrity by handling database save operations within a transaction
        /// and categorizes the fetch outcome based on success or specific failures (e.g., database errors).
        /// It acts as a bridge between the data ingestion and user notification phases.
        /// </summary>
        /// <param name="rssSource">The <see cref="RssSource"/> that generated these news items, used for contextual logging and outcome reporting.</param>
        /// <param name="newNewsEntitiesToSave">A <see cref="List{NewsItem}"/> containing the unique and new news entities ready for persistence and potential dispatch. This list is the output of the filtering process.</param>
        /// <param name="httpResponse">The <see cref="HttpResponseMessage"/> from the original feed fetch, used to extract cache control headers (ETag, Last-Modified) for the final outcome, regardless of success or failure in later stages.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to observe for cancellation requests during database operations and notification dispatch orchestration.</param>
        /// <returns>
        /// A <see cref="Task{RssFetchOutcome}"/> representing the asynchronous operation. The task completes with:
        /// <list type="bullet">
        ///     <item><description>
        ///         <see cref="RssFetchOutcome.Success"/>: Returned if:
        ///         <list type="bullet">
        ///             <item><description>No new unique items were found from the feed to begin with.</description></item>
        ///             <item><description>All new items were successfully saved to the database, and notifications were initiated for all *eligible* items (e.g., those meeting specific dispatch criteria like having an image). The outcome will include an <see cref="IEnumerable{NewsItemDto}"/> of items actually enqueued for notification, and the ETag/Last-Modified headers from the HTTP response.</description></item>
        ///         </list>
        ///     </description></item>
        ///     <item><description>
        ///         <see cref="RssFetchOutcome.Failure"/> (with <see cref="RssFetchErrorType.Database"/>): Returned if a <see cref="RepositoryException"/> (or other unexpected exception during save) occurs during the database persistence stage. This prevents subsequent notification dispatch for the current batch. The outcome will include the error details and the ETag/Last-Modified headers from the HTTP response.
        ///     </description></item>
        /// </list>
        /// </returns>
        private async Task<RssFetchOutcome> SaveAndDispatchAsync(
            RssSource rssSource,
            List<NewsItem> newNewsEntitiesToSave,
            HttpResponseMessage httpResponse,
            CancellationToken cancellationToken)
        {
            const string methodName = nameof(SaveAndDispatchAsync);
            _logger.LogTrace("Entering {MethodName}", methodName);

            string? etagFromResponse = CleanETag(httpResponse?.Headers.ETag?.Tag);
            string? lastModifiedFromResponse = GetLastModifiedFromHeaders(httpResponse?.Headers);

            if (!newNewsEntitiesToSave.Any())
            {
                _logger.LogInformation("No new unique items were found to save for '{SourceName}'.", rssSource.SourceName);
                return RssFetchOutcome.Success(Enumerable.Empty<NewsItemDto>(), etagFromResponse, lastModifiedFromResponse);
            }

            // --- Stage 1: Persist all new items to the database in a single transaction ---
            try
            {
                await SaveNewsItemsToDatabaseAsync(newNewsEntitiesToSave, cancellationToken);
            }
            catch (RepositoryException ex)
            {
                _logger.LogError(ex, "Database persistence failed for '{SourceName}'. Notifications will not be dispatched.", rssSource.SourceName);
                return RssFetchOutcome.Failure(RssFetchErrorType.Database, "Database save failed.", ex, etagFromResponse, lastModifiedFromResponse);
            }

            // --- Stage 2: Dispatch notifications based on the image-only prioritization logic ---
            var dispatchedDtos = await DispatchNotificationsForImageItemsAsync(newNewsEntitiesToSave, cancellationToken);

            _logger.LogTrace("Exiting {MethodName}", methodName);
            return RssFetchOutcome.Success(dispatchedDtos, etagFromResponse, lastModifiedFromResponse);
        }


        /// <summary>
        /// Saves a list of <see cref="NewsItem"/> entities to the database using Dapper within a single, resilient transaction.
        /// This method ensures that all items are either successfully persisted together or none are, maintaining data consistency.
        /// It utilizes a configured retry policy (`_dbRetryPolicy`) to handle transient database connection or operation failures,
        /// making the save operation robust against temporary database unavailability.
        /// </summary>
        /// <param name="items">The <see cref="List{NewsItem}"/> of news items to be persisted. These items are inserted in a single batch
        /// to optimize performance and ensure atomicity.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to observe for cancellation requests during
        /// database connection establishment, transaction management, and SQL execution. If cancellation is requested,
        /// the operation will attempt to gracefully terminate.</param>
        /// <returns>
        /// A <see cref="Task"/> that represents the asynchronous save operation. The task completes when all items have been successfully
        /// inserted into the database and the transaction has been committed.
        /// </returns>
        /// <exception cref="RepositoryException">
        /// Thrown if any error occurs during the database interaction that prevents successful completion of the transaction
        /// (e.g., connection issues after retries, SQL execution errors, or failures during commit/rollback).
        /// This custom exception wraps the original underlying exception for consistent error handling by the caller.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// Thrown if the <paramref name="cancellationToken"/> is signaled while the operation is in progress,
        /// specifically during connection opening, command execution, or transaction commit/rollback,
        /// and before the operation can complete its work.
        /// </exception>
        private async Task SaveNewsItemsToDatabaseAsync(List<NewsItem> items, CancellationToken cancellationToken)
        {
            const string methodName = nameof(SaveNewsItemsToDatabaseAsync);
            _logger.LogTrace("Entering {MethodName} for {ItemCount} items.", methodName, items.Count);

            await _dbRetryPolicy.ExecuteAsync(async () =>
            {
                await using var connection = CreateConnection();
                await connection.OpenAsync(cancellationToken);
                await using var transaction = connection.BeginTransaction();
                _logger.LogDebug("Database connection opened and transaction started.");

                try
                {
                    var sql = @"
                        INSERT INTO NewsItems (Id, Title, Link, Summary, FullContent, ImageUrl, PublishedDate, CreatedAt, LastProcessedAt, SourceName, SourceItemId, SentimentScore, SentimentLabel, DetectedLanguage, AffectedAssets, RssSourceId, IsVipOnly, AssociatedSignalCategoryId) 
                        VALUES (@Id, @Title, @Link, @Summary, @FullContent, @ImageUrl, @PublishedDate, @CreatedAt, @LastProcessedAt, @SourceName, @SourceItemId, @SentimentScore, @SentimentLabel, @DetectedLanguage, @AffectedAssets, @RssSourceId, @IsVipOnly, @AssociatedSignalCategoryId);";

                    var rowsAffected = await connection.ExecuteAsync(sql, items, transaction);
                    await transaction.CommitAsync(cancellationToken);
                    _logger.LogInformation("Database save successful. Transaction committed. {RowsAffected} rows affected.", rowsAffected);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occurred during the database transaction. Rolling back transaction.");
                    await transaction.RollbackAsync(cancellationToken);
                    // Re-throw as a custom exception to be handled by the caller.
                    throw new RepositoryException("Dapper transaction failed during news item save.", ex);
                }
            });

            _logger.LogTrace("Exiting {MethodName}", methodName);
        }


        /// <summary>
        /// Implements the specific business logic for dispatching news notifications based on content characteristics.
        /// This version prioritizes and **exclusively dispatches notifications for news items that contain an image URL.**
        /// Items without an image URL are intentionally skipped from the notification queue.
        /// This method serves as an orchestration step, filtering the saved news items and then delegating
        /// the actual background job enqueuing to a helper method.
        /// </summary>
        /// <param name="savedItems">A <see cref="List{NewsItem}"/> containing the news items that have just been saved to the database. These items are the candidates for potential notification dispatch.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to observe for cancellation requests during the dispatch orchestration process, particularly during the iteration and enqueuing of individual tasks.</param>
        /// <returns>
        /// A <see cref="Task{TResult}"/> representing the asynchronous dispatch orchestration. The task completes with:
        /// <list type="bullet">
        ///     <item><description>
        ///         An <see cref="IEnumerable{NewsItemDto}"/> containing the Data Transfer Objects for the <see cref="NewsItem"/>s
        ///         that were successfully filtered (i.e., had an associated image URL) and subsequently enqueued
        ///         for background notification processing via Hangfire.
        ///     </description></item>
        ///     <item><description>
        ///         An empty <see cref="IEnumerable{NewsItemDto}"/> if no news items were provided in <paramref name="savedItems"/>,
        ///         or if none of the provided items met the criteria for dispatch (i.e., none had an image URL).
        ///     </description></item>
        /// </list>
        /// **Note:** The successful completion of this task indicates that the relevant notification jobs have been *enqueued* in the background processing system (e.g., Hangfire), not that the notifications have been fully sent to users. The actual sending is handled by subsequent background jobs.
        /// </returns>
        private async Task<IEnumerable<NewsItemDto>> DispatchNotificationsForImageItemsAsync(List<NewsItem> savedItems, CancellationToken cancellationToken)
        {
            const string methodName = nameof(DispatchNotificationsForImageItemsAsync);
            _logger.LogTrace("Entering {MethodName}", methodName);

            // --- Business Logic: Filter for items with an image ONLY ---
            var itemsToDispatch = savedItems
                .Where(item => !string.IsNullOrWhiteSpace(item.ImageUrl))
                .OrderByDescending(item => item.PublishedDate)
                .ToList();

            int totalSavedCount = savedItems.Count;
            int withImageCount = itemsToDispatch.Count;
            int withoutImageCount = totalSavedCount - withImageCount;

            _logger.LogInformation(
                "Dispatch Filtering: From {TotalSaved} saved items, {WithImageCount} have an image and will be dispatched. {WithoutImageCount} items without an image will be skipped.",
                totalSavedCount, withImageCount, withoutImageCount);

            if (!itemsToDispatch.Any())
            {
                _logger.LogInformation("No news items with images found to dispatch.");
                _logger.LogTrace("Exiting {MethodName}", methodName);
                return Enumerable.Empty<NewsItemDto>();
            }

            var dispatchedItemIds = new HashSet<Guid>();

            _logger.LogInformation("Dispatching Batch: Enqueuing all {Count} notifications for news items with images.", itemsToDispatch.Count);
            await EnqueueDispatchTasks(itemsToDispatch, "ImageOnlyBatch", dispatchedItemIds, cancellationToken);

            _logger.LogInformation("Completed all dispatch enqueueing. Total items queued for notification: {TotalDispatchedCount}", dispatchedItemIds.Count);

            var dispatchedDtos = _mapper.Map<IEnumerable<NewsItemDto>>(savedItems.Where(ni => dispatchedItemIds.Contains(ni.Id)));
            _logger.LogTrace("Exiting {MethodName}", methodName);
            return dispatchedDtos;
        }



        /// <summary>
        /// A helper method to encapsulate the logic for asynchronously enqueuing a batch of individual news item
        /// notification tasks (Hangfire jobs) for background processing. This method iterates through a list of news items,
        /// creating and enqueuing a separate job for each. It incorporates cancellation support and robust error handling
        /// for each enqueue operation, ensuring that an error with one item does not prevent others from being enqueued.
        /// </summary>
        /// <param name="itemsToEnqueue">The <see cref="List{NewsItem}"/> of news items for which individual notification jobs should be enqueued. These items are the payload for the background tasks.</param>
        /// <param name="batchName">A descriptive string name for this batch, used primarily for logging and diagnostic purposes to identify the context of the enqueuing operation.</param>
        /// <param name="dispatchedTracker">A thread-safe <see cref="HashSet{Guid}"/> used to track the unique identifiers of
        /// news items that have been successfully enqueued into the Hangfire system. This set is updated with a lock to ensure concurrency safety.</param>
        /// <param name="ct">The <see cref="CancellationToken"/> to monitor for stop requests. If signalled, the method will cease enqueuing
        /// further jobs for the current batch and allow previously initiated tasks to complete or be cancelled.</param>
        /// <returns>
        /// A <see cref="Task"/> that represents the asynchronous enqueueing operation.
        /// The task completes when:
        /// <list type="bullet">
        ///     <item><description>All items in <paramref name="itemsToEnqueue"/> have been processed (either successfully enqueued, skipped due to cancellation, or encountered an internal error during the enqueue attempt and logged).</description></item>
        ///     <item><description>The <paramref name="ct"/> is cancelled, leading to an early exit from the enqueue loop and potential <see cref="OperationCanceledException"/> propagation if <see cref="Task.WhenAll"/> is affected.</description></item>
        /// </list>
        /// <para>
        /// **Important:** The completion of this <see cref="Task"/> signifies only that the individual notification jobs have been
        /// submitted to the Hangfire background processing system. It does NOT guarantee that the actual notifications have
        /// been fully processed or sent to users; that is handled by the Hangfire worker roles executing the enqueued jobs.
        /// </para>
        /// </returns>
        /// <exception cref="OperationCanceledException">
        /// Thrown if the <paramref name="ct"/> is cancelled while this method is awaiting for all its internal enqueue tasks to complete
        /// (i.e., during the <see cref="Task.WhenAll"/> call). Individual enqueue tasks that are cancelled before starting their
        /// Hangfire submission will be caught and logged internally without re-throwing.
        /// </exception>
        private async Task EnqueueDispatchTasks(List<NewsItem> itemsToEnqueue, string batchName, HashSet<Guid> dispatchedTracker, CancellationToken ct)
        {
            const string methodName = nameof(EnqueueDispatchTasks);
            _logger.LogTrace("Entering {MethodName} for batch '{BatchName}' with {ItemCount} items.", methodName, batchName, itemsToEnqueue.Count);

            var tasks = new List<Task>();
            foreach (var item in itemsToEnqueue)
            {
                if (ct.IsCancellationRequested)
                {
                    _logger.LogWarning("Cancellation token triggered during enqueue loop for batch '{BatchName}'. Halting further enqueueing for this batch.", batchName);
                    break;
                }

                var capturedItem = item;
                tasks.Add(Task.Run(() => {
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        _logger.LogTrace("Enqueueing job for NewsItem ID: {NewsId}, Title: '{Title}' (Batch: {Batch})",
                            capturedItem.Id, capturedItem.Title.Truncate(30), batchName);

                        // Enqueue the job to be processed by Hangfire. Use CancellationToken.None as Hangfire manages job lifetime independently.
                        _backgroundJobClient.Enqueue<INotificationDispatchService>(s => s.DispatchNewsNotificationAsync(capturedItem.Id, CancellationToken.None));

                        // Safely add the ID to the tracker for final reporting.
                        lock (dispatchedTracker)
                        {
                            dispatchedTracker.Add(capturedItem.Id);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // This is an expected, non-error outcome if the task is cancelled before starting.
                        _logger.LogInformation("Enqueue task for NewsItem ID {NewsId} was cancelled before it could be sent to Hangfire.", capturedItem.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "An unexpected error occurred while trying to enqueue job for NewsItemID {NewsId} in batch '{BatchName}'. This item will not be dispatched.",
                            capturedItem.Id, batchName);
                    }
                }, ct));
            }

            try
            {
                await Task.WhenAll(tasks);
                _logger.LogInformation("Successfully awaited all {TaskCount} initiated dispatch tasks for batch '{BatchName}'.", tasks.Count, batchName);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("The waiting task for batch '{BatchName}' was cancelled. Not all enqueue tasks may have completed successfully.", batchName);
            }
            _logger.LogTrace("Exiting {MethodName}", methodName);
        }

        #endregion

        #region Status Update and Exception Handling




        /// <summary>
        /// Updates the <see cref="RssSource"/>'s status in the database after an RSS feed fetch cycle is complete.
        /// This method centralizes the logic for managing the source's operational state, including:
        /// <list type="bullet">
        ///     <item><description>Resetting error counts and updating success timestamps and ETag/Last-Modified headers upon successful fetches.</description></item>
        ///     <item><description>Incrementing error counts for transient failures or immediately escalating for permanent HTTP/XML parsing errors.</description></item>
        ///     <item><description>Deactivating the <see cref="RssSource"/> if its accumulated error count reaches a configured threshold.</description></item>
        ///     <item><description>Persisting all status changes to the database in a resilient manner using a retry policy.</description></item>
        ///     <item><description>Constructing and returning a <see cref="Result{T}"/> object that reflects the overall outcome of the fetch operation.</description></item>
        /// </list>
        /// </summary>
        /// <param name="source">The <see cref="RssSource"/> object whose status is to be updated. This object's properties will be modified in-place and then persisted.</param>
        /// <param name="outcome">The <see cref="RssFetchOutcome"/> object, which encapsulates the result of the preceding RSS feed fetch attempt (success, failure, type of error, dispatched items, etc.).</param>
        /// <param name="ct">A <see cref="CancellationToken"/> to observe for cancellation requests during the database update operation.</param>
        /// <returns>
        /// A <see cref="Task{TResult}"/> that represents the asynchronous operation, yielding a <see cref="Result{IEnumerable{NewsItemDto}}"/>.
        /// <list type="bullet">
        ///     <item><description>
        ///         **On Success (<see cref="RssFetchOutcome.IsSuccess"/> is <c>true</c>):**
        ///         Returns a <see cref="Result{T}.Success"/> containing an <see cref="IEnumerable{NewsItemDto}"/> of the news items that were successfully dispatched for notification during this fetch cycle.
        ///         The <paramref name="source"/>'s <see cref="RssSource.LastSuccessfulFetchAt"/> will be updated, <see cref="RssSource.FetchErrorCount"/> reset to 0,
        ///         and its ETag and Last-Modified headers (if present in the <paramref name="outcome"/>) will be updated. <see cref="RssSource.IsActive"/> is set to <c>true</c>.
        ///     </description></item>
        ///     <item><description>
        ///         **On Failure (<see cref="RssFetchOutcome.IsSuccess"/> is <c>false</c>):**
        ///         Returns a <see cref="Result{T}.Failure"/> containing an array of error messages.
        ///         The <paramref name="source"/>'s <see cref="RssSource.FetchErrorCount"/> is incremented (or immediately maxed out for permanent errors).
        ///         If the <see cref="RssSource.FetchErrorCount"/> reaches or exceeds the configured <see cref="RssProcessorSettings.MaxFetchErrorsToDeactivate"/> threshold,
        ///         the <see cref="RssSource.IsActive"/> property will be set to <c>false</c>.
        ///     </description></item>
        /// </list>
        /// </returns>
        /// <exception cref="RepositoryException">
        /// Thrown if a critical error occurs during the database persistence of the <see cref="RssSource"/>'s updated status,
        /// indicating a failure to maintain the source's state in the database.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// Thrown if the <paramref name="ct"/> is signaled during the database update operation.
        /// </exception>
        private async Task<Result<IEnumerable<NewsItemDto>>> UpdateRssSourceStatusAfterFetchOutcomeAsync(RssSource source, RssFetchOutcome outcome, CancellationToken ct)
        {
            const string methodName = nameof(UpdateRssSourceStatusAfterFetchOutcomeAsync);
            _logger.LogTrace("Entering {MethodName}", methodName);

            source.UpdatedAt = DateTime.UtcNow;

            if (outcome.IsSuccess)
            {
                _logger.LogInformation("Fetch was successful. Resetting error count and updating metadata.");
                source.LastSuccessfulFetchAt = source.LastFetchAttemptAt;
                source.FetchErrorCount = 0;
                if (!string.IsNullOrWhiteSpace(outcome.ETag)) source.ETag = outcome.ETag;
                if (!string.IsNullOrWhiteSpace(outcome.LastModifiedHeader)) source.LastModifiedHeader = outcome.LastModifiedHeader;
                source.IsActive = true;
            }
            else
            {
                // Change logging level based on error type for better clarity.
                var logLevel = outcome.ErrorType == RssFetchErrorType.Cancellation ? LogLevel.Warning : LogLevel.Error;

                _logger.Log(logLevel, outcome.Exception, "Fetch failed. Updating error status. ErrorType: {ErrorType}, Message: {ErrorMessage}", outcome.ErrorType, outcome.ErrorMessage);

                if (outcome.ErrorType != RssFetchErrorType.Cancellation)
                {
                    if (outcome.ErrorType is RssFetchErrorType.PermanentHttp or RssFetchErrorType.XmlParsing)
                    {
                        source.FetchErrorCount = _settings.MaxFetchErrorsToDeactivate;
                        _logger.LogWarning("RssSource error count maxed out immediately due to a permanent error: {ErrorType}", outcome.ErrorType);
                    }
                    else
                    {
                        source.FetchErrorCount++;
                        _logger.LogWarning("RssSource error count incremented to {ErrorCount} due to a transient error: {ErrorType}", source.FetchErrorCount, outcome.ErrorType);
                    }
                }
            }

            // Deactivation Logic
            if (source.FetchErrorCount >= _settings.MaxFetchErrorsToDeactivate && source.IsActive)
            {
                source.IsActive = false;
                _logger.LogWarning("DEACTIVATING RssSource {Id} due to reaching the error threshold of {Threshold}. Last error: {Error}",
                    source.Id, _settings.MaxFetchErrorsToDeactivate, outcome.ErrorMessage);
            }

            try
            {
                await _dbRetryPolicy.ExecuteAsync(async () => {
                    await using var connection = CreateConnection();
                    var sql = @"
                 UPDATE RssSources SET 
                     LastSuccessfulFetchAt = @LastSuccessfulFetchAt, 
                     FetchErrorCount = @FetchErrorCount, 
                     UpdatedAt = @UpdatedAt, 
                     ETag = @ETag, 
                     LastModifiedHeader = @LastModifiedHeader, 
                     IsActive = @IsActive, 
                     LastFetchAttemptAt = @LastFetchAttemptAt 
                 WHERE Id = @Id;";
                    _logger.LogDebug("Executing final status update to database for RssSource {RssSourceId}.", source.Id);
                    await connection.ExecuteAsync(sql, source);
                });
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "CRITICAL FAILURE: Could not persist the final status for RssSource '{SourceName}'. Its state may now be inconsistent in the database.", source.SourceName);
                throw new RepositoryException($"Failed to update RssSource status for '{source.SourceName}'.", ex);
            }

            // --- Construct Final Return Value ---
            if (outcome.IsSuccess)
            {
                var successMessage = $"Fetch successful. {outcome.DispatchedNewsItems.Count()} items were dispatched for notification.";
                _logger.LogInformation(successMessage);
                return Result<IEnumerable<NewsItemDto>>.Success(outcome.DispatchedNewsItems, successMessage);
            }
            else
            {
                var failureMessage = $"Fetch failed for '{source.SourceName}'. Error: {outcome.ErrorMessage}";
                // Use the determined LogLevel for the final failure message.           
                return Result<IEnumerable<NewsItemDto>>.Failure(new[] { outcome.ErrorMessage ?? "An unknown fetch error occurred." });
            }
        }

        /// <summary>
        /// Centralized handler to categorize and encapsulate exceptions encountered during the main RSS feed fetching and processing pipeline
        /// into a structured <see cref="RssFetchOutcome"/>. This allows for consistent error reporting and subsequent status updates
        /// of the <see cref="RssSource"/> in the database.
        /// </summary>
        /// <param name="ex">The <see cref="Exception"/> that was caught in the fetch pipeline.</param>
        /// <param name="response">The <see cref="HttpResponseMessage"/> (if available) that led to the exception. This is used to extract
        /// ETag and Last-Modified headers even in failure scenarios, which can be useful for debugging or future conditional requests.</param>
        /// <returns>
        /// An <see cref="RssFetchOutcome"/> representing a failure. This outcome will contain:
        /// <list type="bullet">
        ///     <item><description>An <see cref="RssFetchErrorType"/> classifying the nature of the error:</description>
        ///         <list type="bullet">
        ///             <item><description><see cref="RssFetchErrorType.PermanentHttp"/>: For HTTP errors like 400, 401, 403, 404, 405, 410, 422.</description></item>
        ///             <item><description><see cref="RssFetchErrorType.TransientHttp"/>: For other HTTP-related errors, including timeouts or temporary server issues.</description></item>
        ///             <item><description><see cref="RssFetchErrorType.XmlParsing"/>: For issues related to invalid or malformed XML content.</description></item>
        ///             <item><description><see cref="RssFetchErrorType.Cancellation"/>: If the operation was explicitly cancelled.</description></item>
        ///             <item><description><see cref="RssFetchErrorType.Unexpected"/>: For any other unhandled or unforeseen exceptions.</description></item>
        ///         </list>
        ///     </item>
        ///     <item><description>A human-readable <see cref="RssFetchOutcome.ErrorMessage"/> describing the issue.</description></item>
        ///     <item><description>The original <see cref="Exception"/> for detailed logging and diagnosis.</description></item>
        ///     <item><description>The ETag and Last-Modified headers from the <paramref name="response"/>, if present and cleaned.</description></item>
        /// </list>
        /// </returns>
        private RssFetchOutcome HandleFetchException(Exception ex, HttpResponseMessage? response)
        {
            RssFetchErrorType errorType;
            string message;
            switch (ex)
            {
                case HttpRequestException httpEx:
                    errorType = IsPermanentHttpError(httpEx.StatusCode) ? RssFetchErrorType.PermanentHttp : RssFetchErrorType.TransientHttp;
                    message = $"HTTP request exception with status {httpEx.StatusCode}.";
                    break;
                case XmlException:
                    errorType = RssFetchErrorType.XmlParsing;
                    message = "The feed response contained invalid or malformed XML.";
                    break;
                case TaskCanceledException taskCanceledEx when taskCanceledEx.InnerException is OperationCanceledException operationCanceledEx && !operationCanceledEx.CancellationToken.IsCancellationRequested:
                    errorType = RssFetchErrorType.TransientHttp;
                    message = "The HTTP request timed out after the configured period.";
                    break;
                case OperationCanceledException:
                    errorType = RssFetchErrorType.Cancellation;
                    message = "The fetch operation was cancelled by the calling process.";
                    break;
                default:
                    errorType = RssFetchErrorType.Unexpected;
                    message = "An unexpected and unhandled error occurred in the processing pipeline.";
                    break;
            }
            _logger.LogError(ex, "Caught Exception in Fetch Pipeline. Classified as {ErrorType}. Message: {Message}", errorType, message);
            return RssFetchOutcome.Failure(errorType, message, ex, CleanETag(response?.Headers.ETag?.Tag), GetLastModifiedFromHeaders(response?.Headers));
        }

        #endregion

        #region HTTP and HTML Helper Methods


        /// <summary>
        /// Determines if a given HTTP status code represents a "permanent" client-side error.
        /// These are errors that typically indicate a problem with the request itself or the requested resource,
        /// and retrying without modification is unlikely to succeed.
        /// </summary>
        /// <param name="code">The nullable <see cref="HttpStatusCode"/> to check.</param>
        /// <returns>
        /// <c>true</c> if the status code is one of the following:
        /// <list type="bullet">
        ///     <item><description><see cref="HttpStatusCode.BadRequest"/> (400)</description></item>
        ///     <item><description><see cref="HttpStatusCode.Unauthorized"/> (401)</description></item>
        ///     <item><description><see cref="HttpStatusCode.Forbidden"/> (403)</description></item>
        ///     <item><description><see cref="HttpStatusCode.NotFound"/> (404)</description></item>
        ///     <item><description><see cref="HttpStatusCode.Gone"/> (410)</description></item>
        /// </list>
        /// <c>false</c> otherwise (e.g., success codes, server errors, or transient client errors).
        /// </returns>
        private bool IsPermanentHttpError(HttpStatusCode? code) => code is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone;


        private void AddConditionalGetHeaders(HttpRequestMessage request, RssSource source)
        {
            if (!string.IsNullOrWhiteSpace(source.ETag))
            {
                request.Headers.IfNoneMatch.ParseAdd(source.ETag.Contains('"') ? source.ETag : $"\"{source.ETag}\"");
            }
            if (!string.IsNullOrWhiteSpace(source.LastModifiedHeader) && DateTimeOffset.TryParse(source.LastModifiedHeader, out var lastMod))
            {
                request.Headers.IfModifiedSince = lastMod;
            }
        }

        /// <summary>
        /// Adds conditional GET headers (If-None-Match and If-Modified-Since) to an <see cref="HttpRequestMessage"/>.
        /// These headers are crucial for implementing efficient caching mechanisms in HTTP requests. By sending the ETag
        /// and Last-Modified date from a previously fetched response, the client can ask the server to send the full
        /// resource only if it has changed, otherwise receiving a "304 Not Modified" status code. This saves bandwidth
        /// and reduces processing load on both the client and server.
        /// </summary>
        /// <param name="request">The <see cref="HttpRequestMessage"/> to which the conditional headers will be added. This object is modified in place.</param>
        /// <param name="source">The <see cref="RssSource"/> object containing the ETag and Last-Modified header values
        /// obtained from a previous successful fetch of the RSS feed. These values represent the current state of the resource.</param>
        /// <returns>
        /// This method does not return a value. It modifies the <paramref name="request"/> object by adding
        /// HTTP headers if the corresponding values are present and valid in the <paramref name="source"/> object.
        /// <list type="bullet">
        ///     <item><description><c>If-None-Match</c> header: Added if <paramref name="source.ETag"/> is a non-empty string. The ETag value is correctly quoted to comply with HTTP standards.</description></item>
        ///     <item><description><c>If-Modified-Since</c> header: Added if <paramref name="source.LastModifiedHeader"/> is a non-empty string and can be successfully parsed into a <see cref="DateTimeOffset"/>.</description></item>
        private RssFetchOutcome HandleNotModifiedResponse(RssSource source, HttpResponseMessage response)
        {
            _logger.LogInformation("Feed '{SourceName}' content has not changed (HTTP 304 Not Modified). The fetch cycle is complete.", source.SourceName);
            return RssFetchOutcome.Success(Enumerable.Empty<NewsItemDto>(), CleanETag(response.Headers.ETag?.Tag), GetLastModifiedFromHeaders(response.Headers));
        }


        /// <summary>
        /// Safely extracts the "Last-Modified" header value from a collection of HTTP response headers.
        /// This header is typically used in conjunction with "If-Modified-Since" for conditional GET requests,
        /// allowing clients to request a resource only if it has been modified since a specific date.
        /// </summary>
        /// <param name="headers">The <see cref="HttpResponseHeaders"/> collection from which to retrieve the "Last-Modified" value. Can be <c>null</c>.</param>
        /// <returns>
        /// A <see cref="string"/> representing the value of the "Last-Modified" header if it exists;
        /// otherwise, <c>null</c> if the <paramref name="headers"/> are null, or if the "Last-Modified"
        /// header is not found or has no values. If multiple "Last-Modified" headers are present,
        /// only the first one is returned.
        /// </returns>
        private string? GetLastModifiedFromHeaders(HttpResponseHeaders? headers)
        {
            if (headers == null || !headers.TryGetValues("Last-Modified", out var values))
            {
                return null;
            }

            return values.FirstOrDefault();
        }


        /// <summary>
        /// Cleans an ETag string by removing leading/trailing double quotes if it represents a strong ETag.
        /// Weak ETags (prefixed with "W/") are returned as is, in accordance with HTTP specifications.
        /// </summary>
        /// <param name="etag">The ETag string to clean. Can be <c>null</c> or whitespace.</param>
        /// <returns>
        /// A cleaned <see cref="string"/> version of the ETag:
        /// <list type="bullet">
        ///     <item><description><c>null</c> if the input <paramref name="etag"/> is <c>null</c> or whitespace.</description></item>
        ///     <item><description>The original <paramref name="etag"/> if it starts with "W/" (indicating a weak ETag).</description></item>
        ///     <item><description>The <paramref name="etag"/> with leading and trailing double quotes removed if it's a strong ETag (not starting with "W/").</description></item>
        /// </list>
        /// </returns>
        private string? CleanETag(string? etag)
        {
            if (string.IsNullOrWhiteSpace(etag)) return null;
            return etag.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? etag : etag.Trim('"');
        }

        /// <summary>
        /// Determines a stable and unique `SourceItemId` for a given <see cref="SyndicationItem"/>.
        /// This identifier is crucial for preventing duplicate news items from being processed and stored.
        /// The method prioritizes existing unique identifiers from the syndication item itself (like GUIDs),
        /// falls back to the item's URL, and as a last resort, generates a cryptographic hash based on core item properties.
        /// </summary>
        /// <param name="item">The <see cref="SyndicationItem"/> from which to extract or derive the unique ID.</param>
        /// <param name="link">The primary link (URL) associated with the news item, derived from the syndication item's links.</param>
        /// <param name="title">The title of the news item, used for hash generation if other identifiers are unavailable.</param>
        /// <param name="sourceId">The unique identifier (<see cref="Guid"/>) of the RSS source, included in hash generation to ensure uniqueness across different sources.</param>
        /// <returns>
        /// A <see cref="string"/> representing the stable and unique `SourceItemId` for the news item.
        /// The method attempts to provide the most reliable unique identifier in the following order of preference:
        /// <list type="ordered">
        ///     <item><description>
        ///         The `Id` property of the <paramref name="item"/> itself, if it's non-empty and sufficiently long (e.g., a GUID or robust external ID).
        ///     </description></item>
        ///     <item><description>
        ///         The provided <paramref name="link"/>, if it's a non-empty and well-formed absolute URI.
        ///     </description></item>
        ///     <item><description>
        ///         A SHA256 hash generated from a combination of the <paramref name="sourceId"/>, <paramref name="title"/>, and <paramref name="item.PublishDate"/>.
        ///         This ensures a stable identifier even for feeds lacking explicit unique IDs or stable links.
        ///     </description></item>
        /// </list>
        /// All generated or selected identifiers are truncated to <c>NewsSourceItemIdMaxLenDb</c> to fit database constraints.
        /// </returns>
        private string DetermineSourceItemId(SyndicationItem item, string? link, string title, Guid sourceId)
        {
            if (!string.IsNullOrWhiteSpace(item.Id) && item.Id.Length > 20) return item.Id.Truncate(NewsSourceItemIdMaxLenDb);
            if (!string.IsNullOrWhiteSpace(link) && Uri.IsWellFormedUriString(link, UriKind.Absolute)) return link.Truncate(NewsSourceItemIdMaxLenDb);

            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes($"{sourceId}_{title}_{item.PublishDate:o}"));
            return Convert.ToHexString(hash).ToLowerInvariant().Truncate(NewsSourceItemIdMaxLenDb);
        }

        /// <summary>
        /// Cleans HTML content from a given string, extracting only the plain text and decoding HTML entities.
        /// This method leverages the HtmlAgilityPack library to robustly parse HTML, remove all tags,
        /// and then uses <see cref="WebUtility.HtmlDecode"/> to convert HTML entities (like &amp;)
        /// into their corresponding characters. Finally, it trims leading/trailing whitespace.
        /// </summary>
        /// <param name="html">The input string that may contain HTML content. Can be <c>null</c> or empty.</param>
        /// <returns>
        /// A <see cref="string"/> containing the cleaned, plain text content:
        /// <list type="bullet">
        ///     <item><description><c>null</c> if the input <paramref name="html"/> is <c>null</c> or consists only of whitespace.</description></item>
        ///     <item><description>The extracted, HTML-decoded, and trimmed inner text if the input <paramref name="html"/> contains valid content.</description></item>
        /// </list>
        /// </returns>
        private string? CleanHtmlWithHtmlAgility(string? html)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            return WebUtility.HtmlDecode(doc.DocumentNode.InnerText).Trim();
        }


        /// <summary>
        /// Attempts to extract a primary image URL associated with a <see cref="SyndicationItem"/>.
        /// This method searches for an image URL in a prioritized order to ensure the most relevant
        /// image is identified:
        /// <list type="ordered">
        ///     <item><description>It first checks for a `media:content` XML extension (common in media RSS feeds).</description></item>
        ///     <item><description>Next, it looks for an `enclosure` link with an image media type.</description></item>
        ///     <item><description>Finally, if no direct media links are found, it parses the item's `content` or `summary` HTML
        ///     to find the `src` attribute of the first `<img>` tag.</description></item>
        /// </list>
        /// It also handles the conversion of relative URLs to absolute URLs using the item's base URI
        /// and filters out `data:` URIs, which are typically embedded images, not external links.
        /// </summary>
        /// <param name="item">The <see cref="SyndicationItem"/> from which to extract the image URL.</param>
        /// <param name="summary">The HTML content of the item's summary, used as a fallback source for image tags if `content` is not available.</param>
        /// <param name="content">The full HTML content of the item, preferred source for image tags.</param>
        /// <returns>
        /// A <see cref="string"/> representing the absolute URL of the extracted image if found;
        /// otherwise, <c>null</c> if no suitable image URL could be identified or if an error occurred during extraction.
        /// The returned URL is guaranteed to be absolute.
        /// </returns>
        private string? ExtractImageUrlWithHtmlAgility(SyndicationItem item, string? summary, string? content)
        {
            try
            {
                var mediaContent = item.ElementExtensions.FirstOrDefault(e => e.OuterName == "content" && e.OuterNamespace.Contains("media"));
                if (mediaContent != null)
                {
                    var url = mediaContent.GetObject<System.Xml.Linq.XElement>()?.Attribute("url")?.Value;
                    if (!string.IsNullOrWhiteSpace(url)) return MakeUrlAbsolute(item, url);
                }

                var enclosure = item.Links.FirstOrDefault(l => l.RelationshipType == "enclosure" && l.MediaType?.StartsWith("image/") == true);
                if (enclosure?.Uri != null) return enclosure.Uri.ToString();

                var htmlToParse = !string.IsNullOrWhiteSpace(content) ? content : summary;
                if (string.IsNullOrWhiteSpace(htmlToParse)) return null;

                var doc = new HtmlDocument();
                doc.LoadHtml(htmlToParse);

                var imgNode = doc.DocumentNode.SelectSingleNode("//img[@src]");
                var src = imgNode?.GetAttributeValue("src", null);

                if (!string.IsNullOrWhiteSpace(src) && !src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    return MakeUrlAbsolute(item, src);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "An error occurred during image extraction for item '{ItemTitle}'", item.Title?.Text.Truncate(50));
            }
            return null;
        }


        /// <summary>
        /// Converts a potentially relative image URL found within a <see cref="SyndicationItem"/> to an absolute URL.
        /// This is crucial for ensuring that image links are universally resolvable, regardless of where the RSS feed is consumed.
        /// The method prioritizes existing absolute URLs, then attempts to resolve relative URLs against the item's base URI
        /// or any absolute URI found in its links.
        /// </summary>
        /// <param name="item">The <see cref="SyndicationItem"/> to which the <paramref name="imageUrl"/> belongs. This item's <see cref="SyndicationItem.BaseUri"/>
        /// or <see cref="SyndicationItem.Links"/> are used as potential base URIs for resolution.</param>
        /// <param name="imageUrl">The image URL string to be converted. This can be an absolute URI, a relative URI, or <c>null</c>/empty.</param>
        /// <returns>
        /// A <see cref="string"/> representing the absolute URL of the image:
        /// <list type="bullet">
        ///     <item><description><c>null</c> if the input <paramref name="imageUrl"/> is <c>null</c> or consists only of whitespace.</description></item>
        ///     <item><description>The original <paramref name="imageUrl"/> if it is already a well-formed absolute URI.</description></item>
        ///     <item><description>A newly constructed absolute URL string if the <paramref name="imageUrl"/> was relative and successfully resolved against a base URI derived from the <paramref name="item"/>.</description></item>
        ///     <item><description>The original (unmodified and still relative) <paramref name="imageUrl"/> <see cref="string"/> if it was a relative URI but could not be resolved to an absolute URI (e.g., no suitable base URI found in the <paramref name="item"/>, or the combination formed an invalid URI).</description></item>
        /// </list>
        /// </returns>
        private string? MakeUrlAbsolute(SyndicationItem item, string? imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl)) return null;
            if (Uri.IsWellFormedUriString(imageUrl, UriKind.Absolute)) return imageUrl;

            var baseUri = item.BaseUri ?? item.Links.FirstOrDefault(l => l.Uri?.IsAbsoluteUri == true)?.Uri;
            if (baseUri != null && Uri.TryCreate(baseUri, imageUrl, out var absoluteUri))
            {
                return absoluteUri.ToString();
            }
            return imageUrl;
        }

        #endregion
    }
}