// File: Infrastructure/Persistence/Repositories/NewsItemRepository.cs
#region Usings
using Application.Common.Interfaces; // برای INewsItemRepository و IAppDbContext
using Domain.Entities;               // برای NewsItem
using Microsoft.EntityFrameworkCore; // برای متدهای EF Core مانند FindAsync, ToListAsync, AnyAsync, Include
using Microsoft.Extensions.Logging;  // برای ILogger (اختیاری اما مفید برای لاگ کردن در Repository)
using Shared.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions; // برای Expression
using System.Threading;
using System.Threading.Tasks;
#endregion

namespace Infrastructure.Persistence.Repositories
{
    /// <summary>
    /// Implements the INewsItemRepository for data operations related to NewsItem entities
    /// using Entity Framework Core.
    /// </summary>
    public class NewsItemRepository : INewsItemRepository
    {
        #region Private Readonly Fields
        private readonly IAppDbContext _dbContext;
        private readonly ILogger<NewsItemRepository> _logger; //  لاگر برای این Repository
        #endregion

        #region Constructor
        public NewsItemRepository(IAppDbContext dbContext, ILogger<NewsItemRepository> logger)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        #endregion

        #region INewsItemRepository Read Operations
        public async Task<NewsItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Fetching NewsItem by ID: {NewsItemId}", id);
            // FindAsync بهینه است برای جستجو بر اساس کلید اصلی
            return await _dbContext.NewsItems
                                 .Include(ni => ni.RssSource) //  بارگذاری منبع RSS مرتبط برای دسترسی به SourceName
                                 .FirstOrDefaultAsync(ni => ni.Id == id, cancellationToken);
        }

