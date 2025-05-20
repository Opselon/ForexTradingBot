namespace Application.DTOs
{
    /// <summary>
    /// Data Transfer Object representing a financial transaction
    /// </summary>
    public class TransactionDto
    {
        #region Properties
        /// <summary>
        /// Unique identifier for the transaction
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Identifier of the user associated with the transaction
        /// </summary>
        public Guid UserId { get; set; }

        /// <summary>
        /// The monetary amount of the transaction
        /// </summary>
        public decimal Amount { get; set; }

        /// <summary>
        /// Type of the transaction (e.g., Deposit, Withdrawal, Transfer)
        /// </summary>
        public string Type { get; set; } = string.Empty; // TransactionType به صورت رشته

        /// <summary>
        /// Optional description of the transaction
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Date and time when the transaction occurred
        /// </summary>
        public DateTime Timestamp { get; set; }
        #endregion
    }
}