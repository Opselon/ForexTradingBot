using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Domain.Enums; // برای دسترسی به SignalType

namespace Domain.Entities
{
    /// <summary>
    /// موجودیتی برای نمایش یک سیگنال معاملاتی.
    /// هر سیگنال شامل اطلاعاتی مانند نوع (خرید/فروش)، نماد معاملاتی، قیمت ورود، حد ضرر، حد سود،
    /// منبع سیگنال، دسته بندی و تحلیل‌های مرتبط با آن است.
    /// این موجودیت هسته اصلی سیستم سیگنال‌دهی ربات است.
    /// </summary>
    public class Signal
    {
        /// <summary>
        /// شناسه یکتای سیگنال.
        /// به عنوان کلید اصلی (Primary Key) در پایگاه داده استفاده می‌شود.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// نوع سیگنال، مشخص می‌کند که سیگنال برای خرید (Buy) است یا فروش (Sell).
        /// از <see cref="Enums.SignalType"/> برای تعیین نوع استفاده می‌شود.
        /// نیازمندی: این فیلد اجباری است.
        /// </summary>
        [Required(ErrorMessage = "نوع سیگنال (خرید/فروش) الزامی است.")]
        public SignalType Type { get; set; }

        /// <summary>
        /// نماد معاملاتی که سیگنال برای آن صادر شده است (مثلاً "EURUSD", "BTCUSDT", "XAUUSD").
        /// نیازمندی: این فیلد اجباری است و باید یک فرمت استاندارد داشته باشد.
        /// </summary>
        [Required(ErrorMessage = "نماد معاملاتی الزامی است.")]
        [MaxLength(50, ErrorMessage = "طول نماد معاملاتی نمی‌تواند بیش از 50 کاراکتر باشد.")]
        //  ممکن است نیاز به اعتبارسنجی فرمت خاصی برای نمادها باشد.
        public string Symbol { get; set; } = null!;

        /// <summary>
        /// قیمت پیشنهادی برای ورود به معامله.
        /// نیازمندی: این فیلد اجباری است و باید مقدار مثبتی داشته باشد (بسته به نوع دارایی).
        /// </summary>
        [Required(ErrorMessage = "قیمت ورود الزامی است.")]
        [Range(0.00000001, double.MaxValue, ErrorMessage = "قیمت ورود باید مقدار مثبتی داشته باشد.")]
        public decimal EntryPrice { get; set; }

        /// <summary>
        /// قیمت تعیین شده برای حد ضرر (Stop Loss).
        /// در صورت رسیدن قیمت بازار به این سطح، معامله باید برای جلوگیری از ضرر بیشتر بسته شود.
        /// نیازمندی: این فیلد اجباری است.
        /// </summary>
        [Required(ErrorMessage = "حد ضرر (StopLoss) الزامی است.")]
        //  اعتبارسنجی بیشتر: برای سیگنال خرید، StopLoss باید کمتر از EntryPrice باشد و برای سیگنال فروش، بیشتر.
        //  این منطق می‌تواند در سرویس‌ها یا در یک متد اعتبارسنجی در موجودیت پیاده‌سازی شود.
        public decimal StopLoss { get; set; }

        /// <summary>
        /// قیمت تعیین شده برای حد سود (Take Profit).
        /// در صورت رسیدن قیمت بازار به این سطح، معامله باید برای کسب سود بسته شود.
        /// نیازمندی: این فیلد اجباری است.
        /// </summary>
        [Required(ErrorMessage = "حد سود (TakeProfit) الزامی است.")]
        //  اعتبارسنجی بیشتر: برای سیگنال خرید، TakeProfit باید بیشتر از EntryPrice باشد و برای سیگنال فروش، کمتر.
        public decimal TakeProfit { get; set; }

        /// <summary>
        /// منبعی که سیگنال از آنجا دریافت یا تولید شده است (مثلاً "RSS Feed X", "Manual Analyst Y", "ML Algorithm Z").
        /// نیازمندی: این فیلد اجباری است.
        /// پیشنهاد: برای یکپارچگی و کنترل بهتر، استفاده از یک enum (مانند SignalSourceType) یا یک موجودیت مجزا (SourceProvider) توصیه می‌شود.
        /// </summary>
        [Required(ErrorMessage = "منبع سیگنال الزامی است.")]
        [MaxLength(100, ErrorMessage = "طول نام منبع نمی‌تواند بیش از 100 کاراکتر باشد.")]
        public string Source { get; set; } = null!;

