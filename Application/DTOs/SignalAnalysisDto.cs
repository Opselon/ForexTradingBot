namespace Application.DTOs
{
    /// <summary>
    /// Data Transfer Object (DTO) representing the analysis of a trading signal.
    /// This class is used to transfer signal analysis data between layers of the application.
    /// </summary>
    public class SignalAnalysisDto
    {
        #region Properties

        /// <summary>
        /// Gets or sets the unique identifier for the signal analysis.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Gets or sets the unique identifier of the associated trading signal.
        /// </summary>
        public Guid SignalId { get; set; }

        /// <summary>
        /// Gets or sets the name of the analyst who performed the analysis.
        /// </summary>
        public string AnalystName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the analysis notes or comments.
        /// </summary>
        public string Notes { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the date and time when the analysis was created.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        #endregion
    }
}