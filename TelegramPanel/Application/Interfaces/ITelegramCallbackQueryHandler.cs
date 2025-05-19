using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Types;

namespace TelegramPanel.Application.Interfaces
{
    public interface ITelegramCallbackQueryHandler
    {
        Task HandleAsync(Update update, CancellationToken cancellationToken);
    }
} 