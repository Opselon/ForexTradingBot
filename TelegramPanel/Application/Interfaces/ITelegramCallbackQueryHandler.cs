using Telegram.Bot.Types;

namespace TelegramPanel.Application.Interfaces
{
    public interface ITelegramCallbackQueryHandler
    {
        bool CanHandle(Update update);
        Task HandleAsync(Update update, CancellationToken cancellationToken);
    }
}