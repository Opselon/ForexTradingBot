// File: Domain\Features\Forwarding\ValueObjects\MessageEditOptions.cs
// این فایل نیاز به using برای TextReplacement ندارد زیرا TextReplacement
// در همین namespace Domain.Features.Forwarding.ValueObjects تعریف شده است.
namespace Domain.Features.Forwarding.ValueObjects
{
    /// <summary>
    /// تنظیمات و گزینه‌های قابل اعمال بر ویرایش پیام‌ها در هنگام فوروارد یا پردازش متن.
    /// این کلاس به عنوان Value Object استفاده می‌شود و تغییرناپذیر (immutable) طراحی شده است.
    /// </summary>
    public class MessageEditOptions
    {
        /// <summary>
        /// متن دلخواهی که باید به ابتدای پیام افزوده شود.
        /// </summary>
        public string? PrependText { get; private set; }

        /// <summary>
        /// متن دلخواهی که باید به انتهای پیام افزوده شود.
        /// </summary>
        public string? AppendText { get; private set; }

        /// <summary>
        /// لیستی از قوانین جایگزینی متن برای اصلاح یا فیلتر کردن پیام.
        /// </summary>
        public IReadOnlyList<TextReplacement>? TextReplacements { get; private set; }

        /// <summary>
        /// حذف سربرگ مربوط به منبع پیام فوروارد شده (Forward Header).
        /// اگر true باشد، "Forwarded from..." حذف می‌شود.
        /// </summary>
        public bool RemoveSourceForwardHeader { get; private set; }

        /// <summary>
        /// حذف لینک‌ها (URLs) از متن پیام.
        /// </summary>
        public bool RemoveLinks { get; private set; }

        /// <summary>
        /// حذف تمام فرمت‌بندی‌ها از پیام (مثل بولد، ایتالیک، لینک‌ها و ...).
        /// اگر true باشد، پیام به صورت plain text فوروارد می‌شود.
        /// </summary>
        public bool StripFormatting { get; private set; }

        /// <summary>
        /// افزودن پاورقی دلخواه در انتهای پیام.
        /// این متن پس از `AppendText` اضافه می‌شود.
        /// </summary>
        public string? CustomFooter { get; private set; }

        /// <summary>
        /// حذف نام نویسنده یا ارسال‌کننده اصلی از پیام.
        /// این مورد معمولاً در حالت کپی کردن پیام (CopyInsteadOfForward) کاربرد دارد.
        /// </summary>
        public bool DropAuthor { get; private set; }

        /// <summary>
        /// حذف کپشن‌های مربوط به مدیا (عکس، ویدیو و ...).
        /// اگر true باشد، رسانه‌ها بدون متن توضیح فوروارد می‌شوند.
        /// </summary>
        public bool DropMediaCaptions { get; private set; }

        /// <summary>
        /// جلوگیری از فوروارد شدن پیام توسط کاربران بعدی.
        /// این یک ویژگی خاص در پلتفرم‌هایی مانند تلگرام است که تگ "Forward" را غیرفعال می‌کند.
        /// </summary>
        public bool NoForwards { get; private set; }

        /// <summary>
        /// سازنده‌ی پیش‌فرض مورد نیاز برای ORM (مانند EF Core) یا عملیات‌های deserialization.
        /// این سازنده خصوصی است تا از ایجاد نمونه‌های ناقص جلوگیری شود.
        /// </summary>
        private MessageEditOptions() { }

