// File: Infrastructure/Services/RssReaderService.cs

#region Usings
using Application.Common.Interfaces; // For INotificationDispatchService
using Application.DTOs.News; // For NewsItemDto
using Application.Interfaces; // For IRssReaderService
using AutoMapper; // For mapping DTOs
using Dapper; // For Dapper operations
using Domain.Entities; // For NewsItem, RssSource, SignalCategory entities
using Hangfire; // For IBackgroundJobClient
using HtmlAgilityPack; // For HTML parsing
using Microsoft.Data.SqlClient; // For SqlConnection (for SQL Server)
using Microsoft.Extensions.Configuration; // To get connection string
using Microsoft.Extensions.Logging; // For logging
using Polly; // For resilience policies
using Polly.Retry; // For retry policies
using Shared.Exceptions; // For custom RepositoryException
using Shared.Extensions; // For Truncate extension method
using Shared.Results; // For Result<T> pattern
using System.Data.Common; // For DbException
using System.Globalization; // Added for CultureInfo
using System.Net; // For HttpStatusCode
using System.Net.Http.Headers; // For HTTP headers
using System.Security.Cryptography; // Added for SHA256 for more robust ID hashing
using System.ServiceModel.Syndication; // For SyndicationFeed
using System.Text; // For Encoding
using System.Xml; // For XmlReader
#endregion

namespace Infrastructure.Services
{
    public class RssReaderService : IRssReaderService
    {
        #region Private Readonly Fields
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMapper _mapper;
        private readonly ILogger<RssReaderService> _logger;
        private readonly IBackgroundJobClient _backgroundJobClient;
        private readonly string _connectionString; // Database connection string for Dapper
        private readonly AsyncRetryPolicy<HttpResponseMessage> _httpRetryPolicy;
        private readonly AsyncRetryPolicy _dbRetryPolicy; // Now for Dapper operations
        #endregion

        #region Public Constants
        public const string HttpClientNamedClient = "RssFeedClientWithRetry";
        public const string DefaultUserAgent = "ForexSignalBot/1.0 (RSSFetcher; Compatible; +https://yourbotdomain.com/contact)";
        public const int DefaultHttpClientTimeoutSeconds = 60;
        #endregion

        #region Private Constants
        private const int MaxNewsSummaryLengthDb = 1000;
        private const int MaxNewsLinkLengthDbForIndex = 450;
        private const int MaxErrorsToDeactivateSource = 10;
        private const int NewsTitleMaxLenDb = 490;
        private const int NewsSourceItemIdMaxLenDb = 490;
        private const int NewsSourceNameMaxLenDb = 140;
        #endregion

        #region Private Nested Types (Level 4: Error Categorization & Outcome Bundling)
        private enum RssFetchErrorType
        {
            None,
            TransientHttp,      // e.g., 5xx, 429, timeouts
            PermanentHttp,      // e.g., 400, 401, 403, 404, 410 (client errors indicating a bad request/resource)
            XmlParsing,
            Database,
            ContentProcessing,  // e.g., invalid item, image extraction failure
            Cancellation,
            Unexpected          // Catch-all for unknown exceptions
        }

        private record RssFetchOutcome(
            bool IsSuccess,
            IEnumerable<NewsItemDto> NewsItems,
            string? ETag,
            string? LastModifiedHeader,
            RssFetchErrorType ErrorType,
            string? ErrorMessage,
            Exception? Exception = null)
        {
            // Simplified success/failure constructors
            public static RssFetchOutcome Success(IEnumerable<NewsItemDto> newsItems, string? etag, string? lastModified) =>
                new(true, newsItems, etag, lastModified, RssFetchErrorType.None, null);
            public static RssFetchOutcome Failure(RssFetchErrorType errorType, string errorMessage, Exception? ex = null, string? etag = null, string? lastModified = null) =>
                new(false, Enumerable.Empty<NewsItemDto>(), etag, lastModified, errorType, errorMessage, ex);
        }
        #endregion

        #region Constructor (MODIFIED DB RETRY POLICY LOGGING & HTTP RETRY HANDLERS - Level 3)


        // Helper to create a new SqlConnection for Dapper
        private SqlConnection CreateConnection()
        {
            return new SqlConnection(_connectionString);
        }



        public RssReaderService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            IMapper mapper,
            ILogger<RssReaderService> logger,
            IBackgroundJobClient backgroundJobClient)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                                ?? throw new ArgumentNullException("DefaultConnection", "DefaultConnection string not found in configuration.");
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _backgroundJobClient = backgroundJobClient ?? throw new ArgumentNullException(nameof(backgroundJobClient));

