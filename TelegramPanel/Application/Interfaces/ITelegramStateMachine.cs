using Telegram.Bot.Types;
using System.Threading.Tasks;
using System.Threading;

namespace TelegramPanel.Application.Interfaces
{
    public interface ITelegramStateMachine
    {
        Task<ITelegramState?> GetCurrentStateAsync(long userId, CancellationToken cancellationToken = default);
        Task SetStateAsync(long userId, string? stateName, Update? triggerUpdate = null, CancellationToken cancellationToken = default);
        Task ProcessUpdateInCurrentStateAsync(long userId, Update update, CancellationToken cancellationToken = default);
        Task ClearStateAsync(long userId, CancellationToken cancellationToken = default);
    }
}