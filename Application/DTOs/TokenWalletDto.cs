namespace Application.DTOs
{
    /// <summary>
    /// Data Transfer Object representing a token wallet with balance and update information
    /// </summary>
    public class TokenWalletDto
    {
        #region Properties

        public Guid Id { get; set; } // <-- ADD THIS PROPERTY
        public Guid UserId { get; set; }
        public decimal Balance { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        #endregion
    }
}