            // HTTP Retry Policy (Level 3: Discriminate HTTP Errors)
            _httpRetryPolicy = Policy
                .Handle<HttpRequestException>() // Includes timeouts (TaskCanceledException with no cancellation request)
                .OrResult<HttpResponseMessage>(response =>
                    response.StatusCode >= HttpStatusCode.InternalServerError || // 5xx errors
                    response.StatusCode == HttpStatusCode.RequestTimeout || // 408
                    response.StatusCode == HttpStatusCode.TooManyRequests // 429
                )
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: (retryAttempt, pollyResponse, context) =>
                    {
                        TimeSpan delay;
                        string requestUri = pollyResponse?.Result?.RequestMessage?.RequestUri?.ToString() ?? "N/A";
                        HttpStatusCode? statusCode = pollyResponse?.Result?.StatusCode;

                        if (pollyResponse?.Result?.Headers?.RetryAfter?.Delta.HasValue == true)
                        {
                            delay = pollyResponse.Result.Headers.RetryAfter.Delta.Value.Add(TimeSpan.FromMilliseconds(new Random().Next(500, 1500)));

                        }
                        else
                        {
                            delay = TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)) + TimeSpan.FromMilliseconds(new Random().Next(0, 500));

                        }
                        return delay;
                    },
                    onRetryAsync: (pollyResponse, timespan, retryAttempt, context) =>
                    {

                        string requestUri = pollyResponse?.Result?.RequestMessage?.RequestUri?.ToString() ?? "N/A";
                        return Task.CompletedTask;
                    });

            // Database Retry Policy (Level 5: Dapper, excluding unique constraint violations)
            _dbRetryPolicy = Policy
            .Handle<DbException>(ex =>
            {
                // Level 5: More specific SQL Server error handling.
                if (ex is SqlException sqlEx)
                {
                    _logger.LogWarning(sqlEx, "PollyDbRetry: SqlException encountered. Error Number: {ErrorNumber}, Class: {ErrorClass}, State: {ErrorState}, ClientConnectionId: {ClientConnectionId}. Message: {SqlErrorMessage}",
                        sqlEx.Number, sqlEx.Class, sqlEx.State, sqlEx.ClientConnectionId, sqlEx.Message.Truncate(100));

                    // Level 5: Do not retry on unique constraint violations or primary key violations.
                    // This is crucial to avoid infinite retries on data issues.
                    if (sqlEx.Number == 2627 || sqlEx.Number == 2601) // Unique constraint violation, Primary Key violation
                    {
                        _logger.LogWarning("PollyDbRetry: Not retrying database operation due to non-transient unique/PK constraint violation. Error: {Message}", sqlEx.Message.Truncate(100));
                        return false; // Do not retry - this is a data error, not a transient network/server error.
                    }
                    // Consider other non-transient SQL errors here (e.g., login failed, syntax error)
                    if (sqlEx.Number == 18456 || sqlEx.Number == 4060) // Login failed, Cannot open database
                    {
                        _logger.LogError(sqlEx, "PollyDbRetry: Not retrying database operation due to permanent SQL error (e.g., authentication/db access issue). Error: {Message}", sqlEx.Message.Truncate(100));
                        return false;
                    }
                }
                _logger.LogWarning(ex, "PollyDbRetry: Transient database error encountered. Retrying. Error: {Message}", ex.Message.Truncate(100));
                return true; // Retry on other DbExceptions considered transient.
            })
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)) + TimeSpan.FromMilliseconds(new Random().Next(0, 500)), // Add jitter
                onRetryAsync: (exception, timeSpan, retryAttempt, context) => // Changed to onRetryAsync
                {

                    return Task.CompletedTask;
                });
        }
        #endregion

        #region IRssReaderService Implementation (FetchAndProcessFeedAsync - Level 9: Clearer Flow)
        public async Task<Result<IEnumerable<NewsItemDto>>> FetchAndProcessFeedAsync(RssSource rssSource, CancellationToken cancellationToken = default)
        {
            // Level 1: Argument validation.
            if (rssSource == null) throw new ArgumentNullException(nameof(rssSource));
            if (string.IsNullOrWhiteSpace(rssSource.Url))
            {
                // FIX: Explicit .ToString() for rssSource.Id in log message parameter
                _logger.LogWarning("RssSource '{SourceName}' (ID: {RssSourceId}) has an empty or invalid URL. Skipping fetch.", rssSource.SourceName, rssSource.Id.ToString());
                return Result<IEnumerable<NewsItemDto>>.Failure($"RSS source '{rssSource.SourceName}' URL is empty or invalid.");
            }

            // Level 2: Define correlation ID for the entire fetch cycle.
            // FIX: Explicit .ToString() for rssSource.Id in string interpolation
            string correlationId = $"RSSFetch_{rssSource.Id}_{Guid.NewGuid():N}";
            using (_logger.BeginScope(new Dictionary<string, object?>
            {
                // FIX: Explicit .ToString() for rssSource.Id in logging scope dictionary
                ["RssSourceId"] = rssSource.Id.ToString(),
                ["RssSourceName"] = rssSource.SourceName,
                ["RssUrl"] = rssSource.Url,
                ["CorrelationId"] = correlationId // Add correlation ID to all logs within this scope
            }))
            {
                _logger.LogInformation("Initiating fetch and process cycle for RSS feed. CorrelationId: {CorrelationId}", correlationId);
                rssSource.LastFetchAttemptAt = DateTime.UtcNow; // Update attempt time before HTTP call

                HttpResponseMessage? httpResponse = null;
                RssFetchOutcome outcome; // Level 4: Centralized outcome object

                try
                {
                    var httpClient = _httpClientFactory.CreateClient(HttpClientNamedClient);
                    using var requestMessage = new HttpRequestMessage(HttpMethod.Get, rssSource.Url);

                    // Level 7: Add conditional headers.
                    AddConditionalGetHeaders(requestMessage, rssSource);

                    // Level 3: HTTP execution with custom timeout and linked cancellation.
                    httpResponse = await _httpRetryPolicy.ExecuteAsync(async (pollyContext, ct) =>
                    {
                        // Use a dedicated CTS for the HttpClient timeout for this request.
                        using var requestTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(DefaultHttpClientTimeoutSeconds));
                        // Link all cancellation tokens: external (cancellationToken), Polly's internal (ct), and request-specific timeout.
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, requestTimeoutCts.Token, cancellationToken);
                        _logger.LogDebug("Polly Execute (Context: {ContextKey}): Sending HTTP GET with conditional headers for {RequestUri}.", pollyContext.CorrelationId, requestMessage.RequestUri);
                        return await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false); // Level 1: ConfigureAwait(false)
                    }, new Context(correlationId), cancellationToken).ConfigureAwait(false); // Level 1: ConfigureAwait(false)

                    // Level 3: Check for NotModified status.
                    if (httpResponse.StatusCode == HttpStatusCode.NotModified)
                    {
                        _logger.LogInformation("Feed content has not changed (HTTP 304 Not Modified). CorrelationId: {CorrelationId}", correlationId);
                        outcome = HandleNotModifiedResponse(rssSource, httpResponse, cancellationToken); // Level 1: ConfigureAwait(false)
                    }
                    else if (!httpResponse.IsSuccessStatusCode)
                    {
                        // Level 3: Handle other non-success HTTP codes immediately for classification.
                        string httpErrorMessage = $"HTTP request failed with status code {httpResponse.StatusCode} ({httpResponse.ReasonPhrase}).";
                        _logger.LogError(httpErrorMessage + " CorrelationId: {CorrelationId}", correlationId);

                        RssFetchErrorType errorType = IsPermanentHttpError(httpResponse.StatusCode) ? RssFetchErrorType.PermanentHttp : RssFetchErrorType.TransientHttp;
                        outcome = RssFetchOutcome.Failure(errorType, httpErrorMessage, new HttpRequestException(httpErrorMessage, null, httpResponse.StatusCode),
                                                           CleanETag(httpResponse.Headers.ETag?.Tag), GetLastModifiedFromHeaders(httpResponse.Headers));
                    }
                    else
                    {
                        _logger.LogInformation("Successfully received HTTP {StatusCode} response. CorrelationId: {CorrelationId}", correlationId);
                        // Level 9: Extract feed content and process items.
                        SyndicationFeed feed = await ParseFeedContentAsync(httpResponse, cancellationToken).ConfigureAwait(false); // Level 1: ConfigureAwait(false)
                        List<NewsItem> newNewsEntities = await ProcessAndFilterSyndicationItemsAsync(feed.Items, rssSource, cancellationToken).ConfigureAwait(false); // Level 1: ConfigureAwait(false)
                        outcome = await SaveProcessedItemsAndDispatchNotificationsAsync(rssSource, newNewsEntities, httpResponse, cancellationToken).ConfigureAwait(false); // Level 1: ConfigureAwait(false)
                    }
                }
                catch (HttpRequestException httpEx)
                {
                    // Level 3: Catch HttpRequestException from Polly's retries or direct failure.
                    _logger.LogError(httpEx, "HTTP error during RSS fetch. StatusCode: {StatusCode}. CorrelationId: {CorrelationId}", httpEx.StatusCode, correlationId);
                    RssFetchErrorType errorType = IsPermanentHttpError(httpEx.StatusCode) ? RssFetchErrorType.PermanentHttp : RssFetchErrorType.TransientHttp;
                    outcome = RssFetchOutcome.Failure(errorType, $"HTTP Error: {httpEx.StatusCode}", httpEx,
                                                       CleanETag(httpResponse?.Headers.ETag?.Tag), GetLastModifiedFromHeaders(httpResponse?.Headers));
                }
                catch (XmlException xmlEx)
                {
                    // Level 3: XML parsing errors.
                    _logger.LogError(xmlEx, "XML parsing error for RSS feed. CorrelationId: {CorrelationId}", correlationId);
                    outcome = RssFetchOutcome.Failure(RssFetchErrorType.XmlParsing, "XML Parsing Error", xmlEx,
                                                       CleanETag(httpResponse?.Headers.ETag?.Tag), GetLastModifiedFromHeaders(httpResponse?.Headers));
                }
                catch (OperationCanceledException opCanceledEx) when (cancellationToken.IsCancellationRequested)
                {
                    // Level 3: External cancellation.
                    _logger.LogInformation(opCanceledEx, "RSS feed fetch operation was cancelled by the main CancellationToken for {SourceName}. CorrelationId: {CorrelationId}", rssSource.SourceName, correlationId);
                    outcome = RssFetchOutcome.Failure(RssFetchErrorType.Cancellation, "Operation cancelled by request.", opCanceledEx);
                }
                catch (TaskCanceledException taskEx)
                {
                    // Level 3: Internal timeout or other TaskCanceledException.
                    _logger.LogError(taskEx, "RSS feed fetch operation timed out or was cancelled internally (TaskCanceledException). CorrelationId: {CorrelationId}", correlationId);
                    outcome = RssFetchOutcome.Failure(RssFetchErrorType.TransientHttp, "Operation Timeout", taskEx,
                                                       CleanETag(httpResponse?.Headers.ETag?.Tag), GetLastModifiedFromHeaders(httpResponse?.Headers));
                }
                catch (Exception ex)
                {
                    // Level 3: Catch-all for unexpected errors.
                    _logger.LogCritical(ex, "Unexpected critical error during RSS feed processing pipeline. CorrelationId: {CorrelationId}", correlationId);
                    outcome = RssFetchOutcome.Failure(RssFetchErrorType.Unexpected, "Unexpected Critical Error", ex,
                                                       CleanETag(httpResponse?.Headers.ETag?.Tag), GetLastModifiedFromHeaders(httpResponse?.Headers));
                }
                finally
                {
                    httpResponse?.Dispose(); // Ensure HTTP response is disposed.
                }

                // Level 9: Final RssSource status update based on outcome.
                return await UpdateRssSourceStatusAfterFetchOutcomeAsync(rssSource, outcome, cancellationToken).ConfigureAwait(false); // Level 1: ConfigureAwait(false)
            }
        }
        #endregion

        #region Feed Parsing, Item Processing Logic (MODIFIED FOR DAPPER, Level 6 & 8)
        private async Task<SyndicationFeed> ParseFeedContentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            // Level 6: Ensure stream is properly handled.
            await using var feedStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var readerSettings = new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Ignore, // Prevents external DTD loading for security/performance
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true,
                MaxCharactersInDocument = 20 * 1024 * 1024 // Increased limit for larger feeds
            };

            // Level 7: Robust character set detection.
            Encoding encoding = Encoding.UTF8;
            string? charset = response.Content.Headers.ContentType?.CharSet;
            if (!string.IsNullOrWhiteSpace(charset))
            {
                try
                {
                    encoding = Encoding.GetEncoding(charset);
                }
                catch (ArgumentException)
                {
                    _logger.LogWarning("Unsupported charset '{CharSet}' for RSS feed. Falling back to UTF-8.", charset);
                }
            }

            // Level 6: Ensure StreamReader and XmlReader are properly disposed.
            using var streamReader = new StreamReader(feedStream, encoding, true);
            using var xmlReader = XmlReader.Create(streamReader, readerSettings);

            // SyndicationFeed.Load is synchronous. If performance is critical for large feeds,
            // consider loading it in a Task.Run to offload from the thread pool.
            return await Task.Run(() => SyndicationFeed.Load(xmlReader), cancellationToken).ConfigureAwait(false); // Level 1: ConfigureAwait(false)
        }

        private async Task<List<NewsItem>> ProcessAndFilterSyndicationItemsAsync(IEnumerable<SyndicationItem> syndicationItems, RssSource rssSource, CancellationToken cancellationToken)
        {
            var newNewsEntities = new List<NewsItem>();
            if (syndicationItems == null || !syndicationItems.Any())
            {
                // FIX: Explicit .ToString() for rssSource.Id
                _logger.LogInformation("No syndication items found in the feed. Returning empty list for RssSourceId {RssSourceId}.", rssSource.Id.ToString());
                return newNewsEntities;
            }

            // Level 5: Fetch existing SourceItemIds with Dapper and retry policy.
            var existingSourceItemIds = await _dbRetryPolicy.ExecuteAsync(async () =>
            {
                using var connection = CreateConnection(); // Level 5: Connection pooling handles efficiency.
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false); // Level 1: ConfigureAwait(false)
                var sql = "SELECT SourceItemId FROM NewsItems WHERE RssSourceId = @RssSourceId AND SourceItemId IS NOT NULL;";
                // Level 5: Dapper for efficient query.
                var ids = await connection.QueryAsync<string>(sql, new { RssSourceId = rssSource.Id }).ConfigureAwait(false); // Level 1: ConfigureAwait(false)
                return ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
            }).ConfigureAwait(false); // Level 1: ConfigureAwait(false)

            // FIX: Explicit .ToString() for rssSource.Id
            _logger.LogDebug("Retrieved {Count} existing SourceItemIds for RssSourceId {RssSourceId}.", existingSourceItemIds.Count, rssSource.Id.ToString());

            var processedInThisBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Level 1: Use ForEach for clarity or parallelize if needed (for very large feeds).
            foreach (var syndicationItem in syndicationItems.OrderByDescending(i => i.PublishDate.UtcDateTime))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    // FIX: Explicit .ToString() for rssSource.Id
                    _logger.LogInformation("Processing of syndication items cancelled mid-batch for RssSourceId {RssSourceId}.", rssSource.Id.ToString());
                    break;
                }

                // Level 7: Robust link extraction.
                string? originalLink = syndicationItem.Links
                    .FirstOrDefault(l => l.RelationshipType == "alternate" || string.IsNullOrEmpty(l.RelationshipType) || l.RelationshipType == "self")?.Uri?.ToString()
                    ?? syndicationItem.Links.FirstOrDefault(l => l.Uri != null)?.Uri?.ToString();


                string title = syndicationItem.Title?.Text?.Trim() ?? "Untitled News Item";
                // Level 8: Improved SourceItemId determination.
                string itemSourceId = DetermineSourceItemId(syndicationItem, originalLink, title, rssSource.Id);

                if (string.IsNullOrWhiteSpace(itemSourceId))
                {
                    // FIX: Explicit .ToString() for rssSource.Id
                    _logger.LogWarning("Skipping syndication item with null/empty SourceItemId after determination. Title: '{Title}', Link: '{Link}' (RssSourceId: {RssSourceId})", title.Truncate(50), originalLink.Truncate(50), rssSource.Id.ToString());
                    continue;
                }
                if (!processedInThisBatch.Add(itemSourceId))
                {
                    // FIX: Explicit .ToString() for rssSource.Id
                    _logger.LogDebug("Skipping duplicate item (SourceItemId: '{SourceItemId}') in current batch for RssSourceId {RssSourceId}.", itemSourceId.Truncate(50), rssSource.Id.ToString());
                    continue;
                }
                if (existingSourceItemIds.Contains(itemSourceId))
                {
                    // FIX: Explicit .ToString() for rssSource.Id
                    _logger.LogDebug("Skipping existing item (SourceItemId: '{SourceItemId}') already in database for RssSourceId {RssSourceId}.", itemSourceId.Truncate(50), rssSource.Id.ToString());
                    continue;
                }

                var newsEntity = new NewsItem
                {
                    Id = Guid.NewGuid(), // Level 1: Ensure new GUID for Dapper insert.
                    CreatedAt = DateTime.UtcNow, // Level 1: Ensure CreatedAt for Dapper insert.
                    Title = title.Truncate(NewsTitleMaxLenDb),
                    Link = (originalLink ?? itemSourceId).Truncate(MaxNewsLinkLengthDbForIndex),
                    // Level 6: Clean HTML and extract Image URL.
                    Summary = CleanHtmlAndTruncateWithHtmlAgility(syndicationItem.Summary?.Text, MaxNewsSummaryLengthDb),
                    FullContent = CleanHtmlWithHtmlAgility(syndicationItem.Content is TextSyndicationContent tc ? tc.Text : syndicationItem.Summary?.Text),
                    ImageUrl = ExtractImageUrlWithHtmlAgility(syndicationItem, syndicationItem.Summary?.Text, syndicationItem.Content?.ToString()),
                    PublishedDate = syndicationItem.PublishDate.UtcDateTime, // Level 7: Always use UtcDateTime.
                    RssSourceId = rssSource.Id,
                    SourceName = rssSource.SourceName.Truncate(NewsSourceNameMaxLenDb),
                    SourceItemId = itemSourceId.Truncate(NewsSourceItemIdMaxLenDb)
                };
                newNewsEntities.Add(newsEntity);
            }
            // FIX: Explicit .ToString() for rssSource.Id
            _logger.LogInformation("Processed {Count} syndication items. Found {NewCount} new unique items to save for RssSourceId {RssSourceId}.", syndicationItems.Count(), newNewsEntities.Count, rssSource.Id.ToString());
            return newNewsEntities;
        }

        // Level 8: Improved SourceItemId determination.
        #region 🔍 Determine a stable SourceItemId for RSS entries
        /// <summary>
        /// Determines a stable, unique SourceItemId based on available metadata of a feed item.
        /// Prioritizes item.Id, then link, then hashes key fields.
        /// </summary>
        private string DetermineSourceItemId(SyndicationItem item, string? primaryLink, string title, Guid rssSourceGuid)
        {
            var itemId = item.Id;
            if (!string.IsNullOrWhiteSpace(itemId))
            {
                // Prefer stable-looking IDs (GUID, URN, or long-enough unique strings)
                if (Uri.IsWellFormedUriString(itemId, UriKind.Absolute) ||
                    itemId.Contains(':', StringComparison.Ordinal) ||
                    itemId.Length > 30)
                {
                    return itemId.Truncate(NewsSourceItemIdMaxLenDb);
                }
            }

            if (!string.IsNullOrWhiteSpace(primaryLink) &&
                Uri.IsWellFormedUriString(primaryLink, UriKind.Absolute))
            {
                return primaryLink.Truncate(NewsSourceItemIdMaxLenDb);
            }

            // Final fallback: deterministic SHA256 hash of sourceGuid + title + publishDate
            return GenerateDeterministicHash(title, item.PublishDate, rssSourceGuid);
        }
        #endregion

        #region ⚙️ SHA256-based fallback hash generator
        /// <summary>
        /// Generates a SHA256-based hash as a string from key fields of the RSS item.
        /// </summary>
        private static string GenerateDeterministicHash(string title, DateTimeOffset publishDate, Guid rssSourceGuid)
        {
            var input = $"{rssSourceGuid}_{title}_{publishDate.ToString("o", CultureInfo.InvariantCulture)}";
            byte[] bytes = Encoding.UTF8.GetBytes(input);

            // Reuse static SHA256 for performance (thread-safe)
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(bytes);

            // Convert to lowercase hex string manually (faster than BitConverter)
            return ConvertHashToHexString(hash).Truncate(NewsSourceItemIdMaxLenDb);
        }

        /// <summary>
        /// Converts byte array to lowercase hexadecimal string.
        /// </summary>
        private static string ConvertHashToHexString(byte[] hash)
        {
            Span<char> chars = stackalloc char[hash.Length * 2];
            for (int i = 0; i < hash.Length; i++)
            {
                byte b = hash[i];
                chars[i * 2] = GetHexChar(b >> 4);
                chars[i * 2 + 1] = GetHexChar(b & 0xF);
            }
            return new string(chars);

            static char GetHexChar(int val) => (char)(val < 10 ? '0' + val : 'a' + (val - 10));
        }
        #endregion

        #endregion

        #region Database Interaction, Metadata Update, and Notification Dispatch (MODIFIED FOR DAPPER & Level 9)

        /// <summary>
        /// Saves newly processed and filtered NewsItem entities to the database,
        /// updates the metadata of the RssSource (ETag, LastModified, timestamps),
        /// and then enqueues background jobs to dispatch notifications for each new item.
        /// Operations that interact with the database are protected by a Polly retry policy.
        /// </summary>
        /// <returns>
        /// A <see cref="Result{IEnumerable{NewsItemDto}}"/> indicating success or failure,
        /// including the mapped DTOs of the saved news items on success.
        /// </returns>
        private async Task<RssFetchOutcome> SaveProcessedItemsAndDispatchNotificationsAsync( // Changed return type to RssFetchOutcome
            RssSource rssSource,
            List<NewsItem> newNewsEntitiesToSave,
            HttpResponseMessage httpResponse,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("Attempting to save {NewItemCount} new news items and dispatch notifications for RssSource: {SourceName}",
                newNewsEntitiesToSave.Count, rssSource.SourceName);

            string? etagFromResponse = CleanETag(httpResponse?.Headers.ETag?.Tag);
            string? lastModifiedFromResponse = GetLastModifiedFromHeaders(httpResponse.Headers);

            if (newNewsEntitiesToSave == null || !newNewsEntitiesToSave.Any())
            {
                _logger.LogInformation("No new unique news items to save for '{SourceName}'. Updating RssSource metadata only.", rssSource.SourceName);
                // Level 9: Direct return with success outcome.
                return RssFetchOutcome.Success(Enumerable.Empty<NewsItemDto>(), etagFromResponse, lastModifiedFromResponse);
            }

            try
            {
                await _dbRetryPolicy.ExecuteAsync(async () =>
                {
                    using var connection = CreateConnection(); // Level 5: Connection pooling.
                    await connection.OpenAsync(cancellationToken).ConfigureAwait(false); // Level 1: ConfigureAwait(false)

                    using var transaction = connection.BeginTransaction(); // Level 5: Transaction for atomicity.
                    try
                    {
                        // Level 5: Batch insert new NewsItems using Dapper's IEnumerable parameter.
                        var insertNewsItemSql = @"
                            INSERT INTO NewsItems (
                                Id, Title, Link, Summary, FullContent, ImageUrl, PublishedDate, CreatedAt, LastProcessedAt,
                                SourceName, SourceItemId, SentimentScore, SentimentLabel, DetectedLanguage, AffectedAssets,
                                RssSourceId, IsVipOnly, AssociatedSignalCategoryId
                            ) VALUES (
                                @Id, @Title, @Link, @Summary, @FullContent, @ImageUrl, @PublishedDate, @CreatedAt, @LastProcessedAt,
                                @SourceName, @SourceItemId, @SentimentScore, @SentimentLabel, @DetectedLanguage, @AffectedAssets,
                                @RssSourceId, @IsVipOnly, @AssociatedSignalCategoryId
                            );";

                        await connection.ExecuteAsync(insertNewsItemSql, newNewsEntitiesToSave.Select(ni => new
                        {
                            ni.Id,
                            ni.Title,
                            ni.Link,
                            ni.Summary,
                            ni.FullContent,
                            ni.ImageUrl,
                            ni.PublishedDate,
                            ni.CreatedAt,
                            ni.LastProcessedAt,
                            ni.SourceName,
                            ni.SourceItemId,
                            ni.SentimentScore,
                            ni.SentimentLabel,
                            ni.DetectedLanguage,
                            ni.AffectedAssets,
                            ni.RssSourceId,
                            ni.IsVipOnly,
                            ni.AssociatedSignalCategoryId
                        }), transaction: transaction).ConfigureAwait(false); // Level 1: ConfigureAwait(false)

                        // Level 9: Update RssSource metadata BEFORE enqueuing notifications.
                        // The RssSource object is mutated here to reflect the successful fetch.
                        rssSource.LastSuccessfulFetchAt = DateTime.UtcNow; // Set to actual time of successful DB save
                        rssSource.FetchErrorCount = 0;
                        rssSource.UpdatedAt = DateTime.UtcNow; // Set to current time
                        rssSource.ETag = etagFromResponse; // Update ETag from successful response
                        rssSource.LastModifiedHeader = lastModifiedFromResponse; // Update LastModified from successful response
                        // IsActive, LastFetchAttemptAt are already updated by FetchAndProcessFeedAsync or UpdateRssSourceStatusAfterFetchOutcomeAsync

                        var updateRssSourceSql = @"
                            UPDATE RssSources SET
                                LastSuccessfulFetchAt = @LastSuccessfulFetchAt,
                                FetchErrorCount = @FetchErrorCount,
                                UpdatedAt = @UpdatedAt,
                                ETag = @ETag,
                                LastModifiedHeader = @LastModifiedHeader,
                                IsActive = @IsActive,
                                LastFetchAttemptAt = @LastFetchAttemptAt
                            WHERE Id = @Id;";

                        await connection.ExecuteAsync(updateRssSourceSql, new
                        {
                            rssSource.LastSuccessfulFetchAt,
                            rssSource.FetchErrorCount,
                            rssSource.UpdatedAt,
                            rssSource.ETag,
                            rssSource.LastModifiedHeader,
                            rssSource.IsActive,
                            rssSource.LastFetchAttemptAt,
                            rssSource.Id
                        }, transaction: transaction).ConfigureAwait(false); // Level 1: ConfigureAwait(false)

                        transaction.Commit();
                        _logger.LogInformation("Successfully saved {NewItemCount} news items and updated RssSource '{SourceName}' metadata in DB.",
                            newNewsEntitiesToSave.Count, rssSource.SourceName);
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        // Level 5: Wrap the exception in a custom RepositoryException for structured error handling.
                        throw new RepositoryException($"Dapper transaction failed during news item save or RssSource update for '{rssSource.SourceName}': {ex.Message}", ex);
                    }
                }).ConfigureAwait(false); // Level 1: ConfigureAwait(false)
            }
            catch (RepositoryException dbEx) // Catch the wrapped DB errors after retries.
            {
                _logger.LogError(dbEx, "Database error while saving new news items or updating RssSource '{SourceName}' after retries. News items will NOT be dispatched for notification.", rssSource.SourceName);
                return RssFetchOutcome.Failure(RssFetchErrorType.Database, $"Database error during save operation for {rssSource.SourceName}: {dbEx.InnerException?.Message ?? dbEx.Message}", dbEx);
            }
            catch (Exception ex) // Catch any other unexpected errors during the DB operation.
            {
                _logger.LogError(ex, "Unexpected error during Dapper save operation for RssSource '{SourceName}'. News items will NOT be dispatched.", rssSource.SourceName);
                return RssFetchOutcome.Failure(RssFetchErrorType.Unexpected, $"Unexpected error saving data for {rssSource.SourceName}: {ex.Message}", ex);
            }

            // Level 9: Enqueue notification dispatch jobs after successful DB save.
            _logger.LogInformation("Starting to enqueue notification dispatch jobs for {NewItemCount} newly saved news items from '{SourceName}'.",
                newNewsEntitiesToSave.Count, rssSource.SourceName);

            var dispatchTasks = new List<Task>();
            foreach (var savedNewsItemEntity in newNewsEntitiesToSave)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Notification dispatch enqueueing process was cancelled for RssSource '{SourceName}'. Not all items may have been enqueued.", rssSource.SourceName);
                    break;
                }

                var currentNewsItem = savedNewsItemEntity; // Capture for lambda.
                // Level 9: Use Task.Run for Hangfire enqueue to explicitly offload to a thread pool thread,
                // preventing blocking of the current async flow and allowing for fine-grained cancellation handling.
                dispatchTasks.Add(Task.Run(async () => // Make the lambda async
                {
                    // Inner cancellation check for long-running enqueue processes if any.
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        _logger.LogDebug("Enqueueing notification dispatch job for NewsItem ID: {NewsId}, Title: '{NewsTitle}'",
                            currentNewsItem.Id, currentNewsItem.Title.Truncate(50));

                        // Hangfire.Enqueue is typically quick, but make it explicit that it's a distinct operation.
                        // Pass cancellationToken if DispatchNewsNotificationAsync respects it.
                        _backgroundJobClient.Enqueue<INotificationDispatchService>(dispatcher =>
                            dispatcher.DispatchNewsNotificationAsync(currentNewsItem.Id, cancellationToken)
                        );
                        _logger.LogDebug("Successfully enqueued dispatch job for NewsItem ID: {NewsId}", currentNewsItem.Id);
                    }
                    catch (OperationCanceledException oce)
                    {
                        _logger.LogWarning(oce, "Enqueueing of notification for NewsItem ID {NewsId} was cancelled. This item will not be processed by notification service in this cycle.",
                            currentNewsItem.Id);
                    }
                    catch (Exception enqueueEx)
                    {
                        _logger.LogError(enqueueEx, "Failed to ENQUEUE notification dispatch job for NewsItemID {NewsId} from '{SourceName}'. This item will not be processed by notification service in this cycle.",
                            currentNewsItem.Id, rssSource.SourceName);
                    }
                    await Task.CompletedTask; // Ensures the async lambda returns a Task
                }, cancellationToken)); // Pass cancellationToken to Task.Run
            }

            try
            {
                await Task.WhenAll(dispatchTasks).ConfigureAwait(false); // Level 1: ConfigureAwait(false)
                _logger.LogInformation("All {TaskCount} notification dispatch jobs have been initiated for news items from '{SourceName}'.",
                    dispatchTasks.Count, rssSource.SourceName);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Enqueueing of some notification dispatch jobs was cancelled for '{SourceName}'.", rssSource.SourceName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while waiting for all notification dispatch enqueue tasks to complete for RssSource '{SourceName}'. Some notifications might not have been enqueued.", rssSource.SourceName);
            }

            // Level 9: Map and return success outcome.
            var resultDtos = _mapper.Map<IEnumerable<NewsItemDto>>(newNewsEntitiesToSave);
            return RssFetchOutcome.Success(resultDtos, etagFromResponse, lastModifiedFromResponse);
        }

        /// <summary>
        /// Updates the RssSource status in the database based on the outcome of a fetch operation.
        /// This centralizes all RssSource state updates. (Level 9: New Method)
        /// </summary>
        private async Task<Result<IEnumerable<NewsItemDto>>> UpdateRssSourceStatusAfterFetchOutcomeAsync(
            RssSource rssSource,
            RssFetchOutcome outcome,
            CancellationToken cancellationToken)
        {
            // Level 4: Update RssSource entity properties based on outcome.
            rssSource.UpdatedAt = DateTime.UtcNow; // Always update 'UpdatedAt'

            if (outcome.IsSuccess)
            {
                rssSource.LastSuccessfulFetchAt = rssSource.LastFetchAttemptAt; // Successful fetch
                rssSource.FetchErrorCount = 0; // Reset error count on success
                // Only update ETag/LastModified if they were actually provided in the response
                if (!string.IsNullOrWhiteSpace(outcome.ETag)) rssSource.ETag = outcome.ETag;
                if (!string.IsNullOrWhiteSpace(outcome.LastModifiedHeader)) rssSource.LastModifiedHeader = outcome.LastModifiedHeader;
                rssSource.IsActive = true; // Reactive if it was deactivated for transient errors
            }
            else // Fetch failed
            {
                // Level 4: Increment error count and handle deactivation based on error type.
                if (outcome.ErrorType == RssFetchErrorType.PermanentHttp || outcome.ErrorType == RssFetchErrorType.ContentProcessing || outcome.ErrorType == RssFetchErrorType.XmlParsing)
                {
                    // For permanent errors, immediately deactivate (or mark as permanently failed)
                    rssSource.FetchErrorCount = MaxErrorsToDeactivateSource; // Force max errors
                    if (rssSource.IsActive)
                    {
                        // FIX: Explicit .ToString() for rssSource.Id
                        _logger.LogWarning("Deactivated RssSource {Id} due to permanent error type {ErrorType}. Error message: {ErrorMessage}",
                            rssSource.Id.ToString(), outcome.ErrorType, outcome.ErrorMessage.Truncate(100));
                    }
                }
                else if (outcome.ErrorType == RssFetchErrorType.TransientHttp || outcome.ErrorType == RssFetchErrorType.Database || outcome.ErrorType == RssFetchErrorType.Unexpected)
                {
                    // For transient errors, increment count towards deactivation threshold
                    rssSource.FetchErrorCount++;
                    if (rssSource.FetchErrorCount >= MaxErrorsToDeactivateSource && rssSource.IsActive)
                    {
                        // FIX: Explicit .ToString() for rssSource.Id
                        _logger.LogWarning("Deactivated RssSource {Id} due to {Count} consecutive transient errors. Error message: {ErrorMessage}",
                            rssSource.Id.ToString(), rssSource.FetchErrorCount, outcome.ErrorMessage.Truncate(100));
                    }
                }
                // Cancellation doesn't increment error count, it's an intentional stop.
            }

            try
            {
                await _dbRetryPolicy.ExecuteAsync(async () =>
                {
                    using var connection = CreateConnection(); // Level 5: Connection pooling.
                    await connection.OpenAsync(cancellationToken).ConfigureAwait(false); // Level 1: ConfigureAwait(false)

                    var updateRssSourceSql = @"
                        UPDATE RssSources SET
                            LastSuccessfulFetchAt = @LastSuccessfulFetchAt,
                            FetchErrorCount = @FetchErrorCount,
                            UpdatedAt = @UpdatedAt,
                            ETag = @ETag,
                            LastModifiedHeader = @LastModifiedHeader,
                            IsActive = @IsActive,
                            LastFetchAttemptAt = @LastFetchAttemptAt
                        WHERE Id = @Id;";

                    await connection.ExecuteAsync(updateRssSourceSql, new
                    {
                        rssSource.LastSuccessfulFetchAt,
                        rssSource.FetchErrorCount,
                        rssSource.UpdatedAt,
                        rssSource.ETag,
                        rssSource.LastModifiedHeader,
                        rssSource.IsActive,
                        rssSource.LastFetchAttemptAt,
                        rssSource.Id
                    }).ConfigureAwait(false); // Level 1: ConfigureAwait(false)
                }).ConfigureAwait(false); // Level 1: ConfigureAwait(false)
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical database error while updating RssSource '{SourceName}' status after fetch outcome. This source's state might be inconsistent.", rssSource.SourceName);
                // Level 5: Wrap in custom exception.
                throw new RepositoryException($"Failed to update RssSource status for '{rssSource.SourceName}': {ex.Message}", ex);
            }

            // Level 9: Convert outcome to Result<IEnumerable<NewsItemDto>> and return.
            if (outcome.IsSuccess)
            {
                return Result<IEnumerable<NewsItemDto>>.Success(
                    outcome.NewsItems,
                    outcome.ErrorMessage ?? $"{outcome.NewsItems.Count()} news items processed for {rssSource.SourceName}."
                );
            }
            else
            {
                string finalMessage = $"Fetch failed for '{rssSource.SourceName}'. Error Type: {outcome.ErrorType}. Message: {outcome.ErrorMessage}. Current errors: {rssSource.FetchErrorCount}.";
                // FIX: Pass new string[] { ... } for errors collection, and the message separately.
                return Result<IEnumerable<NewsItemDto>>.Failure(new string[] { outcome.Exception?.Message ?? finalMessage }, finalMessage);
            }
        }
        #endregion

        #region HTTP and HTML Parsing Helper Methods (MODIFIED FOR ROBUSTNESS - Level 7)
        // Level 7: Determine if an HTTP status code indicates a permanent (non-retryable) error.
        private bool IsPermanentHttpError(HttpStatusCode? statusCode)
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

        // Level 7: Added defensive checks for RequestUri before adding headers.
        private void AddConditionalGetHeaders(HttpRequestMessage requestMessage, RssSource rssSource)
        {
            if (requestMessage.RequestUri == null)
            {
                _logger.LogWarning("Cannot add conditional headers: HttpRequestMessage.RequestUri is null.");
                return;
            }

            // ETag: If-None-Match header.
            if (!string.IsNullOrWhiteSpace(rssSource.ETag))
            {
                string formattedETag = rssSource.ETag;
                // If it doesn't start with W/" and doesn't start with ", add quotes for strong ETags.
                // Keep W/ prefix as is for weak ETags (already handled by CleanETag to keep W/ prefix).
                if (!formattedETag.StartsWith("W/", StringComparison.OrdinalIgnoreCase) && !formattedETag.StartsWith("\"", StringComparison.Ordinal))
                {
                    formattedETag = $"\"{formattedETag}\"";
                }
                try
                {
                    // Level 7: Use TryAddWithoutValidation as a fallback for strict feeds.
                    if (!requestMessage.Headers.TryAddWithoutValidation("If-None-Match", formattedETag))
                    {
                        _logger.LogWarning("Failed to add If-None-Match header with ETag '{ETag}' for {Url}. Skipping header.", rssSource.ETag.Truncate(30), requestMessage.RequestUri);
                    }
                    else
                    {
                        _logger.LogTrace("Added If-None-Match header with ETag '{ETag}' for {Url}.", formattedETag.Truncate(30), requestMessage.RequestUri);
                    }
                }
                catch (FormatException ex) // Should be rare with TryAddWithoutValidation
                {
                    _logger.LogWarning(ex, "FormatException when adding If-None-Match header with ETag '{ETag}' for {Url}. Skipping header.", rssSource.ETag.Truncate(30), requestMessage.RequestUri);
                }
            }

            // Last-Modified: If-Modified-Since header.
            if (!string.IsNullOrWhiteSpace(rssSource.LastModifiedHeader))
            {
                // Level 7: Use RFC1123 format (standard for HTTP dates).
                // DateTimeOffset.TryParse handles many common formats robustly.
                // HttpContentHeaders has LastModified; HttpResponseHeaders might not directly.
                // Correct way to get the last-modified header from HttpResponseHeaders:
                if (requestMessage.Headers.TryGetValues("Last-Modified", out IEnumerable<string>? lastModifiedValues))
                {
                    string? lastModifiedString = lastModifiedValues.FirstOrDefault();
                    if (lastModifiedString != null && DateTimeOffset.TryParse(lastModifiedString, out DateTimeOffset lastModifiedOffset))
                    {
                        requestMessage.Headers.IfModifiedSince = lastModifiedOffset;
                        _logger.LogTrace("Added If-Modified-Since header with value '{LastModified}' for {Url}.", lastModifiedOffset.ToString("R"), requestMessage.RequestUri);
                    }
                    else
                    {
                        _logger.LogWarning("Failed to parse Last-Modified header '{LastModifiedHeader}' for {Url}. Format invalid. Skipping header.", rssSource.LastModifiedHeader.Truncate(30), requestMessage.RequestUri);
                    }
                }
                else
                {
                    _logger.LogWarning("No 'Last-Modified' header value found in RssSource for {Url}. Skipping header.", requestMessage.RequestUri);
                }
            }
        }

        // Level 7: Handle 304 response.
        private RssFetchOutcome HandleNotModifiedResponse(RssSource rssSource, HttpResponseMessage httpResponse, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Feed '{SourceName}' (HTTP 304 Not Modified). Updating metadata from 304 response.", rssSource.SourceName);
            // This is a success path, so return success outcome. Status update will be handled by the main flow.
            return RssFetchOutcome.Success(Enumerable.Empty<NewsItemDto>(), CleanETag(httpResponse.Headers.ETag?.Tag), GetLastModifiedFromHeaders(httpResponse.Headers));
        }

        // Level 7: More robust Last-Modified header retrieval from HttpResponseHeaders.
        private string? GetLastModifiedFromHeaders(HttpResponseHeaders headers)
        {
            if (headers == null) return null;

            // FIX: Use TryGetValues to get the "Last-Modified" header string value(s)
            if (headers.TryGetValues("Last-Modified", out IEnumerable<string>? values))
            {
                string? lastModifiedString = values.FirstOrDefault();
                if (lastModifiedString != null && DateTimeOffset.TryParse(lastModifiedString, out DateTimeOffset lastModifiedOffset))
                {
                    // Format to RFC1123 which is the standard for HTTP dates ("R" format specifier).
                    return lastModifiedOffset.ToString("R", CultureInfo.InvariantCulture);
                }
            }
            return null;
        }

        // Level 7: Refined ETag cleaning.
        private string? CleanETag(string? etag)
        {
            if (string.IsNullOrWhiteSpace(etag)) return null;

            // Weak ETags start with W/. Keep the W/ part for integrity.
            if (etag.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) return etag;

            // For strong ETags, remove leading/trailing quotes if present.
            return etag.Trim('"');
        }

        // Other helper methods for HTML parsing (unchanged for this request)
        private string? CleanHtmlAndTruncateWithHtmlAgility(string? htmlText, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(htmlText)) return null;
            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(htmlText);
                string plainText = WebUtility.HtmlDecode(doc.DocumentNode.InnerText).Trim();
                return plainText.Truncate(maxLength);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean/truncate HTML. Falling back to simple truncation. Original text (first 100 chars): '{HtmlStart}'", htmlText.Truncate(100));
                return htmlText.Truncate(maxLength);
            }
        }

        private string? CleanHtmlWithHtmlAgility(string? htmlText)
        {
            if (string.IsNullOrWhiteSpace(htmlText)) return null;
            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(htmlText);
                return WebUtility.HtmlDecode(doc.DocumentNode.InnerText.Trim());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean HTML. Falling back to original text. Original text (first 100 chars): '{HtmlStart}'", htmlText.Truncate(100));
                return htmlText;
            }
        }

        private string? ExtractImageUrlWithHtmlAgility(SyndicationItem item, string? summaryHtml, string? contentHtml)
        {
            try
            {
                var mediaContentElement = item.ElementExtensions
                    .Where(ext => ext.OuterName == "content" && ext.OuterNamespace == "http://search.yahoo.com/mrss/")
                    .Select(ext => ext.GetObject<System.Xml.Linq.XElement>())
                    .FirstOrDefault(el => el?.Attribute("medium")?.Value == "image" && !string.IsNullOrWhiteSpace(el?.Attribute("url")?.Value));

                if (mediaContentElement != null)
                {
                    string imageUrl = mediaContentElement.Attribute("url")!.Value;
                    _logger.LogTrace("Extracted image URL from media:content: {ImageUrl}", imageUrl);
                    return MakeUrlAbsolute(item, imageUrl);
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Error parsing media:content for image from {ItemTitle}.", item.Title?.Text.Truncate(50)); }

            var enclosureImageLink = item.Links.FirstOrDefault(l =>
                l.RelationshipType?.Equals("enclosure", StringComparison.OrdinalIgnoreCase) == true &&
                l.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true &&
                l.Uri != null);
            if (enclosureImageLink != null)
            {
                _logger.LogTrace("Extracted image URL from enclosure link: {ImageUrl}", enclosureImageLink.Uri.ToString());
                return enclosureImageLink.Uri.ToString();
            }

            var htmlToParse = !string.IsNullOrWhiteSpace(contentHtml) ? contentHtml : summaryHtml;
            if (string.IsNullOrWhiteSpace(htmlToParse)) return null;

            var doc = new HtmlDocument();
            try { doc.LoadHtml(htmlToParse); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HtmlAgilityPack failed to load HTML for image extraction from {ItemTitle}. Original (first 100): '{HtmlStart}'", item.Title?.Text.Truncate(50), htmlToParse.Truncate(100));
                return null;
            }

            var ogImageNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image' or @name='og:image' or @property='twitter:image' or @name='twitter:image']");
            if (ogImageNode != null && !string.IsNullOrWhiteSpace(ogImageNode.GetAttributeValue("content", null)))
            {
                string imageUrl = ogImageNode.GetAttributeValue("content", null);
                _logger.LogTrace("Extracted image URL from OpenGraph/Twitter meta tag: {ImageUrl}", imageUrl);
                return MakeUrlAbsolute(item, imageUrl);
            }

            var imgNodes = doc.DocumentNode.SelectNodes("//img[@src]");
            if (imgNodes != null)
            {
                foreach (var imgNode in imgNodes)
                {
                    var src = imgNode.GetAttributeValue("src", null);
                    if (!string.IsNullOrWhiteSpace(src) && !src.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogTrace("Extracted image URL from <img> tag: {ImageUrl}", src);
                        return MakeUrlAbsolute(item, src);
                    }
                }
            }
            _logger.LogDebug("No suitable image URL found for item: {ItemTitle}", item.Title?.Text.Truncate(50));
            return null;
        }

        private string? MakeUrlAbsolute(SyndicationItem item, string? imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl)) return null;
            if (Uri.IsWellFormedUriString(imageUrl, UriKind.Absolute)) return imageUrl;

            Uri? baseUri = item.BaseUri?.IsAbsoluteUri == true ? item.BaseUri : null;
            if (baseUri == null)
            {
                var firstAbsoluteLink = item.Links.FirstOrDefault(l => l.Uri != null && l.Uri.IsAbsoluteUri);
                if (firstAbsoluteLink != null)
                {
                    baseUri = firstAbsoluteLink.Uri;
                }
            }

            if (baseUri != null)
            {
                if (Uri.TryCreate(baseUri, imageUrl, out Uri? absoluteUri))
                {
                    _logger.LogTrace("Converted relative image URL '{RelativeUrl}' to absolute '{AbsoluteUrl}' using base '{BaseUrl}'.", imageUrl.Truncate(50), absoluteUri.ToString().Truncate(50), baseUri.ToString().Truncate(50));
                    return absoluteUri.ToString();
                }
                else
                {
                    _logger.LogWarning("Could not make URL '{ImageUrl}' absolute using base URI '{BaseUri}'. Returning original.", imageUrl.Truncate(50), baseUri.ToString().Truncate(50));
                }
            }
            else
            {
                _logger.LogDebug("No suitable BaseUri found for item '{ItemTitle}' to make image URL '{ImageUrl}' absolute. Returning original.", item.Title?.Text.Truncate(50), imageUrl.Truncate(50));
            }
            return imageUrl;
        }
        #endregion
    }
}