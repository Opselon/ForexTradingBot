﻿// File: Infrastructure/Services/RssReaderService.cs

#region Usings
// Standard .NET & NuGet
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
                catch (OperationCanceledException opCancelledEx) when (cancellationToken.IsCancellationRequested)
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
        { /* ... کد قبلی ... */
            var newNewsEntities = new List<NewsItem>();
            if (syndicationItems == null || !syndicationItems.Any()) return newNewsEntities;

            var existingSourceItemIds = await _dbContext.NewsItems // ✅ این متغیر اینجا تعریف شده
                .Where(n => n.RssSourceId == rssSource.Id && n.SourceItemId != null)
                .Select(n => n.SourceItemId!)
                .Distinct()
                .ToHashSetAsync(cancellationToken);
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
        // File: Infrastructure/Services/RssReaderService.cs (بخشی از کلاس)

        #region Database Interaction and Dispatch
        /// <summary>
        /// Saves newly processed and filtered NewsItem entities to the database,
        /// updates the metadata of the RssSource (ETag, LastModified, timestamps),
        /// and then enqueues background jobs to dispatch notifications for each new item.
        /// This method consolidates database writes and notification job creation.
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
            HttpResponseMessage httpResponse, //  این باید non-nullable باشد اگر به اینجا رسیده‌ایم
            CancellationToken cancellationToken)
        {
            // لاگ کردن شروع این مرحله مهم
            _logger.LogInformation("Attempting to save {NewItemCount} new news items and dispatch notifications for RssSource: {SourceName}",
                newNewsEntitiesToSave.Count, rssSource.SourceName);

            // اگر هیچ آیتم جدیدی برای ذخیره وجود ندارد، فقط متادیتای منبع RSS را آپدیت کن
            if (newNewsEntitiesToSave == null || !newNewsEntitiesToSave.Any())
            {
                _logger.LogInformation("No new unique news items to save for '{SourceName}'. Updating RssSource metadata only.", rssSource.SourceName);
                //  ETag و LastModified از پاسخ HTTP (حتی اگر 304 بوده و اینجا رسیده‌ایم، یعنی در HandleNotModifiedResponseAsync خطا بوده)
                string? etagFromResponse = CleanETag(httpResponse?.Headers.ETag?.Tag);
                string? lastModifiedFromResponse = GetLastModifiedFromHeaders(httpResponse?.Content?.Headers);
                return await UpdateRssSourceFetchStatusAsync(rssSource, true, etagFromResponse, lastModifiedFromResponse, cancellationToken, "No new news items were found to save; RssSource metadata updated if changed.");
            }

            // اضافه کردن موجودیت‌های NewsItem جدید به DbContext برای ذخیره‌سازی
            await _dbContext.NewsItems.AddRangeAsync(newNewsEntitiesToSave, cancellationToken);
            _logger.LogDebug("Added {Count} new NewsItem entities to DbContext for RssSource '{SourceName}'.", newNewsEntitiesToSave.Count, rssSource.SourceName);

            // آپدیت کردن متادیتای RssSource بر اساس پاسخ HTTP موفق
            rssSource.LastSuccessfulFetchAt = rssSource.LastFetchAttemptAt; // زمان آخرین تلاش موفق، همین الان بود
            rssSource.FetchErrorCount = 0; // ریست کردن تعداد خطاها در صورت fetch موفق
            rssSource.UpdatedAt = DateTime.UtcNow; // زمان آپدیت رکورد RssSource
            rssSource.ETag = CleanETag(httpResponse.Headers.ETag?.Tag); // ذخیره ETag جدید (اگر وجود دارد)
            rssSource.LastModifiedHeader = GetLastModifiedFromHeaders(httpResponse.Content?.Headers); // ذخیره LastModified جدید (اگر وجود دارد)

            int changesSaved = 0;
            try
            {
                // ذخیره تمام تغییرات (NewsItems جدید و RssSource آپدیت شده) در یک تراکنش واحد
                changesSaved = await _dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Successfully saved {ChangesCount} changes to the database (including {NewItemCount} news items and metadata for RssSource '{SourceName}').",
                    changesSaved, newNewsEntitiesToSave.Count, rssSource.SourceName);
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogError(dbEx, "Database update error while saving new news items or updating RssSource '{SourceName}'. New items will not be dispatched for notification.", rssSource.SourceName);
                // در صورت بروز خطا در ذخیره‌سازی، از ارسال نوتیفیکیشن صرف نظر می‌کنیم
                return Result<IEnumerable<NewsItemDto>>.Failure($"Database error during save operation for {rssSource.SourceName}: {dbEx.InnerException?.Message ?? dbEx.Message}");
            }
            catch (Exception ex) // سایر خطاهای احتمالی SaveChangesAsync
            {
                _logger.LogError(ex, "Unexpected error during SaveChangesAsync for RssSource '{SourceName}'. New items will not be dispatched.", rssSource.SourceName);
                return Result<IEnumerable<NewsItemDto>>.Failure($"Unexpected error saving data for {rssSource.SourceName}: {ex.Message}");
            }

            // اگر هیچ تغییری ذخیره نشده (که بعید است اگر newNewsEntitiesToSave آیتم داشته باشد)،
            // یا اگر آیتم جدیدی وجود نداشته، از ارسال نوتیفیکیشن صرف نظر کن.
            if (changesSaved == 0 || !newNewsEntitiesToSave.Any())
            {
                _logger.LogInformation("No database changes were saved or no new news items to dispatch for RssSource '{SourceName}'. Skipping notification dispatch.", rssSource.SourceName);
                //  اگرچه آیتم جدیدی نبود، اما متادیتای RssSource ممکن است آپدیت شده باشد (اگر changesSaved > 0 بود).
                //  در این حالت، یک نتیجه موفقیت‌آمیز بدون DTO برمی‌گردانیم.
                return Result<IEnumerable<NewsItemDto>>.Success(
                    Enumerable.Empty<NewsItemDto>(), //  هیچ NewsItemDto جدیدی برای برگرداندن نیست
                    $"No new news items were saved from {rssSource.SourceName}, but RssSource metadata might have been updated."
                );
            }

            // مرحله ارسال نوتیفیکیشن برای اخبار جدیدی که با موفقیت در دیتابیس ذخیره شده‌اند.
            // این موجودیت‌ها حالا Id معتبر از دیتابیس دارند.
            _logger.LogInformation("Starting to enqueue notification dispatch jobs for {NewItemCount} newly saved news items from '{SourceName}'.",
                newNewsEntitiesToSave.Count, rssSource.SourceName);

            var dispatchTasks = new List<Task>();
            foreach (var savedNewsItemEntity in newNewsEntitiesToSave) //  حالا savedNewsItemEntity.Id معتبر است
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Notification dispatch enqueueing process was cancelled for RssSource '{SourceName}'. Not all items may have been enqueued.", rssSource.SourceName);
                    break; // خروج از حلقه در صورت کنسل شدن
                }

                // کپی کردن متغیر حلقه برای جلوگیری از مشکلات Closure در Task.Run (اگرچه در C# جدیدتر کمتر مشکل‌ساز است)
                var currentNewsItem = savedNewsItemEntity;
                dispatchTasks.Add(Task.Run(async () => // استفاده از Task.Run برای اجرای Enqueue به صورت ناهمزمان (اختیاری)
                {
                    if (cancellationToken.IsCancellationRequested) return; // بررسی مجدد توکن در داخل Task
                    try
                    {
                        _logger.LogDebug("Enqueueing notification dispatch job for NewsItem ID: {NewsId}, Title: '{NewsTitle}'",
                            currentNewsItem.Id, currentNewsItem.Title.Truncate(50));

                        // ✅✅✅ اصلاح: فقط currentNewsItem.Id را به عنوان پارامتر پاس دهید ✅✅✅
                        _backgroundJobClient.Enqueue<INotificationDispatchService>(dispatcher =>
                            dispatcher.DispatchNewsNotificationAsync(currentNewsItem.Id, CancellationToken.None) // CancellationToken.None برای خود جاب Hangfire
                        );
                        _logger.LogDebug("Successfully enqueued dispatch job for NewsItem ID: {NewsId}", currentNewsItem.Id);
                    }
                    catch (Exception enqueueEx)
                    {
                        // ثبت خطای مربوط به قرار دادن جاب در صف Hangfire.
                        // این خطا معمولاً به معنی مشکل در سریالایز کردن آرگومان‌ها یا ارتباط با Storage هنگ‌فایر است.
                        _logger.LogError(enqueueEx, "Failed to ENQUEUE notification dispatch job for NewsItemID {NewsId} from '{SourceName}'. This item will not be processed by notification service in this cycle.",
                            currentNewsItem.Id, rssSource.SourceName);
                        //  می‌توانید این NewsItem.Id را در یک لیست خطاها برای بررسی‌های بعدی ذخیره کنید.
                    }
                }, cancellationToken)); // پاس دادن توکن به Task.Run
            }

            try
            {
                await Task.WhenAll(dispatchTasks); // منتظر بمانید تا تمام عملیات Enqueue تکمیل شوند (معمولاً سریع است)
                _logger.LogInformation("All {TaskCount} notification dispatch jobs have been enqueued for news items from '{SourceName}'.",
                    dispatchTasks.Count, rssSource.SourceName);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Enqueueing of some notification dispatch jobs was cancelled for '{SourceName}'.", rssSource.SourceName);
            }
            catch (Exception ex) // خطای احتمالی در Task.WhenAll (اگر یکی از Task ها Exception مدیریت نشده داشته باشد)
            {
                _logger.LogError(ex, "Error occurred while waiting for all notification dispatch enqueue tasks to complete for RssSource '{SourceName}'. Some notifications might not have been enqueued.", rssSource.SourceName);
            }

            // تبدیل موجودیت‌های ذخیره شده به DTO برای بازگرداندن نتیجه
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
            // ... (کد قبلی این متد) ...
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
            try { await _dbContext.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException dbEx) { /* ... */ return Result<IEnumerable<NewsItemDto>>.Failure("DB error updating status."); }

            if (success) return Result<IEnumerable<NewsItemDto>>.Success(Enumerable.Empty<NewsItemDto>(), operationMessage ?? "Status updated.");
            return Result<IEnumerable<NewsItemDto>>.Failure($"Failed to update status. Errors: {rssSource.FetchErrorCount}");
        }
        #endregion

        #region HTTP and HTML Parsing Helper Methods
        private void AddConditionalGetHeaders(HttpRequestMessage requestMessage, RssSource rssSource)
        { /* ... کد قبلی ... */
            if (!string.IsNullOrWhiteSpace(rssSource.ETag) && EntityTagHeaderValue.TryParse(rssSource.ETag, out var etagHeader)) requestMessage.Headers.IfNoneMatch.Add(etagHeader);
            if (!string.IsNullOrWhiteSpace(rssSource.LastModifiedHeader) && DateTimeOffset.TryParse(rssSource.LastModifiedHeader, out var lastModifiedDate)) requestMessage.Headers.IfModifiedSince = lastModifiedDate;
        }

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


        /*
        // Optional: AI Analysis Integration Point (Placeholder)
        private async Task<NewsItem> PerformAiAnalysisAsync(NewsItem newsItem, CancellationToken cancellationToken)
        {
            // if (_aiAnalysisService == null) return newsItem;
            // _logger.LogDebug("Performing AI analysis for NewsItem: {NewsId}", newsItem.Id);
            // try
            // {
            //     var textToAnalyze = $"{newsItem.Title}. {newsItem.Summary ?? ""}";
            //     if (textToAnalyze.Length > 30) // Minimum length for meaningful analysis
            //     {
            //         var analysisResult = await _aiAnalysisService.AnalyzeTextAsync(textToAnalyze, cancellationToken);
            //         if (analysisResult != null) // Assuming AnalyzeTextAsync returns a specific result object
            //         {
            //             newsItem.SentimentScore = analysisResult.SentimentScore;
            //             newsItem.SentimentLabel = analysisResult.SentimentLabel;
            //             // ... populate other AI-derived fields ...
            //             _logger.LogInformation("AI analysis completed for NewsItem: {NewsId}. Sentiment: {Sentiment}", newsItem.Id, newsItem.SentimentLabel);
            //         }
            //     } else { _logger.LogDebug("Text too short for AI analysis: {NewsId}", newsItem.Id); }
            // }
            // catch (Exception ex)
            // {
            //     _logger.LogError(ex, "Error during AI analysis for NewsItem: {NewsId}", newsItem.Id);
            // }
            return newsItem;
        }
        */
        #endregion
    }
}