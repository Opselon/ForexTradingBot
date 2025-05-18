// File: Infrastructure/Services/RssReaderService.cs

#region Usings
// Standard .NET & NuGet
// Project specific
using Application.Common.Interfaces;    // برای IRssReaderService, IAppDbContext, (اختیاری) IAiAnalysisService
using Application.DTOs.News;          // ✅ برای NewsItemDto (مطمئن شوید این DTO و namespace صحیح است)
using AutoMapper;                     // برای IMapper
using Domain.Entities;                // برای RssSource, NewsItem
using Microsoft.EntityFrameworkCore;  // برای AnyAsync, ToListAsync
using Microsoft.Extensions.Logging;
using Shared.Results;                 // برای Result<T>
using System.Net;                       // برای WebUtility
using System.Net.Http.Headers;
using System.ServiceModel.Syndication;  // برای SyndicationFeed, SyndicationItem
using System.Text.RegularExpressions;   // برای Regex
using System.Xml;
#endregion

namespace Infrastructure.Services
{
    /// <summary>
    /// Service responsible for fetching, parsing, and processing RSS/Atom feeds.
    /// It handles conditional GET requests using ETag and Last-Modified headers to optimize fetching
    /// and prevent re-processing of already fetched content.
    /// </summary>
    public class RssReaderService : IRssReaderService // ✅ اطمینان از اینکه اینترفیس IRssReaderService به درستی تعریف شده
    {
        #region Private Readonly Fields
        private readonly HttpClient _httpClient;
        private readonly IAppDbContext _dbContext;
        private readonly IMapper _mapper;
        private readonly ILogger<RssReaderService> _logger;
        // private readonly IAiAnalysisService _aiAnalysisService; // Optional
        #endregion

        #region Constructor
        public RssReaderService(
            IHttpClientFactory httpClientFactory, //  استفاده از IHttpClientFactory برای ایجاد HttpClient
            IAppDbContext dbContext,
            IMapper mapper,
            ILogger<RssReaderService> logger
            /*, IAiAnalysisService aiAnalysisService = null */)
        {
            if (httpClientFactory == null) throw new ArgumentNullException(nameof(httpClientFactory));
            _httpClient = httpClientFactory.CreateClient("RssFeedClient"); //  نام کلاینت باید در DI پیکربندی شود
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            // _aiAnalysisService = aiAnalysisService;

            //  تنظیمات پیش‌فرض برای HttpClient (می‌تواند در DI هم انجام شود)
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ForexSignalBot/1.0 RssReader");
            _httpClient.Timeout = TimeSpan.FromSeconds(30); // Timeout برای درخواست‌ها
        }
        #endregion

