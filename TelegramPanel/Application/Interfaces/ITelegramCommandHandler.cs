using Telegram.Bot.Types;

namespace TelegramPanel.Application.Interfaces
{
    /// <summary>
    /// نشان‌دهنده یک Handler برای یک نوع خاص از دستور یا پیام تلگرام.
    /// </summary>
    public interface ITelegramCommandHandler
    {
        /// <summary>
        /// بررسی می‌کند که آیا این Handler می‌تواند آپدیت داده شده را پردازش کند یا خیر.
        /// (مثلاً بر اساس متن دستور، نوع پیام، وضعیت کاربر و ...)
        /// </summary>
        bool CanHandle(Update update);

        /// <summary>
        /// آپدیت را پردازش می‌کند.
        /// </summary>
        Task HandleAsync(Update update, CancellationToken cancellationToken = default);
    }
}