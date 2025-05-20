namespace Application.DTOs
{
    /// <summary>
    /// Data Transfer Object for user signal preferences
    /// </summary>
    public class UserSignalPreferenceDto // برای نمایش علاقه‌مندی‌های کاربر
    {
        #region Properties

        /// <summary>
        /// Gets or sets the unique identifier of the signal category
        /// </summary>
        public Guid CategoryId { get; set; }

        /// <summary>
        /// Gets or sets the name of the signal category for display purposes
        /// </summary>
        public string CategoryName { get; set; } = string.Empty; // نام دسته برای نمایش

        /// <summary>
        /// Gets or sets the date and time when the user subscribed to this signal category
        /// </summary>
        public DateTime SubscribedAt { get; set; } // همان CreatedAt از UserSignalPreference

        #endregion
    }
}