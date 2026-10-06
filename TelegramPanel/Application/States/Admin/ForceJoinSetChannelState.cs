// --- START OF UPGRADED FILE: TelegramPanel/Application/States/Admin/ForceJoinSetChannelState.cs ---
using Application.Interfaces;
using Telegram.Bot; // --- ADDED: For ITelegramBotClient ---
using Telegram.Bot.Exceptions; // --- ADDED: For ApiRequestException ---
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums; // --- ADDED: For ChatType ---
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters;
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions;

namespace TelegramPanel.Application.States.Admin
{
    /// <summary>
    /// A state that waits for an admin to provide the target channel,
    /// either by forwarding a message or sending the numeric ID directly.
    /// </summary>
    public class ForceJoinSetChannelState : ITelegramState
    {
        private readonly ITelegramMessageSender _messageSender;
        private readonly IAdminService _adminService;
        private readonly ITelegramBotClient _botClient; // --- ADDED ---

        public string Name => "WaitingForForceJoinChannel";

        public ForceJoinSetChannelState(
            ITelegramMessageSender messageSender,
            IAdminService adminService,
            ITelegramBotClient botClient) // --- ADDED ---
        {
            _messageSender = messageSender;
            _adminService = adminService;
            _botClient = botClient; // --- ADDED ---
        }

        public Task<string?> GetEntryMessageAsync(long chatId, Update? triggerUpdate = null, CancellationToken cancellationToken = default)
        {
            // --- UPGRADED PROMPT to explain both options ---
            string text = "Please provide the target channel using one of the following methods:\n\n" +
                       "1️⃣ **Forward a message** from the channel to me (recommended).\n\n" +
                       "2️⃣ **Send the channel's numeric ID** directly (e.g., `-1001234567890`).\n\n" +
                       "Type /cancel to abort.";
            return Task.FromResult<string?>(text);
        }

        public async Task<string?> ProcessUpdateAsync(Update update, CancellationToken cancellationToken = default)
        {
            Message? message = update.Message;
            if (message == null)
            {
                return Name; // Stay in this state if it's not a message
            }

            long adminChatId = message.Chat.Id;

            // Handle cancellation first
            if (message.Text?.Equals("/cancel", StringComparison.OrdinalIgnoreCase) == true)
            {
                await _messageSender.SendTextMessageAsync(adminChatId, "Operation cancelled.", cancellationToken: cancellationToken);
                return null; // Exit state
            }

            // --- OPTION 1: Handle Forwarded Message (Preferred) ---
            if (message.ForwardFromChat?.Type == ChatType.Channel)
            {
                await UpdateSettingsAndReplyAsync(adminChatId, message.ForwardFromChat, cancellationToken);
                return null; // Success, exit state
            }

            // --- OPTION 2: Handle Direct ID Input ---
            if (long.TryParse(message.Text?.Trim(), out long channelId))
            {
                try
                {
                    // Verify the ID by fetching channel info. This also confirms the bot has access.
                    ChatFullInfo channelInfo = await _botClient.GetChat(channelId, cancellationToken);

                    if (channelInfo.Type is not ChatType.Channel and not ChatType.Supergroup)
                    {
                        await _messageSender.SendTextMessageAsync(adminChatId, $"❌ The ID `{channelId}` belongs to a `{channelInfo.Type}`, not a channel or supergroup. Please provide a valid channel ID.", parseMode: ParseMode.MarkdownV2, cancellationToken: cancellationToken);
                        return Name; // Stay in state
                    }

                    await UpdateSettingsAndReplyAsync(adminChatId, channelInfo, cancellationToken);
                    return null; // Success, exit state
                }
                catch (ApiRequestException)
                {
                    // Handle cases where the ID is invalid or the bot can't access the chat.

                    await _messageSender.SendTextMessageAsync(adminChatId, $"❌ Could not access channel with ID `{channelId}`. Please ensure the ID is correct and that the bot is an administrator in that channel.", parseMode: ParseMode.MarkdownV2, cancellationToken: cancellationToken);
                    return Name; // Stay in state
                }
            }

            // --- Fallback: Invalid Input ---
            await _messageSender.SendTextMessageAsync(adminChatId, "❌ Invalid input. You must either **forward a message** or send a **numeric channel ID**. Please try again or type /cancel.", cancellationToken: cancellationToken);
            return Name; // Stay in state
        }

        /// <summary>
        /// A private helper to centralize the logic for updating settings and replying to the admin.
        /// </summary>
        private async Task UpdateSettingsAndReplyAsync(long adminChatId, Chat channel, CancellationToken cancellationToken)
        {
            global::Application.DTOs.Settings.ForceJoinSettingsDto settings = await _adminService.GetForceJoinSettingsAsync(cancellationToken);
            settings.ChannelId = channel.Id;

            // Store a user-friendly link. Prefer the public username if it exists.
            settings.ChannelLink = !string.IsNullOrEmpty(channel.Username)
                ? $"@{channel.Username}"
                : channel.Title ?? $"Private Channel ({channel.Id})"; // Fallback for private channels without a title in the forward

            await _adminService.UpdateForceJoinSettingsAsync(settings, cancellationToken);

            string successMessage = $"✅ Force Join channel updated successfully!\n\n" +
                                 $"**Title:** {TelegramMessageFormatter.EscapeMarkdownV2(channel.Title ?? string.Empty)}\n" +
                                 $"**ID:** `{channel.Id}`\n" +
                                 $"**Link:** `{settings.ChannelLink}`";

            await _messageSender.SendTextMessageAsync(adminChatId, successMessage, parseMode: ParseMode.MarkdownV2, cancellationToken: cancellationToken);
        }
    }
}