        #region IRssReaderService Implementation
        /// <summary>
        /// Fetches new items from the specified RSS feed, avoiding duplicates,
        /// processes them (e.g., cleans HTML, extracts image), and saves new items to the database.
        /// Updates the RssSource entity with the latest fetch metadata (ETag, LastModified, LastSuccessfulFetchAt).
        /// </summary>
        public async Task<Result<IEnumerable<NewsItemDto>>> FetchAndProcessFeedAsync(RssSource rssSource, CancellationToken cancellationToken = default)
        {
            if (rssSource == null)
            {
                _logger.LogError("FetchAndProcessFeedAsync called with a null RssSource.");
                throw new ArgumentNullException(nameof(rssSource));
            }
            if (string.IsNullOrWhiteSpace(rssSource.Url))
            {
                _logger.LogWarning("RSS source {SourceId} has an empty or invalid URL. Skipping fetch.", rssSource.Id);
                return Result<IEnumerable<NewsItemDto>>.Failure("RSS source URL cannot be empty or whitespace.");
            }

            _logger.LogInformation("Starting fetch for RSS feed: {SourceName} (ID: {SourceId}) from URL: {Url}",
                rssSource.SourceName, rssSource.Id, rssSource.Url);

            var newNewsEntities = new List<NewsItem>();
            rssSource.LastFetchAttemptAt = DateTime.UtcNow; //  ثبت زمان تلاش برای fetch

            try
            {
                // 1. Build HTTP request with conditional GET headers
                using var requestMessage = new HttpRequestMessage(HttpMethod.Get, rssSource.Url);
                AddConditionalGetHeaders(requestMessage, rssSource);

                // 2. Send HTTP request
                _logger.LogDebug("Sending HTTP GET request to {Url} for feed {SourceName}.", rssSource.Url, rssSource.SourceName);
                HttpResponseMessage response = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                // 3. Handle HTTP response
                if (response.StatusCode == HttpStatusCode.NotModified) // 304 Not Modified
                {
                    return await HandleNotModifiedResponseAsync(rssSource, cancellationToken);
                }

                response.EnsureSuccessStatusCode(); // Throws HttpRequestException for non-success codes (other than 304)
                _logger.LogInformation("Successfully received HTTP {StatusCode} response from {Url} for {SourceName}.", response.StatusCode, rssSource.Url, rssSource.SourceName);

                // 4. Read and parse feed content
                SyndicationFeed feed;
                await using (var feedStream = await response.Content.ReadAsStreamAsync(cancellationToken))
                {
                    var readerSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, Async = true, IgnoreWhitespace = true };
                    using (var xmlReader = XmlReader.Create(feedStream, readerSettings))
                    {
                        feed = SyndicationFeed.Load(xmlReader);
                    } // xmlReader is disposed here
                } // feedStream is disposed here

                _logger.LogInformation("Successfully parsed feed '{SourceName}'. Found {ItemCount} items in total.", rssSource.SourceName, feed.Items.Count());

                // 5. Process each item in the feed
                foreach (var syndicationItem in feed.Items.OrderByDescending(i => i.PublishDate)) // Process newest first
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogInformation("Feed processing for '{SourceName}' cancelled by token.", rssSource.SourceName);
                        break;
                    }
                    await ProcessSyndicationItemAsync(syndicationItem, rssSource, newNewsEntities, cancellationToken);
                }

