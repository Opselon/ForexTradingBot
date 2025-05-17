// File: Application/Common/Interfaces/INotificationService.cs
#region Usings
using System.Threading;
using System.Threading.Tasks;
#endregion

namespace Application.Common.Interfaces // ✅ Namespace: Application.Common.Interfaces
{
    /// <summary>
    /// اینترفیس عمومی برای ارسال نوتیفیکیشن‌ها.
    /// </summary>
    public interface INotificationService
    {
        /// <summary>
        /// یک پیام متنی به یک گیرنده خاص ارسال می‌کند.
        /// </summary>
        /// <param name="recipientIdentifier">شناسه گیرنده (مثلاً Telegram User ID یا ایمیل).</param>
        /// <param name="message">متن پیام.</param>
        /// <param name="useRichText">آیا پیام باید با فرمت غنی (Markdown/HTML) ارسال شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task SendNotificationAsync(string recipientIdentifier, string message, bool useRichText = false, CancellationToken cancellationToken = default);
    }
}