        public async Task<NewsItem?> GetBySourceDetailsAsync(Guid rssSourceId, string sourceItemId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sourceItemId)) return null;
            _logger.LogDebug("Fetching NewsItem by RssSourceId: {RssSourceId} and SourceItemId: {SourceItemId}", rssSourceId, sourceItemId);
            return await _dbContext.NewsItems
                                 .Include(ni => ni.RssSource)
                                 .FirstOrDefaultAsync(ni => ni.RssSourceId == rssSourceId && ni.SourceItemId == sourceItemId, cancellationToken);
        }

        public async Task<bool> ExistsBySourceDetailsAsync(Guid rssSourceId, string sourceItemId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sourceItemId)) return false;
            _logger.LogDebug("Checking existence of NewsItem by RssSourceId: {RssSourceId} and SourceItemId: {SourceItemId}", rssSourceId, sourceItemId);
            return await _dbContext.NewsItems.AnyAsync(ni => ni.RssSourceId == rssSourceId && ni.SourceItemId == sourceItemId, cancellationToken);
        }

        public async Task<IEnumerable<NewsItem>> GetRecentNewsAsync(
            int count,
            Guid? rssSourceId = null,
            bool includeRssSource = false,
            CancellationToken cancellationToken = default)
        {
            if (count <= 0) return Enumerable.Empty<NewsItem>();

            _logger.LogDebug("Fetching {Count} recent news items. RssSourceIdFilter: {RssSourceIdFilter}, IncludeRssSource: {IncludeRssSource}",
                count, rssSourceId?.ToString() ?? "Any", includeRssSource);

            var query = _dbContext.NewsItems.AsQueryable();

            if (rssSourceId.HasValue)
            {
                query = query.Where(ni => ni.RssSourceId == rssSourceId.Value);
            }
            if (includeRssSource)
            {
                query = query.Include(ni => ni.RssSource);
            }

            //  مرتب‌سازی بر اساس تاریخ انتشار (اگر معتبر است) یا تاریخ دریافت
            return await query.OrderByDescending(ni => ni.PublishedDate) // PublishedDate از موجودیت شما Required است
                              .ThenByDescending(ni => ni.CreatedAt) //  برای آیتم‌هایی با تاریخ انتشار یکسان
                              .Take(count)
                              .AsNoTracking() //  برای کوئری‌های فقط خواندنی
                              .ToListAsync(cancellationToken);
        }

        public async Task<IEnumerable<NewsItem>> FindAsync(
            Expression<Func<NewsItem, bool>> predicate,
            bool includeRssSource = false,
            CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Finding NewsItems with custom predicate. IncludeRssSource: {IncludeRssSource}", includeRssSource);
            var query = _dbContext.NewsItems.Where(predicate);
            if (includeRssSource)
            {
                query = query.Include(ni => ni.RssSource);
            }
            return await query.AsNoTracking().ToListAsync(cancellationToken);
        }

        public async Task<HashSet<string>> GetExistingSourceItemIdsAsync(Guid rssSourceId, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Fetching existing SourceItemIds for RssSourceId: {RssSourceId}", rssSourceId);
            //  فقط SourceItemId هایی را که null نیستند، انتخاب می‌کنیم.
            //  این متد در RssReaderService برای بهینه‌سازی بررسی تکراری بودن استفاده می‌شود.
            var ids = await _dbContext.NewsItems
                .Where(n => n.RssSourceId == rssSourceId && n.SourceItemId != null)
                .Select(n => n.SourceItemId!) //  اطمینان از اینکه SourceItemId null نیست (به خاطر شرط Where)
                .Distinct()
                .ToListAsync(cancellationToken); //  ابتدا به لیست تبدیل می‌کنیم
            return new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase); //  سپس به HashSet برای جستجوی سریع (با مقایسه case-insensitive)
        }
        #endregion

        #region INewsItemRepository Write Operations
        public async Task AddAsync(NewsItem newsItem, CancellationToken cancellationToken = default)
        {
            if (newsItem == null) throw new ArgumentNullException(nameof(newsItem));
            _logger.LogDebug("Adding new NewsItem to DbContext. NewsItemId: {NewsItemId}, Title: {Title}", newsItem.Id, newsItem.Title.Truncate(50));
            await _dbContext.NewsItems.AddAsync(newsItem, cancellationToken);
            //  SaveChangesAsync در Unit of Work (معمولاً در سرویس یا Command Handler) فراخوانی می‌شود.
        }

        public async Task AddRangeAsync(IEnumerable<NewsItem> newsItems, CancellationToken cancellationToken = default)
        {
            if (newsItems == null || !newsItems.Any())
            {
                _logger.LogDebug("AddRangeAsync called with no news items to add.");
                return;
            }
            _logger.LogDebug("Adding a range of {Count} news items to DbContext.", newsItems.Count());
            await _dbContext.NewsItems.AddRangeAsync(newsItems, cancellationToken);
        }

        public Task UpdateAsync(NewsItem newsItem, CancellationToken cancellationToken = default)
        {
            if (newsItem == null) throw new ArgumentNullException(nameof(newsItem));
            _logger.LogDebug("Marking NewsItem as modified in DbContext. NewsItemId: {NewsItemId}", newsItem.Id);
            //  EF Core به طور خودکار تغییرات موجودیت‌های ردیابی شده را تشخیص می‌دهد.
            //  این خط اطمینان می‌دهد که وضعیت به Modified تغییر می‌کند، حتی اگر از یک نمونه detached آمده باشد.
            _dbContext.NewsItems.Entry(newsItem).State = EntityState.Modified;
            return Task.CompletedTask;
        }

        public async Task DeleteAsync(NewsItem newsItem, CancellationToken cancellationToken = default)
        {
            if (newsItem == null) throw new ArgumentNullException(nameof(newsItem));
            _logger.LogDebug("Removing NewsItem from DbContext. NewsItemId: {NewsItemId}", newsItem.Id);
            _dbContext.NewsItems.Remove(newsItem);
            await Task.CompletedTask; //  برای سازگاری با async، هرچند Remove همزمان است.
        }

        public async Task<bool> DeleteByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Attempting to delete NewsItem by ID: {NewsItemId}", id);
            var newsItemToDelete = await _dbContext.NewsItems.FindAsync(new object?[] { id }, cancellationToken);
            if (newsItemToDelete != null)
            {
                _dbContext.NewsItems.Remove(newsItemToDelete);
                _logger.LogInformation("NewsItem with ID {NewsItemId} marked for deletion.", id);
                return true;
            }
            _logger.LogWarning("NewsItem with ID {NewsItemId} not found for deletion.", id);
            return false;
        }
        #endregion
    }
}