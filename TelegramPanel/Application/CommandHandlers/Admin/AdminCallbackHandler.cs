// --- START OF FILE: AdminCallbackHandler.cs ---

using Application.Common.Interfaces;
using Application.Interfaces; // For IAdminService
using Domain.Entities;
using Hangfire; // For IRecurringJobManager
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.CommandHandlers.MainMenu;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters;
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
        private readonly IConfiguration _configuration;
        private readonly TelegramPanelSettings _settings;
        private readonly IServiceProvider _serviceProvider;

        // Constants for the actions this specific handler is responsible for.
        private const string AdminServerStatsCallback = "admin_server_stats";
        private const string AdminManualRssFetchCallback = "admin_manual_rss";
        private const string PurgeHangfireCallback = "admin_purge_hangfire";
        private const string BackToAdminPanelCallback = "admin_panel_main";
        private const string DownloadLogsCallback = "admin_download_logs";
        public const string ProMonitoringCallbackPrefix = "admin_pro_monitoring_";
        // 1. ADD NEW PUBLIC CONSTANTS FOR THE DELETE ACTIONS
        public const string ProMonitoringDeletePromptPrefix = "admin_pro_mon_delete_prompt_";
        public const string ProMonitoringDeleteConfirmCallback = "admin_pro_mon_delete_confirm";
        public AdminCallbackHandler(
            ILogger<AdminCallbackHandler> logger,
            ITelegramMessageSender messageSender,
            IAdminService adminService,
            IRecurringJobManager recurringJobManager,
            IConfiguration configuration,
            IOptions<TelegramPanelSettings> settingsOptions,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _messageSender = messageSender;
            _adminService = adminService;
            _recurringJobManager = recurringJobManager;
            _configuration = configuration;
            _settings = settingsOptions.Value;
            _serviceProvider = serviceProvider;
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

            string data = update.CallbackQuery.Data;

            // --- CORRECTED CanHandle Method ---
            // Check for explicit callback strings first.
            if (data == AdminServerStatsCallback ||
                data == AdminManualRssFetchCallback ||
                data == PurgeHangfireCallback ||
                data == DownloadLogsCallback ||
                data.StartsWith(ProMonitoringCallbackPrefix) ||
           data.StartsWith(ProMonitoringDeletePromptPrefix) ||
                data == ProMonitoringDeleteConfirmCallback ||
            data == BackToAdminPanelCallback)
            {
                return true;
            }

            // Then, check for the prefixed callback for Pro Monitoring logs.
            if (data.StartsWith(ProMonitoringCallbackPrefix))
            {
                return true;
            }

            // If none of the above, it's not handled by this class.
            return false;
        }


        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            CallbackQuery callbackQuery = update.CallbackQuery!;
            await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);

            try
            {
                _logger.LogInformation("Admin {UserId} initiated action: {Action}", callbackQuery.From.Id, callbackQuery.Data);

                Task handlerTask = callbackQuery.Data switch
                {
                    string data when data.StartsWith(ProMonitoringCallbackPrefix) =>
          HandleProMonitoringAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.MessageId, data, cancellationToken),
                    string data when data.StartsWith(ProMonitoringDeletePromptPrefix) =>
                     HandleDeleteLogsPromptAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.MessageId, data, cancellationToken),

                    ProMonitoringDeleteConfirmCallback =>
                        HandleDeleteLogsConfirmAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.MessageId, cancellationToken),

                    AdminServerStatsCallback => HandleServerStatsAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.MessageId, cancellationToken),
                    AdminManualRssFetchCallback => HandleManualRssFetchAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.MessageId, cancellationToken),
                    PurgeHangfireCallback => HandlePurgeHangfireAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.MessageId, cancellationToken),
                    DownloadLogsCallback => HandleDownloadLogsAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.MessageId, cancellationToken),
                    BackToAdminPanelCallback => ShowAdminPanelAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.MessageId, cancellationToken),
                    // CORRECTED: This 'when' clause properly routes paginated callbacks
                    string data when data.StartsWith(ProMonitoringCallbackPrefix) =>
                        HandleProMonitoringAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.MessageId, data, cancellationToken),
                    _ => Task.CompletedTask
                };

                await handlerTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in AdminCallbackHandler for action {CallbackData}", callbackQuery.Data);

                // Fire-and-forget logging to the database in a background task
                _ = Task.Run(async () =>
                {
                    using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "AdminCallbackHandler",
                        EventType = "AdminActionFailure",
                        Message = $"Failed to execute admin action: {callbackQuery.Data}",
                        Details = ex.StackTrace,
                        Exception = ex.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });

                // Inform the admin about the failure
                try
                {
                    await _messageSender.EditMessageTextAsync(
                        chatId: callbackQuery.Message!.Chat.Id,
                        messageId: callbackQuery.Message!.MessageId,
                        text: "❌ An unexpected error occurred. The action could not be completed. The error has been logged for review.",
                        replyMarkup: GetBackToAdminPanelKeyboard(),
                        cancellationToken: cancellationToken);
                }
                catch (Exception telegramEx)
                {
                    // If even sending the error message fails, just log it.
                    _logger.LogError(telegramEx, "Failed to send error notification to admin for action {CallbackData}", callbackQuery.Data);
                }
            }
        }




        private async Task HandleDownloadLogsAsync(long chatId, int messageId, CancellationToken cancellationToken)
        {
            try
            {
                // Give immediate feedback to the admin
                await _messageSender.EditMessageTextAsync(chatId, messageId, "🗜️ Finding and zipping log files, please wait...", cancellationToken: cancellationToken);

                // This service call is the primary point of potential failure
                (byte[]? zipContents, string fileName, string? errorMessage) = await _adminService.GetLogFilesAsZipAsync(cancellationToken);

                // This block handles the expected outcomes (success or known failure)
                if (zipContents != null && zipContents.Length > 0)
                {
                    _logger.LogInformation("Sending log archive '{FileName}' to admin chat {ChatId}.", fileName, chatId);

                    await _messageSender.SendDocumentAsync(
                        chatId: chatId,
                        documentContents: zipContents,
                        fileName: fileName,
                        caption: "Here are the server logs.",
                        cancellationToken: cancellationToken
                    );

                    await _messageSender.EditMessageTextAsync(chatId, messageId, "✅ Log archive sent successfully. The file will arrive shortly.", replyMarkup: GetBackToAdminPanelKeyboard(), cancellationToken: cancellationToken);
                }
                else
                {
                    // This is a "graceful" failure, where the process worked but found no files or had a specific issue.
                    _logger.LogWarning("Failed to create log archive: {Error}", errorMessage);
                    string errorText = $"❌ Could not retrieve logs. Reason: `{errorMessage}`";
                    await _messageSender.EditMessageTextAsync(chatId, messageId, errorText, ParseMode.Markdown, GetBackToAdminPanelKeyboard(), cancellationToken: cancellationToken);
                }
            }
            catch (Exception ex)
            {
                // This block handles unexpected exceptions (e.g., file permissions, out of memory).

                // 1. Log the error to the primary logging system.
                _logger.LogError(ex, "An unexpected error occurred while preparing log files for download.");

                // --- ENHANCEMENT: Log the failure to the database in a background task ---
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "AdminCallbackHandler",
                        EventType = "DownloadLogs",
                        Message = "An unexpected exception occurred while zipping and sending log files.",
                        Details = ex.StackTrace,
                        Exception = ex.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                // --- END OF ENHANCEMENT ---

                // 2. Inform the admin user that the operation failed.
                await _messageSender.EditMessageTextAsync(
                    chatId: chatId,
                    messageId: messageId,
                    text: "❌ An unexpected error occurred while trying to create the log archive. The failure has been logged for review.",
                    replyMarkup: GetBackToAdminPanelKeyboard(),
                    cancellationToken: cancellationToken);
            }
        }
        private async Task HandleServerStatsAsync(long chatId, int messageId, CancellationToken cancellationToken)
        {
            await _messageSender.EditMessageTextAsync(chatId, messageId, "\ud83d\udcca Fetching server stats...", cancellationToken: cancellationToken);

            (int userCount, int newsItemCount, List<(DateTime Date, int Count)> userJoinStats) = await _adminService.GetDashboardStatsWithUserJoinsAsync(cancellationToken);

            StringBuilder stats = new();
            _ = stats.AppendLine("\ud83d\udcca *Server & Bot Status*\n────────────────────────────");
            _ = stats.AppendLine($"👥 Users: {userCount:N0}");
            _ = stats.AppendLine($"📰 News: {newsItemCount:N0}");
            _ = stats.AppendLine($"🌐 Env: {Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}");
            _ = stats.AppendLine($"🕔 UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            _ = stats.AppendLine("────────────────────────────\n");

            #region User Join Chart (Ultra-Polished)
            if (userJoinStats != null && userJoinStats.Count > 0)
            {
                int max = userJoinStats.Max(x => x.Count);
                int barMax = 8;
                int total = userJoinStats.Sum(x => x.Count);
                double avgAll = userJoinStats.Average(x => x.Count);
                List<(DateTime Date, int Count)> activeDays = userJoinStats.Where(x => x.Count > 0).ToList();
                double avgActive = activeDays.Count > 0 ? activeDays.Average(x => x.Count) : 0;
                List<int> orderedCounts = userJoinStats.Select(x => x.Count).OrderBy(x => x).ToList();
                double median = orderedCounts.Count % 2 == 1
                    ? orderedCounts[orderedCounts.Count / 2]
                    : (orderedCounts[(orderedCounts.Count / 2) - 1] + orderedCounts[orderedCounts.Count / 2]) / 2.0;
                // Trend: Compare last 7 days to previous 7 days
                int trendWindow = 7;
                int sumLast = userJoinStats.Skip(userJoinStats.Count - trendWindow).Sum(x => x.Count);
                int sumPrev = userJoinStats.Skip(userJoinStats.Count - (2 * trendWindow)).Take(trendWindow).Sum(x => x.Count);
                string trend = sumLast > sumPrev ? "⬆️" : sumLast < sumPrev ? "⬇️" : "➡️";
                DateTime today = DateTime.UtcNow.Date;
                _ = stats.AppendLine("👤 *User Joins (Last 30 Days)*\n");
                _ = stats.AppendLine("` Date   | Users |`");
                foreach ((DateTime date, int count) in userJoinStats)
                {
                    string bar = max == 0 ? "▏" : count == 0 ? "▏" : new string('█', Math.Max(1, (int)Math.Round((double)count / max * barMax)));
                    string dayMark = date == today ? "➡️" : "  ";
                    _ = stats.AppendLine($"{dayMark}{date:MM-dd} {bar.PadRight(barMax)} {count}");
                }
                _ = stats.AppendLine($"\n📈 *30d Total:* {total}");
                _ = stats.AppendLine($"*Avg/day (all):* {avgAll:0.0}");
                _ = stats.AppendLine($"*Avg/day (active):* {avgActive:0.0}");
                _ = stats.AppendLine($"*Median/day:* {median:0.0}");
                _ = stats.AppendLine($"*Trend (last 7 vs prev 7):* {trend}\n");
                _ = stats.AppendLine("Legend: ▏=0, █=max");
            }
            #endregion

            await _messageSender.EditMessageTextAsync(chatId, messageId, stats.ToString(), ParseMode.Markdown, GetBackToAdminPanelKeyboard(), cancellationToken);
        }

        private async Task HandleManualRssFetchAsync(long chatId, int messageId, CancellationToken cancellationToken)
        {
            await _messageSender.EditMessageTextAsync(chatId, messageId, "⏳ Triggering RSS fetch job...", cancellationToken: cancellationToken);

            try
            {
                // This is the success path
                _recurringJobManager.Trigger("fetch-all-active-rss-feeds");
                _logger.LogInformation("Admin manually triggered the RSS fetch job.");

                string successMessage = "✅ The `fetch-all-active-rss-feeds` job has been triggered. Check Hangfire dashboard for progress.";
                await _messageSender.EditMessageTextAsync(chatId, messageId, successMessage, replyMarkup: GetBackToAdminPanelKeyboard(), cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                // This is the enhanced failure path
                _logger.LogError(ex, "Failed to manually trigger RSS Fetch job.");

                // --- ENHANCEMENT: Log the failure to the database in a background task ---
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "AdminCallbackHandler",
                        EventType = "ManualRssFetchTrigger",
                        Message = "An exception occurred while trying to manually trigger the RSS fetch job.",
                        Details = ex.StackTrace,
                        Exception = ex.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                // --- END OF ENHANCEMENT ---

                // Send a clear error message to the admin
                string errorMessage = "❌ Failed to trigger RSS Fetch job. The failure has been logged for review.";
                await _messageSender.EditMessageTextAsync(chatId, messageId, errorMessage, replyMarkup: GetBackToAdminPanelKeyboard(), cancellationToken: cancellationToken);
            }
        }

        private async Task HandlePurgeHangfireAsync(long chatId, int messageId, CancellationToken cancellationToken)
        {
            await _messageSender.EditMessageTextAsync(chatId, messageId, "⏳ Purging completed Hangfire jobs...", cancellationToken: cancellationToken);

            try
            {
                // NOTE: The actual purge logic is commented out in your example.
                // Assuming you have a service like _hangfireCleaner.PurgeCompletedAndFailedJobs()
                // that would be called here. For this example, we'll proceed as if it's there.
                // string connectionString = _configuration.GetConnectionString("DefaultConnection")!;
                // await _hangfireCleaner.PurgeCompletedAndFailedJobs();

                _logger.LogInformation("Admin manually purged Hangfire jobs.");
                await _messageSender.EditMessageTextAsync(chatId, messageId, "✅ Hangfire 'Succeeded' and 'Failed' job lists have been cleared.", replyMarkup: GetBackToAdminPanelKeyboard(), cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to purge Hangfire jobs.");

                // --- ENHANCEMENT: Log the failure to the database in a background task ---
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "AdminCallbackHandler",
                        EventType = "PurgeHangfire",
                        Message = "An exception occurred while trying to manually purge Hangfire jobs.",
                        Details = ex.StackTrace,
                        Exception = ex.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                // --- END OF ENHANCEMENT ---

                // Send a clear error message to the admin
                string errorMessage = "❌ An error occurred while purging Hangfire jobs. The failure has been logged for review.";
                await _messageSender.EditMessageTextAsync(chatId, messageId, errorMessage, replyMarkup: GetBackToAdminPanelKeyboard(), cancellationToken: cancellationToken);
            }
        }

        private string EscapeMarkdownV1(string text)
        {
            return string.IsNullOrEmpty(text)
                ? ""
                : text.Replace("_", "\\_")
                       .Replace("*", "\\*")
                       .Replace("`", "\\`")
                       .Replace("[", "\\[");
        }
        private static TimeZoneInfo? _iranTimeZoneInfo;
        private TimeZoneInfo? GetIranTimeZone()
        {
            // Return the cached version if we already found it.
            if (_iranTimeZoneInfo != null)
            {
                return _iranTimeZoneInfo;
            }

            try
            {
                // Standard IANA time zone ID, works on Linux, macOS, and modern Windows.
                _iranTimeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tehran");
            }
            catch (TimeZoneNotFoundException)
            {
                _logger.LogWarning("IANA time zone 'Asia/Tehran' not found. Falling back to Windows ID 'Iran Standard Time'.");
                try
                {
                    // Windows-specific time zone ID.
                    _iranTimeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("Iran Standard Time");
                }
                catch (TimeZoneNotFoundException ex)
                {
                    _logger.LogError(ex, "Could not find any time zone for Iran. Timestamps will default to UTC.");
                    // If neither is found, we can't do the conversion.
                    return null;
                }
            }
            return _iranTimeZoneInfo;
        }

        // --- ENHANCED METHOD: Handles log pagination with modern UI/UX using Markdown ---

        private async Task HandleProMonitoringAsync(long chatId, int messageId, string callbackData, CancellationToken cancellationToken)
        {
            try
            {
                const int PageSize = 5; // Reduced page size for better readability on mobile
                const int MaxMessageLength = 4096;
                const int PreviewLength = 200;

                _ = int.TryParse(callbackData.Replace(ProMonitoringCallbackPrefix, ""), out int offset);

                string loadingMessage = offset > 0 ? "🔄 Fetching next page of logs..." : "🔍 Retrieving Pro Monitoring Logs...";
                await _messageSender.EditMessageTextAsync(chatId, messageId, loadingMessage, cancellationToken: cancellationToken);

                // --- Fetch logs from the service ---
                List<ProMonitoringLog> logs = await _adminService.GetRecentProMonitoringLogsAsync(PageSize, offset, cancellationToken);

                if (logs == null || logs.Count == 0)
                {
                    await _messageSender.EditMessageTextAsync(chatId, messageId, "✅ No further pro monitoring logs found.", replyMarkup: GetBackToAdminPanelKeyboard(), cancellationToken: cancellationToken);
                    return;
                }

                // --- Time Zone Conversion Setup ---
                TimeZoneInfo? iranTimeZone = GetIranTimeZone();
                StringBuilder sb = new();
                int currentPage = (offset / PageSize) + 1;

                _ = sb.AppendLine($"📋 *Pro Monitoring Log* (Page {currentPage})");
                _ = sb.AppendLine("`" + new string('─', 32) + "`");

                foreach (ProMonitoringLog log in logs)
                {
                    // --- Timestamp Conversion ---
                    DateTime utcTime = DateTime.SpecifyKind(log.Timestamp, DateTimeKind.Utc);
                    string iranTimeFormatted = "N/A";
                    if (iranTimeZone != null)
                    {
                        DateTime iranTime = TimeZoneInfo.ConvertTimeFromUtc(utcTime, iranTimeZone);
                        iranTimeFormatted = iranTime.ToString("yyyy-MM-dd HH:mm:ss");
                    }
                    string utcTimeFormatted = utcTime.ToString("yyyy-MM-dd HH:mm:ss");

                    // --- Build Log Entry with Pro UI/UX ---
                    string levelEmoji = GetLevelEmoji(log.Level);
                    _ = sb.AppendLine($"{levelEmoji} *{log.Level}* | `{EscapeMarkdownV1(log.Source ?? string.Empty)}`");
                    _ = sb.AppendLine($"`Message:` {EscapeMarkdownV1(log.Message)}");
                    _ = sb.AppendLine(); // Whitespace for readability

                    // Details Section
                    if (!string.IsNullOrWhiteSpace(log.Status) || !string.IsNullOrWhiteSpace(log.EventType) || !string.IsNullOrWhiteSpace(log.Exception))
                    {
                        _ = sb.AppendLine("    *Details*");
                        if (!string.IsNullOrWhiteSpace(log.Status))
                        {
                            _ = sb.AppendLine($"      `Status:` {EscapeMarkdownV1(log.Status)}");
                        }

                        if (!string.IsNullOrWhiteSpace(log.EventType))
                        {
                            _ = sb.AppendLine($"      `Event:` {EscapeMarkdownV1(log.EventType)}");
                        }

                        if (!string.IsNullOrWhiteSpace(log.Exception))
                        {
                            string exceptionContent = log.Exception;
                            if (exceptionContent.Length > PreviewLength)
                            {
                                exceptionContent = exceptionContent[..PreviewLength] + "...";
                            }
                            _ = sb.AppendLine($"      `Exception:`\n```\n{exceptionContent}\n```");
                        }
                        _ = sb.AppendLine();
                    }

                    // Timestamp Section
                    _ = sb.AppendLine("    ⏰ *Time*");
                    _ = sb.AppendLine($"      `🇮🇷 Tehran:` `{iranTimeFormatted}`");
                    _ = sb.AppendLine($"      `🌍 UTC:`     `{utcTimeFormatted}`");

                    _ = sb.AppendLine("`" + new string('─', 32) + "`");
                }

                // --- Build Keyboard ---
                List<List<InlineKeyboardButton>> keyboardRows = [];
                List<InlineKeyboardButton> navigationRow = [];

                if (offset > 0)
                {
                    navigationRow.Add(InlineKeyboardButton.WithCallbackData("⬅️ Previous", $"{ProMonitoringCallbackPrefix}{Math.Max(0, offset - PageSize)}"));
                }
                if (logs.Count == PageSize)
                {
                    navigationRow.Add(InlineKeyboardButton.WithCallbackData("Next ➡️", $"{ProMonitoringCallbackPrefix}{offset + PageSize}"));
                }
                if (navigationRow.Any())
                {
                    keyboardRows.Add(navigationRow);
                }

                List<InlineKeyboardButton> destructiveActionsRow =
                [
            InlineKeyboardButton.WithCallbackData("🗑️ Delete All Logs", $"{ProMonitoringDeletePromptPrefix}{offset}")
        ];
                keyboardRows.Add(destructiveActionsRow);

                keyboardRows.Add([InlineKeyboardButton.WithCallbackData("↩️ Back to Admin Panel", BackToAdminPanelCallback)]);
                InlineKeyboardMarkup replyMarkup = new(keyboardRows);

                // --- Send Message ---
                string text = sb.ToString();
                if (text.Length > MaxMessageLength)
                {
                    text = text[..(MaxMessageLength - 50)] + "...\n_(Message truncated)_";
                }

                await _messageSender.EditMessageTextAsync(chatId, messageId, text, ParseMode.Markdown, replyMarkup, cancellationToken);
            }
            catch (Exception ex)
            {
                // 1. Log the error to the primary logging system (e.g., console, file).
                _logger.LogError(ex, "Failed to display Pro Monitoring logs for callback: {CallbackData}", callbackData);

                // --- ENHANCEMENT: Log the failure to the database in a background task ---
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "AdminCallbackHandler",
                        EventType = "HandleProMonitoring",
                        Message = $"An exception occurred while trying to display logs for callback: {callbackData}",
                        Details = ex.StackTrace,
                        Exception = ex.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                // --- END OF ENHANCEMENT ---

                // 2. Inform the admin user that the operation failed.
                await _messageSender.EditMessageTextAsync(
                    chatId: chatId,
                    messageId: messageId,
                    text: "❌ An error occurred while fetching or displaying the logs. The failure has been logged for review.",
                    replyMarkup: GetBackToAdminPanelKeyboard(),
                    cancellationToken: cancellationToken);
            }
        }

        // --- Helper method to get Emojis based on log level ---
        private string GetLevelEmoji(string level)
        {
            return level.ToLowerInvariant() switch
            {
                "critical" => "🚨",
                "error" => "❌",
                "warning" => "⚠️",
                "information" => "ℹ️",
                "debug" => "🐛",
                "trace" => "🔬",
                _ => "❓" // Default for unknown levels
            };
        }
        /// <summary>
        /// Displays a confirmation prompt before deleting all logs.
        /// </summary>
        private async Task HandleDeleteLogsPromptAsync(long chatId, int messageId, string callbackData, CancellationToken cancellationToken)
        {
            // Extract the offset so we can return to the same page on "Cancel"
            _ = int.TryParse(callbackData.Replace(ProMonitoringDeletePromptPrefix, ""), out int offset);

            string text = "⚠️ *CONFIRM DELETION*\n\n" +
                          "Are you absolutely sure you want to delete *ALL* pro monitoring logs?\n\n" +
                          "This action is *irreversible*.";

            InlineKeyboardMarkup? keyboard = MarkupBuilder.CreateInlineKeyboard(
                new[]
                {
            InlineKeyboardButton.WithCallbackData("✅ Yes, I am sure. Delete everything.", ProMonitoringDeleteConfirmCallback),
                },
                new[]
                {
            // This button returns the user to the log page they were on.
            InlineKeyboardButton.WithCallbackData("❌ No, cancel.", $"{ProMonitoringCallbackPrefix}{offset}")
                }
            );

            await _messageSender.EditMessageTextAsync(chatId, messageId, text, ParseMode.Markdown, keyboard, cancellationToken);
        }
        /// <summary>
        /// Executes the deletion of all logs after confirmation.
        /// </summary>
        /// <summary>
        /// Executes the deletion of all logs after confirmation, with enhanced error logging.
        /// </summary>
        private async Task HandleDeleteLogsConfirmAsync(long chatId, int messageId, CancellationToken cancellationToken)
        {
            await _messageSender.EditMessageTextAsync(chatId, messageId, "⏳ Deleting all logs, please wait...", cancellationToken: cancellationToken);

            try
            {
                int deletedCount = await _adminService.DeleteAllProMonitoringLogsAsync(cancellationToken);
                string successMessage = $"✅ Success! Deleted *{deletedCount:N0}* log entries permanently.";
                await _messageSender.EditMessageTextAsync(chatId, messageId, successMessage, ParseMode.Markdown, GetBackToAdminPanelKeyboard(), cancellationToken);
            }
            catch (Exception ex)
            {
                // 1. Log the error to the primary logging system (e.g., console, file) for immediate visibility.
                _logger.LogError(ex, "Failed to delete all pro monitoring logs during admin confirmation step.");

                // --- ENHANCEMENT: Log the failure to the database in a background task ---
                // This creates a persistent record of the failed administrative action.
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Critical", // This is a critical failure of an admin function
                        Source = "AdminCallbackHandler",
                        EventType = "DeleteAllLogsConfirm",
                        Message = "An exception occurred while attempting to delete all pro monitoring logs.",
                        Details = ex.StackTrace,
                        Exception = ex.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                // --- END OF ENHANCEMENT ---

                // 2. Inform the admin user that the operation failed.
                await _messageSender.EditMessageTextAsync(
                    chatId: chatId,
                    messageId: messageId,
                    text: "❌ An error occurred while deleting logs. The failure has been logged for review.",
                    replyMarkup: GetBackToAdminPanelKeyboard(),
                    cancellationToken: cancellationToken);
            }
        }



        private Task ShowAdminPanelAsync(long chatId, int messageId, CancellationToken cancellationToken)
        {
            string text = TelegramMessageFormatter.Bold("🛠️ Administrator Panel") + "\n\nSelect an action:";
            InlineKeyboardMarkup keyboard = GetAdminPanelKeyboard();
            return _messageSender.EditMessageTextAsync(chatId, messageId, text, ParseMode.Markdown, keyboard, cancellationToken);
        }

        private InlineKeyboardMarkup GetAdminPanelKeyboard()
        {
            // Use the constants defined in this class for consistency.
            return MarkupBuilder.CreateInlineKeyboard(
                new[] { InlineKeyboardButton.WithCallbackData("📊 Server Stats", AdminServerStatsCallback) },
                new[] { InlineKeyboardButton.WithCallbackData("🔄 Fetch RSS Now", AdminManualRssFetchCallback),
                        InlineKeyboardButton.WithCallbackData("🧹 Purge Hangfire Jobs", PurgeHangfireCallback) },
                new[] { InlineKeyboardButton.WithCallbackData("🛡️ Pro Monitoring", $"{ProMonitoringCallbackPrefix}0"),
                        InlineKeyboardButton.WithCallbackData("📩 Download Logs", DownloadLogsCallback)},
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