        /// <summary>
        /// شناسه دسته‌بندی که این سیگنال به آن تعلق دارد.
        /// این یک کلید خارجی است که به شناسه دسته‌بندی سیگنال (SignalCategory.Id) اشاره دارد.
        /// </summary>
        // [ForeignKey(nameof(Category))] // می‌تواند برای وضوح بیشتر یا پیکربندی دقیق‌تر EF Core استفاده شود.
        public Guid CategoryId { get; set; }

        /// <summary>
        /// نویگیشن به موجودیت دسته‌بندی سیگنال (SignalCategory) که این سیگنال به آن مرتبط است.
        /// </summary>
        [Required]
        public SignalCategory Category { get; set; } = null!;

        /// <summary>
        /// تاریخ و زمان ایجاد این سیگنال به وقت جهانی (UTC).
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// مجموعه‌ای از تحلیل‌های انجام شده برای این سیگنال.
        /// این یک خصوصیت ناوبری برای رابطه یک-به-چند بین Signal و SignalAnalysis است.
        /// (هر سیگنال می‌تواند چندین تحلیل داشته باشد).
        /// </summary>
        public ICollection<SignalAnalysis> Analyses { get; set; } = new List<SignalAnalysis>();

        // ملاحظات و نیازمندی‌های اضافی بالقوه:
        // 1. Status (enum SignalStatus): وضعیت فعلی سیگنال (مثلاً: Pending, Active, ReachedTP, HitSL, Cancelled, Expired).
        //    public SignalStatus Status { get; set; } = SignalStatus.Pending;
        //
        // 2. Timeframe (string یا enum): تایم‌فریمی که سیگنال برای آن معتبر است (مثلاً "M15", "H1", "D1").
        //    public string? Timeframe { get; set; }
        //
        // 3. RiskRewardRatio (decimal, ReadOnly Property): محاسبه نسبت ریسک به ریوارد.
        //    public decimal RiskRewardRatio => (TakeProfit - EntryPrice) / (EntryPrice - StopLoss); (برای سیگنال خرید، برای فروش معکوس است و باید مدیریت شود)
        //
        // 4. Multiple TakeProfit Levels: اگر سیستم نیاز به چندین حد سود دارد.
        //    public decimal? TakeProfit2 { get; set; }
        //    public decimal? TakeProfit3 { get; set; }
        //
        // 5. ValidityPeriod (TimeSpan یا DateTime ExpireAt): مدت زمان اعتبار سیگنال یا تاریخ انقضا.
        //
        // 6. PipsToStopLoss / PipsToTakeProfit (int?): مقدار حد ضرر/سود به پیپ (اگر قابل محاسبه باشد).
        //
        // 7. SignalProviderId (Guid?): اگر سیگنال‌ها از ارائه‌دهندگان خاصی می‌آیند که به عنوان موجودیت مدیریت می‌شوند.

        // سازنده پیش‌فرض برای EF Core
        public Signal() { }

        // سازنده برای ایجاد یک سیگنال جدید (مثال ساده)
        // public Signal(SignalType type, string symbol, decimal entryPrice, decimal stopLoss, decimal takeProfit, string source, Guid categoryId)
        // {
        //     //  اعتبارسنجی‌های اولیه در اینجا انجام شود
        //     Id = Guid.NewGuid();
        //     Type = type;
        //     Symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
        //     EntryPrice = entryPrice;
        //     StopLoss = stopLoss;
        //     TakeProfit = takeProfit;
        //     Source = source ?? throw new ArgumentNullException(nameof(source));
        //     CategoryId = categoryId;
        //     CreatedAt = DateTime.UtcNow;
        //     Analyses = new List<SignalAnalysis>();
        //
        //     //  اعتبارسنجی‌های پیچیده‌تر مانند رابطه قیمت‌ها
        //     ValidatePrices();
        // }

        // private void ValidatePrices()
        // {
        //     if (Type == SignalType.Buy)
        //     {
        //         if (StopLoss >= EntryPrice) throw new ArgumentException("برای سیگنال خرید، حد ضرر باید کمتر از قیمت ورود باشد.");
        //         if (TakeProfit <= EntryPrice) throw new ArgumentException("برای سیگنال خرید، حد سود باید بیشتر از قیمت ورود باشد.");
        //     }
        //     else // Sell
        //     {
        //         if (StopLoss <= EntryPrice) throw new ArgumentException("برای سیگنال فروش، حد ضرر باید بیشتر از قیمت ورود باشد.");
        //         if (TakeProfit >= EntryPrice) throw new ArgumentException("برای سیگنال فروش، حد سود باید کمتر از قیمت ورود باشد.");
        //     }
        // }
    }
}