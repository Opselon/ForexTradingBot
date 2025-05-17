using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Domain.Entities
{
    /// <summary>
    /// موجودیتی برای نگهداری نتایج تحلیل انجام شده بر روی یک سیگنال معاملاتی.
    /// این تحلیل می‌تواند توسط یک تحلیلگر انسانی یا یک سیستم خودکار (مانند مدل یادگیری ماشین) انجام شده باشد.
    /// هر تحلیل به یک سیگنال خاص مرتبط است و شامل جزئیات و یادداشت‌های تحلیلگر می‌باشد.
    /// </summary>
    public class SignalAnalysis
    {
        /// <summary>
        /// شناسه یکتای تحلیل سیگنال.
        /// به عنوان کلید اصلی (Primary Key) در پایگاه داده استفاده می‌شود.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// شناسه سیگنالی که این تحلیل برای آن انجام شده است.
        /// این یک کلید خارجی است که به شناسه سیگنال (Signal.Id) اشاره دارد.
        /// </summary>
        // [ForeignKey(nameof(Signal))] // می‌تواند برای وضوح بیشتر یا پیکربندی دقیق‌تر EF Core استفاده شود.
        public Guid SignalId { get; set; }

        /// <summary>
        /// نویگیشن به موجودیت سیگنال (Signal) که این تحلیل به آن تعلق دارد.
        /// این خصوصیت توسط Entity Framework Core برای بارگذاری موجودیت مرتبط سیگنال استفاده می‌شود.
        /// انتظار می‌رود `null!` نباشد زیرا هر تحلیل باید به یک سیگنال معتبر مرتبط باشد.
        /// </summary>
        [Required]
        public Signal Signal { get; set; } = null!;

        /// <summary>
        /// نام تحلیلگر یا منبعی که این تحلیل را ارائه داده است.
        /// می‌تواند نام یک شخص، نام یک الگوریتم (مثلاً "ML Sentiment Analyzer v1.2") یا یک سرویس باشد.
        /// نیازمندی: این فیلد اجباری است.
        /// </summary>
        [Required(ErrorMessage = "نام تحلیلگر یا منبع تحلیل الزامی است.")]
        [MaxLength(150, ErrorMessage = "طول نام تحلیلگر/منبع نمی‌تواند بیش از 150 کاراکتر باشد.")]
        public string AnalystName { get; set; } = null!;

        /// <summary>
        /// یادداشت‌ها، توضیحات یا جزئیات مربوط به تحلیل انجام شده.
        /// می‌تواند شامل دلایل تحلیل، مشاهدات کلیدی یا خلاصه‌ای از یافته‌ها باشد.
        /// نیازمندی: این فیلد اجباری است (اگرچه می‌توان آن را اختیاری `string?` در نظر گرفت اگر تحلیل‌ها می‌توانند بدون یادداشت باشند).
        /// </summary>
        [Required(ErrorMessage = "یادداشت‌های تحلیل الزامی است.")]
        [MaxLength(2000, ErrorMessage = "طول یادداشت‌های تحلیل نمی‌تواند بیش از 2000 کاراکتر باشد.")]
        public string Notes { get; set; } = null!;

        /// <summary>
        /// تاریخ و زمان ایجاد این رکورد تحلیل به وقت جهانی (UTC).
        /// برای اهداف ممیزی و پیگیری زمان انجام تحلیل استفاده می‌شود.
        /// به صورت پیش‌فرض با زمان جاری UTC در لحظه ایجاد نمونه از کلاس مقداردهی می‌شود.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // ملاحظات و نیازمندی‌های اضافی بالقوه:
        // 1. AnalysisType (enum): برای مشخص کردن نوع تحلیل (مثلاً: Technical, Fundamental, Sentiment).
        //    public AnalysisType Type { get; set; }
        //
        // 2. SentimentScore (double? یا decimal?): اگر تحلیل شامل امتیاز احساسات است (مثلاً از -1.0 تا 1.0).
        //    public double? SentimentScore { get; set; }
        //
        // 3. PredictedOutcome (enum?): نتیجه پیش‌بینی شده تحلیل (مثلاً: Bullish, Bearish, Neutral).
        //    public SignalOutcome? Outcome { get; set; }
        //
        // 4. ConfidenceLevel (double?): سطح اطمینان تحلیلگر یا مدل به نتیجه تحلیل (مثلاً از 0.0 تا 1.0).
        //    public double? ConfidenceLevel { get; set; }
        //
        // 5. AnalystId (Guid?): اگر تحلیلگران کاربران سیستم هستند، شناسه کاربر تحلیلگر.
        //    public Guid? AnalystUserId { get; set; }
        //    public User? AnalystUser { get; set; }

        // سازنده پیش‌فرض برای EF Core
        public SignalAnalysis() { }

        // سازنده برای ایجاد یک تحلیل جدید
        // public SignalAnalysis(Guid signalId, string analystName, string notes)
        // {
        //     if (string.IsNullOrWhiteSpace(analystName))
        //         throw new ArgumentException("نام تحلیلگر نمی‌تواند خالی باشد.", nameof(analystName));
        //     if (string.IsNullOrWhiteSpace(notes))
        //         throw new ArgumentException("یادداشت‌های تحلیل نمی‌تواند خالی باشد.", nameof(notes));

        //     Id = Guid.NewGuid();
        //     SignalId = signalId;
        //     AnalystName = analystName;
        //     Notes = notes;
        //     CreatedAt = DateTime.UtcNow;
        //     // Signal باید توسط EF Core یا از طریق سرویس‌ها بارگذاری/پیوند داده شود.
        // }
    }
}