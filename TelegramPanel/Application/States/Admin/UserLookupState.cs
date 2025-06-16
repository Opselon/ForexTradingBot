using Application.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Infrastructure;
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions;

namespace TelegramPanel.Application.States.Admin
{
    public class UserLookupState : ITelegramState
    {
        private readonly ILogger<UserLookupState> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly IAdminService _adminService;
        public string Name => "WaitingForUserLookupId";

        public UserLookupState(ILogger<UserLookupState> logger, ITelegramMessageSender messageSender, IAdminService adminService)
        {
            _logger = logger;
            _messageSender = messageSender;
            _adminService = adminService;
        }

        public Task<string?> GetEntryMessageAsync(long userId, Update? triggerUpdate, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Admin {AdminId} entered user lookup state.", userId);
            return Task.FromResult<string?>("🔎 *User Lookup*\n\nPlease send the numerical Telegram ID of the user you wish to find, or type /cancel to abort.");
        }

        public async Task<string?> ProcessUpdateAsync(Update update, CancellationToken cancellationToken = default)
        {
            if (update.Message?.From == null || string.IsNullOrWhiteSpace(update.Message.Text))
            {
                return Name;
            }

            var message = update.Message;
            var adminId = message.From.Id;

            if (message.Text.Trim() == "/cancel")
            {
                await _messageSender.SendTextMessageAsync(adminId, "User lookup cancelled.", cancellationToken: cancellationToken);
                return null;
            }

            if (!long.TryParse(message.Text, out var targetUserId))
            {
                await _messageSender.SendTextMessageAsync(adminId, "⚠️ Invalid format. Please send a numerical Telegram ID.", cancellationToken: cancellationToken);
                return Name;
            }

            // ✅ FIX: Call the correct method name from the IAdminService interface.
            var userDetail = await _adminService.GetUserDetailByTelegramIdAsync(targetUserId, cancellationToken);

            var response = new StringBuilder();
            if (userDetail == null)
            {
                _ = response.AppendLine($"❌ User with Telegram ID `{targetUserId}` not found.");
            }
            else
            {
                _ = response.AppendLine($"✅ *User Found: {userDetail.Username}*");
                _ = response.AppendLine($"`------------------------------`");
                _ = response.AppendLine($"• *System ID:* `{userDetail.UserId}`");
                _ = response.AppendLine($"• *Telegram ID:* `{userDetail.TelegramId}`");
                _ = response.AppendLine($"• *Level:* `{userDetail.Level}`");
                _ = response.AppendLine($"• *Joined:* `{userDetail.CreatedAt:yyyy-MM-dd}`");
                _ = response.AppendLine($"• *Token Balance:* `{userDetail.TokenBalance:N2}`");
                _ = userDetail.ActiveSubscription != null
                    ? response.AppendLine($"• *VIP Expires:* `{userDetail.ActiveSubscription.EndDate:yyyy-MM-dd}` ({userDetail.ActiveSubscription.DaysRemaining} days left)")
                    : response.AppendLine($"• *Subscription:* `None`");
                // ... add more details from the DTO as needed ...
            }

            await _messageSender.SendTextMessageAsync(adminId, response.ToString(), ParseMode.Markdown, cancellationToken: cancellationToken);
            return null; // Exit state
        }
    }
}