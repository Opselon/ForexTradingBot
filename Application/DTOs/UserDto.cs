using Domain.Enums;

namespace Application.DTOs
{
    public class UserDto
    {
        public Guid Id { get; set; }
        public string Username { get; set; } = null!;
        public string TelegramId { get; set; } = null!;
        public string Email { get; set; } = null!;
        public UserLevel Level { get; set; } = UserLevel.Free;
        public decimal TokenBalance { get; set; } // از TokenWallet برداشت می‌کنیم
        public DateTime CreatedAt { get; set; }

        public TokenWalletDto? TokenWallet { get; set; }
        public SubscriptionDto? ActiveSubscription { get; set; }
    }
}
