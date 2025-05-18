// File: Domain/Entities/SignalAnalysis.cs
#region Usings
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema; // برای ForeignKey
#endregion

namespace Domain.Entities
{
    public class SignalAnalysis
    {
        #region Core Properties
        public Guid Id { get; set; }

        [Required]
        public Guid SignalId { get; set; }

        /// <summary>
        /// Name of the analyst or the source of the analysis (e.g., "AI Sentiment Bot v2", "Senior Trader John").
        /// </summary>
        [Required]
        [MaxLength(150)]
        public string AnalystName { get; set; } = null!;

        /// <summary>
        /// Detailed text of the analysis, commentary, or notes.
        /// </summary>
        [Required]
        public string AnalysisText { get; set; } = null!; // ✅ تغییر نام از Notes

        /// <summary>
        /// (Optional) A numerical score representing the sentiment or confidence of the analysis (e.g., -1.0 to 1.0).
        /// </summary>
        public double? SentimentScore { get; set; } // ✅ اضافه شد

        /*
        /// <summary>
        /// (Optional) Type of analysis performed (e.g., "Technical", "Fundamental", "Sentiment").
        /// Could be an enum if predefined types are used.
        /// </summary>
        [MaxLength(50)]
        public string? AnalysisType { get; set; }
        */

        /// <summary>
        /// Date and time when this analysis was created (UTC).
        /// </summary>
        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        #endregion

        #region Navigation Properties
        /// <summary>
        /// Navigation property to the Signal this analysis pertains to.
        /// </summary>
        [ForeignKey(nameof(SignalId))] //  صریحاً کلید خارجی را مشخص می‌کند
        [Required]
        public virtual Signal Signal { get; set; } = null!; // ✅ virtual
        #endregion

        #region Constructors
        public SignalAnalysis()
        {
            Id = Guid.NewGuid();
            CreatedAt = DateTime.UtcNow;
        }
        #endregion
    }
}