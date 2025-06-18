// File: Application/DTOs/News/NewsItemDto.cs
#region Usings
#endregion

namespace Application.DTOs.News // ✅ Namespace صحیح
{
    /// <summary>
    /// Data Transfer Object for representing a news item.
    /// Used to transfer news data between layers, e.g., from Application to Presentation.
    /// </summary>
    public class NewsItemDto
    {
        #region Properties

        /// <summary>
        /// Unique identifier of the news item.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// The title of the news item.
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// The direct URL link to the original news article.
        /// </summary>
        public string Link { get; set; } = string.Empty;

        /// <summary>
        /// A short summary or description of the news item.
        /// May contain HTML that needs to be cleaned for display.
        /// </summary>
        public string? Summary { get; set; }

        /// <summary>
        /// URL of the main image associated with the news item, if available.
        /// If no image is found, a default image will be used.
        /// </summary>
        public string? ImageUrl { get; set; }

        /// <summary>
        /// The fallback image URL used if no specific image is provided for the news item.
        /// </summary>
        private const string DefaultImageUrl = "http://localhost:5000/Breaking_News.jpg"; // Example default image path, adjust as needed

        /// <summary>
        /// The actual image URL that will be used for display. If <see cref="ImageUrl"/> is null or empty,
        /// the <see cref="DefaultImageUrl"/> will be used instead.
        /// </summary>
        public string ImageUrlOrDefault =>
            string.IsNullOrWhiteSpace(ImageUrl) ? DefaultImageUrl : ImageUrl;

        /// <summary>
        /// The original publication date and time of the news item from the RSS source (UTC).
        /// </summary>
        public DateTime PublishedDate { get; set; }

        /// <summary>
        /// The name of the RSS source from which this news item was fetched.
        /// </summary>
        public string SourceName { get; set; } = string.Empty; // نام منبع RSS برای نمایش

        /// <summary>
        /// (Optional) Sentiment score of the news item (e.g., -1.0 to 1.0).
        /// Populated if sentiment analysis is performed.
        /// </summary>
        public double? SentimentScore { get; set; }

        /// <summary>
        /// (Optional) Label for the sentiment (e.g., "Positive", "Negative", "Neutral").
        /// </summary>
        public string? SentimentLabel { get; set; }

        /// <summary>
        /// (Optional) Comma-separated list or JSON string of assets/currencies potentially affected by this news.
        /// </summary>
        public string? AffectedAssets { get; set; }

        /// <summary>
        /// The date and time when this news item was added to our system (UTC).
        /// </summary>
        public DateTime CreatedAtInSystem { get; set; }

        #endregion
    }
}