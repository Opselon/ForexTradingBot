using Application.DTOs.News; //  یک DTO برای آیتم‌های خبری
using Domain.Entities; // برای RssSource
using Shared.Results;

namespace Application.Common.Interfaces
{
    public interface IRssReaderService
    {
        /// <summary>
        /// تمام آیتم‌های جدید از یک منبع RSS خاص را می‌خواند و پردازش می‌کند.
        /// </summary>
        /// <param name="rssSource">موجودیت منبع RSS که باید خوانده شود.</param>
        /// <param name="cancellationToken"></param>
        /// <returns>لیستی از NewsItemDto های جدید یا Result.Failure در صورت بروز خطا.</returns>
        Task<Result<IEnumerable<NewsItemDto>>> FetchAndProcessFeedAsync(RssSource rssSource, CancellationToken cancellationToken = default);
    }
}