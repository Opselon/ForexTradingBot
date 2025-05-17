using System;
using System.ComponentModel.DataAnnotations;

namespace Domain.Entities
{
    /// <summary>
    /// موجودیتی برای نمایش یک منبع فید RSS (Really Simple Syndication).
    /// ربات از این منابع برای جمع‌آوری اخبار، داده‌ها یا سیگنال‌های بالقوه استفاده می‌کند.
    /// شامل اطلاعاتی مانند URL فید، نام منبع، وضعیت فعالیت و زمان ایجاد است.
    /// </summary>
    public class RssSource
    {
        /// <summary>
        /// شناسه یکتای منبع RSS.
        /// به عنوان کلید اصلی (Primary Key) در پایگاه داده استفاده می‌شود.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// آدرس URL کامل فید RSS.
        /// این آدرس برای دسترسی و خواندن محتوای فید استفاده می‌شود.
        /// نیازمندی: این فیلد اجباری است و باید یک URL معتبر باشد. معمولاً باید منحصر به فرد باشد تا از تکرار منابع جلوگیری شود.
        /// </summary>
        [Required(ErrorMessage = "آدرس URL منبع RSS الزامی است.")]
        [Url(ErrorMessage = "آدرس URL وارد شده معتبر نیست.")]
        [MaxLength(500, ErrorMessage = "طول آدرس URL نمی‌تواند بیش از 500 کاراکتر باشد.")]
        //  [Index(IsUnique = true)] // برای منحصر به فرد بودن URL، معمولاً با Fluent API در DbContext تعریف می‌شود.
        public string Url { get; set; } = null!;

        /// <summary>
        /// نام قابل خواندن برای انسان که این منبع RSS را توصیف می‌کند (مثلاً "ForexLive News", "Investing.com Economic Calendar").
        /// برای نمایش و شناسایی آسان منبع استفاده می‌شود.
        /// نیازمندی: این فیلد اجباری است.
        /// </summary>
        [Required(ErrorMessage = "نام منبع RSS الزامی است.")]
        [MaxLength(150, ErrorMessage = "طول نام منبع نمی‌تواند بیش از 150 کاراکتر باشد.")]
        public string SourceName { get; set; } = null!;

        /// <summary>
        /// نشان می‌دهد که آیا این منبع RSS در حال حاضر برای جمع‌آوری داده فعال است یا خیر.
        /// اگر `false` باشد، ربات نباید از این منبع اطلاعاتی بخواند.
        /// مقدار پیش‌فرض `true` (فعال) است.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// تاریخ و زمان ایجاد این رکورد منبع RSS به وقت جهانی (UTC).
        /// برای اهداف ممیزی و پیگیری استفاده می‌شود.
        /// به صورت پیش‌فرض با زمان جاری UTC در لحظه ایجاد نمونه از کلاس مقداردهی می‌شود.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; } 

        // ملاحظات و نیازمندی‌های اضافی بالقوه:
        // 1. LastFetchedAt (DateTime?): زمان آخرین باری که اطلاعات از این فید با موفقیت خوانده شد.
        public DateTime? LastFetchedAt { get; set; }
        //
        // 2. LastFetchAttemptAt (DateTime?): زمان آخرین تلاش برای خواندن (موفق یا ناموفق).
        //    public DateTime? LastFetchAttemptAt { get; set; }
        //
        // 3. FetchIntervalMinutes (int?): فاصله زمانی دلخواه (به دقیقه) برای خواندن این فید. اگر null باشد، از یک مقدار پیش‌فرض سیستمی استفاده می‌شود.
            public int? FetchIntervalMinutes { get; set; }
        //
        // 4. FetchErrorCount (int): تعداد خطاهای متوالی هنگام تلاش برای خواندن این فید.
            public int FetchErrorCount { get; set; } = 0;
        //
        // 5. Description (string?): توضیح کوتاهی در مورد محتوای این منبع RSS.
            public string? Description { get; set; }
        //
        // 6. DefaultSignalCategoryId (Guid?): شناسه دسته‌بندی پیش‌فرض برای سیگنال‌ها/اخبار دریافتی از این منبع.
            public Guid? DefaultSignalCategoryId { get; set; }
            public SignalCategory? DefaultSignalCategory { get; set; }
        //
        // 7. ETag (string?) یا LastModified (string?): برای بهینه‌سازی فرآیند خواندن فید با استفاده از هدرهای HTTP (Conditional GET).
            public string? ETag { get; set; }

        // سازنده پیش‌فرض برای EF Core
        public RssSource() { }

        // سازنده برای ایجاد یک منبع RSS جدید
        // public RssSource(string url, string sourceName, bool isActive = true)
        // {
        //     if (string.IsNullOrWhiteSpace(url))
        //         throw new ArgumentException("URL نمی‌تواند خالی باشد.", nameof(url));
        //     if (!Uri.TryCreate(url, UriKind.Absolute, out _)) // اعتبارسنجی اولیه URL
        //         throw new ArgumentException("URL نامعتبر است.", nameof(url));
        //     if (string.IsNullOrWhiteSpace(sourceName))
        //         throw new ArgumentException("نام منبع نمی‌تواند خالی باشد.", nameof(sourceName));

        //     Id = Guid.NewGuid();
        //     Url = url;
        //     SourceName = sourceName;
        //     IsActive = isActive;
        //     CreatedAt = DateTime.UtcNow;
        // }
    }
}