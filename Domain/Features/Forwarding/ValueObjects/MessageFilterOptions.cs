// فایل: Domain\Features\Forwarding\ValueObjects\MessageFilterOptions.cs
using System.Text.RegularExpressions;

namespace Domain.Features.Forwarding.ValueObjects
{
    /// <summary>
    /// مجموعه‌ای از گزینه‌ها را برای فیلتر کردن پیام‌ها بر اساس معیارهای مختلف مانند نوع، محتوا، فرستنده و طول نشان می‌دهد.
    /// این یک شیء مقداری (Value Object) است.
    /// </summary>
    public class MessageFilterOptions
    {
        /// <summary>
        /// لیست انواع پیام‌های مجاز را دریافت می‌کند.
        /// اگر لیست خالی باشد، فیلترینگ خاصی بر اساس نوع پیام اعمال نمی‌شود.
        /// مثال‌ها: "text", "photo", "video", "document".
        /// </summary>
        public IReadOnlyList<string>? AllowedMessageTypes { get; private set; }

        /// <summary>
        /// لیست انواع MIME مجاز را برای پیام‌های مبتنی بر فایل (مانند اسناد، عکس‌ها، ویدیوها) دریافت می‌کند.
        /// اگر لیست خالی باشد، فیلترینگ خاصی بر اساس نوع MIME اعمال نمی‌شود.
        /// مثال‌ها: "image/jpeg", "application/pdf".
        /// </summary>
        public IReadOnlyList<string>? AllowedMimeTypes { get; private set; }

        /// <summary>
        /// رشته متنی را که باید در محتوای پیام جستجو شود، دریافت می‌کند.
        /// اگر null باشد، فیلترینگ محتوای متنی اعمال نمی‌شود.
        /// </summary>
        public string? ContainsText { get; private set; }

        /// <summary>
        /// مقداری را دریافت می‌کند که نشان می‌دهد آیا <see cref="ContainsText"/> باید به عنوان یک عبارت منظم (Regular Expression) در نظر گرفته شود یا خیر.
        /// اگر false باشد، <see cref="ContainsText"/> به عنوان یک زیررشته تحت‌اللفظی (Literal Substring) در نظر گرفته می‌شود.
        /// </summary>
        public bool ContainsTextIsRegex { get; private set; }

        /// <summary>
        /// گزینه‌های عبارت منظم را که در صورت true بودن <see cref="ContainsTextIsRegex"/> باید اعمال شوند، دریافت می‌کند.
        /// </summary>
        public RegexOptions ContainsTextRegexOptions { get; private set; }

        /// <summary>
        /// لیست شناسه‌های کاربری فرستندگان مجاز را دریافت می‌کند.
        /// اگر لیست خالی باشد، فیلترینگ خاصی بر اساس مجاز بودن شناسه فرستنده اعمال نمی‌شود.
        /// </summary>
        public IReadOnlyList<long>? AllowedSenderUserIds { get; private set; }

        /// <summary>
        /// لیست شناسه‌های کاربری فرستندگان مسدود شده را دریافت می‌کند.
        /// اگر لیست خالی باشد، فیلترینگ خاصی بر اساس مسدود بودن شناسه فرستنده اعمال نمی‌شود.
        /// </summary>
        public IReadOnlyList<long>? BlockedSenderUserIds { get; private set; }

        /// <summary>
        /// مقداری را دریافت می‌کند که نشان می‌دهد آیا پیام‌های ویرایش شده باید نادیده گرفته شوند (با فیلتر مطابقت نداشته باشند).
        /// </summary>
        public bool IgnoreEditedMessages { get; private set; }

        /// <summary>
        /// مقداری را دریافت می‌کند که نشان می‌دهد آیا پیام‌های سرویس (مانند پیوستن/ترک کاربر، اعلان‌های پیام پین شده) باید نادیده گرفته شوند (با فیلتر مطابقت نداشته باشند).
        /// </summary>
        public bool IgnoreServiceMessages { get; private set; }

        /// <summary>
        /// حداقل طول مجاز متن پیام را دریافت می‌کند.
        /// Null نشان‌دهنده عدم وجود محدودیت حداقل طول است.
        /// </summary>
        public int? MinMessageLength { get; private set; }

