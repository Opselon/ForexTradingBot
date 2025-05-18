// File: Domain/Entities/NewsItem.cs
#region Usings
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema; // برای ForeignKey و Column
#endregion

namespace Domain.Entities // ✅ Namespace صحیح
{
    /// <summary>
    /// Represents a news item fetched from an RSS source or other news feeds.
    /// This entity stores the core information of a news article along with metadata
    /// about its origin and processing within our system.
    /// </summary>
    public class NewsItem
    {
        #region Core Properties
        /// <summary>
        /// Unique identifier for the news item in our system (Primary Key).
        /// </summary>
        [Key]
        public Guid Id { get; set; }

        /// <summary>
        /// The main title of the news item.
        /// </summary>
        [Required(ErrorMessage = "Title is required for a news item.")]
        [MaxLength(500)] //  طول مناسب برای عنوان، قابل تنظیم
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// The direct URL/Link to the original news article on the source website.
        /// </summary>
        [Required(ErrorMessage = "Link to the original article is required.")]
        [MaxLength(2083)] //  طول استاندارد برای URL
        public string Link { get; set; } = string.Empty;

        /// <summary>
        /// A brief summary or description of the news item.
        /// This can be plain text or contain HTML depending on the RSS feed.
        /// </summary>
        [Column(TypeName = "nvarchar(max)")] //  برای ذخیره متن‌های طولانی (SQL Server)
                                             //  یا "text" برای PostgreSQL
        public string? Summary { get; set; } //  نامی که شما استفاده کردید (Summary)

        /// <summary>
        /// (Optional) The full content of the news item, if fetched and stored.
        /// This might be useful for local analysis or if the original link becomes unavailable.
        /// </summary>
        [Column(TypeName = "nvarchar(max)")]
        public string? FullContent { get; set; } //  نامی که شما استفاده کردید

        /// <summary>
        /// (Optional) URL to the main image associated with the news article.
        /// </summary>
        [MaxLength(2083)]
        public string? ImageUrl { get; set; } //  نامی که شما استفاده کردید
        #endregion

        #region Source and Timing Information
        /// <summary>
        /// The original publication date of the news item from the source (UTC).
        /// </summary>
        public DateTime PublishedDate { get; set; } //  نامی که شما استفاده کردید (قبلاً DateTime? بود، اگر همیشه مقدار دارد Required کنید)

        /// <summary>
        /// Date and time when this news item was fetched and added to our system (UTC).
        /// </summary>
        [Required]
        public DateTime CreatedAt { get; set; } //  نامی که شما استفاده کردید (زمان ورود به سیستم ما)

        /// <summary>
        /// (Optional) Date and time when this news item was last processed by any system job (e.g., sent as notification, analyzed by AI) (UTC).
        /// </summary>
        public DateTime? LastProcessedAt { get; set; } //  نامی که شما استفاده کردید

        /// <summary>
        /// The human-readable name of the source from which this news item was fetched (e.g., "ForexLive", "Investing.com").
        /// This can be copied from RssSource.SourceName for easier display and querying, reducing joins.
        /// </summary>
        [MaxLength(150)]
        public string? SourceName { get; set; } // ✅✅✅ این فیلد اضافه شد ✅✅✅

        /// <summary>
        /// A unique identifier for this news item as provided by its original source (e.g., 'guid' or 'id' tag in an RSS item).
        /// This is crucial for preventing duplicate entries when re-fetching feeds, especially if PublishDate or Link might change slightly.
        /// It should be unique in combination with RssSourceId.
        /// </summary>
        [MaxLength(500)] //  طول مناسب برای شناسه‌های خارجی
        public string? SourceItemId { get; set; } // ✅✅✅ این فیلد اضافه شد ✅✅✅
        #endregion

        #region Analysis and Categorization
        /// <summary>
        /// (Optional) Sentiment score if an analysis has been performed (e.g., -1.0 for very negative to 1.0 for very positive).
        /// </summary>
        public double? SentimentScore { get; set; } //  نامی که شما استفاده کردید

        /// <summary>
        /// (Optional) A textual label for the sentiment (e.g., "Positive", "Negative", "Neutral").
        /// </summary>
        [MaxLength(50)]
        public string? SentimentLabel { get; set; } //  نامی که شما استفاده کردید

        /// <summary>
        /// (Optional) The detected language of the news item (e.g., "en", "fa").
        /// </summary>
        [MaxLength(10)]
        public string? DetectedLanguage { get; set; } //  نامی که شما استفاده کردید

        /// <summary>
        /// (Optional) A comma-separated list or JSON array of financial assets (e.g., "EURUSD,GBPUSD,XAUUSD")
        /// that this news item is likely to affect.
        /// </summary>
        [MaxLength(500)] //  طول مناسب برای لیست دارایی‌ها
        public string? AffectedAssets { get; set; } //  نامی که شما استفاده کردید
        #endregion

        #region Foreign Keys and Navigation Properties
        /// <summary>
        /// Foreign key to the <see cref="RssSource"/> entity from which this news item was fetched.
        /// </summary>
        [Required]
        public Guid RssSourceId { get; set; } //  این فیلد از قبل در کد شما وجود داشت

        /// <summary>
        /// Navigation property to the <see cref="RssSource"/> from which this news item originated.
        /// The 'virtual' keyword enables lazy loading if configured in EF Core.
        /// </summary>
        [ForeignKey(nameof(RssSourceId))]
        public virtual RssSource RssSource { get; set; } = null!; //  این فیلد از قبل در کد شما وجود داشت

        //  می‌توانید در آینده روابط دیگری اضافه کنید، مثلاً با SignalCategory یا User (اگر کاربر بتواند خبری را بوکمارک کند)
        // public Guid? AssociatedSignalCategoryId { get; set; }
        // public virtual SignalCategory? AssociatedSignalCategory { get; set; }
        #endregion

        #region Constructor
        /// <summary>
        /// Default constructor. Initializes ID and CreatedAt.
        /// </summary>
        public NewsItem()
        {
            Id = Guid.NewGuid();
            CreatedAt = DateTime.UtcNow; // زمان ورود به سیستم ما
        }
        #endregion
    }
}