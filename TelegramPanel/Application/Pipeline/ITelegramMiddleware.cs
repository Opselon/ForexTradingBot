using Telegram.Bot.Types;
using System.Threading.Tasks;
using System.Threading;

namespace TelegramPanel.Application.Pipeline
{
    public delegate Task TelegramPipelineDelegate(Update update, CancellationToken cancellationToken);

    public interface ITelegramMiddleware
    {
        Task InvokeAsync(Update update, TelegramPipelineDelegate next, CancellationToken cancellationToken = default);
    }
}