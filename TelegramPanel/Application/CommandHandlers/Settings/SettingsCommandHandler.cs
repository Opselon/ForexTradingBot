
#region Usings
// Using های استاندارد .NET
using Microsoft.Extensions.Logging; // برای لاگ‌برداری وقایع و خطا.
// Using های مربوط به کتابخانه Telegram.Bot
using Telegram.Bot.Types;           // برای Update, Message و سایر تایپ‌های تلگرام
using Telegram.Bot.Types.Enums;     // برای UpdateType, ParseMode
using Telegram.Bot.Types.ReplyMarkups; // برای InlineKeyboardMarkup, InlineKeyboardButton
using TelegramPanel.Application.CommandHandlers.MainMenu;


// Using های مربوط به پروژه TelegramPanel
using TelegramPanel.Application.Interfaces; // برای ITelegramCommandHandler
using TelegramPanel.Formatters;           // برای TelegramMessageFormatter (ابزار فرمت‌بندی متن)
using TelegramPanel.Infrastructure;
using TelegramPanel.Infrastructure.Helper;
#endregion

namespace TelegramPanel.Application.CommandHandlers.Settings
{
    /// <summary>
    /// Command Handler برای پردازش دستور "/settings" ارسالی توسط کاربر.
    /// مسئولیت این کلاس، نمایش منوی اصلی تنظیمات به کاربر با دکمه‌های Inline مناسب است.
    /// پردازش کلیک روی این دکمه‌ها به SettingsCallbackQueryHandler واگذار می‌شود.
    /// </summary>
    public class SettingsCommandHandler : ITelegramCommandHandler
    {
        #region Private Readonly Fields
        // سرویس لاگینگ برای ثبت اطلاعات و خطاهای مربوط به این Handler.
        private readonly ILogger<SettingsCommandHandler> _logger;
        // سرویس برای ارسال پیام‌های متنی و کیبوردها به کاربر از طریق ربات تلگرام.
        private readonly ITelegramMessageSender _messageSender;
        #endregion

        #region Public Callback Data Constants for Settings Menu
        // این ثابت‌ها مقادیر رشته‌ای منحصربه‌فردی هستند که به عنوان داده (CallbackData)
        // به دکمه‌های Inline منوی تنظیمات اختصاص داده می‌شوند.
        // SettingsCallbackQueryHandler از این ثابت‌ها برای تشخیص اینکه کدام دکمه کلیک شده استفاده می‌کند.

        /// <summary>
        /// CallbackData برای دکمه "My Signal Preferences" (تنظیمات برگزیده دسته‌بندی سیگنال‌ها).
        /// </summary>
        public const string PrefsSignalCategoriesCallback = "settings_prefs_signal_categories";

        /// <summary>
        /// CallbackData برای دکمه "Notification Settings" (تنظیمات مربوط به نوتیفیکیشن‌ها).
        /// </summary>
        public const string PrefsNotificationsCallback = "settings_prefs_notifications";

        /// <summary>
        /// CallbackData برای دکمه "My Subscription" (مشاهده اطلاعات اشتراک فعلی و گزینه‌های پرداخت).
        /// </summary>
        public const string MySubscriptionInfoCallback = "settings_my_subscription_info";

        /// <summary>
        /// CallbackData (اختیاری) برای دکمه "Signal History / Performance" (نمایش تاریخچه یا عملکرد سیگنال‌ها).
        /// </summary>
        public const string SignalHistoryCallback = "settings_signal_history";

        /// <summary>
        /// CallbackData (اختیاری) برای دکمه "View Public Signals" (اگر سیگنال‌های عمومی رایگان دارید).
        /// </summary>
        public const string PublicSignalsCallback = "settings_public_signals";

        /// <summary>
        /// CallbackData برای نمایش مجدد خود منوی تنظیمات (مثلاً برای بازگشت از یک زیرمنو به این منو).
        /// این می‌تواند توسط SettingsCallbackQueryHandler برای بازنمایش این منو استفاده شود.
        /// </summary>
        public const string ShowSettingsMenuCallback = "settings_show_main_menu";
        #endregion

        #region Constructor
        /// <summary>
        /// سازنده کلاس SettingsCommandHandler.
        /// وابستگی‌های لازم (سرویس لاگینگ و ارسال پیام) از طریق Dependency Injection تزریق می‌شوند.
        /// </summary>
        /// <param name="logger">سرویس لاگینگ.</param>
        /// <param name="messageSender">سرویس ارسال پیام تلگرام.</param>
        public SettingsCommandHandler(
            ILogger<SettingsCommandHandler> logger,
            ITelegramMessageSender messageSender)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
        }
        #endregion

        #region ITelegramCommandHandler Implementation

