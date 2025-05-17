using Telegram.Bot.Types;
using System.Threading.Tasks;
using System.Threading;

namespace TelegramPanel.Application.Interfaces
{
    /// <summary>
    /// نشان‌دهنده یک وضعیت در ماشین وضعیت مکالمه کاربر.
    /// </summary>
    public interface ITelegramState
    {
        /// <summary>
        /// نام منحصر به فرد وضعیت.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// پیام یا سوالی که باید هنگام ورود به این وضعیت به کاربر نمایش داده شود.
        /// </summary>
        Task<string?> GetEntryMessageAsync(long chatId, Update? triggerUpdate = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// آپدیت دریافتی را در این وضعیت پردازش می‌کند.
        /// </summary>
        /// <returns>نام وضعیت بعدی یا null اگر مکالمه در همین وضعیت باقی بماند یا پایان یابد.</returns>
        Task<string?> ProcessUpdateAsync(Update update, CancellationToken cancellationToken = default);
    }
}