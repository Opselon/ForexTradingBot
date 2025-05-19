// File: Application/DTOs/Notifications/NotificationJobPayload.cs
#region Usings
using System.Collections.Generic; // برای List<NotificationButton>
#endregion

namespace Application.DTOs.Notifications // ✅ Namespace: Application.DTOs.Notifications
{
    /// <summary>
    /// Represents the data payload for a notification job that will be processed by a background service.
    /// This DTO contains all necessary information to construct and send a notification to a specific user.
    /// </summary>
    public class NotificationJobPayload
    {

        public Guid? NewsItemSignalCategoryId { get; set; } // ✅ شناسه دسته‌بندی خبر (اگر دارد)
        public string? NewsItemSignalCategoryName { get; set; } // ✅ نام دسته‌بندی خبر (برای نمایش در پیام)
        public Guid NewsItemId { get; set; } // ✅ شناسه خود خبر برای ساخت CallbackData های خاص

        /// <summary>
        /// The Telegram User ID of the recipient.
        /// </summary>
        public long TargetTelegramUserId { get; set; }

        /// <summary>
        /// The main text content of the notification message.
        /// This text might contain basic Markdown if <see cref="UseMarkdown"/> is true,
        /// but final escaping and formatting for a specific platform (e.g., Telegram MarkdownV2)
        /// should be handled by the <see cref="Application.Common.Interfaces.INotificationSendingService"/>.
        /// </summary>
        public string MessageText { get; set; } = string.Empty;

        /// <summary>
        /// Indicates whether the <see cref="MessageText"/> contains Markdown formatting
        /// that needs to be interpreted by the notification sending service.
        /// </summary>
        public bool UseMarkdown { get; set; } = false;

        /// <summary>
        /// (Optional) URL of an image to be included with the notification.
        /// </summary>
        public string? ImageUrl { get; set; }

        /// <summary>
        /// (Optional) A list of buttons to be displayed with the notification.
        /// </summary>
        public List<NotificationButton>? Buttons { get; set; }

        /// <summary>
        /// (Optional) Additional custom data related to the notification,
        /// which might be used by the notification sending service for richer formatting or context.
        /// Example: {"NewsItemId": "guid-value", "Sentiment": "Positive"}
        /// </summary>
        public Dictionary<string, string>? CustomData { get; set; }

        public NotificationJobPayload()
        {
            Buttons = new List<NotificationButton>();
            CustomData = new Dictionary<string, string>();
        }
    }
}