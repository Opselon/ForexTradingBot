﻿namespace Domain.Settings
{
    /// <summary>
    /// کلاسی برای نگهداری تنظیمات مربوط به ربات تلگرام.
    /// این تنظیمات معمولاً از منابع خارجی مانند فایل‌های پیکربندی (appsettings.json)
    /// یا متغیرهای محیطی در هنگام شروع برنامه بارگذاری می‌شوند.
    /// </summary>
    public class TelegramSettings
    {
        /// <summary>
        /// توکن احراز هویت ربات تلگرام.
        /// این توکن توسط BotFather تلگرام ارائه می‌شود و برای ارتباط ربات با API تلگرام ضروری است.
        /// نیازمندی: این فیلد اجباری است و باید در پیکربندی برنامه مقداردهی شود.
        /// مقدار پیش‌فرض: <see cref="string.Empty"/> (انتظار می‌رود در زمان اجرا مقداردهی شود).
        /// </summary>
        // [Required(ErrorMessage = "توکن ربات تلگرام الزامی است.")] // اگر از اعتبارسنجی Options استفاده می‌کنید
        public string BotToken { get; set; } = string.Empty;

        /// <summary>
        /// شناسه کاربری (عددی) ادمین اصلی ربات در تلگرام.
        /// می‌تواند برای ارسال پیام‌های مدیریتی، گزارش خطاها یا محدود کردن دسترسی به دستورات خاص استفاده شود.
        /// اگر چندین ادمین وجود دارد، می‌توان از یک لیست از شناسه‌ها یا یک رشته جدا شده با کاما استفاده کرد.
        /// نیازمندی: این فیلد برای عملکرد صحیح برخی ویژگی‌های مدیریتی توصیه می‌شود.
        /// مقدار پیش‌فرض: <see cref="string.Empty"/> (انتظار می‌رود در زمان اجرا مقداردهی شود).
        /// </summary>
        public string AdminUserId { get; set; } = string.Empty; //  می‌تواند long یا int باشد اگر فقط عددی است.

        /// <summary>
        /// (پیشنهادی) آدرس URL عمومی که تلگرام آپدیت‌ها را به آن از طریق Webhook ارسال می‌کند.
        /// اگر از مکانیزم Webhook برای دریافت پیام‌ها استفاده می‌شود، این فیلد ضروری است.
        /// مثال: "https://yourdomain.com/api/telegram/update"
        /// مقدار پیش‌فرض: <see cref="string.Empty"/> (انتظار می‌رود در زمان اجرا مقداردهی شود، اگر از Webhook استفاده می‌شود).
        /// </summary>
        public string WebhookUrl { get; set; } = string.Empty;

        /// <summary>
        /// (اختیاری) نام کاربری ربات تلگرام (مثلاً "@YourBotName").
        /// می‌تواند برای برخی لینک‌سازی‌ها یا نمایش در لاگ‌ها مفید باشد.
        /// مقدار پیش‌فرض: <see cref="string.Empty"/>.
        /// </summary>
        public string BotUsername { get; set; } = string.Empty;


        // ملاحظات و نیازمندی‌های اضافی بالقوه:
        // 1. AdminChatId (string یا long): شناسه چت خصوصی با ادمین، گاهی اوقات متفاوت از UserId است یا برای ارسال پیام راحت‌تر است.
        // 2. AllowedChatIds (List<string> یا string[]): لیستی از شناسه‌های چت (کاربران یا گروه‌ها) که ربات مجاز به فعالیت در آن‌ها است.
        // 3. ParseMode (string): حالت پیش‌فرض پارس متن پیام‌ها (مثلاً "MarkdownV2", "HTML").
        //    public string DefaultParseMode { get; set; } = "MarkdownV2"; // یا از یک enum
        // 4. MaxMessageLength (int): حداکثر طول پیام‌هایی که ربات ارسال می‌کند.

        // سازنده پیش‌فرض
        public TelegramSettings() { }
    }
}