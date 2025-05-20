using Domain.Enums; // برای SignalType
using System.ComponentModel.DataAnnotations;

namespace Application.DTOs
{
    /// <summary>
    /// Data Transfer Object for creating a new trading signal.
    /// </summary>
    public class CreateSignalDto
    {
        #region Properties

        /// <summary>
        /// Gets or sets the type of the signal (e.g., Buy, Sell).
        /// </summary>
        [Required] // Ensures Type is provided
        public SignalType Type { get; set; }

        /// <summary>
        /// Gets or sets the trading symbol (e.g., EURUSD, BTCUSD).
        /// </summary>
        [Required] // Ensures Symbol is provided
        [StringLength(50)] // Limits the symbol length to 50 characters
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the entry price for the signal.
        /// </summary>
        [Required] // Ensures EntryPrice is provided
        [Range(0.00000001, double.MaxValue)] // Ensures EntryPrice is a positive value
        public decimal EntryPrice { get; set; }

        /// <summary>
        /// Gets or sets the stop-loss price for the signal.
        /// </summary>
        [Required] // Ensures StopLoss is provided
        public decimal StopLoss { get; set; }

        /// <summary>
        /// Gets or sets the take-profit price for the signal.
        /// </summary>
        [Required] // Ensures TakeProfit is provided
        public decimal TakeProfit { get; set; }

        /// <summary>
        /// Gets or sets the source or provider of the signal.
        /// </summary>
        [Required] // Ensures Source is provided
        [StringLength(100)] // Limits the source length to 100 characters
        public string Source { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the unique identifier of the category this signal belongs to.
        /// </summary>
        [Required] // Ensures CategoryId is provided
        public Guid CategoryId { get; set; }

        #endregion
    }
}