// File: Infrastructure/Services/RssReaderService.cs

#region Usings
// Standard .NET & NuGet
using System.Data.Common; // برای DbException
// Project specific
using Application.Common.Interfaces;
using Application.DTOs.News;
using Application.Interfaces;
using AutoMapper;
using Domain.Entities;
using Hangfire; // برای IBackgroundJobClient
using HtmlAgilityPack;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Shared.Extensions;
using Shared.Results;
using System.Net;
using System.Net.Http.Headers;
using System.ServiceModel.Syndication;
using System.Text;
using System.Xml;
#endregion

namespace Infrastructure.Services
{
    public class RssReaderService : IRssReaderService
    {
        #region Private Readonly Fields
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IAppDbContext _dbContext;
        private readonly IMapper _mapper;
        private readonly ILogger<RssReaderService> _logger;
        private readonly IBackgroundJobClient _backgroundJobClient;
        private readonly AsyncRetryPolicy<HttpResponseMessage> _httpRetryPolicy;
        private readonly AsyncRetryPolicy _dbRetryPolicy; // ✅ جدید: سیاست Polly برای عملیات پایگاه داده
        #endregion

        #region Public Constants
        public const string HttpClientNamedClient = "RssFeedClientWithRetry";
        public const string DefaultUserAgent = "ForexSignalBot/1.0 (RSSFetcher; Compatible; +https://yourbotdomain.com/contact)";
        public const int DefaultHttpClientTimeoutSeconds = 60;
        #endregion

        #region Private Constants
        private const int MaxNewsSummaryLengthDb = 1000;
        private const int MaxErrorsToDeactivateSource = 10;
        #endregion

        #region Constructor
        public RssReaderService(
            IHttpClientFactory httpClientFactory,
            IAppDbContext dbContext,
            IMapper mapper,
            ILogger<RssReaderService> logger,
            IBackgroundJobClient backgroundJobClient)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _backgroundJobClient = backgroundJobClient ?? throw new ArgumentNullException(nameof(backgroundJobClient));

            // سیاست تلاش مجدد برای درخواست‌های HTTP
            _httpRetryPolicy = Policy
                .Handle<HttpRequestException>()
                .Or<TaskCanceledException>(ex => !ex.CancellationToken.IsCancellationRequested) // اگر لغو توسط CancellationToken اصلی نباشد
                .OrResult<HttpResponseMessage>(response =>
                    response.StatusCode >= HttpStatusCode.InternalServerError || // 5xx errors
                    response.StatusCode == HttpStatusCode.RequestTimeout ||     // 408
                    response.StatusCode == HttpStatusCode.TooManyRequests       // 429
                )
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: (retryAttempt, pollyResponse, context) =>
                    {
                        TimeSpan delay;
                        // اگر هدر Retry-After وجود داشته باشد، از آن استفاده کن
                        if (pollyResponse?.Result?.Headers?.RetryAfter?.Delta.HasValue == true)
                        {
                            delay = pollyResponse.Result.Headers.RetryAfter.Delta.Value.Add(TimeSpan.FromMilliseconds(new Random().Next(500, 1500)));
                            _logger.LogWarning(
                                "PollyRetry: HTTP request to {RequestUri} (Context: {ContextKey}) failed with {StatusCode}. Retry-After received. Retrying in {DelaySeconds:F1}s (Attempt {RetryAttempt}/3).",
                                pollyResponse.Result.RequestMessage?.RequestUri, context.CorrelationId, pollyResponse.Result.StatusCode, delay.TotalSeconds, retryAttempt);
                        }
                        else // در غیر این صورت، از تأخیر نمایی استفاده کن
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

            // ✅ جدید: سیاست تلاش مجدد برای عملیات پایگاه داده
            _dbRetryPolicy = Policy
                .Handle<DbException>(ex => !(ex is DbUpdateConcurrencyException)) // خطاهای پایگاه داده را مدیریت می‌کند، به جز خطاهای همزمانی
                .WaitAndRetryAsync(
                    retryCount: 3, // حداکثر 3 بار تلاش مجدد
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // تأخیر نمایی: 2s, 4s, 8s
                    onRetry: (exception, timeSpan, retryAttempt, context) =>
                    {
                        _logger.LogWarning(exception,
                            "PollyDbRetry: Database operation failed. Retrying in {TimeSpan} for attempt {RetryAttempt}. Error: {Message}",
                            timeSpan, retryAttempt, exception.Message);
                    });
        }
        #endregion

