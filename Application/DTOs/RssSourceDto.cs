namespace Application.DTOs
{
    public class RssSourceDto
    {
        public Guid Id { get; set; }
        public string Url { get; set; } = string.Empty;
        public string SourceName { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? LastFetchedAt { get; set; }
        public int? FetchIntervalMinutes { get; set; }
        public int FetchErrorCount { get; set; }
        public string? Description { get; set; }
        public Guid? DefaultSignalCategoryId { get; set; }
        public string? DefaultSignalCategoryName { get; set; } // نام دسته پیش‌فرض برای نمایش
        public string? ETag { get; set; }
    }
}