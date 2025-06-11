// File: Domain/Entities/TokenWallet.cs
#region Usings
using System.ComponentModel.DataAnnotations;        // برای [Key], [Required]
using System.ComponentModel.DataAnnotations.Schema; // برای [Column], [ForeignKey]
#endregion

namespace Domain.Entities
{
    /// <summary>
    /// Represents a user's token wallet in the system.
    /// This entity stores the user's token balance and related metadata.
    /// Each user typically has one unique token wallet for each supported currency/token type.
    /// </summary>
    public class TokenWallet
    {
        #region Core Properties
        /// <summary>
        /// Unique identifier for the token wallet (Primary Key).
        /// </summary>
        [Key]
        public Guid Id { get; private set; }

        /// <summary>
        /// Foreign key referencing the User who owns this wallet.
        /// This establishes a one-to-one relationship with the User entity.
        /// </summary>
        [Required]
        public Guid UserId { get; private set; }

        /// <summary>
        /// Current balance of tokens in the user's wallet.
        /// This value is updated through various transactions (e.g., token purchases, service payments).
        /// It's crucial that updates to this field are handled atomically and with concurrency control.
        /// </summary>
        [Required]
        [Column(TypeName = "decimal(18, 8)")]
        public decimal Balance { get; set; }

        /// <summary>
        /// Indicates if the wallet is currently active and can be used for transactions.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Date and time when this token wallet was created (UTC).
        /// Typically set when the user is registered or when the wallet is first initialized.
        /// </summary>
        [Required]
        public DateTime CreatedAt { get; private set; }

        /// <summary>
        /// Date and time of the last update to the wallet's balance or status (UTC).
        /// Useful for auditing and tracking the last activity.
        /// </summary>
        [Required]
        public DateTime UpdatedAt { get; set; }
        #endregion

        #region Navigation Properties
        /// <summary>
        /// Navigation property to the User who owns this wallet.
        /// Configured via Fluent API or data annotations for the one-to-one relationship.
        /// </summary>
        [ForeignKey(nameof(UserId))]
        public virtual User User { get; private set; } = null!;
        #endregion

        #region Constructors
        /// <summary>
        /// Private constructor for EF Core and for use by the factory method.
        /// </summary>
        private TokenWallet()
        {
            Id = Guid.NewGuid();
            CreatedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Public constructor for Dapper mapping and reconstructing entities from the database.
        /// This allows setting properties with private setters during object creation.
        /// </summary>
        public TokenWallet(Guid id, Guid userId, decimal balance, bool isActive, DateTime createdAt, DateTime updatedAt)
        {
            Id = id;
            UserId = userId;
            Balance = balance;
            IsActive = isActive;
            CreatedAt = createdAt;
            UpdatedAt = updatedAt;
        }

        /// <summary>
        /// Creates a new token wallet for a specific user with an initial balance.
        /// </summary>
        /// <param name="userId">The ID of the user owning this wallet.</param>
        /// <param name="initialBalance">The initial balance for the wallet (defaults to 0).</param>
        /// <returns>A new instance of <see cref="TokenWallet"/>.</returns>
        public static TokenWallet Create(Guid userId, decimal initialBalance = 0m)
        {
            if (userId == Guid.Empty)
                throw new ArgumentException("User ID cannot be empty.", nameof(userId));
            if (initialBalance < 0)
                throw new ArgumentOutOfRangeException(nameof(initialBalance), "Initial balance cannot be negative.");

            var now = DateTime.UtcNow;
            // Use the new public constructor here for consistency
            return new TokenWallet(Guid.NewGuid(), userId, initialBalance, true, now, now);
        }
        #endregion

        #region Methods for Balance Management (Example - Business logic might be in a service)
        /// <summary>
        /// Increases the wallet balance by the specified amount.
        /// Note: Concurrency and transactional integrity should be handled by the calling service.
        /// </summary>
        /// <param name="amount">The positive amount to add.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if the amount is not positive.</exception>
        public void AddToBalance(decimal amount)
        {
            if (amount <= 0)
                throw new ArgumentOutOfRangeException(nameof(amount), "Amount to add must be positive.");

            Balance += amount;
            UpdatedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Decreases the wallet balance by the specified amount.
        /// Throws InvalidOperationException if insufficient funds.
        /// Note: Concurrency and transactional integrity should be handled by the calling service.
        /// </summary>
        /// <param name="amount">The positive amount to deduct.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if the amount is not positive.</exception>
        /// <exception cref="InvalidOperationException">Thrown if funds are insufficient.</exception>
        public void DeductFromBalance(decimal amount)
        {
            if (amount <= 0)
                throw new ArgumentOutOfRangeException(nameof(amount), "Amount to deduct must be positive.");
            if (Balance < amount)
                throw new InvalidOperationException("Insufficient funds in the wallet to perform this deduction.");

            Balance -= amount;
            UpdatedAt = DateTime.UtcNow;
        }
        #endregion
    }
}