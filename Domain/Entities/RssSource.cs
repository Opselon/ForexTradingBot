// File: Domain/Entities/RssSource.cs
#region Usings
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema; // برای ForeignKey
#endregion

namespace Domain.Entities
{
    /// <summary>
    /// Represents an RSS feed source.
    /// The bot uses these sources to gather news, data, or potential signals.
    /// </summary>
    public class RssSource
    {
        #region Core Properties
        /// <summary>
        /// Unique identifier for the RSS source (Primary Key).
        /// </summary>
        [Key]
        public Guid Id { get; set; }

        /// <summary>
        /// Full URL of the RSS feed.
        /// </summary>
        [Required(ErrorMessage = "URL is required for the RSS source.")]
        [Url(ErrorMessage = "The URL format is invalid.")]
        [MaxLength(2083)] // Standard max URL length
        public string Url { get; set; } = null!;

        /// <summary>
        /// Human-readable name describing this RSS source (e.g., "ForexLive News").
        /// </summary>
        [Required(ErrorMessage = "Source name is required.")]
        [MaxLength(150)]
        public string SourceName { get; set; } = null!;

        /// <summary>
        /// Indicates if this RSS source is currently active for data collection.
        /// If false, the bot should not fetch information from this source.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Date and time when this RSS source record was created in the system (UTC).
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Date and time of the last update to this RSS source record (UTC).
        /// Nullable if the record has never been updated.
        /// </summary>
        public DateTime? UpdatedAt { get; set; }
        #endregion

        #region RSS Fetching Specific Properties
        /// <summary>
        /// Stores the value of the 'Last-Modified' HTTP header from the last successful fetch.
        /// Used for conditional GET requests to optimize fetching.
        /// </summary>
        [MaxLength(100)]
        public string? LastModifiedHeader { get; set; } // ✅✅✅ این فیلد اضافه شد ✅✅✅

        /// <summary>
        /// Stores the value of the 'ETag' HTTP header from the last successful fetch.
        /// Also used for conditional GET requests.
        /// </summary>
        [MaxLength(255)]
        public string? ETag { get; set; } //  این فیلد از قبل در کد شما وجود داشت

        /// <summary>
        /// The last time an attempt was made to fetch this RSS feed (UTC), regardless of success.
        /// </summary>
        public DateTime? LastFetchAttemptAt { get; set; } // ✅ این فیلد را هم اضافه کنید

        /// <summary>
        /// The last time this RSS feed was successfully fetched and its content processed (UTC).
        /// (Renamed from your 'LastFetchedAt' for clarity, but you can keep your name if preferred)
        /// </summary>
        public DateTime? LastSuccessfulFetchAt { get; set; } // ✅ این فیلد را هم اضافه کنید (جایگزین LastFetchedAt)

        /// <summary>
        /// Custom fetch interval in minutes for this specific RSS source.
        /// If null, a default system-wide interval is used.
        /// </summary>
        public int? FetchIntervalMinutes { get; set; } //  این فیلد از قبل در کد شما وجود داشت

        /// <summary>
        /// Count of consecutive errors encountered while trying to fetch this feed.
        /// This can be used to temporarily disable a problematic feed or reduce its fetch frequency.
        /// </summary>
        public int FetchErrorCount { get; set; } = 0; //  این فیلد از قبل در کد شما وجود داشت

        /// <summary>
        /// (Optional) A brief description of the RSS source or the type of content it provides.
        /// </summary>
        [MaxLength(1000)]
        public string? Description { get; set; } //  این فیلد از قبل در کد شما وجود داشت

        /// <summary>
        /// (Optional) Default Signal Category ID to associate with news items from this source.
        /// </summary>
        public Guid? DefaultSignalCategoryId { get; set; } //  این فیلد از قبل در کد شما وجود داشت
        #endregion

        #region Navigation Properties
        /// <summary>
        /// (Optional) Navigation property to the default SignalCategory.
        /// </summary>
        [ForeignKey(nameof(DefaultSignalCategoryId))]
        public virtual SignalCategory? DefaultSignalCategory { get; set; } //  این فیلد از قبل در کد شما وجود داشت

        /// <summary>
        /// Collection of news items fetched from this RSS source.
        /// This defines the "many" side of the one-to-many relationship.
        /// </summary>
        public virtual ICollection<NewsItem> NewsItems { get; set; } //  برای رابطه با NewsItem
        #endregion

        #region Constructor
        /// <summary>
        /// Default constructor required by EF Core.
        /// Initializes collections to prevent null reference issues.
        /// </summary>
        public RssSource()
        {
            Id = Guid.NewGuid(); // Initialize Id
            CreatedAt = DateTime.UtcNow; // Initialize CreatedAt
            IsActive = true;
            FetchErrorCount = 0;
            NewsItems = []; // Initialize collection
        }
        #endregion
    }
}