// File: Application/DTOs/Notifications/NotificationButton.cs
#region Usings
// No specific usings needed for this simple DTO
#endregion

namespace Application.DTOs.Notifications // ✅ Namespace: Application.DTOs.Notifications
{
    /// <summary>
    /// Represents a button to be included in a notification message.
    /// </summary>
    public class NotificationButton
    {
        #region Properties

        /// <summary>
        /// The text displayed on the button.
        /// </summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>
        /// The callback data (if it's an inline keyboard button triggering a callback)
        /// or the URL (if it's a URL button).
        /// </summary>
        public string CallbackDataOrUrl { get; set; } = string.Empty;

        /// <summary>
        /// Indicates if the <see cref="CallbackDataOrUrl"/> is a URL.
        /// If true, an inline button with a URL will be created.
        /// If false, an inline button with callback data will be created.
        /// </summary>
        public bool IsUrl { get; set; } = false;

        #endregion
    }
}