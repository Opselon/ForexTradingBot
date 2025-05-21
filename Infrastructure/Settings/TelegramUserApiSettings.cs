// File: src/Infrastructure/Settings/TelegramUserApiSettings.cs
namespace Infrastructure.Settings
{
    public class TelegramUserApiSettings
    {
        public int ApiId { get; set; }
        public string ApiHash { get; set; } = string.Empty;
        public string? PhoneNumber { get; set; } // Optional if session exists and is valid
        public string SessionPath { get; set; } = "telegram_user.session"; // Default session file name, make it distinct
        public string VerificationCodeSource { get; set; } = "Console"; // e.g., Console, File, Environment
        public string TwoFactorPasswordSource { get; set; } = "Console"; // e.g., Console, File, Environment
    }
}