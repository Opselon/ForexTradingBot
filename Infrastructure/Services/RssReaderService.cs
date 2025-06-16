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
    public class RssReaderServiceSettings
    {

        
        /// <summary>
        /// The section name in the application's configuration file.
        /// </summary>
        public const string ConfigurationSectionName = "RssReaderService";

        /// <summary>
        /// The timeout in seconds for an individual HTTP request to an RSS feed endpoint.
        /// </summary>
        /// <remarks>
        /// Defaults to 60 seconds.
        /// </remarks>
        public int HttpClientTimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// The number of times to retry a failed HTTP request for transient errors.
        /// </summary>
        /// <remarks>
        /// Defaults to 3 retries.
        /// </remarks>
        public int HttpRetryCount { get; set; } = 3;

        /// <summary>
        /// The number of times to retry a failed database operation for transient errors.
        /// </summary>
        /// <remarks>
        /// Defaults to 3 retries.
        /// </remarks>
        public int DbRetryCount { get; set; } = 3;

        /// <summary>
        /// The maximum number of consecutive fetch errors before an RSS source is automatically deactivated.
        /// </summary>
        /// <remarks>
        /// Defaults to 10 errors.
        /// </remarks>
        public int MaxFetchErrorsToDeactivate { get; set; } = 10;

        /// <summary>
        /// The default, polite User-Agent string to send with every HTTP request.
        /// This helps identify our service to feed providers.
        /// </summary>
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
    public class RssReaderService : IRssReaderService
    {

        public const string HttpClientNamedClient = "RssFeedClient";

        #region Service Dependencies and Configuration Fields

        /// <summary>
        /// Factory for creating configured HttpClient instances.
        /// </summary>
        private readonly IHttpClientFactory _httpClientFactory;

        /// <summary>
        /// AutoMapper instance for mapping between domain entities and DTOs.
        /// </summary>
        private readonly IMapper _mapper;

        /// <summary>
        /// Logger for capturing detailed diagnostic information and errors.
        /// </summary>
        private readonly ILogger<RssReaderService> _logger;

        /// <summary>
        /// Hangfire client used to enqueue background jobs for notification dispatch.
        /// </summary>
        private readonly IBackgroundJobClient _backgroundJobClient;

        /// <summary>
        /// The database connection string used for all Dapper operations.
        /// </summary>
        private readonly string _connectionString;

        /// <summary>
        /// The configured settings for this service, injected via IOptions.
        /// </summary>
        private readonly RssReaderServiceSettings _settings;

        /// <summary>
        /// Polly policy for resiliently handling transient HTTP errors (e.g., server errors, timeouts).
        /// </summary>
        private readonly AsyncRetryPolicy<HttpResponseMessage> _httpRetryPolicy;

        /// <summary>
        /// Polly policy for resiliently handling transient database errors (e.g., connection issues, deadlocks).
        /// </summary>
        private readonly AsyncRetryPolicy _dbRetryPolicy;

        #endregion

        #region Database Column Length Constants

        /// <summary>
        /// The maximum length for a news item's title, matching the 'NewsItems.Title' database column schema.
        /// </summary>
        private const int NewsTitleMaxLenDb = 500;

        /// <summary>
        /// The maximum length for a news item's unique identifier from its source, matching the 'NewsItems.SourceItemId' database column schema.
        /// </summary>
        private const int NewsSourceItemIdMaxLenDb = 500;

        /// <summary>
        /// The maximum length for the name of the RSS source, matching the 'NewsItems.SourceName' database column schema.
        /// </summary>
        private const int NewsSourceNameMaxLenDb = 150;

        /// <summary>
        /// The maximum length for a news item's link, matching the 'NewsItems.Link' database column schema.
        /// </summary>
        private const int NewsLinkMaxLenDb = 2083;

        #endregion

        #region Private Nested Types for Internal State Management

        /// <summary>
        /// Enumerates the possible high-level categories of errors that can occur during the RSS fetch pipeline.
        /// This is crucial for structured error handling, logging, and determining if a source should be deactivated.
        /// </summary>
        private enum RssFetchErrorType
        {
            None,
            TransientHttp,
            PermanentHttp,
            XmlParsing,
            Database,
            ContentProcessing,
            Cancellation,
            Unexpected
        }

        /// <summary>
        /// A record to encapsulate the complete result of a single fetch cycle.
        /// This pattern centralizes all possible outcomes (success or failure) and associated metadata
        /// (news items, HTTP headers, error details) into a single, immutable object.
        /// </summary>
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
            /// Creates a standard success outcome.
            /// </summary>
            public static RssFetchOutcome Success(IEnumerable<NewsItemDto> dispatchedNewsItems, string? etag, string? lastModified)
                => new(true, dispatchedNewsItems, etag, lastModified, RssFetchErrorType.None, null);

            /// <summary>
            /// Creates a standard failure outcome.
            /// </summary>
            public static RssFetchOutcome Failure(RssFetchErrorType errorType, string errorMessage, Exception? ex = null, string? etag = null, string? lastModified = null)
                => new(false, Enumerable.Empty<NewsItemDto>(), etag, lastModified, errorType, errorMessage, ex);
        }

        /// <summary>
        /// A context record passed to the item creation logic, bundling all necessary information
        /// to prevent duplicate processing and ensure data consistency.
        /// </summary>
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

        /// <inheritdoc />
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
            rssSource.LastFetchAttemptAt = DateTime.UtcNow;

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
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "A critical, unhandled exception was caught in the main pipeline. The operation will be marked as a failure.");
                outcome = HandleFetchException(ex, httpResponse);
            }
            finally
            {
                httpResponse?.Dispose();
                _logger.LogTrace("HTTP response object has been disposed.");
            }

            // --- 4. Final Status Update and Return ---
            _logger.LogInformation("Fetch cycle has concluded. Updating final status of RssSource in the database.");
            var finalResult = await UpdateRssSourceStatusAfterFetchOutcomeAsync(rssSource, outcome, cancellationToken);

            _logger.LogTrace("Exiting {MethodName}", methodName);
            return finalResult;
        }

        /// <summary>
        /// Orchestrates the processing of a received HTTP response. It determines whether the content
        /// is new, not modified, or an error, and routes the processing to the appropriate logic path.
        /// </summary>
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
        /// Executes the HTTP GET request for the given RSS source, wrapped in the configured Polly retry policy to handle transient failures.
        /// </summary>
        private async Task<HttpResponseMessage> ExecuteHttpRequestAsync(RssSource rssSource, string correlationId, CancellationToken cancellationToken)
        {
            const string methodName = nameof(ExecuteHttpRequestAsync);
            _logger.LogTrace("Entering {MethodName}", methodName);

            var httpClient = _httpClientFactory.CreateClient("RssFeedClient"); // Assuming named client is configured
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, rssSource.Url);
            requestMessage.Headers.UserAgent.ParseAdd(_settings.UserAgent);

            AddConditionalGetHeaders(requestMessage, rssSource);

            var response = await _httpRetryPolicy.ExecuteAsync(async (context, ct) =>
            {
                using var requestTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.HttpClientTimeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, requestTimeoutCts.Token, cancellationToken);
                _logger.LogDebug("Polly Execute: Sending HTTP GET request with a {Timeout}s timeout.", _settings.HttpClientTimeoutSeconds);
                return await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
            }, new Context(correlationId), cancellationToken).ConfigureAwait(false);

            _logger.LogTrace("Exiting {MethodName}", methodName);
            return response;
        }

        #endregion

        #region Feed Parsing, Filtering, and Entity Creation

        /// <summary>
        /// Parses the XML content from an HTTP response stream into a <see cref="SyndicationFeed"/> object.
        /// </summary>
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
        /// Processes a collection of <see cref="SyndicationItem"/>s, filters out duplicates and already-existing items,
        /// and maps the new items to <see cref="NewsItem"/> entities.
        /// </summary>
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
        /// Fetches the set of existing `SourceItemId` values for a given RSS source from the database. This is a critical
        /// step to prevent processing and dispatching duplicate news items.
        /// </summary>
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
        /// Attempts to create a single <see cref="NewsItem"/> entity from a <see cref="SyndicationItem"/>,
        /// performing all necessary validation, content cleaning, and duplicate filtering logic.
        /// </summary>
        /// <returns>A new <see cref="NewsItem"/> entity if it's valid and new; otherwise, <c>null</c>.</returns>
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
        /// Orchestrates the two-stage process of saving new items and then dispatching notifications
        /// according to the defined business logic.
        /// </summary>
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
        /// </summary>
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
        /// Implements the business logic for dispatching notifications.
        /// **This version exclusively dispatches notifications for news items that have an image.**
        /// </summary>
        /// <returns>A collection of DTOs for the news items that were actually enqueued for dispatch.</returns>
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
        /// A helper method to encapsulate the logic for enqueuing a batch of news items into Hangfire for background processing.
        /// </summary>
        /// <param name="itemsToEnqueue">The list of news items to process in this batch.</param>
        /// <param name="batchName">A descriptive name for the batch, used for logging and diagnostics.</param>
        /// <param name="dispatchedTracker">A thread-safe set to track the IDs of successfully enqueued items.</param>
        /// <param name="ct">The cancellation token to monitor for stop requests during the enqueue loop.</param>
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
        /// Updates the RssSource's status in the database after a fetch cycle is complete. This method centralizes
        /// the logic for resetting error counts on success or incrementing them on failure, including deactivation logic.
        /// </summary>
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
                _logger.LogWarning("Fetch failed. Updating error status. ErrorType: {ErrorType}", outcome.ErrorType);
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
                _logger.LogError(outcome.Exception, failureMessage);
                return Result<IEnumerable<NewsItemDto>>.Failure(new[] { outcome.ErrorMessage ?? "An unknown fetch error occurred." });
            }
        }

        /// <summary>
        /// Centralized handler to convert any exception from the main pipeline into a structured <see cref="RssFetchOutcome"/>.
        /// </summary>
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

        private RssFetchOutcome HandleNotModifiedResponse(RssSource source, HttpResponseMessage response)
        {
            _logger.LogInformation("Feed '{SourceName}' content has not changed (HTTP 304 Not Modified). The fetch cycle is complete.", source.SourceName);
            return RssFetchOutcome.Success(Enumerable.Empty<NewsItemDto>(), CleanETag(response.Headers.ETag?.Tag), GetLastModifiedFromHeaders(response.Headers));
        }

        private string? GetLastModifiedFromHeaders(HttpResponseHeaders? headers)
        {
            if (headers == null || !headers.TryGetValues("Last-Modified", out var values))
            {
                return null;
            }

            return values.FirstOrDefault();
        }

        private string? CleanETag(string? etag)
        {
            if (string.IsNullOrWhiteSpace(etag)) return null;
            return etag.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? etag : etag.Trim('"');
        }

        private string DetermineSourceItemId(SyndicationItem item, string? link, string title, Guid sourceId)
        {
            if (!string.IsNullOrWhiteSpace(item.Id) && item.Id.Length > 20) return item.Id.Truncate(NewsSourceItemIdMaxLenDb);
            if (!string.IsNullOrWhiteSpace(link) && Uri.IsWellFormedUriString(link, UriKind.Absolute)) return link.Truncate(NewsSourceItemIdMaxLenDb);

            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes($"{sourceId}_{title}_{item.PublishDate:o}"));
            return Convert.ToHexString(hash).ToLowerInvariant().Truncate(NewsSourceItemIdMaxLenDb);
        }

        private string? CleanHtmlWithHtmlAgility(string? html)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            return WebUtility.HtmlDecode(doc.DocumentNode.InnerText).Trim();
        }

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