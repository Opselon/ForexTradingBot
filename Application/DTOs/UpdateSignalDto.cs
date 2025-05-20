using Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace Application.DTOs
{
    /// <summary>
    /// Data Transfer Object for updating trading signal information.
    /// Contains nullable properties to allow partial updates of signal data.
    /// </summary>
    public class UpdateSignalDto
    {
        #region Signal Properties

        /// <summary>
        /// The type of trading signal (e.g., Buy, Sell)
        /// </summary>
        public SignalType? Type { get; set; }

        /// <summary>
        /// The trading symbol (e.g., EURUSD, GBPJPY)
        /// </summary>
        [StringLength(50)]
        public string? Symbol { get; set; }

        #endregion

        #region Price Levels

        /// <summary>
        /// The entry price for the trade
        /// </summary>
        [Range(0.00000001, double.MaxValue)]
        public decimal? EntryPrice { get; set; }

        /// <summary>
        /// The stop loss price level
        /// </summary>
        public decimal? StopLoss { get; set; }

        /// <summary>
        /// The take profit price level
        /// </summary>
        public decimal? TakeProfit { get; set; }

        #endregion

        #region Additional Information

        /// <summary>
        /// The source of the trading signal
        /// </summary>
        [StringLength(100)]
        public string? Source { get; set; }

        /// <summary>
        /// The category identifier for the signal
        /// </summary>
        public Guid? CategoryId { get; set; }

        #endregion
    }
}