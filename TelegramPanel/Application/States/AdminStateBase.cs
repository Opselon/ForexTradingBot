using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using TelegramPanel.Application.Interfaces;

namespace TelegramPanel.Application.States
{
    public abstract class AdminStateBase : ITelegramState
    {
        protected readonly ILogger<AdminStateBase> Logger;
        public abstract string Name { get; }

        protected AdminStateBase(ILogger<AdminStateBase> logger) => Logger = logger;
        public virtual Task<string> GetEntryMessageAsync(long userId, Update? triggerUpdate = null, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public abstract Task<string?> ProcessUpdateAsync(Update update, CancellationToken cancellationToken = default);
    }
}