        /// <summary>
        /// سازنده‌ی کامل برای تنظیم تمام گزینه‌های ویرایش پیام.
        /// </summary>
        /// <param name="prependText">متن افزوده‌شده به ابتدای پیام. می‌تواند null باشد.</param>
        /// <param name="appendText">متن افزوده‌شده به انتهای پیام. می‌تواند null باشد.</param>
        /// <param name="textReplacements">لیستی از قوانین جایگزینی متنی. اگر null باشد، لیست خالی در نظر گرفته می‌شود.</param>
        /// <param name="removeSourceForwardHeader">اگر true باشد، سربرگ فوروارد حذف می‌شود.</param>
        /// <param name="removeLinks">اگر true باشد، لینک‌ها از متن حذف می‌شوند.</param>
        /// <param name="stripFormatting">اگر true باشد، تمام فرمت‌بندی‌ها حذف می‌شوند.</param>
        /// <param name="customFooter">پاورقی سفارشی. می‌تواند null باشد.</param>
        /// <param name="dropAuthor">اگر true باشد، نام نویسنده حذف می‌شود.</param>
        /// <param name="dropMediaCaptions">اگر true باشد، کپشن مدیا حذف می‌شود.</param>
        /// <param name="noForwards">اگر true باشد، فوروارد بعدی پیام توسط دیگران غیرفعال می‌شود.</param>
        public MessageEditOptions(
            string? prependText,
            string? appendText,
            IReadOnlyList<TextReplacement>? textReplacements,
            bool removeSourceForwardHeader,
            bool removeLinks,
            bool stripFormatting,
            string? customFooter,
            bool dropAuthor,
            bool dropMediaCaptions,
            bool noForwards)
        {
            PrependText = prependText;
            AppendText = appendText;
            // اطمینان حاصل می‌شود که لیست جایگزینی متن هرگز null نباشد.
            TextReplacements = textReplacements ?? new List<TextReplacement>();
            RemoveSourceForwardHeader = removeSourceForwardHeader;
            RemoveLinks = removeLinks;
            StripFormatting = stripFormatting;
            CustomFooter = customFooter;
            DropAuthor = dropAuthor;
            DropMediaCaptions = dropMediaCaptions;
            NoForwards = noForwards;
        }

        /// <summary>
        /// متد ساخت یک نمونه‌ی جدید از <see cref="MessageEditOptions"/> با اعمال تغییرات دلخواه بر فیلدهای خاص،
        /// بدون تغییر سایر مقادیر موجود. این الگو به عنوان "Builder-Like" برای حفظ تغییرناپذیری (immutability) استفاده می‌شود.
        /// </summary>
        /// <param name="prependText">مقدار جدید برای PrependText. اگر null باشد، مقدار فعلی حفظ می‌شود.</param>
        /// <param name="appendText">مقدار جدید برای AppendText. اگر null باشد، مقدار فعلی حفظ می‌شود.</param>
        /// <param name="textReplacements">مقدار جدید برای TextReplacements. اگر null باشد، مقدار فعلی حفظ می‌شود.</param>
        /// <param name="removeSourceForwardHeader">مقدار جدید برای RemoveSourceForwardHeader. اگر null باشد، مقدار فعلی حفظ می‌شود.</param>
        /// <param name="removeLinks">مقدار جدید برای RemoveLinks. اگر null باشد، مقدار فعلی حفظ می‌شود.</param>
        /// <param name="stripFormatting">مقدار جدید برای StripFormatting. اگر null باشد، مقدار فعلی حفظ می‌شود.</param>
        /// <param name="customFooter">مقدار جدید برای CustomFooter. اگر null باشد، مقدار فعلی حفظ می‌شود.</param>
        /// <param name="dropAuthor">مقدار جدید برای DropAuthor. اگر null باشد، مقدار فعلی حفظ می‌شود.</param>
        /// <param name="dropMediaCaptions">مقدار جدید برای DropMediaCaptions. اگر null باشد، مقدار فعلی حفظ می‌شود.</param>
        /// <param name="noForwards">مقدار جدید برای NoForwards. اگر null باشد، مقدار فعلی حفظ می‌شود.</param>
        /// <returns>یک نمونه جدید از <see cref="MessageEditOptions"/> با تغییرات اعمال شده.</returns>
        public MessageEditOptions With(
            string? prependText = null,
            string? appendText = null,
            IReadOnlyList<TextReplacement>? textReplacements = null,
            bool? removeSourceForwardHeader = null,
            bool? removeLinks = null,
            bool? stripFormatting = null,
            string? customFooter = null,
            bool? dropAuthor = null,
            bool? dropMediaCaptions = null,
            bool? noForwards = null)
        {
            return new MessageEditOptions(
                prependText ?? this.PrependText,
                appendText ?? this.AppendText,
                textReplacements ?? this.TextReplacements,
                removeSourceForwardHeader ?? this.RemoveSourceForwardHeader,
                removeLinks ?? this.RemoveLinks,
                stripFormatting ?? this.StripFormatting,
                customFooter ?? this.CustomFooter,
                dropAuthor ?? this.DropAuthor,
                dropMediaCaptions ?? this.DropMediaCaptions,
                noForwards ?? this.NoForwards
            );
        }
    }
}