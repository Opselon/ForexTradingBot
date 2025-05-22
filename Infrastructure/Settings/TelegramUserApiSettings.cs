namespace Infrastructure.Settings
{
    public class TelegramUserApiSettings
    {
        public int ApiId { get; set; }
        public string ApiHash { get; set; } = string.Empty;
        public string? PhoneNumber { get; set; }
        public string SessionPath { get; set; } = "telegram_user.session";
        public string VerificationCodeSource { get; set; } = "Console";
        public string TwoFactorPasswordSource { get; set; } = "Console";
    }
} 