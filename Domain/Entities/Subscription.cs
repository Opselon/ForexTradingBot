using System;
using System.ComponentModel.DataAnnotations.Schema; // برای ForeignKey Attribute (اختیاری)

namespace Domain.Entities
{
    /// <summary>
    /// موجودیتی برای نمایش یک اشتراک فعال یا گذشته کاربر به یک سرویس یا پلن خاص.
    /// این کلاس دوره زمانی اعتبار اشتراک کاربر را مشخص می‌کند.
    /// هر اشتراک به یک کاربر خاص تعلق دارد و معمولاً به یک نوع پلن اشتراکی مشخص مرتبط است (که در اینجا مستقیماً مدل نشده اما پیشنهاد می‌شود).
    /// </summary>
    public class Subscription
    {
        /// <summary>
        /// شناسه یکتای اشتراک.
        /// به عنوان کلید اصلی (Primary Key) در پایگاه داده استفاده می‌شود.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// شناسه کاربری که این اشتراک به او تعلق دارد.
        /// این یک کلید خارجی است که به شناسه کاربر (User.Id) اشاره دارد.
        /// </summary>
        // [ForeignKey(nameof(User))] // می‌تواند برای وضوح بیشتر یا پیکربندی دقیق‌تر EF Core استفاده شود.
        public Guid UserId { get; set; }

        /// <summary>
        /// نویگیشن به موجودیت کاربر (User) که صاحب این اشتراک است.
        /// این خصوصیت توسط Entity Framework Core برای بارگذاری موجودیت مرتبط کاربر استفاده می‌شود.
        /// انتظار می‌رود `null!` نباشد زیرا هر اشتراک باید به یک کاربر معتبر مرتبط باشد.
        /// </summary>
        public User User { get; set; } = null!; // مقداردهی اولیه برای جلوگیری از هشدار nullability.

        /// <summary>
        /// تاریخ و زمان شروع اعتبار اشتراک به وقت جهانی (UTC).
        /// اشتراک از این تاریخ و زمان به بعد معتبر تلقی می‌شود (با در نظر گرفتن EndDate).
        /// </summary>
        public DateTime StartDate { get; set; }

        /// <summary>
        /// تاریخ و زمان پایان اعتبار اشتراک به وقت جهانی (UTC).
        /// اشتراک تا این تاریخ و زمان (شامل خود این لحظه) معتبر است.
        /// </summary>
        public DateTime EndDate { get; set; }

        /// <summary>
        /// یک خصوصیت محاسباتی که نشان می‌دهد آیا اشتراک در حال حاضر فعال است یا خیر.
        /// اشتراک فعال در نظر گرفته می‌شود اگر تاریخ و زمان جاری (UTC) بین تاریخ شروع و تاریخ پایان (شامل هر دو) باشد.
        /// </summary>
        public bool IsActive => DateTime.UtcNow >= StartDate && DateTime.UtcNow <= EndDate;

        /// <summary>
        /// تاریخ و زمان ایجاد رکورد اشتراک به وقت جهانی (UTC).
        /// برای اهداف ممیزی و پیگیری زمان ثبت اشتراک استفاده می‌شود.
        /// به صورت پیش‌فرض با زمان جاری UTC در لحظه ایجاد نمونه از کلاس مقداردهی می‌شود.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // ملاحظات برای بهبود و کامل‌تر شدن:
        // 1. SubscriptionPlanId / ServiceTierId: یک شناسه برای ارجاع به پلن یا سطح خدماتی که کاربر مشترک آن شده است.
        //    همراه با یک خصوصیت ناوبری به موجودیت SubscriptionPlan. مثال:
        //    public Guid SubscriptionPlanId { get; set; }
        //    public SubscriptionPlan Plan { get; set; } = null!;
        //
        // 2. Status: ممکن است یک enum برای وضعیت‌های بیشتر اشتراک (مانند PendingPayment, Cancelled, Expired) مفید باشد
        //    اگر IsActive به تنهایی کافی نباشد.
        //
        // 3. TransactionId: شناسه تراکنشی که منجر به ایجاد یا تمدید این اشتراک شده است (اختیاری).

        // سازنده پیش‌فرض برای EF Core
        public Subscription() { }

        // سازنده برای ایجاد یک اشتراک جدید
        // public Subscription(Guid userId, DateTime startDate, DateTime endDate /*, Guid subscriptionPlanId */)
        // {
        //     Id = Guid.NewGuid();
        //     UserId = userId;
        //     StartDate = startDate;
        //     EndDate = endDate;
        //     // SubscriptionPlanId = subscriptionPlanId;
        //     CreatedAt = DateTime.UtcNow;
        //     // User و SubscriptionPlan باید توسط EF Core یا از طریق سرویس‌ها بارگذاری/پیوند داده شوند.
        // }
    }
}