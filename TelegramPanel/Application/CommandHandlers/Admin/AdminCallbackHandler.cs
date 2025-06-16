// --- START OF FILE: AdminCallbackHandler.cs ---

using Application.Interfaces; // For IAdminService
using Hangfire; // For IRecurringJobManager
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Maintenance; // For IHangfireCleaner
using System.Text;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.CommandHandlers.MainMenu;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters;
using TelegramPanel.Infrastructure;
using TelegramPanel.Infrastructure.Helper;
using TelegramPanel.Settings;
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions;

namespace TelegramPanel.Application.CommandHandlers.Admin
{
    /// <summary>
    /// Handles simple, stateless callback queries from the Admin Panel,
    /// such as fetching stats or triggering one-off background jobs.
    /// Stateful actions like broadcasting are handled by dedicated InitiationHandlers.
    /// </summary>
    public class AdminCallbackHandler : ITelegramCallbackQueryHandler
    {
        private readonly ILogger<AdminCallbackHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly IAdminService _adminService;
        private readonly IRecurringJobManager _recurringJobManager;
        private readonly IHangfireCleaner _hangfireCleaner;
        private readonly IConfiguration _configuration;
        private readonly TelegramPanelSettings _settings;

        // Constants for the actions this specific handler is responsible for.
        private const string AdminServerStatsCallback = "admin_server_stats";
        private const string AdminManualRssFetchCallback = "admin_manual_rss";
        private const string PurgeHangfireCallback = "admin_purge_hangfire";
        private const string BackToAdminPanelCallback = "admin_panel_main";

        public AdminCallbackHandler(
            ILogger<AdminCallbackHandler> logger,
            ITelegramMessageSender messageSender,
            IAdminService adminService,
            IRecurringJobManager recurringJobManager,
            IHangfireCleaner hangfireCleaner,
            IConfiguration configuration,
            IOptions<TelegramPanelSettings> settingsOptions)
        {
            _logger = logger;
            _messageSender = messageSender;
            _adminService = adminService;
            _recurringJobManager = recurringJobManager;
            _hangfireCleaner = hangfireCleaner;
            _configuration = configuration;
            _settings = settingsOptions.Value;
        }

        public bool CanHandle(Update update)
        {
            if (update.Type != UpdateType.CallbackQuery || update.CallbackQuery?.Data == null || update.CallbackQuery.From == null)
            {
                return false;
            }

            if (!_settings.AdminUserIds.Contains(update.CallbackQuery.From.Id))
            {
                return false;
            }

            var data = update.CallbackQuery.Data;

            // This handler is now responsible for all these stateless admin actions.
            return data is AdminServerStatsCallback or
                   AdminManualRssFetchCallback or
                   PurgeHangfireCallback or
                   BackToAdminPanelCallback;
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            var callbackQuery = update.CallbackQuery!;
            await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);

            _logger.LogInformation("Admin {UserId} initiated action: {Action}", callbackQuery.From.Id, callbackQuery.Data);

