// File: Infrastructure/Persistence/Repositories/NewsItemRepository.cs
#region Usings
using Application.Common.Interfaces; // برای INewsItemRepository و IAppDbContext
using Domain.Entities;               // برای NewsItem
using Microsoft.EntityFrameworkCore; // برای متدهای EF Core مانند FindAsync, ToListAsync, AnyAsync, Include
using Microsoft.Extensions.Logging;  // برای ILogger (اختیاری اما مفید برای لاگ کردن در Repository)
using Polly;
using Shared.Extensions;
using System.Linq.Expressions; // برای Expression
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

        #region SearchNewsAsync Implementation
        /// <summary>
        /// Searches for news items based on keywords, date range, and pagination.
        /// </summary>
        public async Task<(List<NewsItem> Items, int TotalCount)> SearchNewsAsync(
            IEnumerable<string> keywords,
            DateTime sinceDate,
            DateTime untilDate,
            int pageNumber,
            int pageSize,
            bool matchAllKeywords = false, // Default is OR search for keywords
            bool isUserVip = false,        // To filter by NewsItem.IsVipOnly
            CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("SearchNewsAsync called. Keywords: [{Keywords}], Since: {SinceDate}, Until: {UntilDate}, Page: {Page}, PageSize: {PageSize}, IsVIP: {IsVip}",
                string.Join(",", keywords ?? Enumerable.Empty<string>()), sinceDate, untilDate, pageNumber, pageSize, isUserVip);

            // Ensure DbSet is not null
            if (_dbContext.NewsItems == null)
            {
                _logger.LogError("NewsItems DbSet is null in AppDbContext.");
                return (new List<NewsItem>(), 0);
            }

            var query = _dbContext.NewsItems.AsQueryable();

            // Date filtering (ensure your NewsItem.PublishedDate is not nullable or handle nulls)
            // Assuming PublishedDate is UTC. If it can be local, convert sinceDate/untilDate to UTC or ensure DB stores UTC.
            query = query.Where(n => n.PublishedDate >= sinceDate && n.PublishedDate <= untilDate);

            // VIP filtering: If user is not VIP, only show non-VIP news. VIPs see all.
            if (!isUserVip)
            {
                query = query.Where(n => !n.IsVipOnly);
            }
            // If isUserVip is true, they see both VIP and non-VIP news matching other criteria.

            var keywordList = keywords?.Select(k => k.ToLowerInvariant()).Where(k => !string.IsNullOrWhiteSpace(k)).ToList();

            if (keywordList != null && keywordList.Any())
            {
                // For OR search (any keyword match) - this is generally more useful for broad topic search.
                // This implementation constructs a predicate that checks if ANY keyword is found.
                // NOTE: For a large number of keywords or a very large dataset,
                // database full-text search is significantly more performant.
                // This LINQ approach can become slow.

                // We build an expression tree for:
                // n => keywordList.Any(keyword => (n.Title != null && n.Title.ToLower().Contains(keyword)) ||
                //                                 (n.Summary != null && n.Summary.ToLower().Contains(keyword)) ||
                //                                 (n.FullContent != null && n.FullContent.ToLower().Contains(keyword)))

                var newsItemParameter = Expression.Parameter(typeof(NewsItem), "n");
                Expression? orExpression = null;

                var titleProperty = Expression.Property(newsItemParameter, nameof(NewsItem.Title));
                var summaryProperty = Expression.Property(newsItemParameter, nameof(NewsItem.Summary));
                // var fullContentProperty = Expression.Property(newsItemParameter, nameof(NewsItem.FullContent)); // Optional

                var toLowerMethod = typeof(string).GetMethod("ToLower", Type.EmptyTypes);
                var containsMethod = typeof(string).GetMethod("Contains", new[] { typeof(string) });

                foreach (var keyword in keywordList)
                {
                    var keywordConstant = Expression.Constant(keyword);

                    // Title contains keyword
                    var titleNotNull = Expression.NotEqual(titleProperty, Expression.Constant(null, typeof(string)));
                    var titleLower = Expression.Call(titleProperty, toLowerMethod!);
                    var titleContains = Expression.Call(titleLower, containsMethod!, keywordConstant);
                    var titleCheck = Expression.AndAlso(titleNotNull, titleContains);

                    // Summary contains keyword
                    var summaryNotNull = Expression.NotEqual(summaryProperty, Expression.Constant(null, typeof(string)));
                    var summaryLower = Expression.Call(summaryProperty, toLowerMethod!);
                    var summaryContains = Expression.Call(summaryLower, containsMethod!, keywordConstant);
                    var summaryCheck = Expression.AndAlso(summaryNotNull, summaryContains);

                    // (Optional) FullContent contains keyword
                    // var fullContentNotNull = Expression.NotEqual(fullContentProperty, Expression.Constant(null, typeof(string)));
                    // var fullContentLower = Expression.Call(fullContentProperty, toLowerMethod!);
                    // var fullContentContains = Expression.Call(fullContentLower, containsMethod!, keywordConstant);
                    // var fullContentCheck = Expression.AndAlso(fullContentNotNull, fullContentContains);

                    var currentKeywordExpression = Expression.OrElse(titleCheck, summaryCheck);
                    // currentKeywordExpression = Expression.OrElse(currentKeywordExpression, fullContentCheck); // If searching FullContent

                    orExpression = orExpression == null ? currentKeywordExpression : Expression.OrElse(orExpression, currentKeywordExpression);
                }

                if (orExpression != null)
                {
                    var lambda = Expression.Lambda<Func<NewsItem, bool>>(orExpression, newsItemParameter);
                    query = query.Where(lambda);
                }
            }
            // Note: 'matchAllKeywords = true' (AND logic for keywords) is much harder to implement efficiently
            // with dynamic Expression Trees without a helper like PredicateBuilder or by chaining .Where() clauses,
            // which can also be complex to build dynamically.
            // The current implementation effectively does an OR search if multiple keywords are provided.

            // Include related data if needed for display (e.g., SourceName from RssSource)
            // Your NewsItem entity has `SourceName` directly, which is good for performance.
            // If it didn't, you'd do: query = query.Include(n => n.RssSource);
            query = query.Include(n => n.RssSource); // To access RssSource.Name if NewsItem.SourceName is not populated


            // Apply ordering, count, and pagination
            query = query.OrderByDescending(n => n.PublishedDate) // Most recent first
                         .ThenByDescending(n => n.CreatedAt);   // Tie-breaker

            int totalCount = 0;
            try
            {
                totalCount = await query.CountAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error counting news items for search.");
                // Decide if you want to return empty or rethrow. For now, return empty.
                return (new List<NewsItem>(), 0);
            }


            if (totalCount == 0)
            {
                return (new List<NewsItem>(), 0);
            }

            var items = await query
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .AsNoTracking() // Good for read-only queries
                .ToListAsync(cancellationToken);

            _logger.LogDebug("SearchNewsAsync found {TotalCount} items, returning page {PageNumber} with {PageItemCount} items.", totalCount, pageNumber, items.Count);
            return (items, totalCount);
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