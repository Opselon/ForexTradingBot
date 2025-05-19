// File: Application/Common/Interfaces/INewsItemRepository.cs
#region Usings
using Domain.Entities; // برای NewsItem
using System;
using System.Collections.Generic;
using System.Linq.Expressions; // برای Expression
using System.Threading;
using System.Threading.Tasks;
#endregion

namespace Application.Common.Interfaces
{
    /// <summary>
    /// Defines the contract for a repository handling data operations for NewsItem entities.
    /// This includes CRUD operations and specific queries related to news items.
    /// </summary>
    public interface INewsItemRepository
    {
        #region Read Operations
        /// <summary>
        /// Gets a specific news item by its unique identifier.
        /// </summary>
        /// <param name="id">The Guid ID of the news item.</param>
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        /// <returns>The <see cref="NewsItem"/> if found; otherwise, null.</returns>
        Task<NewsItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a specific news item by its RssSourceId and its unique identifier within that source (SourceItemId).
        /// This is useful for checking if an item from a feed already exists.
        /// </summary>
        /// <param name="rssSourceId">The ID of the RssSource.</param>
        /// <param name="sourceItemId">The unique identifier of the item within the RSS source.</param>
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        /// <returns>The <see cref="NewsItem"/> if found; otherwise, null.</returns>
        Task<NewsItem?> GetBySourceDetailsAsync(Guid rssSourceId, string sourceItemId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Checks if a news item with the given RssSourceId and SourceItemId already exists.
        /// More performant than GetBySourceDetailsAsync if you only need to check existence.
        /// </summary>
        /// <param name="rssSourceId">The ID of the RssSource.</param>
        /// <param name="sourceItemId">The unique identifier of the item within the RSS source.</param>
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        /// <returns>True if the item exists; otherwise, false.</returns>
        Task<bool> ExistsBySourceDetailsAsync(Guid rssSourceId, string sourceItemId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a list of recent news items, optionally filtered by RssSourceId.
        /// </summary>
        /// <param name="count">The maximum number of recent news items to retrieve.</param>
        /// <param name="rssSourceId">(Optional) Filter by a specific RssSource.</param>
        /// <param name="includeRssSource">Whether to include the related RssSource entity.</param>
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        /// <returns>An enumerable of <see cref="NewsItem"/>.</returns>
        Task<IEnumerable<NewsItem>> GetRecentNewsAsync(
            int count,
            Guid? rssSourceId = null,
            bool includeRssSource = false,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Finds news items that match a specific predicate.
        /// </summary>
        /// <param name="predicate">The predicate to filter news items.</param>
        /// <param name="includeRssSource">Whether to include the related RssSource entity.</param>
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        /// <returns>An enumerable of <see cref="NewsItem"/> matching the predicate.</returns>
        Task<IEnumerable<NewsItem>> FindAsync(
            Expression<Func<NewsItem, bool>> predicate,
            bool includeRssSource = false,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets all SourceItemIds for a specific RssSource.
        /// Useful for efficient duplicate checking when processing a feed.
        /// </summary>
        /// <param name="rssSourceId">The ID of the RssSource.</param>
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        /// <returns>A HashSet of existing SourceItemIds for the given RssSource.</returns>
        Task<HashSet<string>> GetExistingSourceItemIdsAsync(Guid rssSourceId, CancellationToken cancellationToken = default);
        #endregion

        #region Write Operations
        /// <summary>
        /// Adds a new news item to the data store.
        /// SaveChangesAsync needs to be called separately to commit the change.
        /// </summary>
        /// <param name="newsItem">The <see cref="NewsItem"/> entity to add.</param>
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        Task AddAsync(NewsItem newsItem, CancellationToken cancellationToken = default);

        /// <summary>
        /// Adds a collection of new news items to the data store.
        /// SaveChangesAsync needs to be called separately to commit the changes.
        /// </summary>
        /// <param name="newsItems">An enumerable of <see cref="NewsItem"/> entities to add.</param>
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        Task AddRangeAsync(IEnumerable<NewsItem> newsItems, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates an existing news item in the data store.
        /// SaveChangesAsync needs to be called separately to commit the change.
        /// </summary>
        /// <param name="newsItem">The <see cref="NewsItem"/> entity to update.</param>
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        Task UpdateAsync(NewsItem newsItem, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes a news item from the data store.
        /// SaveChangesAsync needs to be called separately to commit the change.
        /// </summary>
        /// <param name="newsItem">The <see cref="NewsItem"/> entity to delete.</param>
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        Task DeleteAsync(NewsItem newsItem, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes a news item by its unique identifier.
        /// SaveChangesAsync needs to be called separately to commit the change.
        /// </summary>
        /// <param name="id">The Guid ID of the news item to delete.</param>
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        /// <returns>True if the item was found and marked for deletion; otherwise, false.</returns>
        Task<bool> DeleteByIdAsync(Guid id, CancellationToken cancellationToken = default);
        #endregion
    }
}