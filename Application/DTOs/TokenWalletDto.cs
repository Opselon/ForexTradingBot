namespace Application.DTOs
{
    /// <summary>
    /// Data Transfer Object representing a token wallet with balance and update information
    /// </summary>
    public class TokenWalletDto
    {
        #region Properties

        /// <summary>
        /// Gets or sets the current balance of the token wallet
        /// </summary>
        public decimal Balance { get; set; }

        /// <summary>
        /// Gets or sets the last update timestamp of the wallet
        /// </summary>
        public DateTime UpdatedAt { get; set; }

        #endregion
    }
}