// --- START OF FILE: AdminPanelCommandHandler.cs ---
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.CommandHandlers.MainMenu; // For BackToMainMenuGeneral
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters;
using TelegramPanel.Infrastructure;
using TelegramPanel.Infrastructure.Helper;
using TelegramPanel.Settings;
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions;

namespace TelegramPanel.Application.CommandHandlers.Admin
{
    public class AdminPanelCommandHandler : ITelegramCommandHandler
    {
        private readonly ILogger<AdminPanelCommandHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly TelegramPanelSettings _settings;

        public AdminPanelCommandHandler(
            ILogger<AdminPanelCommandHandler> logger,
            ITelegramMessageSender messageSender,
            IOptions<TelegramPanelSettings> settingsOptions)
        {
            _logger = logger;
            _messageSender = messageSender;
            _settings = settingsOptions.Value;
        }

        public bool CanHandle(Update update)
        {
            // Handles /admin command ONLY from an authorized user
            return update.Type == UpdateType.Message &&
                   update.Message?.From != null &&
                   _settings.AdminUserIds.Contains(update.Message.From.Id) &&
                   update.Message.Text?.Trim().Equals("/admin", StringComparison.OrdinalIgnoreCase) == true;
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            var message = update.Message!;
            _logger.LogInformation("Admin user {UserId} accessed the admin panel.", message.From!.Id);

            var text = TelegramMessageFormatter.Bold("🛠️ Administrator Panel V2");
            text += "\n\nSelect an action:";

            var keyboard = MarkupBuilder.CreateInlineKeyboard(
                new[] { // Row 1: User & Server Info
            InlineKeyboardButton.WithCallbackData("📊 Server Stats", "admin_server_stats"),
            InlineKeyboardButton.WithCallbackData("🔍 User Lookup", "admin_user_lookup")
                },
                new[] { // Row 2: Maintenance Tasks
            InlineKeyboardButton.WithCallbackData("🔄 Fetch RSS Now", "admin_manual_rss"),
            InlineKeyboardButton.WithCallbackData("🧹 Purge Hangfire Jobs", "admin_purge_hangfire")
                },
                new[] { // Row 3: Advanced & Dangerous
            InlineKeyboardButton.WithCallbackData("📣 Broadcast Message", "admin_broadcast"),
            InlineKeyboardButton.WithCallbackData("☠️ Execute SQL", "admin_execute_sql") // New button
                },
                new[] { // Row 4: Navigation
            InlineKeyboardButton.WithCallbackData("🏠 Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral)
                }
            );
            await _messageSender.SendTextMessageAsync(message.Chat.Id, text, ParseMode.Markdown, keyboard, cancellationToken);
        }
    }
}