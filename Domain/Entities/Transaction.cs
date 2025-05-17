using Domain.Enums; // برای دسترسی به TransactionType
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema; // برای اعتبارسنجی (اختیاری)

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
        /// </summary>
        [MaxLength(100)] // طول مناسب برای شناسه‌های درگاه
        public string? PaymentGatewayInvoiceId { get; set; }

        /// <summary>
        /// نام درگاه پرداختی که این تراکنش از طریق آن انجام شده است (مثلاً "CryptoPay", "Stripe").
        /// این فیلد اختیاری است اما برای گزارش‌گیری و تفکیک می‌تواند مفید باشد.
        /// </summary>
        [MaxLength(50)]
        public string? PaymentGatewayName { get; set; }

        /// <summary>
        /// وضعیت فعلی تراکنش.
        /// مثال: "Pending", "Completed", "Failed", "Cancelled", "Refunded".
        /// بهتر است برای این مورد از یک enum استفاده شود اگر تعداد وضعیت‌ها زیاد و ثابت است.
        /// فعلاً به صورت رشته برای انعطاف‌پذیری بیشتر.
        /// </summary>
        [Required]
        [MaxLength(50)]
        public string Status { get; set; } = "Pending"; // مقدار پیش‌فرض می‌تواند "Pending" باشد

        /// <summary>
        /// تاریخ و زمانی که تراکنش واقعاً پرداخت و تکمیل شده است (به وقت UTC).
        /// این فیلد می‌تواند null باشد اگر تراکنش هنوز در وضعیت Pending یا Failed است.
        /// </summary>
        public DateTime? PaidAt { get; set; }

        /// <summary>
        /// داده‌های اضافی یا Payload که به درگاه پرداخت ارسال شده یا از آن دریافت شده است.
        /// می‌تواند برای ذخیره اطلاعات خاص درگاه یا پاسخ کامل درگاه به صورت JSON استفاده شود.
        /// </summary>
        public string? PaymentGatewayPayload { get; set; } // برای ذخیره داده‌های JSON مانند payload ارسالی یا پاسخ دریافتی

        /// <summary>
        /// پاسخ کامل یا بخشی از پاسخ دریافتی از درگاه پرداخت (اختیاری).
        /// برای اهداف اشکال‌زدایی یا ممیزی می‌تواند مفید باشد.
        /// </summary>
        [Column(TypeName = "nvarchar(max)")] // اگر می‌خواهید JSON طولانی ذخیره کنید
        public string? PaymentGatewayResponse { get; set; }


        // سازنده پیش‌فرض برای EF Core
        public Transaction() { }

        // سازنده برای ایجاد یک تراکنش جدید با مقادیر ضروری
        public Transaction(
            Guid userId,
            decimal amount,
            TransactionType type,
            string status,
            string? description = null,
            string? paymentGatewayInvoiceId = null,
            string? paymentGatewayName = null)
        {
            Id = Guid.NewGuid();
            UserId = userId;
            Amount = amount;
            Type = type;
            Status = status;
            Description = description;
            PaymentGatewayInvoiceId = paymentGatewayInvoiceId;
            PaymentGatewayName = paymentGatewayName;
            Timestamp = DateTime.UtcNow;
        }

    }
}