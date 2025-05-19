
namespace Application.Interfaces // ✅ Namespace صحیح
{
    /// <summary>
    /// Service responsible for coordinating the fetching of all active RSS feeds.
    /// This is typically run as a recurring background job.
    /// </summary>
    public interface IRssFetchingCoordinatorService
    {
        /// <summary>
        /// Fetches and processes all active RSS feeds.
        /// </summary>
        Task FetchAllActiveFeedsAsync(CancellationToken cancellationToken = default);
    }
}