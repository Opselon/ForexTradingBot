// File: Application/Features/Rss/Queries/FetchRssSourceManualQueryHandler.cs
#region Usings
using Application.Common.Interfaces; // برای IRssSourceRepository, IRssReaderService
using Application.DTOs.News;         // برای NewsItemDto
using Domain.Entities; // برای RssSource
using MediatR;                       // برای IRequestHandler
using Microsoft.Extensions.Logging;
using Shared.Results;                // برای Result
#endregion

namespace Application.Features.Rss.Queries
{
    public class FetchRssSourceManualQueryHandler : IRequestHandler<FetchRssSourceManualQuery, Result<IEnumerable<NewsItemDto>>>
    {
        private readonly IRssSourceRepository _rssSourceRepository;
        private readonly IRssReaderService _rssReaderService; //  این سرویس مسئول خواندن و پردازش یک فید است
        private readonly ILogger<FetchRssSourceManualQueryHandler> _logger;

        public FetchRssSourceManualQueryHandler(
            IRssSourceRepository rssSourceRepository,
            IRssReaderService rssReaderService,
            ILogger<FetchRssSourceManualQueryHandler> logger)
        {
            _rssSourceRepository = rssSourceRepository ?? throw new ArgumentNullException(nameof(rssSourceRepository));
            _rssReaderService = rssReaderService ?? throw new ArgumentNullException(nameof(rssReaderService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<Result<IEnumerable<NewsItemDto>>> Handle(FetchRssSourceManualQuery request, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Handling FetchRssSourceManualQuery. RssSourceId: {RssSourceId}, ForceFetch: {ForceFetch}",
                request.RssSourceId?.ToString() ?? "All Active", request.ForceFetch);

            var allFetchedNews = new List<NewsItemDto>();
            List<string> errors = [];

            IEnumerable<RssSource> sourcesToFetch;

            if (request.RssSourceId.HasValue)
            {
                var source = await _rssSourceRepository.GetByIdAsync(request.RssSourceId.Value, cancellationToken);
                if (source == null || !source.IsActive)
                {
                    _logger.LogWarning("Specified RssSource ID {RssSourceId} not found or is not active.", request.RssSourceId.Value);
                    return Result<IEnumerable<NewsItemDto>>.Failure($"RSS Source with ID {request.RssSourceId.Value} not found or not active.");
                }
                sourcesToFetch = [source];
            }
            else
            {
                sourcesToFetch = await _rssSourceRepository.GetActiveSourcesAsync(cancellationToken);
                if (!sourcesToFetch.Any())
                {
                    _logger.LogInformation("No active RSS sources found to fetch.");
                    return Result<IEnumerable<NewsItemDto>>.Success(Enumerable.Empty<NewsItemDto>(), "No active RSS sources to fetch.");
                }
            }

            _logger.LogInformation("Found {Count} RSS source(s) to process.", sourcesToFetch.Count());

            foreach (var source in sourcesToFetch)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Manual RSS fetch operation cancelled.");
                    break;
                }

                //  برای ForceFetch، می‌توانیم ETag و LastModifiedHeader را موقتاً null کنیم
                //  تا RssReaderService حتماً فید را بخواند.
                //  این کار باید با احتیاط انجام شود و فقط برای تست.
                string? originalETag = null;
                string? originalLastModified = null;

                if (request.ForceFetch)
                {
                    _logger.LogInformation("ForceFetch is enabled for RssSource: {SourceName}. Temporarily clearing ETag and LastModifiedHeader.", source.SourceName);
                    originalETag = source.ETag;
                    originalLastModified = source.LastModifiedHeader;
                    source.ETag = null;
                    source.LastModifiedHeader = null;
                    //  این تغییر در RssSource در DbContext ردیابی می‌شود، اما چون بلافاصله FetchAndProcessFeedAsync
                    //  هم آن را آپدیت می‌کند، ممکن است SaveChangesAsync در نهایت مقادیر جدید را ذخیره کند.
                    //  بهتر است RssReaderService پارامتری برای forceFetch داشته باشد.
                    //  فعلاً این روش ساده را استفاده می‌کنیم.
                }

                var result = await _rssReaderService.FetchAndProcessFeedAsync(source, cancellationToken);

                if (request.ForceFetch) // بازگرداندن مقادیر اصلی ETag و LastModified
                {
                    source.ETag = originalETag;
                    source.LastModifiedHeader = originalLastModified;
                    // نیازی به SaveChanges برای این بازگرداندن نیست چون در ادامه اگر تغییر دیگری باشد، ذخیره می‌شود.
                }

                if (result.Succeeded && result.Data != null)
                {
                    _logger.LogInformation("Successfully fetched {Count} new items from {SourceName}.", result.Data.Count(), source.SourceName);
                    allFetchedNews.AddRange(result.Data);
                }
                else
                {
                    _logger.LogWarning("Failed to fetch or process feed for {SourceName}. Errors: {Errors}", source.SourceName, string.Join(", ", result.Errors));
                    errors.AddRange(result.Errors.Select(e => $"{source.SourceName}: {e}"));
                }
            }

            if (errors.Any())
            {
                // اگر همه فیدها خطا داشتند یا برخی خطا و برخی موفق بودند
                if (!allFetchedNews.Any())
                {
                    return Result<IEnumerable<NewsItemDto>>.Failure(errors);
                }
                else
                {
                    //  موفقیت نسبی، با لیست خطاها
                    return Result<IEnumerable<NewsItemDto>>.Success(allFetchedNews, $"Partial success. Some feeds failed: {string.Join("; ", errors)}");
                }
            }

            return Result<IEnumerable<NewsItemDto>>.Success(allFetchedNews, $"{allFetchedNews.Count} new news items fetched and processed in total.");
        }
    }
}