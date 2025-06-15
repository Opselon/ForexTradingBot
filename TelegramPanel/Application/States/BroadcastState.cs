// NEW FILE: TelegramPanel/Application/States/Admin/BroadcastState.cs
using Application.Interfaces;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramPanel.Application.Interfaces;
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions;

namespace TelegramPanel.Application.States
{
    public class BroadcastState : ITelegramState
    {
        private readonly ITelegramMessageSender _messageSender;
        private readonly IBroadcastScheduler _broadcastScheduler;
        private readonly IAdminService _adminService;
        public string Name => "WaitingForBroadcastMessage";

        public BroadcastState(ITelegramMessageSender ms, IBroadcastScheduler bs, IAdminService ads)
        { _messageSender = ms; _broadcastScheduler = bs; _adminService = ads; }

        public Task<string?> GetEntryMessageAsync(long userId, Update? triggerUpdate, CancellationToken ct)
        {
            return Task.FromResult<string?>("📝 *Broadcast Message:*\nPlease send the message you wish to broadcast. Type /cancel to abort.");
        }

        // In the BroadcastState class...

        public async Task<string?> ProcessUpdateAsync(Update update, CancellationToken cancellationToken = default)
        {
            // --- ✅ NULL CHECK AND TYPE VALIDATION ---
            // First, check if the update is a message. If not, ignore it and stay in the current state.
            if (update.Type != UpdateType.Message || update.Message?.From == null)
            {
                return Name; // "Name" refers to "WaitingForBroadcastMessage", keeping the user in this state.
            }

            Message message = update.Message;
            long adminId = message.From.Id;

            // --- Handle Cancellation Command ---
            if (message.Text?.Trim().Equals("/cancel", StringComparison.OrdinalIgnoreCase) == true)
            {
                await _messageSender.SendTextMessageAsync(adminId, "Broadcast cancelled.", cancellationToken: cancellationToken);

                return null; // Returning null exits the state machine.
            }

            // --- Process the Broadcast Content ---
            List<long> userChatIds = await _adminService.GetAllActiveUserChatIdsAsync(cancellationToken);
            if (!userChatIds.Any())
            {
                await _messageSender.SendTextMessageAsync(adminId, "⚠️ No active users found. Broadcast aborted.", cancellationToken: cancellationToken);
                return null; // Exit state.
            }

            // Enqueue the broadcast jobs
            foreach (long userChatId in userChatIds)
            {
                // This will now copy any type of message: text, photo, video, etc.
                _broadcastScheduler.EnqueueBroadcastMessage(userChatId, message.Chat.Id, message.MessageId);
            }



            string confirmationText = $"✅ Broadcast has been successfully enqueued for delivery to *{userChatIds.Count}* users.";
            await _messageSender.SendTextMessageAsync(adminId, confirmationText, ParseMode.Markdown, cancellationToken: cancellationToken);

            // Return null to signify that this conversation is complete and the user's state should be cleared.
            return null;
        }
    }
}