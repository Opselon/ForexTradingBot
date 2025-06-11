using Hangfire;
using Microsoft.Extensions.Logging;
using TelegramPanel.Application.Interfaces;

namespace TelegramPanel.Infrastructure
{
    public class BroadcastScheduler : IBroadcastScheduler
    {
        private readonly IBackgroundJobClient _backgroundJobClient;
        private readonly ILogger<BroadcastScheduler> _logger;

        public BroadcastScheduler(IBackgroundJobClient backgroundJobClient, ILogger<BroadcastScheduler> logger)
        {
            _backgroundJobClient = backgroundJobClient;
            _logger = logger;
        }

        public void EnqueueBroadcastMessage(long targetChatId, long sourceChatId, int messageId)
        {
            _logger.LogDebug("Enqueueing broadcast CopyMessage job for TargetChatID {TargetChatId}", targetChatId);
            // This is the correct way - it calls the real sender interface that Hangfire executes.
            _ = _backgroundJobClient.Enqueue<IActualTelegramMessageActions>(sender =>
                sender.CopyMessageToTelegramAsync(targetChatId, sourceChatId, messageId, CancellationToken.None));
        }
    }
}