// --- START OF FILE: Application/DTOs/Admin/AdminUserDetailDto.cs ---
using Domain.Enums;

namespace Application.DTOs.Admin
{
    public class AdminUserDetailDto
    {
        // Core User Info
        public Guid UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public long TelegramId { get; set; }
        public UserLevel Level { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }

        // Notification Settings
        public bool EnableGeneralNotifications { get; set; }
        public bool EnableVipSignalNotifications { get; set; }
        public bool EnableRssNewsNotifications { get; set; }
        public string PreferredLanguage { get; set; } = "en";

        // Wallet Info
        public decimal TokenBalance { get; set; }
        public DateTime WalletLastUpdated { get; set; }

        // Subscription Info
        public List<SubscriptionSummaryDto> Subscriptions { get; set; } = [];
        public ActiveSubscriptionDto? ActiveSubscription { get; set; }

        // Transaction Info
        public int TotalTransactions { get; set; }
        public decimal TotalSpent { get; set; } // Based on subscription/token purchases
        public List<TransactionSummaryDto> RecentTransactions { get; set; } = [];
    }

    // --- Supporting DTOs for the main detail view ---

    public class SubscriptionSummaryDto
    {
        public Guid SubscriptionId { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public string Status { get; set; } = string.Empty;
        public bool IsCurrentlyActive { get; set; }
    }

    public class ActiveSubscriptionDto
    {
        public Guid SubscriptionId { get; set; }
        public DateTime EndDate { get; set; }
        public int DaysRemaining { get; set; }
    }

    public class TransactionSummaryDto
    {
        public Guid TransactionId { get; set; }
        public decimal Amount { get; set; }
        public TransactionType Type { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }
}