using System.ComponentModel.DataAnnotations;

namespace Application.DTOs
{
    #region CreateRssSourceDto
    /// <summary>
    /// Data transfer object for creating a new RSS source.
    /// </summary>
    public class CreateRssSourceDto
    {
        /// <summary>
        /// Gets or sets the URL of the RSS feed.
        /// </summary>
        [Required]
        [Url]
        [StringLength(500)]
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the name of the RSS source.
        /// </summary>
        [Required]
        [StringLength(150)]
        public string SourceName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether the RSS source is active.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Gets or sets the description of the RSS source.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets the fetch interval in minutes for the RSS source.
        /// </summary>
        public int? FetchIntervalMinutes { get; set; }

        /// <summary>
        /// Gets or sets the default signal category ID for signals generated from this RSS source.
        /// </summary>
        public Guid? DefaultSignalCategoryId { get; set; }

        /// <summary>
        /// Gets or sets the ETag for the RSS feed to manage caching.
        /// </summary>
        public string? ETag { get; set; }
    }
    #endregion
}