using Domain.Enums;

namespace Application.DTOs
{
    /// <summary>
    /// Data Transfer Object for user information.
    /// </summary>
    public class UserDto
    {
        #region Properties

        /// <summary>
        /// Gets or sets the unique identifier of the user.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Gets or sets the username.
        /// </summary>
        public string Username { get; set; } = null!;

        /// <summary>
        /// Gets or sets the user's Telegram ID.
        /// </summary>
        public string TelegramId { get; set; } = null!;

        /// <summary>
        /// Gets or sets the user's email address.
        /// </summary>
        public string Email { get; set; } = null!;

        /// <summary>
        /// Gets or sets the user's level (e.g., Free, Premium).
        /// </summary>
        public UserLevel Level { get; set; } = UserLevel.Free;

        /// <summary>
        /// Gets or sets the user's token balance (obtained from TokenWallet).
        /// </summary>
        public decimal TokenBalance { get; set; } // Sourced from TokenWallet

        /// <summary>
        /// Gets or sets the date and time when the user was created.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Gets or sets the user's token wallet details.
        /// </summary>
        public TokenWalletDto? TokenWallet { get; set; }

        /// <summary>
        /// Gets or sets the user's active subscription details.
        /// </summary>
        public SubscriptionDto? ActiveSubscription { get; set; }

        #endregion
    }
}
