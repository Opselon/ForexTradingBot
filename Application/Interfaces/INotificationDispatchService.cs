// File: Application/Interfaces/INotificationDispatchService.cs
#region Usings
using Domain.Entities; // برای NewsItem
#endregion

namespace Application.Interfaces
{
    /// <summary>
    /// Defines the contract for a service that dispatches notification requests for later processing.
    /// This service identifies target users for a given piece of content (like a news item)
    /// and queues individual notification jobs for each user.
    /// </summary>
    public interface INotificationDispatchService
    {
        /// <summary>
        /// Identifies target users for a given news item based on their notification preferences
        /// and active subscriptions (if applicable for VIP news), then enqueues a notification job
        /// for each eligible user.
        /// </summary>
        /// <param name="newsItem">The news item for which notifications are to be dispatched.</param>
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        /// <remarks>
        /// This method should not send notifications directly but rather schedule them
        /// using a background job system (e.g., Hangfire via INotificationJobScheduler).
        /// </remarks>
        Task DispatchNewsNotificationAsync(Guid newsItemId, CancellationToken cancellationToken = default); // ✅ تغییر

        //  می‌توانید متدهای دیگری برای انواع دیگر نوتیفیکیشن‌ها اضافه کنید، مثلاً:
        //  Task DispatchSignalNotificationAsync(Signal signal, CancellationToken cancellationToken = default);
        //  Task DispatchWelcomeNotificationAsync(User user, CancellationToken cancellationToken = default);
    }
}