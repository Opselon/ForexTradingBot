using System;
using System.ComponentModel.DataAnnotations;

namespace Application.DTOs
{
    /// <summary>
    /// Data Transfer Object for an RSS source.
    /// </summary>
    public class RssSourceDto
    {
        #region Properties

        /// <summary>
        /// Gets or sets the unique identifier of the RSS source.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Gets or sets the URL of the RSS feed.
        /// </summary>
        [Required(ErrorMessage = "URL is required.")]
        [StringLength(500, ErrorMessage = "URL cannot exceed 500 characters.")]
        [Url(ErrorMessage = "Invalid URL format.")]
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the name of the RSS source.
        /// </summary>
        [Required(ErrorMessage = "Source name is required.")]
        [StringLength(150, ErrorMessage = "Source name cannot exceed 150 characters.")]
        public string SourceName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether the RSS source is currently active.
        /// </summary>
        public bool IsActive { get; set; }

        /// <summary>
        /// Gets or sets the date and time when the RSS source was created.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Gets or sets the date and time when the RSS source was last updated.
        /// </summary>
        public DateTime? UpdatedAt { get; set; }

        /// <summary>
        /// Gets or sets the date and time when the RSS source was last fetched.
        /// </summary>
        public DateTime? LastFetchedAt { get; set; }

        /// <summary>
        /// Gets or sets the fetch interval in minutes for the RSS source.
        /// </summary>
        public int? FetchIntervalMinutes { get; set; }

        /// <summary>
        /// Gets or sets the count of errors encountered during fetching.
        /// </summary>
        public int FetchErrorCount { get; set; }

        /// <summary>
        /// Gets or sets an optional description for the RSS source.
        /// </summary>
        [StringLength(1000, ErrorMessage = "Description cannot exceed 1000 characters.")]
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets the unique identifier of the default signal category for signals generated from this RSS source.
        /// </summary>
        public Guid? DefaultSignalCategoryId { get; set; }

        /// <summary>
        /// Gets or sets the name of the default signal category (for display purposes).
        /// </summary>
        public string? DefaultSignalCategoryName { get; set; } // نام دسته پیش‌فرض برای نمایش

        /// <summary>
        /// Gets or sets the ETag for the RSS feed to manage caching and avoid re-fetching unchanged content.
        /// </summary>
        [StringLength(255, ErrorMessage = "ETag cannot exceed 255 characters.")]
        public string? ETag { get; set; }

        #endregion
    }
}