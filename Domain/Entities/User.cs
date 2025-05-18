// File: Domain/Entities/User.cs
#region Usings
using Domain.Enums;
using System.ComponentModel.DataAnnotations;
#endregion

namespace Domain.Entities
{
    public class User
    {
        #region Core Properties
        public Guid Id { get; set; }
        [Required]
        public string Username { get; set; } = null!;
        [Required]
        public string TelegramId { get; set; } = null!;
        [EmailAddress]
        [Required]
        public string Email { get; set; } = null!;
        public UserLevel Level { get; set; } = UserLevel.Free;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Date and time of the last update to the user's record (UTC).
        /// </summary>
        public DateTime? UpdatedAt { get; set; } // ✅ این باید وجود داشته باشد
        #endregion

        #region Notification Settings
        /// <summary>
        /// Indicates if the user wants to receive general notifications from the bot.
        /// </summary>
        public bool EnableGeneralNotifications { get; set; } = true; // ✅ این باید وجود داشته باشد

        /// <summary>
        /// Indicates if the user (if VIP) wants to receive notifications for VIP signals.
        /// </summary>
        public bool EnableVipSignalNotifications { get; set; } = true; // ✅ این باید وجود داشته باشد

        /// <summary>
        /// Indicates if the user wants to receive notifications for new RSS news/articles.
        /// </summary>
        public bool EnableRssNewsNotifications { get; set; } = true; // ✅ این باید وجود داشته باشد
        #endregion

        #region Language Preference
        /// <summary>
        /// User's preferred language for bot interaction (e.g., "en", "fa").
        /// </summary>
        [MaxLength(10)]
        public string PreferredLanguage { get; set; } = "en"; // ✅ اضافه شد برای بخش تنظیمات زبان
        #endregion

        #region Navigation Properties
        public TokenWallet TokenWallet { get; set; } = null!;
        public List<Subscription> Subscriptions { get; set; } = new();
        public List<Transaction> Transactions { get; set; } = new();
        public ICollection<UserSignalPreference> Preferences { get; set; } = new List<UserSignalPreference>();
        #endregion

        #region Constructors
        public User()
        {
            Id = Guid.NewGuid();
            CreatedAt = DateTime.UtcNow;
            Subscriptions = new List<Subscription>();
            Transactions = new List<Transaction>();
            Preferences = new List<UserSignalPreference>();
            EnableGeneralNotifications = true;
            EnableVipSignalNotifications = false; // پیش‌فرض برای کاربر جدید
            EnableRssNewsNotifications = true;
            PreferredLanguage = "en";
        }

        public User(string username, string telegramId, string email) : this()
        {
            Username = username?.Trim() ?? throw new ArgumentNullException(nameof(username));
            TelegramId = telegramId ?? throw new ArgumentNullException(nameof(telegramId));
            Email = email?.Trim().ToLowerInvariant() ?? throw new ArgumentNullException(nameof(email));
            // TokenWallet در سازنده UserService مقداردهی می‌شود یا در یک متد جداگانه پس از SaveChanges اولیه User
        }
        #endregion
    }
}