        /// <summary>
        /// حداکثر طول مجاز متن پیام را دریافت می‌کند.
        /// Null نشان‌دهنده عدم وجود محدودیت حداکثر طول است.
        /// </summary>
        public int? MaxMessageLength { get; private set; }

        /// <summary>
        /// یک نمونه جدید از کلاس <see cref="MessageFilterOptions"/> را مقداردهی اولیه می‌کند.
        /// این سازنده خصوصی عمدتاً برای فرایند مادی‌سازی ORM (مانند EF Core) است.
        /// </summary>
        private MessageFilterOptions() { } // برای EF Core

        /// <summary>
        /// یک نمونه جدید از کلاس <see cref="MessageFilterOptions"/> را با معیارهای فیلترینگ مشخص شده، مقداردهی اولیه می‌کند.
        /// </summary>
        /// <param name="allowedMessageTypes">لیستی از انواع پیام‌های مجاز. اگر null یا خالی باشد، همه انواع به طور بالقوه مجاز هستند (با توجه به فیلترهای دیگر).</param>
        /// <param name="allowedMimeTypes">لیستی از انواع MIME مجاز برای پیام‌های مبتنی بر فایل. اگر null یا خالی باشد، همه انواع MIME به طور بالقوه مجاز هستند.</param>
        /// <param name="containsText">رشته متنی یا الگوی عبارت منظم برای جستجو در محتوای پیام.</param>
        /// <param name="containsTextIsRegex">مقداری که نشان می‌دهد آیا <paramref name="containsText"/> باید به عنوان یک عبارت منظم در نظر گرفته شود یا خیر.</param>
        /// <param name="containsTextRegexOptions">گزینه‌های عبارت منظم برای اعمال در صورت true بودن <paramref name="containsTextIsRegex"/>.</param>
        /// <param name="allowedSenderUserIds">لیستی از شناسه‌های کاربری فرستندگان مجاز. اگر null یا خالی باشد، هیچ مجوز شناسه فرستنده خاصی اعمال نمی‌شود.</param>
        /// <param name="blockedSenderUserIds">لیستی از شناسه‌های کاربری فرستندگان مسدود شده. اگر null یا خالی باشد، هیچ مسدودسازی شناسه فرستنده خاصی اعمال نمی‌شود.</param>
        /// <param name="ignoreEditedMessages">اگر true باشد، پیام‌هایی که ویرایش شده‌اند نادیده گرفته می‌شوند.</param>
        /// <param name="ignoreServiceMessages">اگر true باشد، پیام‌های سرویس (مانند پیوستن کاربر) نادیده گرفته می‌شوند.</param>
        /// <param name="minMessageLength">حداقل طول مجاز برای متن پیام. Null برای عدم وجود حداقل.</param>
        /// <param name="maxMessageLength">حداکثر طول مجاز برای متن پیام. Null برای عدم وجود حداکثر.</param>
        public MessageFilterOptions(
            IReadOnlyList<string>? allowedMessageTypes,
            IReadOnlyList<string>? allowedMimeTypes,
            string? containsText,
            bool containsTextIsRegex,
            RegexOptions containsTextRegexOptions,
            IReadOnlyList<long>? allowedSenderUserIds,
            IReadOnlyList<long>? blockedSenderUserIds,
            bool ignoreEditedMessages,
            bool ignoreServiceMessages,
            int? minMessageLength,
            int? maxMessageLength)
        {
            AllowedMessageTypes = allowedMessageTypes ?? new List<string>();
            AllowedMimeTypes = allowedMimeTypes ?? new List<string>();
            ContainsText = containsText;
            ContainsTextIsRegex = containsTextIsRegex;
            ContainsTextRegexOptions = containsTextRegexOptions;
            AllowedSenderUserIds = allowedSenderUserIds ?? new List<long>();
            BlockedSenderUserIds = blockedSenderUserIds ?? new List<long>();
            IgnoreEditedMessages = ignoreEditedMessages;
            IgnoreServiceMessages = ignoreServiceMessages;
            MinMessageLength = minMessageLength;
            MaxMessageLength = maxMessageLength;
        }
    }
}