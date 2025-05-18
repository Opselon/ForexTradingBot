using Telegram.Bot.Types;

namespace TelegramPanel.Application.Pipeline
{
    public delegate Task TelegramPipelineDelegate(Update update, CancellationToken cancellationToken);

    public interface ITelegramMiddleware
    {
        Task InvokeAsync(Update update, TelegramPipelineDelegate next, CancellationToken cancellationToken = default);
    }
}