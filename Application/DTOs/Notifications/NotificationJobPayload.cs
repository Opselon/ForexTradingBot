// File: Application/DTOs/Notifications/NotificationJobPayload.cs
#region Usings
#endregion

namespace Application.DTOs.Notifications // ✅ Namespace: Application.DTOs.Notifications
{
    /// <summary>
    /// Represents the data payload for a notification job that will be processed by a background service.
    /// This DTO contains all necessary information to construct and send a notification to a specific user.
    /// </summary>
    public class NotificationJobPayload
    {
        #region Properties

        /// <summary>
        /// Gets or sets the unique identifier of the news item's signal category, if applicable.
        /// </summary>
        public Guid? NewsItemSignalCategoryId { get; set; }

        /// <summary>
        /// The fallback image URL used if no specific image is provided in the payload.
        /// </summary>
        private const string DefaultImageUrl = "http://localhost:5000/Breaking_News.jpg";

        /// <summary>
        /// Gets or sets the name of the news item's signal category, used for display purposes in the notification message.
        /// </summary>
        public string? NewsItemSignalCategoryName { get; set; }

        /// <summary>
        /// Gets or sets the unique identifier of the news item itself, used for constructing specific callback data.
        /// </summary>
        public Guid NewsItemId { get; set; }

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
        /// The actual image URL that will be sent. If none is set, the default image URL will be used.
        /// </summary>
        public string ImageUrlOrDefault =>
            string.IsNullOrWhiteSpace(ImageUrl) ? DefaultImageUrl : ImageUrl;


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

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="NotificationJobPayload"/> class.
        /// Initializes <see cref="Buttons"/> and <see cref="CustomData"/> to new instances.
        /// </summary>
        public NotificationJobPayload()
        {
            Buttons = [];
            CustomData = [];
        }

        #endregion
    }
}