        /// <summary>
        /// بررسی می‌کند که آیا این Handler می‌تواند آپدیت (پیام) داده شده را پردازش کند یا خیر.
        /// این Handler فقط پیام‌هایی را که دقیقاً دستور "/settings" (بدون در نظر گرفتن حروف بزرگ و کوچک و با حذف فضاهای خالی اضافی) باشند، پردازش می‌کند.
        /// </summary>
        /// <param name="update">آپدیت دریافتی از تلگرام.</param>
        /// <returns>True اگر این Handler باید آپدیت را پردازش کند، در غیر این صورت false.</returns>
        public bool CanHandle(Update update)
        {
            // بررسی می‌کند که آپدیت از نوع پیام متنی باشد
            // و متن پیام (پس از حذف فضاهای خالی اضافی از ابتدا و انتها) برابر با "/settings" باشد.
            return update.Type == UpdateType.Message &&
                   update.Message?.Text?.Trim().Equals("/settings", StringComparison.OrdinalIgnoreCase) == true;
        }

        /// <summary>
        /// دستور "/settings" را پردازش می‌کند.
        /// یک پیام حاوی منوی اصلی تنظیمات با دکمه‌های Inline به کاربر ارسال می‌کند.
        /// </summary>
        /// <param name="update">آپدیت دریافتی از تلگرام (حاوی دستور /settings).</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات در صورت نیاز.</param>
        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            // استخراج پیام و اطلاعات کاربر از آپدیت
            var message = update.Message;
            // بررسی null بودن message و message.From برای جلوگیری از NullReferenceException و اطمینان از وجود اطلاعات لازم
            if (message?.From == null)
            {
                _logger.LogWarning("SettingsCommand: Message or From user is null in UpdateID {UpdateId}. Ignoring request.", update.Id);
                return; // اگر اطلاعات لازم وجود ندارد، از ادامه پردازش صرف نظر کن
            }

            var chatId = message.Chat.Id;          // شناسه چتی که باید پاسخ به آن ارسال شود
            var userId = message.From.Id;            // شناسه کاربر تلگرامی که دستور را ارسال کرده

            // لاگ کردن شروع پردازش دستور
            _logger.LogInformation("Handling /settings command for UserID {TelegramUserId} in ChatID {ChatId}.", userId, chatId);

            // فراخوانی متد کمکی برای دریافت متن و دکمه‌های منوی تنظیمات
            var (settingsMenuText, settingsKeyboard) = GetSettingsMenuMarkup();

            // ارسال پیام منوی تنظیمات به کاربر
            // ParseMode.MarkdownV2 برای فعال کردن فرمت‌بندی Markdown در متن پیام استفاده می‌شود.
            await _messageSender.SendTextMessageAsync(
                chatId: chatId,
                text: settingsMenuText,
                parseMode: ParseMode.MarkdownV2, //  اطمینان از اینکه TelegramMessageFormatter متن را برای این مود آماده می‌کند
                replyMarkup: settingsKeyboard,   //  ارسال دکمه‌های Inline ساخته شده
                cancellationToken: cancellationToken);

            _logger.LogInformation("Settings menu sent to UserID {TelegramUserId} in ChatID {ChatId}.", userId, chatId);
        }
        #endregion

        #region Static Menu Markup Generation
        /// <summary>
        /// یک متد استاتیک (یا می‌تواند در یک کلاس Builder جداگانه باشد) برای ایجاد متن و
        /// کیبورد Inline منوی اصلی تنظیمات.
        /// این کار از تکرار کد جلوگیری می‌کند اگر این منو از جاهای دیگر نیز فراخوانی شود
        /// (مثلاً از SettingsCallbackQueryHandler برای بازگشت به این منو).
        /// </summary>
        /// <returns>یک Tuple شامل متن منو (string) و آبجکت InlineKeyboardMarkup.</returns>
        public static (string text, InlineKeyboardMarkup keyboard) GetSettingsMenuMarkup()
        {
            // متن پیام برای منوی تنظیمات. از TelegramMessageFormatter برای فرمت‌بندی Bold استفاده شده.
            // escapePlainText: false چون متن "⚙️ User Settings" از قبل شامل کاراکترهای Markdown (ایموجی) است و نباید دوباره escape شود.
            var text = TelegramMessageFormatter.Bold("⚙️ User Settings", escapePlainText: false) + "\n\n" +
                       "Please choose an option below to configure your preferences or view information:";

            // ساخت دکمه‌های Inline برای منوی تنظیمات.
            // هر دکمه یک متن نمایشی و یک CallbackData دارد که هنگام کلیک ارسال می‌شود.
            var keyboard = MarkupBuilder.CreateInlineKeyboard(
      new[] // ردیف اول
      {
            InlineKeyboardButton.WithCallbackData("📊 My Signal Preferences", PrefsSignalCategoriesCallback),
            InlineKeyboardButton.WithCallbackData("🔔 Notification Settings", PrefsNotificationsCallback)
      },
      new[] // ردیف دوم
      {
            InlineKeyboardButton.WithCallbackData("⭐ My Subscription & Billing", MySubscriptionInfoCallback)
      },
      new[] // ردیف سوم
      {
            InlineKeyboardButton.WithCallbackData("⬅️ Back to Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral)
      }
  );

            return (text, keyboard);
        }
        #endregion

    }
}