using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations; // برای توضیحات بیشتر در مورد اعتبارسنجی‌های احتمالی (اختیاری)
using Domain.Enums; // برای دسترسی به UserLevel

namespace Domain.Entities
{
    /// <summary>
    /// موجودیت اصلی برای نمایش اطلاعات کاربر در سیستم.
    /// این کلاس شامل اطلاعات هویتی، سطح دسترسی، کیف پول، اشتراک‌ها، تراکنش‌ها و تنظیمات برگزیده کاربر است.
    /// </summary>
    public class User
    {
        /// <summary>
        /// شناسه یکتای کاربر در سیستم.
        /// به عنوان کلید اصلی (Primary Key) در پایگاه داده استفاده می‌شود.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// نام کاربری انتخاب شده توسط کاربر.
        /// می‌تواند برای نمایش یا ورود (در صورت وجود پنل وب) استفاده شود.
        /// این فیلد اجباری است و انتظار می‌رود هنگام ایجاد کاربر مقداردهی شود.
        /// </summary>
        [Required] // مثال: اگر از DataAnnotations برای اعتبارسنجی استفاده می‌کنید
        public string Username { get; set; } = null!;

        /// <summary>
        /// شناسه یکتای کاربر در تلگرام.
        /// این شناسه برای شناسایی کاربر و ارسال پیام از طریق ربات تلگرام حیاتی است.
        /// این فیلد اجباری است و انتظار می‌رود هنگام ثبت‌نام کاربر از طریق تلگرام مقداردهی شود.
        /// </summary>
        [Required]
        public string TelegramId { get; set; } = null!;

        /// <summary>
        /// آدرس ایمیل کاربر.
        /// می‌تواند برای اطلاع‌رسانی‌ها، بازیابی حساب (در صورت وجود) یا به عنوان یک شناسه جایگزین استفاده شود.
        /// این فیلد اجباری است و انتظار می‌رود هنگام ایجاد کاربر مقداردهی شود.
        /// </summary>
        [EmailAddress] // مثال
        [Required]
        public string Email { get; set; } = null!;

        /// <summary>
        /// سطح دسترسی کاربر (مانند رایگان، پولی).
        /// برای کنترل دسترسی به امکانات مختلف ربات استفاده می‌شود.
        /// مقدار پیش‌فرض <see cref="UserLevel.Free"/> است.
        /// </summary>
        public UserLevel Level { get; set; } = UserLevel.Free;

        /// <summary>
        /// نویگیشن به موجودیت کیف پول توکن کاربر.
        /// برای مدیریت توکن‌ها یا اعتبارات کاربر در سیستم استفاده می‌شود (در صورت پیاده‌سازی چنین مکانیزمی).
        /// انتظار می‌رود این موجودیت مرتبط هنگام ایجاد کاربر ایجاد یا پیوند داده شود.
        /// </summary>
        public TokenWallet TokenWallet { get; set; } = null!; // این باید در زمان ایجاد کاربر، مدیریت شود که null نباشد.

        /// <summary>
        /// لیستی از اشتراک‌های کاربر.
        /// یک کاربر می‌تواند چندین اشتراک فعال یا منقضی شده داشته باشد.
        /// به صورت پیش‌فرض به عنوان یک لیست خالی مقداردهی اولیه می‌شود تا از NullReferenceException جلوگیری شود.
        /// </summary>
        public List<Subscription> Subscriptions { get; set; } = new();

        /// <summary>
        /// لیستی از تراکنش‌های مالی یا توکنی کاربر.
        /// برای نگهداری سابقه پرداخت‌ها، خرید توکن یا استفاده از آن‌ها استفاده می‌شود.
        /// به صورت پیش‌فرض به عنوان یک لیست خالی مقداردهی اولیه می‌شود.
        /// </summary>
        public List<Transaction> Transactions { get; set; } = new();

        /// <summary>
        /// مجموعه‌ای از تنظیمات برگزیده کاربر برای سیگنال‌ها.
        /// به کاربر اجازه می‌دهد تا انواع سیگنال‌هایی را که مایل به دریافت آن‌ها است، شخصی‌سازی کند.
        /// (رابطه یک به چند با <see cref="UserSignalPreference"/>)
        /// به صورت پیش‌فرض به عنوان یک لیست خالی مقداردهی اولیه می‌شود.
        /// </summary>
        public ICollection<UserSignalPreference> Preferences { get; set; } = new List<UserSignalPreference>();

        /// <summary>
        /// تاریخ و زمان ایجاد حساب کاربری به وقت جهانی (UTC).
        /// برای اهداف ممیزی و پیگیری استفاده می‌شود.
        /// به صورت پیش‌فرض با زمان جاری UTC در لحظه ایجاد نمونه از کلاس مقداردهی می‌شود.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // سازنده‌ها و متدها در صورت نیاز می‌توانند در اینجا اضافه شوند.
        // برای مثال، یک سازنده که مقادیر اولیه ضروری را دریافت کند:
        // public User(string username, string telegramId, string email)
        // {
        //     Id = Guid.NewGuid();
        //     Username = username ?? throw new ArgumentNullException(nameof(username));
        //     TelegramId = telegramId ?? throw new ArgumentNullException(nameof(telegramId));
        //     Email = email ?? throw new ArgumentNullException(nameof(email));
        //     Level = UserLevel.Free;
        //     TokenWallet = new TokenWallet(); // یا روش مناسب دیگر برای مقداردهی اولیه
        //     Subscriptions = new List<Subscription>();
        //     Transactions = new List<Transaction>();
        //     Preferences = new List<UserSignalPreference>();
        //     CreatedAt = DateTime.UtcNow;
        // }
    }
}