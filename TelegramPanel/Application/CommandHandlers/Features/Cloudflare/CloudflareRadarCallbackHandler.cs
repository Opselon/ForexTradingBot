using Application.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Linq;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.CommandHandlers.MainMenu;
using TelegramPanel.Application.Interfaces; // For ITelegramMessageSender
using TelegramPanel.Formatters;
using TelegramPanel.Infrastructure; // For IActualTelegramMessageActions
using TelegramPanel.Infrastructure.Helper;
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions; // For CountryHelper

namespace TelegramPanel.Application.CommandHandlers.Features.Cloudflare
{
    /// <summary>
    /// Handles all callback queries related to the Cloudflare Radar feature AFTER the initial menu entry.
    /// This includes displaying country lists, handling pagination, and showing detailed country reports.
    /// </summary>
    public class CloudflareRadarCallbackHandler : ITelegramCallbackQueryHandler
    {
        // --- PUBLIC PROPERTY TO EXPOSE INTERNAL SENDER FOR INITIATION HANDLER ---
        // This allows CloudflareRadarInitiationHandler to answer the initial callback.
        public ITelegramMessageSender MessageSender => _messageSender;
        // --- END PUBLIC EXPOSURE ---

        private readonly ITelegramMessageSender _messageSender; // Used for general queued messages and initial ACK via public property
        private readonly ILogger<CloudflareRadarCallbackHandler> _logger;
        private readonly ICloudflareRadarService _radarService;
        private readonly IActualTelegramMessageActions _directMessageSender; // Used for immediate, non-queued Telegram API calls (e.g., animations, photo sends)

        // Callback Data Constants
        private const string CallbackPrefix = "cf_radar";
        private const string ListPageAction = "list_page";
        private const string SelectCountryAction = "select_country";
        private const int CountriesPerPage = 12; // Number of countries to show per page in the selection menu

        /// <summary>
        /// Constructor for CloudflareRadarCallbackHandler. Dependencies are injected via DI.
        /// </summary>
        public CloudflareRadarCallbackHandler(
            ITelegramMessageSender messageSender, // Main sender for queued operations
            ILogger<CloudflareRadarCallbackHandler> logger,
            ICloudflareRadarService radarService,
            IActualTelegramMessageActions directMessageSender) // Direct sender for immediate operations
        {
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _radarService = radarService ?? throw new ArgumentNullException(nameof(radarService));
            _directMessageSender = directMessageSender ?? throw new ArgumentNullException(nameof(directMessageSender));
        }

        /// <summary>
        /// Determines if this handler can process the incoming Telegram update.
        /// It handles any callback query that starts with the defined CallbackPrefix.
        /// </summary>
        public bool CanHandle(Update update)
        {
            return update.Type == UpdateType.CallbackQuery &&
                   update.CallbackQuery?.Data?.StartsWith(CallbackPrefix) == true;
        }

        /// <summary>
        /// Handles the incoming callback query. This is the main dispatch method for the feature.
        /// It parses the callback data and dispatches to the appropriate sub-handler method.
        /// </summary>
        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            var callbackQuery = update.CallbackQuery!; // Guaranteed non-null by CanHandle
            var chatId = callbackQuery.Message!.Chat.Id; // Guaranteed non-null by CanHandle
            var messageId = callbackQuery.Message.MessageId; // Guaranteed non-null by CanHandle

            // Acknowledge the callback query immediately to remove the loading spinner from the button.
            // This can be done via the main _messageSender, as it's a quick operation.
            await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);

            var parts = callbackQuery.Data!.Split(':'); // Guaranteed non-null by CanHandle
            var action = parts[1]; // The action identifier (e.g., "list_page", "select_country")
            var payload = parts.Length > 2 ? parts[2] : null; // The associated payload (e.g., page number, country code)

            _logger.LogInformation("CloudflareRadarCallbackHandler: Handling action '{Action}' with payload '{Payload}' for Chat {ChatId}", action, payload, chatId);

