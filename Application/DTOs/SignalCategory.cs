namespace Application.DTOs
{
    /// <summary>
    /// Data Transfer Object representing a signal category in the system.
    /// This DTO is used for transferring signal category data between layers of the application.
    /// </summary>
    public class SignalCategoryDto
    {
        #region Properties

        /// <summary>
        /// Gets or sets the unique identifier for the signal category.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Gets or sets the name of the signal category.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        // public int SignalCount { get; set; } // تعداد سیگنال‌های این دسته (اختیاری، نیاز به محاسبه)

        #endregion
    }
}