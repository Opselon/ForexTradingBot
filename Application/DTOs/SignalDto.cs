namespace Application.DTOs
{
    /// <summary>
    /// Data Transfer Object representing a trading signal with its associated properties and relationships.
    /// </summary>
    public class SignalDto
    {
        #region Basic Properties

        /// <summary>
        /// Unique identifier for the signal
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Type of the trading signal (e.g., Buy, Sell)
        /// </summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Trading symbol or pair (e.g., EURUSD, GBPJPY)
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        #endregion

        #region Price Levels

        /// <summary>
        /// Entry price for the trade
        /// </summary>
        public decimal EntryPrice { get; set; }

        /// <summary>
        /// Stop loss price level
        /// </summary>
        public decimal StopLoss { get; set; }

        /// <summary>
        /// Take profit price level
        /// </summary>
        public decimal TakeProfit { get; set; }

        #endregion

        #region Metadata

        /// <summary>
        /// Source of the trading signal
        /// </summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>
        /// Timestamp when the signal was created
        /// </summary>
        public DateTime CreatedAt { get; set; }

        #endregion

        #region Relationships

        /// <summary>
        /// Category information for the signal
        /// </summary>
        public SignalCategoryDto? Category { get; set; } // ✅ این پراپرتی باید وجود داشته باشد

        /// <summary>
        /// Collection of analyses associated with this signal
        /// </summary>
        public IEnumerable<SignalAnalysisDto>? Analyses { get; set; }

        #endregion
    }
}