                // 6. Save new news items and update RssSource metadata
                return await SaveNewItemsAndUpdateSourceAsync(rssSource, newNewsEntities, response, cancellationToken);
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError(httpEx, "HTTP error fetching RSS: {SourceName} from {Url}. StatusCode: {StatusCode}",
                    rssSource.SourceName, rssSource.Url, httpEx.StatusCode);
                await UpdateRssSourceErrorStateAsync(rssSource, $"HTTP Error: {httpEx.StatusCode?.ToString() ?? "N/A"}", cancellationToken);
                return Result<IEnumerable<NewsItemDto>>.Failure($"HTTP error fetching feed '{rssSource.SourceName}': {httpEx.Message}");
            }
            // ... (سایر catch block ها مانند XmlException, OperationCanceledException, Exception که قبلاً داشتیم) ...
            catch (XmlException xmlEx)
            {
                _logger.LogError(xmlEx, "XML parsing error for RSS: {SourceName} from {Url}.", rssSource.SourceName, rssSource.Url);
                await UpdateRssSourceErrorStateAsync(rssSource, "XML Parsing Error", cancellationToken);
                return Result<IEnumerable<NewsItemDto>>.Failure($"XML error for feed '{rssSource.SourceName}': {xmlEx.Message}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("RSS fetch for '{SourceName}' was cancelled.", rssSource.SourceName);
                return Result<IEnumerable<NewsItemDto>>.Failure("Operation cancelled by request.");
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Unexpected critical error processing RSS: {SourceName} from {Url}.", rssSource.SourceName, rssSource.Url);
                await UpdateRssSourceErrorStateAsync(rssSource, "Unexpected critical error.", cancellationToken);
                return Result<IEnumerable<NewsItemDto>>.Failure($"Unexpected error for feed '{rssSource.SourceName}': {ex.Message}");
            }
        }
        #endregion

        #region Private Helper Methods for FetchAndProcessFeedAsync
        private void AddConditionalGetHeaders(HttpRequestMessage requestMessage, RssSource rssSource)
        {
            if (!string.IsNullOrWhiteSpace(rssSource.ETag))
            {
                // ETag values are typically quoted. Ensure they are correctly formatted.
                if (EntityTagHeaderValue.TryParse(CleanETag(rssSource.ETag), out var etagHeader))
                {
                    requestMessage.Headers.IfNoneMatch.Add(etagHeader);
                    _logger.LogDebug("Added If-None-Match (ETag): {ETag} for {Url}", etagHeader.Tag, rssSource.Url);
                }
                else
                {
                    _logger.LogWarning("Could not parse stored ETag '{StoredETag}' for {Url}", rssSource.ETag, rssSource.Url);
                }
            }

            if (!string.IsNullOrWhiteSpace(rssSource.LastModifiedHeader))
            {
                if (DateTimeOffset.TryParse(rssSource.LastModifiedHeader, out DateTimeOffset lastModifiedDate))
                {
                    requestMessage.Headers.IfModifiedSince = lastModifiedDate;
                    _logger.LogDebug("Added If-Modified-Since: {LastModified} for {Url}", lastModifiedDate, rssSource.Url);
                }
                else
                {
                    _logger.LogWarning("Could not parse stored LastModifiedHeader '{HeaderValue}' for {Url}", rssSource.LastModifiedHeader, rssSource.Url);
                }
            }
        }

        private async Task<Result<IEnumerable<NewsItemDto>>> HandleNotModifiedResponseAsync(RssSource rssSource, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Feed '{SourceName}' (HTTP 304 Not Modified). Updating LastSuccessfulFetchAt.", rssSource.SourceName);
            rssSource.LastSuccessfulFetchAt = DateTime.UtcNow;
            rssSource.FetchErrorCount = 0; // Reset error count
            rssSource.UpdatedAt = DateTime.UtcNow;
            // _dbContext.RssSources.Update(rssSource); // EF Core tracks changes
            await _dbContext.SaveChangesAsync(cancellationToken);
            return Result<IEnumerable<NewsItemDto>>.Success(Enumerable.Empty<NewsItemDto>(), "Feed content has not changed.");
        }

        private async Task ProcessSyndicationItemAsync(SyndicationItem syndicationItem, RssSource rssSource, List<NewsItem> newNewsEntities, CancellationToken cancellationToken)
        {
            string? itemLink = syndicationItem.Links.FirstOrDefault(l => l.Uri != null)?.Uri.ToString();
            string itemSourceId = syndicationItem.Id ?? itemLink ?? $"{syndicationItem.Title?.Text}_{syndicationItem.PublishDate.ToUnixTimeSeconds()}"; //  یک شناسه آیتم از منبع

            if (string.IsNullOrWhiteSpace(itemLink) && string.IsNullOrWhiteSpace(syndicationItem.Id)) //  حداقل یکی باید باشد
            {
                _logger.LogWarning("Skipping feed item from '{SourceName}' due to missing link and ID. Title: {ItemTitle}",
                    rssSource.SourceName, syndicationItem.Title?.Text ?? "N/A");
                return;
            }

            //  SourceItemId را برای بررسی تکراری بودن استفاده می‌کنیم
            if (await _dbContext.NewsItems.AnyAsync(n => n.RssSourceId == rssSource.Id && n.SourceItemId == itemSourceId, cancellationToken))
            {
                _logger.LogDebug("Skipping already existing item from '{SourceName}' with SourceItemId: {SourceItemId} (Link: {Link})",
                   rssSource.SourceName, itemSourceId, itemLink ?? "N/A");
                return;
            }


            var newsEntity = new NewsItem
            {
                // Id = Guid.NewGuid(); //  سازنده NewsItem این کار را انجام می‌دهد
                Title = syndicationItem.Title?.Text?.Trim() ?? "Untitled News",
                Link = itemLink ?? itemSourceId, // اگر لینک نیست، از SourceItemId استفاده کن
                Summary = CleanHtmlAndTruncate(syndicationItem.Summary?.Text, 1000), // افزایش طول خلاصه
                FullContent = CleanHtml(syndicationItem.Content is TextSyndicationContent textContent ? textContent.Text : syndicationItem.Summary?.Text),
                ImageUrl = ExtractImageUrl(syndicationItem),
                PublishedDate = syndicationItem.PublishDate.UtcDateTime,
                RssSourceId = rssSource.Id,
                SourceName = rssSource.SourceName, //  کپی کردن نام منبع برای دسترسی آسان
                SourceItemId = itemSourceId, //  ذخیره شناسه آیتم از منبع
                // FetchedAt در سازنده NewsItem مقداردهی می‌شود
            };

            //  (اختیاری) بخش تحلیل AI
            // if (_aiAnalysisService != null) { ... }

            newNewsEntities.Add(newsEntity);
            _logger.LogInformation("Prepared new NewsItem: '{ItemTitle}' from {SourceName} (SourceItemID: {SourceItemId})",
                newsEntity.Title, rssSource.SourceName, newsEntity.SourceItemId);
        }

        private async Task<Result<IEnumerable<NewsItemDto>>> SaveNewItemsAndUpdateSourceAsync(
            RssSource rssSource, List<NewsItem> newNewsEntities, HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (newNewsEntities.Any())
            {
                await _dbContext.NewsItems.AddRangeAsync(newNewsEntities, cancellationToken);
                _logger.LogInformation("Added {Count} new news items from '{SourceName}' to DB.", newNewsEntities.Count, rssSource.SourceName);
            }
            else
            {
                _logger.LogInformation("No new unique items to add from '{SourceName}'.", rssSource.SourceName);
            }

            // Update RssSource metadata
            rssSource.LastSuccessfulFetchAt = DateTime.UtcNow;
            rssSource.FetchErrorCount = 0;
            rssSource.UpdatedAt = DateTime.UtcNow;

            if (response.Headers.ETag != null)
            {
                rssSource.ETag = CleanETag(response.Headers.ETag.Tag);
            }
            if (response.Content.Headers.LastModified.HasValue)
            {
                rssSource.LastModifiedHeader = response.Content.Headers.LastModified.Value.ToString("R");
            }

            // _dbContext.RssSources.Update(rssSource); // EF Core tracks changes
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Successfully processed feed '{SourceName}'. {NewItemCount} new items added. RssSource metadata updated.",
                rssSource.SourceName, newNewsEntities.Count);

            return Result<IEnumerable<NewsItemDto>>.Success(_mapper.Map<IEnumerable<NewsItemDto>>(newNewsEntities),
                $"{newNewsEntities.Count} new items fetched from {rssSource.SourceName}.");
        }

        // ... (متدهای کمکی CleanHtmlAndTruncate, CleanHtml, ExtractImageUrl, UpdateRssSourceErrorStateAsync که قبلاً داشتیم) ...
        private string? CleanHtmlAndTruncate(string? htmlText, int maxLength = 0) { /* ... */ return string.IsNullOrWhiteSpace(htmlText) ? null : Regex.Replace(WebUtility.HtmlDecode(htmlText), "<.*?>", string.Empty).Trim().Truncate(maxLength); }
        private string? CleanHtml(string? htmlText) { /* ... */ return string.IsNullOrWhiteSpace(htmlText) ? null : Regex.Replace(WebUtility.HtmlDecode(htmlText), "<.*?>", string.Empty).Trim(); }
        private string? ExtractImageUrl(SyndicationItem item) { /* ... */ return null; } //  پیاده‌سازی واقعی لازم است
        private string? CleanETag(string? etag) => etag?.Trim('"');
        private async Task UpdateRssSourceErrorStateAsync(RssSource rssSource, string errorMessage, CancellationToken cancellationToken)
        {
            try
            {
                rssSource.FetchErrorCount++; // ✅ حالا صحیح است
                rssSource.LastFetchAttemptAt = DateTime.UtcNow;
                rssSource.UpdatedAt = DateTime.UtcNow;
                if (rssSource.FetchErrorCount > 10) rssSource.IsActive = false;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception dbEx) { _logger.LogError(dbEx, "Failed to update RssSource error state for {SourceName}.", rssSource.SourceName); }
        }

        #endregion
    }

    //  یک متد توسعه‌دهنده برای Truncate (می‌تواند در Shared/Extensions/StringExtensions.cs باشد)
    public static class StringExtensionsForRss
    {
        public static string Truncate(this string? value, int maxLength, string truncationSuffix = "...")
        {
            if (string.IsNullOrEmpty(value) || maxLength <= 0) return string.Empty;
            if (value.Length <= maxLength) return value;
            if (maxLength <= truncationSuffix.Length) return truncationSuffix.Substring(0, Math.Min(maxLength, truncationSuffix.Length));
            return value.Substring(0, maxLength - truncationSuffix.Length) + truncationSuffix;
        }
    }
}