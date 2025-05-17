using Domain.Enums; // برای دسترسی به TransactionType
using System.ComponentModel.DataAnnotations; // برای اعتبارسنجی (اختیاری)

namespace Domain.Entities
{
    /// <summary>
    /// موجودیتی برای نمایش یک تراکنش مالی یا توکنی در سیستم.
    /// هر تراکنش به یک کاربر خاص مرتبط است و شامل اطلاعاتی مانند مبلغ، نوع تراکنش، توضیحات و زمان انجام آن است.
    /// این کلاس برای ردیابی تاریخچه فعالیت‌های مالی یا استفاده از توکن‌های کاربر استفاده می‌شود.
    /// </summary>
    public class Transaction
    {
        /// <summary>
        /// شناسه یکتای تراکنش.
        /// به عنوان کلید اصلی (Primary Key) در پایگاه داده استفاده می‌شود.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// </summary>
        // [ForeignKey(nameof(User))] // می‌تواند برای وضوح بیشتر استفاده شود
        public Guid UserId { get; set; }

        /// <summary>
        /// نویگیشن به موجودیت کاربر (<see cref="Entities.User"/>) که این تراکنش برای او ثبت شده است.
        /// این خصوصیت توسط Entity Framework Core برای بارگذاری موجودیت مرتبط کاربر استفاده می‌شود.
        /// انتظار می‌رود `null!` نباشد زیرا هر تراکنش باید به یک کاربر معتبر مرتبط باشد.
        /// </summary>
        public User User { get; set; } = null!;

        /// <summary>
        /// مبلغ تراکنش.
        /// این مقدار می‌تواند مثبت (برای واریز، خرید اشتراک) یا منفی (برای برداشت، در صورت پیاده‌سازی) باشد.
        /// واحد پولی یا نوع توکن باید در سطح برنامه یا بر اساس <see cref="Type"/> مشخص شود.
        /// </summary>
        public decimal Amount { get; set; }

        /// <summary>
        /// نوع تراکنش، به عنوان مثال: پرداخت اشتراک، خرید توکن، واریز، برداشت و غیره.
        /// از <see cref="Enums.TransactionType"/> برای تعیین نوع استفاده می‌شود.
        /// </summary>
        public TransactionType Type { get; set; }

        /// <summary>
        /// توضیحات اختیاری برای تراکنش.
        /// می‌تواند شامل جزئیات بیشتری مانند شماره پیگیری پرداخت، دلیل تراکنش و غیره باشد.
        /// این فیلد می‌تواند `null` باشد اگر توضیحات اضافی لازم نباشد.
        /// </summary>
        [MaxLength(500)] // مثال: محدود کردن طول توضیحات
        public string? Description { get; set; } // علامت سوال نشان می‌دهد که این رشته می‌تواند null باشد.

        /// <summary>
        /// تاریخ و زمان دقیق انجام تراکنش به وقت جهانی (UTC).
        /// به صورت پیش‌فرض با زمان جاری UTC در لحظه ایجاد نمونه از کلاس مقداردهی می‌شود.
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        // سازنده‌ها در صورت نیاز:
        // public Transaction(Guid userId, decimal amount, TransactionType type, string? description = null)
        // {
        //     Id = Guid.NewGuid();
        //     UserId = userId;
        //     Amount = amount;
        //     Type = type;
        //     Description = description;
        //     Timestamp = DateTime.UtcNow;
        //     // User باید توسط EF Core یا از طریق سرویس‌ها بارگذاری شود
        // }
    }
}