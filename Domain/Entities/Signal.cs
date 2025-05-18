// File: Domain/Entities/Signal.cs
#region Usings
// using System.ComponentModel.DataAnnotations.Schema; // اگر از ForeignKey Attribute استفاده می‌کنید
using Domain.Enums; // ✅ برای SignalType و SignalStatus
using System.ComponentModel.DataAnnotations;
#endregion

namespace Domain.Entities
{
    public class Signal
    {
        #region Core Properties
        public Guid Id { get; set; }

        [Required]
        public SignalType Type { get; set; } // Buy or Sell

        [Required]
        [MaxLength(50)]
        public string Symbol { get; set; } = null!;

        [Required]
        public decimal EntryPrice { get; set; }

        [Required]
        public decimal StopLoss { get; set; }

        [Required]
        public decimal TakeProfit { get; set; }

        //  می‌توانید سطوح TP بیشتری اضافه کنید
        // public decimal? TakeProfit2 { get; set; }
        // public decimal? TakeProfit3 { get; set; }
        #endregion

        #region Additional Signal Details
        /// <summary>
        /// The source or provider of the signal (e.g., "RSS Feed X", "Analyst Y", "AI System Z").
        /// </summary>
        [Required]
        [MaxLength(100)]
        public string SourceProvider { get; set; } = null!; // ✅ تغییر نام از Source

        /// <summary>
        /// Current status of the signal (e.g., Pending, Active, ReachedTakeProfit, HitStopLoss).
        /// </summary>
        [Required]
        public SignalStatus Status { get; set; } = SignalStatus.Pending; // ✅ اضافه شد

        /// <summary>
        /// The timeframe for which this signal is most relevant (e.g., "M15", "H1", "D1"). Optional.
        /// </summary>
        [MaxLength(10)]
        public string? Timeframe { get; set; } // ✅ اضافه شد

        /// <summary>
        /// Additional notes, commentary, or reasoning behind the signal. Optional.
        /// </summary>
        [MaxLength(1000)]
        public string? Notes { get; set; } // ✅ اضافه شد

        /// <summary>
        /// Indicates if this signal is exclusive to VIP/subscribed users.
        /// </summary>
        public bool IsVipOnly { get; set; } = false; // ✅ اضافه شد
        #endregion

        #region Timestamps
        /// <summary>
        /// Date and time when the signal was initially created or published by the source (UTC).
        /// </summary>
        [Required]
        public DateTime PublishedAt { get; set; } = DateTime.UtcNow; // ✅ تغییر نام از CreatedAt

        /// <summary>
        /// Date and time of the last update to this signal's information (UTC). Nullable.
        /// </summary>
        public DateTime? UpdatedAt { get; set; } // ✅ اضافه شد

        /// <summary>
        /// Date and time when the signal was closed (reached TP/SL, cancelled, or expired) (UTC). Nullable.
        /// </summary>
        public DateTime? ClosedAt { get; set; } // ✅ اضافه شد
        #endregion

        #region Navigation Properties
        /// <summary>
        /// Foreign key to the SignalCategory.
        /// </summary>
        [Required]
        public Guid CategoryId { get; set; }

        /// <summary>
        /// Navigation property to the SignalCategory this signal belongs to.
        /// </summary>
        [Required]
        public virtual SignalCategory Category { get; set; } = null!; // ✅ virtual برای Lazy Loading

        /// <summary>
        /// Collection of analyses performed on this signal.
        /// </summary>
        public virtual ICollection<SignalAnalysis> Analyses { get; set; } = new List<SignalAnalysis>(); // ✅ virtual
        #endregion

        #region Constructors
        public Signal()
        {
            Id = Guid.NewGuid();
            PublishedAt = DateTime.UtcNow; //  مقداردهی اولیه
            Status = SignalStatus.Pending;
            Analyses = new List<SignalAnalysis>();
        }
        #endregion
    }
}