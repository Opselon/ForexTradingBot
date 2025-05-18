// File: Domain/Entities/TokenWallet.cs
#region Usings
using System.ComponentModel.DataAnnotations;        // برای [Key], [Required]
using System.ComponentModel.DataAnnotations.Schema; // برای [Column], [ForeignKey]
#endregion

namespace Domain.Entities // ✅ Namespace صحیح
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
        [Key] //  صریحاً کلید اصلی را مشخص می‌کند (اگرچه EF Core معمولاً Id را تشخیص می‌دهد)
        public Guid Id { get; private set; } //  تغییر به private set برای کنترل بیشتر از طریق سازنده

        /// <summary>
        /// Foreign key referencing the User who owns this wallet.
        /// This establishes a one-to-one relationship with the User entity.
        /// </summary>
        [Required]
        public Guid UserId { get; private set; } //  تغییر به private set

        /// <summary>
        /// Current balance of tokens in the user's wallet.
        /// This value is updated through various transactions (e.g., token purchases, service payments).
        /// It's crucial that updates to this field are handled原子的に (atomically) and with concurrency control.
        /// </summary>
        [Required]
        [Column(TypeName = "decimal(18, 8)")] //  دقت بالا برای موجودی توکن (مثلاً ۸ رقم اعشار)
        public decimal Balance { get; set; } //  set می‌تواند protected یا internal باشد اگر تغییرات فقط از طریق متدها انجام شود

        /*
        /// <summary>
        /// (Optional - For Future Use) The currency code or token symbol for this wallet (e.g., "BOT_TOKEN", "USD_CREDIT").
        /// This allows supporting multiple types of wallets per user if needed in the future.
        /// If only one type of token is used system-wide, this field might be redundant.
        /// </summary>
        [MaxLength(10)]
        public string? CurrencyCode { get; private set; } //  تغییر به private set
        */

        /// <summary>
        /// Indicates if the wallet is currently active and can be used for transactions.
        /// </summary>
        public bool IsActive { get; set; } = true; //  Default to active

        /// <summary>
        /// Date and time when this token wallet was created (UTC).
        /// Typically set when the user is registered or when the wallet is first initialized.
        /// </summary>
        [Required]
        public DateTime CreatedAt { get; private set; } // ✅ فیلد اضافه شد و private set

        /// <summary>
        /// Date and time of the last update to the wallet's balance or status (UTC).
        /// Useful for auditing and tracking the last activity.
        /// </summary>
        [Required]
        public DateTime UpdatedAt { get; set; } //  set برای به‌روزرسانی موجودی و ...

        /*
        /// <summary>
        /// (Optional) Date and time of the last transaction that affected this wallet's balance (UTC).
        /// Can be useful for specific reporting or user-facing information.
        /// </summary>
        public DateTime? LastTransactionDate { get; set; }
        */
        #endregion

        #region Navigation Properties
        /// <summary>
        /// Navigation property to the User who owns this wallet.
        /// Configured via Fluent API or data annotations for the one-to-one relationship.
        /// </summary>
        [ForeignKey(nameof(UserId))] //  صریحاً کلید خارجی را مشخص می‌کند
        public virtual User User { get; private set; } = null!; //  تغییر به private set و virtual برای Lazy Loading (اگر فعال است)
        #endregion

        #region Constructors
        /// <summary>
        /// Private constructor for EF Core and for use by the factory method or public constructor.
        /// </summary>
        private TokenWallet()
        {
            //  EF Core نیاز به یک سازنده بدون پارامتر دارد (می‌تواند private باشد).
            //  مقداردهی اولیه Id اینجا انجام می‌شود تا همیشه یک شناسه داشته باشد.
            Id = Guid.NewGuid();
        }

        /// <summary>
        /// Creates a new token wallet for a specific user with an initial balance.
        /// </summary>
        /// <param name="userId">The ID of the user owning this wallet.</param>
        /// <param name="initialBalance">The initial balance for the wallet (defaults to 0).</param>
        /// <param name="currencyCode">(Optional) The currency code for this wallet.</param>
        /// <returns>A new instance of <see cref="TokenWallet"/>.</returns>
        public static TokenWallet Create(Guid userId, decimal initialBalance = 0m /*, string? currencyCode = "DEFAULT_TOKEN" */)
        {
            if (userId == Guid.Empty)
                throw new ArgumentException("User ID cannot be empty.", nameof(userId));
            if (initialBalance < 0)
                throw new ArgumentOutOfRangeException(nameof(initialBalance), "Initial balance cannot be negative.");

            var now = DateTime.UtcNow;
            return new TokenWallet
            {
                UserId = userId,
                Balance = initialBalance,
                // CurrencyCode = currencyCode,
                IsActive = true,
                CreatedAt = now, // ✅ مقداردهی شد
                UpdatedAt = now
            };
        }
        #endregion

        #region Methods for Balance Management (Example - Business logic might be in a service)
        //  منطق مربوط به تغییر موجودی بهتر است در یک سرویس (TokenWalletService) قرار گیرد
        //  تا اصول DDD (Aggregate Root و تغییر وضعیت از طریق متدهای خود Aggregate) رعایت شود
        //  و همچنین برای مدیریت تراکنش‌ها و همزمانی بهتر است.
        //  این متدها فقط به عنوان نمونه آورده شده‌اند.

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
            // LastTransactionDate = UpdatedAt;
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
            // LastTransactionDate = UpdatedAt;
        }
        #endregion
    }
}