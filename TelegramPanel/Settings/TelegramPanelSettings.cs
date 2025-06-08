using Telegram.Bot.Types.Enums;   // برای UpdateType

namespace TelegramPanel.Settings
{
    /// <summary>
    /// کلاسی برای نگهداری و مدل‌سازی تنظیمات اختصاصی مربوط به پنل/ربات تلگرام.
    /// </summary>
    public class TelegramPanelSettings
    {
        public const string SectionName = "TelegramPanel";

        /// <summary>
        /// توکن احراز هویت ربات تلگرام.
        /// </summary>
        public string BotToken { get; set; } = string.Empty;


        public List<long> AdminUserIds { get; set; } = new();



        /// <summary>
        /// مشخص می‌کند که آیا ربات باید از Webhook برای دریافت آپدیت‌ها استفاده کند یا خیر.
        /// اگر false باشد یا WebhookAddress خالی باشد، ربات از Polling استفاده خواهد کرد.
        /// </summary>
        public bool UseWebhook { get; set; } = false; // مقدار پیش‌فرض می‌تواند true یا false باشد بسته به اولویت شما

        /// <summary>
        /// آدرس URL عمومی که تلگرام آپدیت‌ها را به آن از طریق Webhook ارسال می‌کند.
        /// فقط زمانی استفاده می‌شود که UseWebhook برابر true باشد.
        /// </summary>
        public string WebhookAddress { get; set; } = string.Empty;

        /// <summary>
        /// یک Secret Token برای امنیت بیشتر Webhook.
        /// </summary>
        public string? WebhookSecretToken { get; set; }

        /// <summary>
        /// (اختیاری) لیستی از انواع آپدیت‌هایی که ربات به آن‌ها گوش می‌دهد (هم برای Webhook و هم برای Polling).
        /// اگر null یا خالی باشد، تمام انواع آپدیت‌ها دریافت می‌شوند.
        /// مقادیر معتبر از enum <see cref="Telegram.Bot.Types.Enums.UpdateType"/> هستند.
        /// مثال در appsettings.json: "AllowedUpdates": ["Message", "CallbackQuery"]
        /// </summary>
        public List<UpdateType>? AllowedUpdates { get; set; } //  می‌تواند null باشد

        /// <summary>
        /// (اختیاری، فقط برای Webhook) مشخص می‌کند که آیا آپدیت‌های در صف مانده تلگرام
        /// باید هنگام تنظیم Webhook جدید، نادیده گرفته شوند (drop شوند) یا خیر.
        /// پیش‌فرض معمولاً true است تا از پردازش آپدیت‌های قدیمی جلوگیری شود.
        /// </summary>
        public bool DropPendingUpdatesOnWebhookSet { get; set; } = true; //  مقدار پیش‌فرض

        // فیلدهای دیگری که در appsettings.json داشتید (اگر برای این پنل هستند):
        // public int PollingInterval { get; set; } = 0; //  در حال حاضر توسط StartReceiving مدیریت نمی‌شود
        // public List<long>? AdminUserIds { get; set; }
        // public bool EnableDebugMode { get; set; }
    }
}