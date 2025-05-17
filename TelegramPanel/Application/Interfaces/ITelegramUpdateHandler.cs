using Telegram.Bot.Types;

namespace TelegramPanel.Application.Interfaces
{
    /// <summary>
    /// مسئول پردازش اولیه یک آپدیت دریافتی از تلگرام.
    /// این کلاس می‌تواند آپدیت را تجزیه، اعتبارسنجی کرده و به Command مناسب مپ کند.
    /// </summary>
    public interface ITelegramUpdateProcessor
    {
        Task ProcessUpdateAsync(Update update, CancellationToken cancellationToken = default);
    }
}