        #region IRssReaderService Implementation
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
                rssSource.LastFetchAttemptAt = DateTime.UtcNow;

                HttpResponseMessage? httpResponse = null;
                try
                {
                    var httpClient = _httpClientFactory.CreateClient(HttpClientNamedClient);

                    //  ایجاد HttpRequestMessage جدید در هر تلاش از طریق Polly
                    httpResponse = await _httpRetryPolicy.ExecuteAsync(async (pollyContext, ct) =>
                    {
                        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, rssSource.Url);
                        AddConditionalGetHeaders(requestMessage, rssSource); //  هدرها به درخواست جدید اضافه می‌شوند
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
                    httpResponse.EnsureSuccessStatusCode();
                    _logger.LogInformation("Successfully received HTTP {StatusCode} response.", httpResponse.StatusCode);

                    SyndicationFeed feed = await ParseFeedContentAsync(httpResponse, cancellationToken);
                    List<NewsItem> newNewsEntities = await ProcessAndFilterSyndicationItemsAsync(feed.Items, rssSource, cancellationToken);
                    return await SaveProcessedItemsAndDispatchNotificationsAsync(rssSource, newNewsEntities, httpResponse, cancellationToken);
                }
                // ✅✅✅ اصلاح فراخوانی UpdateRssSourceFetchStatusAsync در catch block ها ✅✅✅
                catch (HttpRequestException httpEx)
                {
                    _logger.LogError(httpEx, "HTTP error during RSS fetch. StatusCode: {StatusCode}", httpEx.StatusCode);
                    //  استخراج ETag و LastModified از httpResponse اگر null نیست
                    string? etag = CleanETag(httpResponse?.Headers.ETag?.Tag);
                    string? lastModified = GetLastModifiedFromHeaders(httpResponse?.Content?.Headers);
                    await UpdateRssSourceFetchStatusAsync(rssSource, false, etag, lastModified, cancellationToken, $"HTTP Error: {httpEx.StatusCode}");
                    return Result<IEnumerable<NewsItemDto>>.Failure($"HTTP error processing feed: {httpEx.Message} (Status: {httpResponse?.StatusCode.ToString() ?? httpEx.StatusCode?.ToString() ?? "N/A"})");
                }
                catch (XmlException xmlEx)
                {
                    _logger.LogError(xmlEx, "XML parsing error for RSS feed.");
                    string? etag = CleanETag(httpResponse?.Headers.ETag?.Tag); // httpResponse ممکن است null باشد اگر خطا قبل از دریافت پاسخ رخ دهد
                    string? lastModified = GetLastModifiedFromHeaders(httpResponse?.Content?.Headers);
                    await UpdateRssSourceFetchStatusAsync(rssSource, false, etag, lastModified, cancellationToken, "XML Parsing Error");
                    return Result<IEnumerable<NewsItemDto>>.Failure($"Invalid XML format in feed: {xmlEx.Message}");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) // Removed opCancelledEx
                {
                    _logger.LogInformation("RSS feed fetch operation was cancelled by the main CancellationToken for {SourceName}.", rssSource.SourceName);
                    return Result<IEnumerable<NewsItemDto>>.Failure("RSS fetch operation cancelled by request.");
                }
                catch (TaskCanceledException taskEx)
                {
                    _logger.LogError(taskEx, "RSS feed fetch operation timed out or was cancelled internally (TaskCanceledException).");
                    string? etag = CleanETag(httpResponse?.Headers.ETag?.Tag);
                    string? lastModified = GetLastModifiedFromHeaders(httpResponse?.Content?.Headers);
                    await UpdateRssSourceFetchStatusAsync(rssSource, false, etag, lastModified, cancellationToken, "Operation Timeout");
                    return Result<IEnumerable<NewsItemDto>>.Failure("Operation timed out or was cancelled by an internal mechanism.");
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "Unexpected critical error during RSS feed processing pipeline.");
                    string? etag = CleanETag(httpResponse?.Headers.ETag?.Tag);
                    string? lastModified = GetLastModifiedFromHeaders(httpResponse?.Content?.Headers);
                    await UpdateRssSourceFetchStatusAsync(rssSource, false, etag, lastModified, cancellationToken, "Unexpected Critical Error");
                    return Result<IEnumerable<NewsItemDto>>.Failure($"An unexpected critical error occurred: {ex.Message}");
                }
                finally { httpResponse?.Dispose(); }
            }
        }
        #endregion

        #region Feed Parsing, Item Processing Logic
        private async Task<SyndicationFeed> ParseFeedContentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        { /* ... کد قبلی ... */
            await using var feedStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var readerSettings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true, IgnoreProcessingInstructions = true, IgnoreWhitespace = true, MaxCharactersInDocument = 20 * 1024 * 1024 };
            Encoding encoding = Encoding.UTF8;
            string? charset = response.Content.Headers.ContentType?.CharSet;
            if (!string.IsNullOrWhiteSpace(charset)) { try { encoding = Encoding.GetEncoding(charset); } catch (ArgumentException) { _logger.LogWarning("Unsupported charset '{CharSet}'. Falling back to UTF-8.", charset); } }
            using var streamReader = new StreamReader(feedStream, encoding, true);
            using var xmlReader = XmlReader.Create(streamReader, readerSettings);
            return SyndicationFeed.Load(xmlReader);
        }

        private async Task<List<NewsItem>> ProcessAndFilterSyndicationItemsAsync(IEnumerable<SyndicationItem> syndicationItems, RssSource rssSource, CancellationToken cancellationToken)
        {
            var newNewsEntities = new List<NewsItem>();
            if (syndicationItems == null || !syndicationItems.Any()) return newNewsEntities;

            // ✅ اعمال سیاست تلاش مجدد Polly به فراخوانی پایگاه داده
            var existingSourceItemIds = await _dbRetryPolicy.ExecuteAsync(async () =>
            {
                return await _dbContext.NewsItems
                    .Where(n => n.RssSourceId == rssSource.Id && n.SourceItemId != null)
                    .Select(n => n.SourceItemId!)
                    .Distinct()
                    .ToHashSetAsync(cancellationToken);
            });

            _logger.LogDebug("Retrieved {Count} existing SourceItemIds for RssSourceId {RssSourceId}.", existingSourceItemIds.Count, rssSource.Id);

            var processedInThisBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // برای جلوگیری از تکرار در همین بچ

            foreach (var syndicationItem in syndicationItems.OrderByDescending(i => i.PublishDate.UtcDateTime))
            {
                if (cancellationToken.IsCancellationRequested) break;
                string? originalLink = syndicationItem.Links.FirstOrDefault(l => l.Uri != null)?.Uri.ToString();
                string title = syndicationItem.Title?.Text?.Trim() ?? "Untitled News Item";
                string itemSourceId = DetermineSourceItemId(syndicationItem, originalLink, title, rssSource.Id);

                if (string.IsNullOrWhiteSpace(itemSourceId)) { /* ... لاگ و continue ... */ continue; }
                if (processedInThisBatch.Contains(itemSourceId)) { /* ... لاگ و continue ... */ continue; } //  جلوگیری از پردازش تکراری در همین بچ
                if (existingSourceItemIds.Contains(itemSourceId)) { /* ... لاگ و continue ... */ continue; } // ✅ استفاده از متغیر صحیح

                var newsEntity = new NewsItem
                { /* ... مقداردهی ... */
                    Title = title.Truncate(490),
                    Link = (originalLink ?? itemSourceId).Truncate(2070),
                    Summary = CleanHtmlAndTruncateWithHtmlAgility(syndicationItem.Summary?.Text, MaxNewsSummaryLengthDb),
                    FullContent = CleanHtmlWithHtmlAgility(syndicationItem.Content is TextSyndicationContent tc ? tc.Text : syndicationItem.Summary?.Text),
                    ImageUrl = ExtractImageUrlWithHtmlAgility(syndicationItem, syndicationItem.Summary?.Text, syndicationItem.Content?.ToString()),
                    PublishedDate = syndicationItem.PublishDate.UtcDateTime,
                    RssSourceId = rssSource.Id,
                    SourceName = rssSource.SourceName.Truncate(140),
                    SourceItemId = itemSourceId.Truncate(490)
                };
                newNewsEntities.Add(newsEntity);
                processedInThisBatch.Add(itemSourceId); //  اضافه کردن به آیتم‌های پردازش شده در این بچ
            }
            return newNewsEntities;
        }

        private string DetermineSourceItemId(SyndicationItem item, string? primaryLink, string title, Guid rssSourceGuid)
        {
            if (!string.IsNullOrWhiteSpace(item.Id) && (Guid.TryParse(item.Id, out _) || item.Id.Length > 20 || item.Id.StartsWith("http", StringComparison.OrdinalIgnoreCase))) return item.Id;
            if (!string.IsNullOrWhiteSpace(primaryLink)) return primaryLink;
            if (!string.IsNullOrWhiteSpace(title))
            {
                // ✅ استفاده از GetHashCode() استاندارد
                return $"GENERATED_{rssSourceGuid}_{title.GetHashCode()}_{item.PublishDate.ToUnixTimeMilliseconds()}";
            }
            return Guid.NewGuid().ToString();
        }
        #endregion

        #region Database Interaction, Metadata Update, and Notification Dispatch

        /// <summary>
        /// Saves newly processed and filtered NewsItem entities to the database,
        /// updates the metadata of the RssSource (ETag, LastModified, timestamps),
        /// and then enqueues background jobs to dispatch notifications for each new item.
        /// This method consolidates database writes and notification job creation.
        /// Operations that interact with the database are protected by a Polly retry policy.
        /// </summary>
        /// <param name="rssSource">The RssSource entity being processed, its metadata will be updated.</param>
        /// <param name="newNewsEntitiesToSave">A list of new NewsItem entities that have been filtered for duplicates and are ready to be saved.</param>
        /// <param name="httpResponse">The HttpResponseMessage from the successful feed fetch, used to extract ETag and Last-Modified headers.</param>
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        /// <returns>
        /// A Result object containing an enumerable of NewsItemDto for the newly saved items if successful,
        /// or an error message if any part of the process fails.
        /// </returns>
        private async Task<Result<IEnumerable<NewsItemDto>>> SaveProcessedItemsAndDispatchNotificationsAsync(
            RssSource rssSource,
            List<NewsItem> newNewsEntitiesToSave,
            HttpResponseMessage httpResponse,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("Attempting to save {NewItemCount} new news items and dispatch notifications for RssSource: {SourceName}",
                newNewsEntitiesToSave.Count, rssSource.SourceName);

            if (newNewsEntitiesToSave == null || !newNewsEntitiesToSave.Any())
            {
                _logger.LogInformation("No new unique news items to save for '{SourceName}'. Updating RssSource metadata only.", rssSource.SourceName);
                string? etagFromResponse = CleanETag(httpResponse?.Headers.ETag?.Tag);
                string? lastModifiedFromResponse = GetLastModifiedFromHeaders(httpResponse?.Content?.Headers);
                return await UpdateRssSourceFetchStatusAsync(rssSource, true, etagFromResponse, lastModifiedFromResponse, cancellationToken, "No new news items were found to save; RssSource metadata updated if changed.");
            }

            await _dbContext.NewsItems.AddRangeAsync(newNewsEntitiesToSave, cancellationToken);
            _logger.LogDebug("Added {Count} new NewsItem entities to DbContext for RssSource '{SourceName}'.", newNewsEntitiesToSave.Count, rssSource.SourceName);

            rssSource.LastSuccessfulFetchAt = rssSource.LastFetchAttemptAt;
            rssSource.FetchErrorCount = 0;
            rssSource.UpdatedAt = DateTime.UtcNow;
            rssSource.ETag = CleanETag(httpResponse.Headers.ETag?.Tag);
            rssSource.LastModifiedHeader = GetLastModifiedFromHeaders(httpResponse.Content?.Headers);

            int changesSaved = 0;
            try
            {
                // ✅ اعمال سیاست تلاش مجدد Polly به فراخوانی SaveChangesAsync
                changesSaved = await _dbRetryPolicy.ExecuteAsync(async () =>
                {
                    return await _dbContext.SaveChangesAsync(cancellationToken);
                });
                _logger.LogInformation("Successfully saved {ChangesCount} changes to the database (including {NewItemCount} news items and metadata for RssSource '{SourceName}').",
                    changesSaved, newNewsEntitiesToSave.Count, rssSource.SourceName);
            }
            catch (DbUpdateException dbEx) // این catch بعد از تمام تلاش‌های Polly اجرا می‌شود
            {
                _logger.LogError(dbEx, "Database update error while saving new news items or updating RssSource '{SourceName}' after retries. New items will not be dispatched for notification.", rssSource.SourceName);
                return Result<IEnumerable<NewsItemDto>>.Failure($"Database error during save operation for {rssSource.SourceName}: {dbEx.InnerException?.Message ?? dbEx.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during SaveChangesAsync for RssSource '{SourceName}' after retries. New items will not be dispatched.", rssSource.SourceName);
                return Result<IEnumerable<NewsItemDto>>.Failure($"Unexpected error saving data for {rssSource.SourceName}: {ex.Message}");
            }

            if (changesSaved == 0 || !newNewsEntitiesToSave.Any()) // This condition is likely already covered by the initial check, but fine as a safeguard.
            {
                _logger.LogInformation("No database changes were saved or no new news items to dispatch for RssSource '{SourceName}'. Skipping notification dispatch.", rssSource.SourceName);
                return Result<IEnumerable<NewsItemDto>>.Success(
                    Enumerable.Empty<NewsItemDto>(),
                    $"No new news items were saved from {rssSource.SourceName}, but RssSource metadata might have been updated."
                );
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
                // ✅ Remove 'async' from the lambda, as there's no 'await' inside.
                dispatchTasks.Add(Task.Run(() => // No 'async' here
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
                // Optionally re-throw or handle partially completed state if critical
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while waiting for all notification dispatch enqueue tasks to complete for RssSource '{SourceName}'. Some notifications might not have been enqueued.", rssSource.SourceName);
                // Depending on requirements, you might want to propagate this error or handle it.
            }

            var resultDtos = _mapper.Map<IEnumerable<NewsItemDto>>(newNewsEntitiesToSave);
            return Result<IEnumerable<NewsItemDto>>.Success(
                resultDtos,
                $"{newNewsEntitiesToSave.Count} new news items were successfully fetched, saved, and notification jobs enqueued from {rssSource.SourceName}."
            );
        }

        #endregion

        private async Task<Result<IEnumerable<NewsItemDto>>> UpdateRssSourceFetchStatusAsync(
            RssSource rssSource, bool success,
            string? eTagFromResponse, string? lastModifiedFromResponse,
            CancellationToken cancellationToken, string? operationMessage = null)
        {
            rssSource.UpdatedAt = DateTime.UtcNow;
            if (!success) rssSource.LastFetchAttemptAt = DateTime.UtcNow; //  LastFetchAttemptAt در ابتدای FetchAndProcessFeedAsync ثبت شده بود

            if (success)
            {
                rssSource.LastSuccessfulFetchAt = rssSource.LastFetchAttemptAt;
                rssSource.FetchErrorCount = 0;
                if (!string.IsNullOrWhiteSpace(eTagFromResponse)) rssSource.ETag = eTagFromResponse; //  CleanETag قبلاً انجام شده
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
                // ✅ اعمال سیاست تلاش مجدد Polly به فراخوانی SaveChangesAsync
                await _dbRetryPolicy.ExecuteAsync(async () =>
                {
                    await _dbContext.SaveChangesAsync(cancellationToken);
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) // Removed opCancelledEx
            {
                _logger.LogInformation("RSS feed fetch operation was cancelled by the main CancellationToken for {SourceName}.", rssSource.SourceName);
                return Result<IEnumerable<NewsItemDto>>.Failure("RSS fetch operation cancelled by request.");
            }
            catch (DbUpdateException dbEx) // این catch بعد از تمام تلاش‌های Polly اجرا می‌شود
            {
                _logger.LogError(dbEx, "Database update error while updating RssSource '{SourceName}' status after retries.", rssSource.SourceName);
                // ممکن است بخواهید خطای سطح بالاتر را پرتاب کنید یا صرفاً لاگ بگیرید.
                // در اینجا، فقط لاگ گرفته می‌شود و اجازه می‌دهیم متد برگردد.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during SaveChangesAsync for RssSource '{SourceName}' status update after retries.", rssSource.SourceName);
            }

            if (success) return Result<IEnumerable<NewsItemDto>>.Success(Enumerable.Empty<NewsItemDto>(), operationMessage ?? "Status updated.");
            return Result<IEnumerable<NewsItemDto>>.Failure($"Failed to update status. Errors: {rssSource.FetchErrorCount}");
        }

        #region HTTP and HTML Parsing Helper Methods
        private void AddConditionalGetHeaders(HttpRequestMessage requestMessage, RssSource rssSource)
        { /* ... کد قبلی ... */ }

        private async Task<Result<IEnumerable<NewsItemDto>>> HandleNotModifiedResponseAsync(RssSource rssSource, HttpResponseMessage httpResponse, CancellationToken cancellationToken)
        { //  httpResponse اضافه شد
            _logger.LogInformation("Feed '{SourceName}' (HTTP 304 Not Modified). Updating metadata from 304 response.", rssSource.SourceName);
            return await UpdateRssSourceFetchStatusAsync(rssSource, true, CleanETag(httpResponse.Headers.ETag?.Tag), GetLastModifiedFromHeaders(httpResponse.Content?.Headers), cancellationToken, "Feed content not changed; metadata updated.");
        }

        private string? GetLastModifiedFromHeaders(HttpContentHeaders? headers)
        {
            // Returns date in RFC1123 format, suitable for HTTP headers
            return headers?.LastModified?.ToString("R");
        }

        private string? CleanETag(string? etag)
        {
            // ETags are often wrapped in double quotes, e.g., W/"<etag_value>" or "<etag_value>"
            return etag?.Trim('"');
        }

        /// <summary>
        /// Cleans HTML from the provided text using HtmlAgilityPack and truncates it to the specified maxLength.
        /// </summary>
        private string? CleanHtmlAndTruncateWithHtmlAgility(string? htmlText, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(htmlText)) return null;
            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(htmlText);
                // Decode HTML entities (e.g., & to &) and then get inner text
                string plainText = WebUtility.HtmlDecode(doc.DocumentNode.InnerText);
                return plainText.Trim().Truncate(maxLength); //  استفاده از متد توسعه‌دهنده Truncate
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean/truncate HTML. Original text (first 100 chars): '{HtmlStart}'", htmlText.Truncate(100));
                return htmlText.Truncate(maxLength); // Fallback to truncating original HTML if parsing fails
            }
        }

        /// <summary>
        /// Cleans all HTML tags from the provided text using HtmlAgilityPack, returning only plain text.
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
                _logger.LogWarning(ex, "Failed to clean HTML. Original text (first 100 chars): '{HtmlStart}'", htmlText.Truncate(100));
                return htmlText; // Fallback to original text if parsing fails
            }
        }

        /// <summary>
        /// Attempts to extract a prominent image URL from a SyndicationItem using various strategies.
        /// It checks media enclosures, OpenGraph meta tags, and then standard img tags within content.
        /// </summary>
        private string? ExtractImageUrlWithHtmlAgility(SyndicationItem item, string? summaryHtml, string? contentHtml)
        {
            // Strategy 1: Check media:content (MRSS - Media RSS)
            try
            {
                var mediaContentElement = item.ElementExtensions
                    .Where(ext => ext.OuterName == "content" && ext.OuterNamespace == "http://search.yahoo.com/mrss/")
                    .Select(ext => ext.GetObject<System.Xml.Linq.XElement>())
                    .FirstOrDefault(el => el?.Attribute("medium")?.Value == "image");

                if (mediaContentElement != null && !string.IsNullOrWhiteSpace(mediaContentElement.Attribute("url")?.Value))
                {
                    string imageUrl = mediaContentElement.Attribute("url")!.Value;
                    _logger.LogTrace("Extracted image URL from media:content: {ImageUrl}", imageUrl);
                    return MakeUrlAbsolute(item, imageUrl);
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Error parsing media:content for image."); }


            // Strategy 2: Check enclosure links specifically for images
            var enclosureImageLink = item.Links.FirstOrDefault(l =>
                l.RelationshipType?.Equals("enclosure", StringComparison.OrdinalIgnoreCase) == true &&
                l.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true &&
                l.Uri != null);
            if (enclosureImageLink != null)
            {
                _logger.LogTrace("Extracted image URL from enclosure link: {ImageUrl}", enclosureImageLink.Uri.ToString());
                return enclosureImageLink.Uri.ToString(); //  Uri.ToString() به طور خودکار URL مطلق را برمی‌گرداند
            }

            // Strategy 3: Parse HTML content (summary or full content) for OpenGraph or prominent <img> tags
            var htmlToParse = !string.IsNullOrWhiteSpace(contentHtml) ? contentHtml : summaryHtml;
            if (string.IsNullOrWhiteSpace(htmlToParse)) return null;

            var doc = new HtmlDocument();
            try { doc.LoadHtml(htmlToParse); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HtmlAgilityPack failed to load HTML for image extraction. Item: {ItemTitle}", item.Title?.Text.Truncate(50));
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

            // Try finding the "best" <img> tag (e.g., first large one, or one with specific attributes)
            // This can be complex. A simpler approach is the first <img>.
            var imgNodes = doc.DocumentNode.SelectNodes("//img[@src]");
            if (imgNodes != null)
            {
                foreach (var imgNode in imgNodes)
                {
                    var src = imgNode.GetAttributeValue("src", null);
                    if (!string.IsNullOrWhiteSpace(src) && !src.StartsWith("data:image", StringComparison.OrdinalIgnoreCase)) // Ignore inline base64 images
                    {
                        //  می‌توانید منطقی برای انتخاب "بهترین" عکس اضافه کنید (بر اساس اندازه، alt text، و ...)
                        //  فعلاً اولین عکس معتبر را برمی‌گردانیم.
                        _logger.LogTrace("Extracted image URL from <img> tag: {ImageUrl}", src);
                        return MakeUrlAbsolute(item, src);
                    }
                }
            }
            _logger.LogDebug("No suitable image URL found for item: {ItemTitle}", item.Title?.Text.Truncate(50));
            return null;
        }

        /// <summary>
        /// Converts a potentially relative image URL to an absolute URL using the feed item's base URI.
        /// </summary>
        private string? MakeUrlAbsolute(SyndicationItem item, string? imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl)) return null;
            if (Uri.IsWellFormedUriString(imageUrl, UriKind.Absolute)) return imageUrl; //  از قبل مطلق است

            Uri? baseUri = null;
            //  ابتدا BaseUri خود آیتم را بررسی کنید (اگر فید آن را ارائه دهد)
            if (item.BaseUri != null && item.BaseUri.IsAbsoluteUri)
            {
                baseUri = item.BaseUri;
            }
            //  اگر نه، BaseUri اولین لینک معتبر آیتم را امتحان کنید
            else
            {
                var firstValidLink = item.Links.FirstOrDefault(l => l.Uri != null && l.Uri.IsAbsoluteUri);
                if (firstValidLink != null)
                {
                    baseUri = firstValidLink.Uri;
                }
            }

            if (baseUri != null)
            {
                if (Uri.TryCreate(baseUri, imageUrl, out Uri? absoluteUri))
                {
                    _logger.LogTrace("Converted relative image URL '{RelativeUrl}' to absolute '{AbsoluteUrl}' using base '{BaseUrl}'.", imageUrl, absoluteUri.ToString(), baseUri.ToString());
                    return absoluteUri.ToString();
                }
                else
                {
                    _logger.LogWarning("Could not make URL '{ImageUrl}' absolute using base URI '{BaseUri}'. Returning original.", imageUrl, baseUri.ToString());
                }
            }
            else
            {
                _logger.LogDebug("No suitable BaseUri found for item '{ItemTitle}' to make image URL '{ImageUrl}' absolute.", item.Title?.Text.Truncate(50), imageUrl);
            }
            return imageUrl; //  اگر نتوانستیم مطلق کنیم یا BaseUri وجود نداشت
        }
        #endregion
    }
}