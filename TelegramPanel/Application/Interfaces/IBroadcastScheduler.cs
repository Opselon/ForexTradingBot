namespace TelegramPanel.Application.Interfaces
{
    public interface IBroadcastScheduler
    {
        void EnqueueBroadcastMessage(long targetChatId, long sourceChatId, int messageId);
    }
}