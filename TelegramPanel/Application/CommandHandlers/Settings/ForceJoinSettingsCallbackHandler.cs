// --- START OF FULLY CORRECTED FILE: ForceJoinSettingsCallbackHandler.cs ---

using Application.DTOs.Settings;
using Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using Telegram.Bot; // <-- ADD THIS for ITelegramBotClient
using Telegram.Bot.Exceptions; // <-- ADD THIS for ApiRequestException
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters;
using TelegramPanel.Settings;
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions;

namespace TelegramPanel.Application.CommandHandlers.Settings
{
    /// <summary>
    /// Manages the UI for the Force Join settings in the admin panel.
    /// </summary>
    public class ForceJoinSettingsCallbackHandler : ITelegramCallbackQueryHandler
    {
        // --- 1. DECLARE THE FIELDS ---
        private readonly ILogger<ForceJoinSettingsCallbackHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly IAdminService _adminService;
        private readonly ITelegramStateMachine _stateMachine;
        private readonly ITelegramBotClient _botClient; // <-- FIX: Field is now declared
        private readonly TelegramPanelSettings _settings;

        // Constants
        private const string MenuCallback = "admin_forcejoin_menu";
        private const string ToggleCallback = "admin_forcejoin_toggle";
        private const string SetChannelCallback = "admin_forcejoin_set_channel";
        private const string SetMessageCallback = "admin_forcejoin_set_message";

        // --- 2. UPDATE THE CONSTRUCTOR ---
        public ForceJoinSettingsCallbackHandler(
            ILogger<ForceJoinSettingsCallbackHandler> logger,
            ITelegramMessageSender messageSender,
            IAdminService adminService,
            ITelegramStateMachine stateMachine,
            ITelegramBotClient botClient, // <-- FIX: Inject the bot client
            IOptions<TelegramPanelSettings> settingsOptions)
        {
            _logger = logger;
            _messageSender = messageSender;
            _adminService = adminService;
            _stateMachine = stateMachine;
            _botClient = botClient; // <-- FIX: Assign the injected client to the field
            _settings = settingsOptions.Value;
        }

        public bool CanHandle(Update update)
        {
            return update.Type == UpdateType.CallbackQuery &&
                   update.CallbackQuery?.Data != null &&
                   update.CallbackQuery.Data.StartsWith("admin_forcejoin_") &&
                   _settings.AdminUserIds.Contains(update.CallbackQuery.From.Id);
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            CallbackQuery callbackQuery = update.CallbackQuery!;
            await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
            _logger.LogInformation("Admin {UserId} managed Force Join settings with action: {Action}", callbackQuery.From.Id, callbackQuery.Data);

            string? action = callbackQuery.Data;
            long chatId = callbackQuery.Message!.Chat.Id;
            int messageId = callbackQuery.Message.MessageId;

            Task handlerTask = action switch
            {
                SetChannelCallback => _stateMachine.SetStateAsync(chatId, "WaitingForForceJoinChannel", update, cancellationToken),
                SetMessageCallback => _stateMachine.SetStateAsync(chatId, "WaitingForForceJoinMessage", update, cancellationToken),
                _ => HandleMenuActions(action!, chatId, messageId, cancellationToken)
            };
            await handlerTask;
        }

        private async Task HandleMenuActions(string action, long chatId, int messageId, CancellationToken cancellationToken)
        {
            if (action == ToggleCallback)
            {
                ForceJoinSettingsDto currentSettings = await _adminService.GetForceJoinSettingsAsync(cancellationToken);
                currentSettings.IsEnabled = !currentSettings.IsEnabled;
                await _adminService.UpdateForceJoinSettingsAsync(currentSettings, cancellationToken);
            }

            await ShowForceJoinMenuAsync(chatId, messageId, cancellationToken);
        }

        private async Task ShowForceJoinMenuAsync(long chatId, int messageId, CancellationToken cancellationToken)
        {
            ForceJoinSettingsDto settings = await _adminService.GetForceJoinSettingsAsync(cancellationToken);
            StringBuilder text = new();

            _ = text.AppendLine(TelegramMessageFormatter.Bold("🛂 Force Join Settings"));
            _ = text.AppendLine();
            _ = text.AppendLine($"**Status:** {(settings.IsEnabled ? "✅ Enabled" : "❌ Disabled")}");

            if (settings.ChannelId != 0)
            {
                try
                {
                    // This code now works because _botClient exists
                    ChatFullInfo chatInfo = await _botClient.GetChat(settings.ChannelId, cancellationToken);
                    int memberCount = await _botClient.GetChatMemberCount(settings.ChannelId, cancellationToken);

                    _ = text.AppendLine($"**Title:** {TelegramMessageFormatter.EscapeMarkdownV2(chatInfo.Title ?? string.Empty)}");
                    _ = text.AppendLine($"**ID:** `{chatInfo.Id}`");
                    _ = text.AppendLine($"**Link:** {TelegramMessageFormatter.EscapeMarkdownV2(settings.ChannelLink)}");
                    _ = text.AppendLine($"**Members:** {memberCount:N0}");
                }
                catch (ApiRequestException ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch details for configured Force Join channel {ChannelId}.", settings.ChannelId);
                    _ = text.AppendLine($"**Channel ID:** `{settings.ChannelId}`");
                    _ = text.AppendLine($"**Status:** ⚠️ **Error Fetching Details**");
                    _ = text.AppendLine($"**Reason:** _{TelegramMessageFormatter.EscapeMarkdownV2(ex.Message)}_");
                }
            }
            else
            {
                _ = text.AppendLine("**Channel:** Not Set");
            }

            _ = text.AppendLine();
            _ = text.AppendLine("**Message:**");

            _ = !string.IsNullOrWhiteSpace(settings.Message)
                ? text.AppendLine(TelegramMessageFormatter.Bold(settings.Message))
                : text.AppendLine("_Default message will be used._");

            InlineKeyboardMarkup keyboard = GetKeyboard(settings);

            await _messageSender.EditMessageTextAsync(
                chatId: chatId,
                messageId: messageId,
                text: text.ToString(),
                parseMode: ParseMode.MarkdownV2,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }

        private InlineKeyboardMarkup GetKeyboard(ForceJoinSettingsDto settings)
        {
            InlineKeyboardButton toggleButton = settings.IsEnabled
                ? InlineKeyboardButton.WithCallbackData("❌ Disable Feature", ToggleCallback)
                : InlineKeyboardButton.WithCallbackData("✅ Enable Feature", ToggleCallback);

            InlineKeyboardButton setChannelButton = InlineKeyboardButton.WithCallbackData("✏️ Set Channel", SetChannelCallback);
            InlineKeyboardButton setMessageButton = InlineKeyboardButton.WithCallbackData("📝 Set Message", SetMessageCallback);

            InlineKeyboardButton backButton = InlineKeyboardButton.WithCallbackData("⬅️ Back to Settings", "admin_bot_settings");

            // --- THIS IS THE FIX ---
            // Instead of creating an array of arrays (new[] { new[] { ... } }),
            // we create a List of Lists (new List<List<...>>).
            // This solves the Hangfire deserialization issue.
            return new InlineKeyboardMarkup(new List<List<InlineKeyboardButton>>
    {
        // Row 1
        new() { toggleButton },
        
        // Row 2
        new() { setChannelButton, setMessageButton },
        
        // Row 3
        new() { backButton }
    });
        }
    }
}