            var handlerTask = callbackQuery.Data switch
            {
                AdminServerStatsCallback => HandleServerStatsAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.MessageId, cancellationToken),
                AdminManualRssFetchCallback => HandleManualRssFetchAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.MessageId, cancellationToken),
                PurgeHangfireCallback => HandlePurgeHangfireAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.MessageId, cancellationToken),
                BackToAdminPanelCallback => ShowAdminPanelAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.MessageId, cancellationToken),
                _ => Task.CompletedTask
            };
            await handlerTask;
        }

        private async Task HandleServerStatsAsync(long chatId, int messageId, CancellationToken cancellationToken)
        {
            await _messageSender.EditMessageTextAsync(chatId, messageId, "📊 Fetching server stats...", cancellationToken: cancellationToken);

            (int userCount, int newsItemCount) = await _adminService.GetDashboardStatsAsync(cancellationToken);

            var stats = new StringBuilder();
            _ = stats.AppendLine(TelegramMessageFormatter.Bold("📊 Server & Bot Status"));
            _ = stats.AppendLine("`------------------------------`");
            _ = stats.AppendLine($"👥 Total Users: `{userCount:N0}`");
            _ = stats.AppendLine($"📰 News Items Indexed: `{newsItemCount:N0}`");
            _ = stats.AppendLine("`------------------------------`");
            _ = stats.AppendLine($"• Environment: `{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}`");
            _ = stats.AppendLine($"• Server Time (UTC): `{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}`");

            await _messageSender.EditMessageTextAsync(chatId, messageId, stats.ToString(), ParseMode.Markdown, GetBackToAdminPanelKeyboard(), cancellationToken);
        }

        private async Task HandleManualRssFetchAsync(long chatId, int messageId, CancellationToken cancellationToken)
        {
            await _messageSender.EditMessageTextAsync(chatId, messageId, "⏳ Triggering RSS fetch job...", cancellationToken: cancellationToken);
            var text = "✅ The `fetch-all-active-rss-feeds` job has been triggered. Check Hangfire dashboard for progress.";
            try
            {
                _recurringJobManager.Trigger("fetch-all-active-rss-feeds");
                _logger.LogInformation("Admin manually triggered the RSS fetch job.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to manually trigger RSS Fetch job.");
                text = "❌ Failed to trigger RSS Fetch job. See server logs for details.";
            }
            await _messageSender.EditMessageTextAsync(chatId, messageId, text, replyMarkup: GetBackToAdminPanelKeyboard(), cancellationToken: cancellationToken);
        }

        private async Task HandlePurgeHangfireAsync(long chatId, int messageId, CancellationToken cancellationToken)
        {
            await _messageSender.EditMessageTextAsync(chatId, messageId, "⏳ Purging completed Hangfire jobs...", cancellationToken: cancellationToken);
            try
            {
                var connectionString = _configuration.GetConnectionString("DefaultConnection")!;
                _hangfireCleaner.PurgeCompletedAndFailedJobs(connectionString);
                _logger.LogInformation("Admin manually purged Hangfire jobs.");
                await _messageSender.EditMessageTextAsync(chatId, messageId, "✅ Hangfire 'Succeeded' and 'Failed' job lists have been cleared.", replyMarkup: GetBackToAdminPanelKeyboard(), cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to purge Hangfire jobs.");
                await _messageSender.EditMessageTextAsync(chatId, messageId, "❌ An error occurred while purging Hangfire jobs.", replyMarkup: GetBackToAdminPanelKeyboard(), cancellationToken: cancellationToken);
            }
        }

        private Task ShowAdminPanelAsync(long chatId, int messageId, CancellationToken cancellationToken)
        {
            var text = TelegramMessageFormatter.Bold("🛠️ Administrator Panel") + "\n\nSelect an action:";
            var keyboard = GetAdminPanelKeyboard();
            return _messageSender.EditMessageTextAsync(chatId, messageId, text, ParseMode.Markdown, keyboard, cancellationToken);
        }

        private InlineKeyboardMarkup GetAdminPanelKeyboard()
        {
            // Use the constants defined in this class for consistency.
            // Other handlers (like BroadcastInitiationHandler) will have their own constants.
            return MarkupBuilder.CreateInlineKeyboard(
                new[] { InlineKeyboardButton.WithCallbackData("📊 Server Stats", AdminServerStatsCallback) },
                new[] { InlineKeyboardButton.WithCallbackData("🔄 Fetch RSS Now", AdminManualRssFetchCallback),
                        InlineKeyboardButton.WithCallbackData("🧹 Purge Hangfire Jobs", PurgeHangfireCallback) },
                new[] { InlineKeyboardButton.WithCallbackData("📣 Broadcast", "admin_broadcast"),
                        InlineKeyboardButton.WithCallbackData("🔍 User Lookup", "admin_user_lookup") },
                new[] { InlineKeyboardButton.WithCallbackData("🏠 Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral) }
            );
        }

        private static InlineKeyboardMarkup GetBackToAdminPanelKeyboard()
        {
            return MarkupBuilder.CreateInlineKeyboard(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Back to Admin Panel", BackToAdminPanelCallback) });
        }
    }
}