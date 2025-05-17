namespace Domain.Entities
{
    /// <summary>
    /// موجودیتی برای نمایش تنظیمات برگزیده یک کاربر برای دسته‌های خاصی از سیگنال‌ها.
    /// این کلاس به عنوان یک جدول اتصال (join table) بین کاربر <see cref="User"/> و دسته‌بندی سیگنال <see cref="SignalCategory"/> عمل می‌کند.
    /// هر رکورد نشان می‌دهد که یک کاربر به کدام دسته از سیگنال‌ها علاقه‌مند است.
    /// </summary>
    public class UserSignalPreference
    {
        /// <summary>
        /// شناسه یکتای تنظیمات برگزیده کاربر برای یک دسته سیگنال.
        /// به عنوان کلید اصلی (Primary Key) در پایگاه داده استفاده می‌شود.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// </summary>
        // [ForeignKey(nameof(User))] //  می‌توان برای وضوح بیشتر و یا کنترل دقیق‌تر نام کلید خارجی استفاده کرد
        public Guid UserId { get; set; }

        /// <summary>
        /// نویگیشن به موجودیت کاربر (<see cref="Entities.User"/>) که این تنظیمات برگزیده به او تعلق دارد.
        /// این خصوصیت توسط Entity Framework Core برای بارگذاری موجودیت مرتبط کاربر استفاده می‌شود.
        /// انتظار می‌رود `null!` نباشد زیرا هر تنظیم برگزیده باید به یک کاربر مرتبط باشد.
        /// </summary>
        public User User { get; set; } = null!;

        /// <summary>
        /// </summary>
        // [ForeignKey(nameof(Category))] //  می‌توان برای وضوح بیشتر و یا کنترل دقیق‌تر نام کلید خارجی استفاده کرد
        public Guid CategoryId { get; set; }

        /// <summary>
        /// نویگیشن به موجودیت دسته سیگنال (<see cref="SignalCategory"/>) مورد علاقه کاربر.
        /// این خصوصیت توسط Entity Framework Core برای بارگذاری موجودیت مرتبط دسته سیگنال استفاده می‌شود.
        /// انتظار می‌رود `null!` نباشد زیرا هر تنظیم برگزیده باید به یک دسته سیگنال مرتبط باشد.
        /// </summary>
        public SignalCategory Category { get; set; } = null!;

        /// <summary>
        /// تاریخ و زمان ایجاد این تنظیم برگزیده به وقت جهانی (UTC).
        /// برای اهداف ممیزی و پیگیری زمان ایجاد علاقه کاربر به یک دسته سیگنال خاص استفاده می‌شود.
        /// به صورت پیش‌فرض با زمان جاری UTC در لحظه ایجاد نمونه از کلاس مقداردهی می‌شود.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // سازنده‌ها در صورت نیاز:
        // public UserSignalPreference(Guid userId, Guid categoryId)
        // {
        //     Id = Guid.NewGuid();
        //     UserId = userId;
        //     CategoryId = categoryId;
        //     CreatedAt = DateTime.UtcNow;
        //     // User و Category باید توسط EF Core یا از طریق سرویس‌ها بارگذاری شوند
        // }
    }
}
