// File: Infrastructure/Services/RssReaderService.cs

#region Usings
using System.Data.Common; // For DbException
using Application.Common.Interfaces; // For INotificationDispatchService
using Application.DTOs.News; // For NewsItemDto
using Application.Interfaces; // For IRssReaderService
using AutoMapper; // For mapping DTOs
using Domain.Entities; // For NewsItem, RssSource, SignalCategory entities
using Hangfire; // For IBackgroundJobClient
using HtmlAgilityPack; // For HTML parsing
using Microsoft.Extensions.Logging; // For logging
using Polly; // For resilience policies
using Polly.Retry; // For retry policies
using Shared.Extensions; // For Truncate extension method
using Shared.Results; // For Result<T> pattern
using Shared.Exceptions; // For custom RepositoryException
using System.Net; // For HttpStatusCode
using System.Net.Http.Headers; // For HTTP headers
using System.ServiceModel.Syndication; // For SyndicationFeed
using System.Text; // For Encoding
using System.Xml; // For XmlReader
using System.Security.Cryptography; // Added for SHA256 for more robust ID hashing
using Microsoft.Extensions.Configuration; // To get connection string
using Microsoft.Data.SqlClient; // For SqlConnection (for SQL Server)
using Dapper; // For Dapper operations
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

        #region Constructor (MODIFIED DB RETRY POLICY LOGGING)
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

            // HTTP Retry Policy (unchanged from previous valid version)
            _httpRetryPolicy = Policy
                .Handle<HttpRequestException>()
                .Or<TaskCanceledException>(ex => !ex.CancellationToken.IsCancellationRequested)
                .OrResult<HttpResponseMessage>(response =>
                    response.StatusCode >= HttpStatusCode.InternalServerError ||
                    response.StatusCode == HttpStatusCode.RequestTimeout ||
                    response.StatusCode == HttpStatusCode.TooManyRequests
                )
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: (retryAttempt, pollyResponse, context) =>
                    {
                        TimeSpan delay;
                        if (pollyResponse?.Result?.Headers?.RetryAfter?.Delta.HasValue == true)
                        {
                            delay = pollyResponse.Result.Headers.RetryAfter.Delta.Value.Add(TimeSpan.FromMilliseconds(new Random().Next(500, 1500)));
                            _logger.LogWarning(
                                "PollyRetry: HTTP request to {RequestUri} (Context: {ContextKey}) failed with {StatusCode}. Retry-After received. Retrying in {DelaySeconds:F1}s (Attempt {RetryAttempt}/3).",
                                pollyResponse.Result.RequestMessage?.RequestUri, context.CorrelationId, pollyResponse.Result.StatusCode, delay.TotalSeconds, retryAttempt);
                        }
                        else
                        {
                            delay = TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)) + TimeSpan.FromMilliseconds(new Random().Next(500, 1500));
                            _logger.LogWarning(pollyResponse?.Exception,
                                "PollyRetry: HTTP request to {RequestUri} (Context: {ContextKey}) failed with {StatusCode}. Retrying in {DelaySeconds:F1}s (Attempt {RetryAttempt}/3).",
                                pollyResponse?.Result?.RequestMessage?.RequestUri, context.CorrelationId, pollyResponse?.Result?.StatusCode.ToString() ?? "N/A", delay.TotalSeconds, retryAttempt);
                        }
                        return delay;
                    },
                    onRetryAsync: (pollyResponse, timespan, retryAttempt, context) =>
                    {
                        _logger.LogInformation(
                            "PollyRetry: Retrying HTTP request to {RequestUri} (Context: {ContextKey}). Attempt {RetryAttempt} of 3. Waiting for {TimespanSeconds:F1} seconds...",
                           pollyResponse?.Result?.RequestMessage?.RequestUri, context.CorrelationId, retryAttempt, timespan.TotalSeconds);
                        return Task.CompletedTask;
                    });

            // Database Retry Policy (now for Dapper, excluding unique constraint violations)
            _dbRetryPolicy = Policy
            .Handle<DbException>(ex =>
            {
                // Log SQL Server specific errors if possible
                if (ex is SqlException sqlEx)
                {
                    _logger.LogWarning(sqlEx, "PollyDbRetry: SqlException encountered. Error Number: {ErrorNumber}, Class: {ErrorClass}, State: {ErrorState}, ClientConnectionId: {ClientConnectionId}",
                        sqlEx.Number, sqlEx.Class, sqlEx.State, sqlEx.ClientConnectionId);
                    // Do not retry on unique constraint violations or primary key violations
                    if (sqlEx.Number == 2627 || sqlEx.Number == 2601) // Unique constraint violation, Primary Key violation
                    {
                        _logger.LogWarning("PollyDbRetry: Not retrying database operation due to non-transient unique/PK constraint violation. Error: {Message}", sqlEx.Message);
                        return false; // Do not retry
                    }
                }
                _logger.LogWarning(ex, "PollyDbRetry: Transient database error encountered. Retrying. Error: {Message}", ex.Message);
                return true; // Retry on other DbExceptions
            })
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (exception, timeSpan, retryAttempt, context) => // This is 'onRetry' not 'onRetryAsync'
                {
                    // Specific retry logging is now handled within the Handle<DbException> predicate above.
                    _logger.LogInformation(
                        "PollyDbRetry: Retrying database operation. Attempt {RetryAttempt} of 3. Waiting for {TimespanSeconds:F1} seconds...",
                        retryAttempt, timeSpan.TotalSeconds);
                    // REMOVED: return Task.CompletedTask; <-- This line caused the CS8030 error.
                    // 'onRetry' is an Action<Exception, TimeSpan, int, Context>, it does not return a Task.
                });
        }
        #endregion

        // Helper to create a new SqlConnection for Dapper
        private SqlConnection CreateConnection()
        {
            return new SqlConnection(_connectionString);
        }

        #region IRssReaderService Implementation (FetchAndProcessFeedAsync - Minor Update)
        public async Task<Result<IEnumerable<NewsItemDto>>> FetchAndProcessFeedAsync(RssSource rssSource, CancellationToken cancellationToken = default)
        {
            if (rssSource == null) throw new ArgumentNullException(nameof(rssSource));
            if (string.IsNullOrWhiteSpace(rssSource.Url))
                return Result<IEnumerable<NewsItemDto>>.Failure($"RSS source '{rssSource.SourceName}' URL is empty or invalid.");

            using (_logger.BeginScope(new Dictionary<string, object?>
            {
                ["RssSourceId"] = rssSource.Id,
                ["RssSourceName"] = rssSource.SourceName,
                ["RssUrl"] = rssSource.Url
            }))
            {
                _logger.LogInformation("Initiating fetch and process cycle for RSS feed.");
                rssSource.LastFetchAttemptAt = DateTime.UtcNow; // Update attempt time before HTTP call

                HttpResponseMessage? httpResponse = null;
                try
                {
                    var httpClient = _httpClientFactory.CreateClient(HttpClientNamedClient);

                    httpResponse = await _httpRetryPolicy.ExecuteAsync(async (pollyContext, ct) =>
                    {
                        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, rssSource.Url);
                        AddConditionalGetHeaders(requestMessage, rssSource); // Now robust
                        _logger.LogDebug("Polly Execute (Context: {ContextKey}): Sending HTTP GET with conditional headers.", pollyContext.CorrelationId);
                        using var requestTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(DefaultHttpClientTimeoutSeconds));
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, requestTimeoutCts.Token, cancellationToken);
                        return await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
                    }, new Context($"RSSFetch_{rssSource.Id}_{Guid.NewGuid():N}"), cancellationToken);


                    if (httpResponse.StatusCode == HttpStatusCode.NotModified)
                    {
                        _logger.LogInformation("Feed content has not changed (HTTP 304 Not Modified).");
                        return await HandleNotModifiedResponseAsync(rssSource, httpResponse, cancellationToken);
                    }
                    httpResponse.EnsureSuccessStatusCode(); // Throws HttpRequestException for non-success codes (4xx, 5xx)
                    _logger.LogInformation("Successfully received HTTP {StatusCode} response.", httpResponse.StatusCode);

                    SyndicationFeed feed = await ParseFeedContentAsync(httpResponse, cancellationToken);
                    List<NewsItem> newNewsEntities = await ProcessAndFilterSyndicationItemsAsync(feed.Items, rssSource, cancellationToken);
                    return await SaveProcessedItemsAndDispatchNotificationsAsync(rssSource, newNewsEntities, httpResponse, cancellationToken);
                }
                catch (HttpRequestException httpEx)
                {
                    _logger.LogError(httpEx, "HTTP error during RSS fetch. StatusCode: {StatusCode}", httpEx.StatusCode);
                    string? etag = CleanETag(httpResponse?.Headers.ETag?.Tag);
                    string? lastModified = GetLastModifiedFromHeaders(httpResponse?.Content?.Headers);
                    // UpdateRssSourceFetchStatusAsync now handles DB updates via Dapper.
                    // Return the result from the status update.
                    return await UpdateRssSourceFetchStatusAsync(rssSource, false, etag, lastModified, cancellationToken, $"HTTP Error: {httpEx.StatusCode}");
                }
                catch (XmlException xmlEx)
                {
                    _logger.LogError(xmlEx, "XML parsing error for RSS feed.");
                    string? etag = CleanETag(httpResponse?.Headers.ETag?.Tag);
                    string? lastModified = GetLastModifiedFromHeaders(httpResponse?.Content?.Headers);
                    return await UpdateRssSourceFetchStatusAsync(rssSource, false, etag, lastModified, cancellationToken, "XML Parsing Error");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("RSS feed fetch operation was cancelled by the main CancellationToken for {SourceName}.", rssSource.SourceName);
                    return Result<IEnumerable<NewsItemDto>>.Failure("RSS fetch operation cancelled by request.");
                }
                catch (TaskCanceledException taskEx)
                {
                    _logger.LogError(taskEx, "RSS feed fetch operation timed out or was cancelled internally (TaskCanceledException).");
                    string? etag = CleanETag(httpResponse?.Headers.ETag?.Tag);
                    string? lastModified = GetLastModifiedFromHeaders(httpResponse?.Content?.Headers);
                    return await UpdateRssSourceFetchStatusAsync(rssSource, false, etag, lastModified, cancellationToken, "Operation Timeout");
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "Unexpected critical error during RSS feed processing pipeline.");
                    string? etag = CleanETag(httpResponse?.Headers.ETag?.Tag);
                    string? lastModified = GetLastModifiedFromHeaders(httpResponse?.Content?.Headers);
                    return await UpdateRssSourceFetchStatusAsync(rssSource, false, etag, lastModified, cancellationToken, "Unexpected Critical Error");
                }
                finally { httpResponse?.Dispose(); }
            }
        }
        #endregion

        #region Feed Parsing, Item Processing Logic (MODIFIED FOR DAPPER)
        private async Task<SyndicationFeed> ParseFeedContentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            await using var feedStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var readerSettings = new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true,
                MaxCharactersInDocument = 20 * 1024 * 1024
            };
            Encoding encoding = Encoding.UTF8;
            string? charset = response.Content.Headers.ContentType?.CharSet;
            if (!string.IsNullOrWhiteSpace(charset))
            {
                try { encoding = Encoding.GetEncoding(charset); }
                catch (ArgumentException) { _logger.LogWarning("Unsupported charset '{CharSet}'. Falling back to UTF-8.", charset); }
            }
            using var streamReader = new StreamReader(feedStream, encoding, true);
            using var xmlReader = XmlReader.Create(streamReader, readerSettings);
            return SyndicationFeed.Load(xmlReader);
        }

        private async Task<List<NewsItem>> ProcessAndFilterSyndicationItemsAsync(IEnumerable<SyndicationItem> syndicationItems, RssSource rssSource, CancellationToken cancellationToken)
        {
            var newNewsEntities = new List<NewsItem>();
            if (syndicationItems == null || !syndicationItems.Any()) return newNewsEntities;

            // --- REPLACED EF CORE DB CALL WITH DAPPER ---
            var existingSourceItemIds = await _dbRetryPolicy.ExecuteAsync(async () =>
            {
                using var connection = CreateConnection();
                await connection.OpenAsync(cancellationToken);
                var sql = "SELECT SourceItemId FROM NewsItems WHERE RssSourceId = @RssSourceId AND SourceItemId IS NOT NULL;";
                var ids = await connection.QueryAsync<string>(sql, new { RssSourceId = rssSource.Id });
                return ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
            });
            // --- END REPLACEMENT ---

            _logger.LogDebug("Retrieved {Count} existing SourceItemIds for RssSourceId {RssSourceId}.", existingSourceItemIds.Count, rssSource.Id);

            var processedInThisBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var syndicationItem in syndicationItems.OrderByDescending(i => i.PublishDate.UtcDateTime))
            {
                if (cancellationToken.IsCancellationRequested) break;

                // Robust link extraction (handles potential non-HTML links or rel='alternate')
                string? originalLink = syndicationItem.Links
                    .FirstOrDefault(l => l.RelationshipType == "alternate" || string.IsNullOrEmpty(l.RelationshipType))?.Uri?.ToString()
                    ?? syndicationItem.Links.FirstOrDefault(l => l.Uri != null)?.Uri?.ToString();


                string title = syndicationItem.Title?.Text?.Trim() ?? "Untitled News Item";
                string itemSourceId = DetermineSourceItemId(syndicationItem, originalLink, title, rssSource.Id);

                if (string.IsNullOrWhiteSpace(itemSourceId))
                {
                    _logger.LogWarning("Skipping syndication item with null/empty SourceItemId after determination. Title: '{Title}', Link: '{Link}'", title.Truncate(50), originalLink.Truncate(50));
                    continue;
                }
                if (!processedInThisBatch.Add(itemSourceId))
                {
                    _logger.LogDebug("Skipping duplicate item (SourceItemId: '{SourceItemId}') in current batch.", itemSourceId.Truncate(50));
                    continue;
                }
                if (existingSourceItemIds.Contains(itemSourceId))
                {
                    _logger.LogDebug("Skipping existing item (SourceItemId: '{SourceItemId}').", itemSourceId.Truncate(50));
                    continue;
                }

                var newsEntity = new NewsItem
                {
                    Id = Guid.NewGuid(), // Ensure new GUID for Dapper insert
                    CreatedAt = DateTime.UtcNow, // Ensure CreatedAt for Dapper insert
                    Title = title.Truncate(NewsTitleMaxLenDb),
                    Link = (originalLink ?? itemSourceId).Truncate(MaxNewsLinkLengthDbForIndex),
                    Summary = CleanHtmlAndTruncateWithHtmlAgility(syndicationItem.Summary?.Text, MaxNewsSummaryLengthDb),
                    FullContent = CleanHtmlWithHtmlAgility(syndicationItem.Content is TextSyndicationContent tc ? tc.Text : syndicationItem.Summary?.Text),
                    ImageUrl = ExtractImageUrlWithHtmlAgility(syndicationItem, syndicationItem.Summary?.Text, syndicationItem.Content?.ToString()),
                    PublishedDate = syndicationItem.PublishDate.UtcDateTime,
                    RssSourceId = rssSource.Id,
                    SourceName = rssSource.SourceName.Truncate(NewsSourceNameMaxLenDb),
                    SourceItemId = itemSourceId.Truncate(NewsSourceItemIdMaxLenDb)
                    // Other NewsItem properties will use their default values or be set by subsequent analysis
                };
                newNewsEntities.Add(newsEntity);
            }
            return newNewsEntities;
        }

        private string DetermineSourceItemId(SyndicationItem item, string? primaryLink, string title, Guid rssSourceGuid)
        {
            if (!string.IsNullOrWhiteSpace(item.Id))
            {
                if (item.Id.Length > 20 || Uri.IsWellFormedUriString(item.Id, UriKind.Absolute) || item.Id.Contains(":"))
                {
                    return item.Id.Truncate(NewsSourceItemIdMaxLenDb);
                }
            }
            if (!string.IsNullOrWhiteSpace(primaryLink))
            {
                return primaryLink.Truncate(NewsSourceItemIdMaxLenDb);
            }
            if (!string.IsNullOrWhiteSpace(title))
            {
                using (var sha256Hash = SHA256.Create())
                {
                    var data = Encoding.UTF8.GetBytes($"{rssSourceGuid}_{title}_{item.PublishDate.ToString("o")}");
                    var hashBytes = sha256Hash.ComputeHash(data);
                    return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                }
            }
            return Guid.NewGuid().ToString();
        }
        #endregion

        #region Database Interaction, Metadata Update, and Notification Dispatch (MODIFIED FOR DAPPER)

        /// <summary>
        /// Saves newly processed and filtered NewsItem entities to the database,
        /// updates the metadata of the RssSource (ETag, LastModified, timestamps),
        /// and then enqueues background jobs to dispatch notifications for each new item.
        /// Operations that interact with the database are protected by a Polly retry policy.
        /// </summary>
        private async Task<Result<IEnumerable<NewsItemDto>>> SaveProcessedItemsAndDispatchNotificationsAsync(
            RssSource rssSource,
            List<NewsItem> newNewsEntitiesToSave,
            HttpResponseMessage httpResponse,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("Attempting to save {NewItemCount} new news items and dispatch notifications for RssSource: {SourceName}",
                newNewsEntitiesToSave.Count, rssSource.SourceName);

            string? etagFromResponse = CleanETag(httpResponse?.Headers.ETag?.Tag);
            string? lastModifiedFromResponse = GetLastModifiedFromHeaders(httpResponse?.Content?.Headers);

            if (newNewsEntitiesToSave == null || !newNewsEntitiesToSave.Any())
            {
                _logger.LogInformation("No new unique news items to save for '{SourceName}'. Updating RssSource metadata only.", rssSource.SourceName);
                // Directly call the status update helper, which now uses Dapper.
                return await UpdateRssSourceFetchStatusAsync(rssSource, true, etagFromResponse, lastModifiedFromResponse, cancellationToken, "No new news items were found to save; RssSource metadata updated if changed.");
            }

            try
            {
                await _dbRetryPolicy.ExecuteAsync(async () =>
                {
                    using var connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    using var transaction = connection.BeginTransaction();
                    try
                    {
                        // 1. Insert new NewsItems (batch insert)
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

                        // Dapper can take IEnumerable for parameters to perform a batch insert
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
                        }), transaction: transaction);

                        // 2. Update RssSource metadata
                        var updateRssSourceSql = @"
                            UPDATE RssSources SET
                                LastSuccessfulFetchAt = @LastSuccessfulFetchAt,
                                FetchErrorCount = @FetchErrorCount,
                                UpdatedAt = @UpdatedAt,
                                ETag = @ETag,
                                LastModifiedHeader = @LastModifiedHeader,
                                IsActive = @IsActive,
                                LastFetchAttemptAt = @LastFetchAttemptAt -- Ensure this is also updated
                            WHERE Id = @Id;";

                        await connection.ExecuteAsync(updateRssSourceSql, new
                        {
                            rssSource.LastSuccessfulFetchAt,
                            rssSource.FetchErrorCount,
                            rssSource.UpdatedAt,
                            ETag = etagFromResponse, // Use the one from the response directly
                            LastModifiedHeader = lastModifiedFromResponse, // Use the one from the response directly
                            rssSource.IsActive,
                            rssSource.LastFetchAttemptAt, // Pass the already updated value
                            rssSource.Id
                        }, transaction: transaction);

                        transaction.Commit();
                        _logger.LogInformation("Successfully saved {NewItemCount} news items and updated RssSource '{SourceName}' metadata.",
                            newNewsEntitiesToSave.Count, rssSource.SourceName);
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        // Wrap the exception in a custom RepositoryException
                        throw new RepositoryException($"Dapper transaction failed during save: {ex.Message}", ex);
                    }
                });
            }
            catch (RepositoryException dbEx) // Catch the wrapped DB errors
            {
                _logger.LogError(dbEx, "Database error while saving new news items or updating RssSource '{SourceName}' after retries. New items will not be dispatched for notification.", rssSource.SourceName);
                return Result<IEnumerable<NewsItemDto>>.Failure($"Database error during save operation for {rssSource.SourceName}: {dbEx.InnerException?.Message ?? dbEx.Message}");
            }
            catch (Exception ex) // Catch any other unexpected errors
            {
                _logger.LogError(ex, "Unexpected error during Dapper save operation for RssSource '{SourceName}'. New items will not be dispatched.", rssSource.SourceName);
                return Result<IEnumerable<NewsItemDto>>.Failure($"Unexpected error saving data for {rssSource.SourceName}: {ex.Message}");
            }

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

                var currentNewsItem = savedNewsItemEntity;
                // Using Task.Run to offload the Hangfire enqueue call from the main thread pool
                // This is generally okay for very short, non-blocking calls or to ensure task execution continues
                // even if the outer loop is cancelled. Hangfire.Enqueue itself is usually very fast.
                dispatchTasks.Add(Task.Run(() =>
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    try
                    {
                        _logger.LogDebug("Enqueueing notification dispatch job for NewsItem ID: {NewsId}, Title: '{NewsTitle}'",
                            currentNewsItem.Id, currentNewsItem.Title.Truncate(50));

                        _backgroundJobClient.Enqueue<INotificationDispatchService>(dispatcher =>
                            dispatcher.DispatchNewsNotificationAsync(currentNewsItem.Id, CancellationToken.None)
                        );
                        _logger.LogDebug("Successfully enqueued dispatch job for NewsItem ID: {NewsId}", currentNewsItem.Id);
                    }
                    catch (Exception enqueueEx)
                    {
                        _logger.LogError(enqueueEx, "Failed to ENQUEUE notification dispatch job for NewsItemID {NewsId} from '{SourceName}'. This item will not be processed by notification service in this cycle.",
                            currentNewsItem.Id, rssSource.SourceName);
                    }
                }, cancellationToken));
            }

            try
            {
                await Task.WhenAll(dispatchTasks);
                _logger.LogInformation("All {TaskCount} notification dispatch jobs have been enqueued for news items from '{SourceName}'.",
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

            var resultDtos = _mapper.Map<IEnumerable<NewsItemDto>>(newNewsEntitiesToSave);
            return Result<IEnumerable<NewsItemDto>>.Success(
                resultDtos,
                $"{newNewsEntitiesToSave.Count} new news items were successfully fetched, saved, and notification jobs enqueued from {rssSource.SourceName}."
            );
        }

        #endregion

        #region HTTP and HTML Parsing Helper Methods (MODIFIED FOR ROBUSTNESS)
        private void AddConditionalGetHeaders(HttpRequestMessage requestMessage, RssSource rssSource)
        {
            // --- FIX FOR FORMATEXCEPTION: ETag and Last-Modified Parsing ---
            // ETag: HttpRequestHeaders.Add expects a correctly formatted ETag.
            // My CleanETag method now handles "W/" prefix and ensures quotes.
            if (!string.IsNullOrWhiteSpace(rssSource.ETag))
            {
                string formattedETag = rssSource.ETag;
                // If it doesn't start with W/" and doesn't start with ", add quotes
                if (!formattedETag.StartsWith("W/\"", StringComparison.OrdinalIgnoreCase) && !formattedETag.StartsWith("\"", StringComparison.Ordinal))
                {
                    formattedETag = $"\"{formattedETag}\"";
                }
                try
                {
                    requestMessage.Headers.Add("If-None-Match", formattedETag);
                }
                catch (FormatException ex)
                {
                    _logger.LogWarning(ex, "Failed to add If-None-Match header with ETag '{ETag}'. Format invalid. Attempting with validation bypass.", rssSource.ETag);
                    // FALLBACK: If standard Add fails, try AddWithoutValidation
                    if (!requestMessage.Headers.TryAddWithoutValidation("If-None-Match", formattedETag))
                    {
                        _logger.LogWarning("Failed to add If-None-Match header with ETag '{ETag}' even with validation bypass. Skipping header.", rssSource.ETag);
                    }
                }
            }

            // Last-Modified: Use TryParseExact for robust parsing
            if (!string.IsNullOrWhiteSpace(rssSource.LastModifiedHeader))
            {
                // Common HTTP date formats (RFC1123, ISO 8601 variations)
                string[] formats = new[] { "R", "r", "ddd, dd MMM yyyy HH:mm:ss 'GMT'", "O", "o", "yyyy-MM-ddTHH:mm:ssZ" };
                if (DateTimeOffset.TryParseExact(rssSource.LastModifiedHeader, formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset lastModifiedOffset))
                {
                    requestMessage.Headers.IfModifiedSince = lastModifiedOffset;
                }
                else
                {
                    _logger.LogWarning("Failed to parse Last-Modified header '{LastModifiedHeader}'. Format invalid. Skipping header.", rssSource.LastModifiedHeader);
                }
            }
            _logger.LogTrace("Added conditional headers: ETag '{ETag}', Last-Modified '{LastModified}' for {Url}",
                             rssSource.ETag.Truncate(30), rssSource.LastModifiedHeader, requestMessage.RequestUri);
        }

        private async Task<Result<IEnumerable<NewsItemDto>>> HandleNotModifiedResponseAsync(RssSource rssSource, HttpResponseMessage httpResponse, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Feed '{SourceName}' (HTTP 304 Not Modified). Updating metadata from 304 response.", rssSource.SourceName);
            return await UpdateRssSourceFetchStatusAsync(rssSource, true, CleanETag(httpResponse.Headers.ETag?.Tag), GetLastModifiedFromHeaders(httpResponse.Content?.Headers), cancellationToken, "Feed content not changed; metadata updated.");
        }

        private string? GetLastModifiedFromHeaders(HttpContentHeaders? headers)
        {
            // Use 'R' for RFC1123 pattern for robust parsing later
            return headers?.LastModified?.ToString("R");
        }

        private string? CleanETag(string? etag)
        {
            // Store ETag without outer quotes but expect them to be re-added when sending.
            // If the ETag is a weak ETag (W/"..."), keep the W/ prefix.
            if (string.IsNullOrWhiteSpace(etag)) return null;
            if (etag.StartsWith("W/\"", StringComparison.OrdinalIgnoreCase)) return etag; // Keep W/ prefix
            return etag.Trim('"'); // Remove only outer quotes for strong ETags
        }

        /// <summary>
        /// Updates RssSource status in the database using Dapper.
        /// This is a crucial helper for FetchAndProcessFeedAsync and HandleNotModifiedResponseAsync.
        /// </summary>
        private async Task<Result<IEnumerable<NewsItemDto>>> UpdateRssSourceFetchStatusAsync(
            RssSource rssSource, bool success,
            string? eTagFromResponse, string? lastModifiedFromResponse,
            CancellationToken cancellationToken, string? operationMessage = null)
        {
            // Update RssSource entity properties before saving
            rssSource.UpdatedAt = DateTime.UtcNow;
            if (!success) rssSource.LastFetchAttemptAt = DateTime.UtcNow; // Only update attempt if not successful initially

            if (success)
            {
                rssSource.LastSuccessfulFetchAt = rssSource.LastFetchAttemptAt; // Successful fetch sets LastSuccessfulFetchAt to LastFetchAttemptAt
                rssSource.FetchErrorCount = 0;
                // Only update ETag/LastModified if they were actually provided in the response
                if (!string.IsNullOrWhiteSpace(eTagFromResponse)) rssSource.ETag = eTagFromResponse;
                if (!string.IsNullOrWhiteSpace(lastModifiedFromResponse)) rssSource.LastModifiedHeader = lastModifiedFromResponse;
            }
            else
            {
                rssSource.FetchErrorCount++;
                if (rssSource.FetchErrorCount >= MaxErrorsToDeactivateSource && rssSource.IsActive)
                {
                    rssSource.IsActive = false;
                    _logger.LogWarning("Deactivated RssSource {Id} due to {Count} consecutive errors.", rssSource.Id, rssSource.FetchErrorCount);
                }
            }

            try
            {
                await _dbRetryPolicy.ExecuteAsync(async () =>
                {
                    using var connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

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
                        rssSource.ETag, // Use the updated ETag property from the RssSource entity
                        rssSource.LastModifiedHeader, // Use the updated LastModifiedHeader property
                        rssSource.IsActive,
                        rssSource.LastFetchAttemptAt,
                        rssSource.Id
                    });
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating RssSource '{SourceName}' status in the database.", rssSource.SourceName);
                // Wrap in custom exception for consistency
                throw new RepositoryException($"Failed to update RssSource status for '{rssSource.SourceName}': {ex.Message}");
            }

            if (success)
            {
                return Result<IEnumerable<NewsItemDto>>.Success(Enumerable.Empty<NewsItemDto>(), operationMessage ?? "Status updated.");
            }
            return Result<IEnumerable<NewsItemDto>>.Failure($"Failed to update status. Errors: {rssSource.FetchErrorCount}. " + (operationMessage ?? ""));
        }


        // Other helper methods for HTML parsing (unchanged for this request)
        /// <summary>
        /// Cleans HTML from the provided text using HtmlAgilityPack and truncates it.
        /// Handles HTML entities and falls back to simple truncation on parsing failure.
        /// </summary>
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

        /// <summary>
        /// Cleans all HTML tags from the provided text, returning only plain text.
        /// Handles HTML entities and falls back to original text on parsing failure.
        /// </summary>
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

        /// <summary>
        /// Attempts to extract a prominent image URL from a SyndicationItem using various strategies.
        /// It checks MRSS, enclosure links, OpenGraph/Twitter meta tags, and img tags.
        /// </summary>
        private string? ExtractImageUrlWithHtmlAgility(SyndicationItem item, string? summaryHtml, string? contentHtml)
        {
            // Strategy 1: Check media:content (MRSS - Media RSS)
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

            // Strategy 2: Check enclosure links specifically for images
            var enclosureImageLink = item.Links.FirstOrDefault(l =>
                l.RelationshipType?.Equals("enclosure", StringComparison.OrdinalIgnoreCase) == true &&
                l.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true &&
                l.Uri != null);
            if (enclosureImageLink != null)
            {
                _logger.LogTrace("Extracted image URL from enclosure link: {ImageUrl}", enclosureImageLink.Uri.ToString());
                return enclosureImageLink.Uri.ToString();
            }

            // Strategy 3: Parse HTML content (from content, then summary) for OpenGraph or prominent <img> tags
            var htmlToParse = !string.IsNullOrWhiteSpace(contentHtml) ? contentHtml : summaryHtml;
            if (string.IsNullOrWhiteSpace(htmlToParse)) return null;

            var doc = new HtmlDocument();
            try { doc.LoadHtml(htmlToParse); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HtmlAgilityPack failed to load HTML for image extraction from {ItemTitle}. Original (first 100): '{HtmlStart}'", item.Title?.Text.Truncate(50), htmlToParse.Truncate(100));
                return null;
            }

            // Try OpenGraph image meta tag (og:image) or Twitter image tag
            var ogImageNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image' or @name='og:image' or @property='twitter:image' or @name='twitter:image']");
            if (ogImageNode != null && !string.IsNullOrWhiteSpace(ogImageNode.GetAttributeValue("content", null)))
            {
                string imageUrl = ogImageNode.GetAttributeValue("content", null);
                _logger.LogTrace("Extracted image URL from OpenGraph/Twitter meta tag: {ImageUrl}", imageUrl);
                return MakeUrlAbsolute(item, imageUrl);
            }

            // Try finding a suitable <img> tag - prioritize by presence, then dimensions (if possible after download, not directly in this loop)
            var imgNodes = doc.DocumentNode.SelectNodes("//img[@src]");
            if (imgNodes != null)
            {
                // A more robust image selection might involve trying to infer image size from attributes
                // or prioritizing the first img tag found. For simplicity, just return the first valid non-base64 one.
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

        /// <summary>
        /// Converts a potentially relative image URL to an absolute URL using the feed item's base URI or primary link.
        /// </summary>
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