            switch (action)
            {
                case ListPageAction when int.TryParse(payload, out int page):
                    await ShowCountrySelectionMenuAsync(chatId, messageId, page, cancellationToken);
                    break;
                case SelectCountryAction when !string.IsNullOrEmpty(payload):
                    await ShowCountryReportAsync(chatId, messageId, payload, cancellationToken);
                    break;
                default:
                    _logger.LogWarning("CloudflareRadarCallbackHandler: Unhandled action '{Action}' received for Chat {ChatId}.", action, chatId);
                    // Optionally, inform the user about an unhandled action.
                    await _directMessageSender.EditMessageTextDirectAsync(
                        chatId, messageId, "Sorry, this action is not recognized.", ParseMode.Markdown, null, cancellationToken);
                    break;
            }
        }

        /// <summary>
        /// Displays the country selection menu, allowing users to choose a country for a report.
        /// Supports pagination for a large list of countries.
        /// </summary>
        // Making this method public as it's called by the InitiationHandler.
        public async Task ShowCountrySelectionMenuAsync(long chatId, int messageId, int page, CancellationToken cancellationToken)
        {
            var text = "☁️ *Cloudflare Radar*\n\nSelect a country to view its internet health report.";
            var totalCountries = CountryHelper.AllCountries.Count;
            var totalPages = (int)Math.Ceiling((double)totalCountries / CountriesPerPage);
            page = Math.Clamp(page, 1, totalPages); // Ensure page number is within valid bounds.

            // Get the subset of countries for the current page.
            var countriesToShow = CountryHelper.AllCountries
                .Skip((page - 1) * CountriesPerPage)
                .Take(CountriesPerPage)
                .ToList();

            // Build dynamic button rows (3 buttons per row).
            var buttonRows = new List<List<InlineKeyboardButton>>();
            for (int i = 0; i < countriesToShow.Count; i += 3)
            {
                buttonRows.Add(countriesToShow.Skip(i).Take(3)
                    .Select(c => InlineKeyboardButton.WithCallbackData(c.Name, $"{CallbackPrefix}:{SelectCountryAction}:{c.Code}"))
                    .ToList());
            }

            // Add pagination buttons (Prev/Page X of Y/Next).
            var navRow = new List<InlineKeyboardButton>();
            if (page > 1)
            {
                navRow.Add(InlineKeyboardButton.WithCallbackData("⬅️ Prev", $"{CallbackPrefix}:{ListPageAction}:{page - 1}"));
            }
            navRow.Add(InlineKeyboardButton.WithCallbackData($"Page {page}/{totalPages}", "noop")); // "noop" prevents this button from doing anything.
            if (page < totalPages)
            {
                navRow.Add(InlineKeyboardButton.WithCallbackData("Next ➡️", $"{CallbackPrefix}:{ListPageAction}:{page + 1}"));
            }
            buttonRows.Add(navRow);

            // Add a "Back to Main Menu" button.
            buttonRows.Add([InlineKeyboardButton.WithCallbackData("🏠 Main Menu", MenuCommandHandler.BackToMainMenuGeneral)]);

            var keyboard = new InlineKeyboardMarkup(buttonRows);

            // Edit the existing message with the new text and keyboard.
            // Using _directMessageSender for immediate UI updates that bypass Hangfire.
            await _directMessageSender.EditMessageTextDirectAsync(chatId, messageId, text, ParseMode.Markdown, keyboard, cancellationToken);
            _logger.LogInformation("Sent country selection menu (Page {Page}) to Chat {ChatId}", page, chatId);
        }

        /// <summary>
        /// Fetches and displays a detailed Cloudflare Radar report for a selected country.
        /// Uses an animation during fetching and sends the report as a photo with a caption.
        /// </summary>
        private async Task ShowCountryReportAsync(long chatId, int messageId, string countryCode, CancellationToken cancellationToken)
        {
            var countryName = CountryHelper.AllCountries.FirstOrDefault(c => c.Code == countryCode).Name ?? countryCode;
            var safeCountryName = TelegramMessageFormatter.EscapeMarkdownV2(countryName); // Escape for MarkdownV2 formatting.

            try
            {
                // Execute the API call with an animation to provide user feedback during the wait.
                var reportResult = await AnimateWhileExecutingAsync(chatId, messageId,
                    $"☁️ Analyzing internet health for *{safeCountryName}*",
                    ct => _radarService.GetCountryReportAsync(countryCode, ct),
                    cancellationToken);

                if (reportResult.Succeeded && reportResult.Data != null)
                {
                    var data = reportResult.Data;
                    var caption = FormatCountryReportMessage(data); // Format the detailed report text.

                    // CORRECTED: Explicitly use List<List<InlineKeyboardButton>> for InlineKeyboardMarkup
                    // to avoid Hangfire deserialization issues (ArgumentException).
                    var keyboard = new InlineKeyboardMarkup(new List<List<InlineKeyboardButton>>
                    {
                        // Button to view the full report on Cloudflare Radar's website.
                        new List<InlineKeyboardButton> { InlineKeyboardButton.WithUrl("View Full Report on Cloudflare Radar ↗️", data.RadarUrl) },
                        // Button to go back to the country list for more selections.
                        new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("⬅️ Back to Country List", $"{CallbackPrefix}:{ListPageAction}:1") }
                    });

                    // Delete the "Analyzing..." message to make way for the new photo message.
                    await _directMessageSender.DeleteMessageAsync(chatId, messageId, cancellationToken);

                    // Send the report image with the detailed caption and keyboard.
                    // This call is now correctly using the _messageSender (which uses Hangfire for photo sends)
                    // and passes the URL string directly.
                    await _messageSender.SendPhotoAsync(
                        chatId: chatId,
                        photoUrlOrFileId: data.ReportImageUrl, // The URL string for the image.
                        caption: caption,
                        parseMode: ParseMode.MarkdownV2, // Use MarkdownV2 for the caption.
                        replyMarkup: keyboard,
                        cancellationToken: cancellationToken);

                    _logger.LogInformation("Sent Cloudflare Radar report for {CountryCode} to Chat {ChatId}.", countryCode, chatId);
                }
                else
                {
                    // Handle cases where API call fails or data is unavailable.
                    var errorText = $"❌ Could not retrieve report for *{safeCountryName}*.\n`{TelegramMessageFormatter.EscapeMarkdownV2(reportResult.Errors.FirstOrDefault() ?? "Unknown error.")}`";
                    var keyboard = new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("⬅️ Back to Countries", $"{CallbackPrefix}:{ListPageAction}:1"));

                    // Use direct sender for immediate error feedback.
                    await _directMessageSender.EditMessageTextDirectAsync(
                        chatId, messageId, errorText, ParseMode.MarkdownV2, keyboard, cancellationToken);
                    _logger.LogWarning("Failed to retrieve report for {CountryCode} for Chat {ChatId}. Error: {Error}", countryCode, chatId, reportResult.Errors.FirstOrDefault());
                }
            }
            catch (Exception ex)
            {
                // Catch any unexpected exceptions during the process.
                _logger.LogError(ex, "An unexpected error occurred while showing Cloudflare report for {CountryCode} to Chat {ChatId}.", countryCode, chatId);
                var keyboard = new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("⬅️ Try Again", $"{CallbackPrefix}:{SelectCountryAction}:{countryCode}"));

                // Provide a generic error message to the user.
                await _directMessageSender.EditMessageTextDirectAsync(
                    chatId, messageId, "An unexpected error occurred. Please try again.", ParseMode.MarkdownV2, replyMarkup: keyboard, cancellationToken: CancellationToken.None);
            }
        }

        /// <summary>
        /// Generates a visual progress bar string based on a percentage value.
        /// </summary>
        private string GenerateProgressBar(double percentage, int size = 10, string filled = "🟩", string empty = "⬜️")
        {
            if (percentage < 0) percentage = 0;
            if (percentage > 100) percentage = 100;

            int filledBlocks = (int)Math.Round(percentage / 100.0 * size);
            int emptyBlocks = size - filledBlocks;

            return string.Concat(Enumerable.Repeat(filled, filledBlocks)) + string.Concat(Enumerable.Repeat(empty, emptyBlocks));
        }

        /// <summary>
        /// Formats the Cloudflare country report data into a human-readable MarkdownV2 message.
        /// Includes extensive null-checking to gracefully handle missing data points.
        /// </summary>
        private string FormatCountryReportMessage(CloudflareCountryReportDto data)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"☁️ *Internet Report: {TelegramMessageFormatter.EscapeMarkdownV2(data.CountryName)}*");
            sb.AppendLine("`-----------------------------------`");

            // Section 1: Internet Quality Index (IQI)
            if (data.InternetQuality != null)
            {
                var iqi = data.InternetQuality;
                var iqiEmoji = iqi.Rating.ToLowerInvariant() switch
                {
                    "excellent" => "✅",
                    "good" => "🟢",
                    "fair" => "🟡",
                    _ => "⚠️" // Default or "poor"
                };
                sb.AppendLine($"*{iqiEmoji} Internet Quality Index (IQI)*");
                sb.AppendLine($"  - Rating: `{TelegramMessageFormatter.EscapeMarkdownV2(iqi.Rating)}`");
                sb.AppendLine($"  - Score: `{iqi.Value:F2}` / 100");
                sb.AppendLine($"    `{GenerateProgressBar(iqi.Value, 10, "🔵", "⚪️")}`");
            }
            else { sb.AppendLine("*⚠️ Quality Index:* `Data Unavailable`"); }
            sb.AppendLine();

            // Section 2: Traffic Anomalies
            if (data.TrafficAnomalies != null)
            {
                var traffic = data.TrafficAnomalies;
                var emoji = traffic.PercentageChange > 0 ? "📈" : "📉"; // Using PercentageChange here
                sb.AppendLine($"*{emoji} Traffic Anomalies (48h)*");
                sb.AppendLine($"  - Trend: `{TelegramMessageFormatter.EscapeMarkdownV2(traffic.ChangeDirection)}`");
                sb.AppendLine($"  - Change: `{traffic.PercentageChange:+0.00;-0.00;0.00}%`");
            }
            else { sb.AppendLine("*⚠️ Traffic Anomalies:* `Data Unavailable`"); }
            sb.AppendLine();

            // Section 3: Bot vs Human Traffic
            if (data.BotVsHumanTraffic != null)
            {
                var bots = data.BotVsHumanTraffic;
                sb.AppendLine($"*👥 Traffic Source (24h)*");
                sb.AppendLine($"  - 👨‍💻 Humans: `{bots.Human:F1}%`");
                sb.AppendLine($"    `{GenerateProgressBar(bots.Human)}`");
                sb.AppendLine($"  - 🤖 Bots: `{bots.Bot:F1}%`");
            }
            else { sb.AppendLine("*⚠️ Traffic Source:* `Data Unavailable`"); }
            sb.AppendLine();

            // Section 4: HTTP Protocol Adoption
            if (data.HttpProtocolDistribution != null)
            {
                var http = data.HttpProtocolDistribution;
                sb.AppendLine($"*🚀 HTTP Protocol Adoption*");
                sb.AppendLine($"  - HTTP/3: `{http.Http3:F1}%`");
                sb.AppendLine($"  - HTTP/2: `{http.Http2:F1}%`");
                // Progress bar for combined adoption of HTTP/2 and HTTP/3
                sb.AppendLine($"    `{GenerateProgressBar(http.Http3 + http.Http2)}`");
            }
            else { sb.AppendLine("*⚠️ HTTP Adoption:* `Data Unavailable`"); }
            sb.AppendLine();

            // Section 5: Device Types
            if (data.DeviceTypeDistribution != null)
            {
                var devices = data.DeviceTypeDistribution;
                sb.AppendLine($"*💻 Device Types (24h)*");
                sb.AppendLine($"  - Desktop: `{devices.Desktop:F1}%`");
                sb.AppendLine($"  - Mobile: `{devices.Mobile:F1}%`");
            }
            else { sb.AppendLine("*⚠️ Device Types:* `Data Unavailable`"); }
            sb.AppendLine();

            // Section 6: DDoS Attack Source
            if (data.Layer7Attacks != null)
            {
                var attack = data.Layer7Attacks;
                sb.AppendLine($"*🛡️ L7 DDoS Attack Source (7d)*");
                sb.AppendLine($"   - Top Origin: `{TelegramMessageFormatter.EscapeMarkdownV2(attack.TopSourceCountry)}`");
                sb.AppendLine($"   - Share of Total: `{attack.PercentageOfTotal:F1}%`");
            }
            else { sb.AppendLine("*⚠️ Attack Source:* `Data Unavailable`"); }

            sb.AppendLine("\n`-----------------------------------`");
            sb.AppendLine($"_Data from Cloudflare Radar as of {DateTime.UtcNow:MMM dd, HH:mm} UTC._");

            return sb.ToString();
        }

        /// <summary>
        /// Executes a long-running asynchronous operation while concurrently displaying an animated loading message.
        /// This method ensures the animation updates reliably while the data is being fetched.
        /// </summary>
        private async Task<TResult> AnimateWhileExecutingAsync<TResult>(long chatId, int messageId, string baseText, Func<CancellationToken, Task<TResult>> operationToExecute, CancellationToken cancellationToken)
        {
            var animationFrames = new[] { "·", "··", "···" };
            var frameIndex = 0;

            // This task will be the long-running API call.
            var operationTask = operationToExecute(cancellationToken);

            // This task will run the UI animation.
            var animationTask = Task.Run(async () =>
            {
                // Loop until the main operation is complete or cancellation is requested.
                while (!operationTask.IsCompleted && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var text = $"{baseText} {animationFrames[frameIndex++ % animationFrames.Length]}";
                        // Use the direct sender for animation to avoid queueing.
                        await _directMessageSender.EditMessageTextDirectAsync(chatId, messageId, text, ParseMode.MarkdownV2, null, cancellationToken);
                    }
                    catch (ApiRequestException apiEx) when (apiEx.Message.Contains("not modified")) { /* Ignore, this is fine */ }
                    catch (OperationCanceledException) { break; /* Exit loop cleanly on cancellation */ }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Animation loop encountered an error for Chat {ChatId}, Message {MessageId}. Animation will stop.", chatId, messageId);
                        break; // Stop animation on unexpected error.
                    }

                    // Wait before the next animation frame.
                    // Using CancellationToken.None to not cancel this delay if the main token is canceled;
                    // the while condition will handle breaking the loop.
                    await Task.Delay(800, CancellationToken.None);
                }
            }, CancellationToken.None); // Run animation task independently from main cancellation token initially.

            try
            {
                // Await the main operation. This will complete first or throw an exception.
                return await operationTask;
            }
            finally
            {
                // Ensure the animation task is fully awaited and gracefully completes.
                // Added a small delay before checking to give the animation task a chance to update one last time
                // or react to the operationTask completion.
                await Task.Delay(100);
                if (!animationTask.IsCompleted)
                {
                    try { await animationTask; }
                    catch (Exception ex) { _logger.LogError(ex, "Animation task completed with error in finally block for Chat {ChatId}, Message {MessageId}.", chatId, messageId); }
                }
            }
        }
    }
}