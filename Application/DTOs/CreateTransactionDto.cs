using Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace Application.DTOs
{
    /// <summary>
    /// Data transfer object for creating a new transaction.
    /// </summary>
    public class CreateTransactionDto
    {
        #region Properties

        /// <summary>
        /// Gets or sets the unique identifier of the user initiating the transaction.
        /// </summary>
        [Required]
        public Guid UserId { get; set; }

        /// <summary>
        /// Gets or sets the amount of the transaction.
        /// </summary>
        [Required]
        public decimal Amount { get; set; }

        /// <summary>
        /// Gets or sets the type of the transaction (e.g., Deposit, Withdrawal).
        /// </summary>
        [Required]
        public TransactionType Type { get; set; }

        /// <summary>
        /// Gets or sets an optional description for the transaction.
        /// Maximum length is 500 characters.
        /// </summary>
        [StringLength(500)]
        public string? Description { get; set; }

        #endregion
    }
}