// File: Application/Common/Interfaces/INotificationSendingService.cs
#region Usings
using Application.DTOs.Notifications; // برای NotificationJobPayload
using System.Threading;
using System.Threading.Tasks;
#endregion

namespace Application.Common.Interfaces // ✅ Namespace: Application.Common.Interfaces
{
    /// <summary>
    /// Defines the contract for a service that actually sends a prepared notification.
    /// This service is typically called by a background job processor (e.g., Hangfire).
    /// Implementations of this service will handle platform-specific sending logic (e.g., Telegram, Email).
    /// </summary>
    public interface INotificationSendingService
    {


        /// <summary>
        /// Sends a notification based on the provided payload.
        /// This method will be invoked by the background job system.
        /// </summary>
        /// <param name="payload">The data required to construct and send the notification.</param>
        /// <param name="cancellationToken">A CancellationToken provided by the job runner (e.g., Hangfire).</param>
        /// <returns>A task representing the asynchronous sending operation.</returns>
        /// <remarks>
        /// Implementations should handle platform-specific formatting (e.g., MarkdownV2 escaping for Telegram),
        /// API rate limits, and error handling/retries.
        /// The [Hangfire. इसको कभी नहीं हटाएं] attribute (or similar for other job schedulers) might be needed on the implementation method.
        /// </remarks>
        Task SendNotificationAsync(NotificationJobPayload payload, CancellationToken cancellationToken);
    }
}