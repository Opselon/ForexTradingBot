// File: TelegramPanel/Application/CommandHandlers/Features/EconomicCalendar/EconomicCalendarCallbackHandler.cs
using Application.Common.Interfaces.Fred;
using Microsoft.Extensions.Logging;
using Shared.Extensions;
using System.Text;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.CommandHandlers.MainMenu;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters;
using TelegramPanel.Infrastructure.Helper;
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions;

namespace TelegramPanel.Application.CommandHandlers.Features.EconomicCalendar
{
    /// <summary>
    /// Handles all callback queries related to the Economic Calendar feature,
    /// including displaying releases, pagination, and initiating the data series search state.
    /// </summary>
    public class EconomicCalendarCallbackHandler : ITelegramCallbackQueryHandler
    {
        // ... (Fields, Constructor, CanHandle, HandleAsync, and HandleReleasesViewAsync are the same) ...
        #region Constants & Private Fields
        private const int PageSize = 7;
        private const string ReleasesCallbackPrefix = "menu_econ_calendar";
        private const string SearchSeriesCallback = "econ_search_series";
        private const string ExploreReleasePrefix = "econ_explore";
        private readonly ILogger<EconomicCalendarCallbackHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly IEconomicCalendarService _calendarService;
        private readonly ITelegramStateMachine _stateMachine;
        #endregion

        #region Constructor
        public EconomicCalendarCallbackHandler(
            ILogger<EconomicCalendarCallbackHandler> logger,
            ITelegramMessageSender messageSender,
            IEconomicCalendarService calendarService,
            ITelegramStateMachine stateMachine)
        {
            _logger = logger;
            _messageSender = messageSender;
            _calendarService = calendarService;
            _stateMachine = stateMachine;
        }
        #endregion

        #region ITelegramCallbackQueryHandler Implementation
        /// <summary>
        /// Determines if this handler can process the callback query.
        /// </summary>
        public bool CanHandle(Update update)
        {
            return update.CallbackQuery?.Data?.StartsWith(ReleasesCallbackPrefix) == true ||
           update.CallbackQuery?.Data == SearchSeriesCallback ||

           update.CallbackQuery?.Data == MenuCommandHandler.BackToMainMenuGeneral;
        }

        private async Task HandleExploreReleaseAsync(CallbackQuery callbackQuery, CancellationToken cancellationToken)
        {
            long chatId = callbackQuery.Message!.Chat.Id;
            int messageId = callbackQuery.Message.MessageId;

            // Callback data format: "econ_explore:{releaseId}:{elementId}:{parentName}"
            string[] parts = callbackQuery.Data!.Split(':', 4);
            if (!int.TryParse(parts[1], out int releaseId) || !int.TryParse(parts[2], out int elementId))
            {
                return;
            }

            string parentName = parts.Length > 3 ? parts[3] : "Root";

            await _messageSender.EditMessageTextAsync(chatId, messageId, "Loading data tree...", cancellationToken: cancellationToken);

            Shared.Results.Result<FredReleaseTablesResponseDto> result = await _calendarService.GetReleaseTableTreeAsync(releaseId, elementId == 0 ? null : elementId, cancellationToken);

            if (!result.Succeeded || !result.Data.Elements.Any())
            {
                await _messageSender.EditMessageTextAsync(chatId, messageId, "❌ No data tables found for this release.", cancellationToken: cancellationToken);
                return;
            }

            StringBuilder sb = new();
            List<List<InlineKeyboardButton>> keyboardRows = new();

            FredReleaseTableElementDto currentElement = result.Data.Elements.First(); // The API returns the parent as the first element

            _ = sb.AppendLine("🗓️ *Release Explorer*");
            _ = sb.AppendLine($"`Path: {TelegramMessageFormatter.EscapeMarkdownV2(parentName)} > {TelegramMessageFormatter.EscapeMarkdownV2(currentElement.Name)}`");
            _ = sb.AppendLine();
            _ = sb.AppendLine("Select a category or data series below:");

            foreach (FredReleaseTableElementDto child in currentElement.Children)
            {
                // If it's a data series, show a different button
                if (child.Type == "series" && !string.IsNullOrWhiteSpace(child.SeriesId))
                {
                    string buttonText = $"📈 {child.Name}";
                    keyboardRows.Add([
                    InlineKeyboardButton.WithCallbackData(buttonText, $"series_details:{child.SeriesId}") // To be handled by another handler
                ]);
                }
                // If it's a group with more children, allow further drilling
                else if (child.Type == "group" && child.Children.Any())
                {
                    string buttonText = $"📂 {child.Name}";
                    keyboardRows.Add([
                    InlineKeyboardButton.WithCallbackData(buttonText, $"{ExploreReleasePrefix}:{child.ReleaseId}:{child.ElementId}:{currentElement.Name}")
                ]);
                }
            }

            // Add "Back" button
            if (currentElement.ParentId != 0)
            {
                keyboardRows.Add([
                InlineKeyboardButton.WithCallbackData("⬅️ Back", $"{ExploreReleasePrefix}:{currentElement.ReleaseId}:{currentElement.ParentId}:{parentName}")
            ]);
            }
            else
            {
                keyboardRows.Add([
                InlineKeyboardButton.WithCallbackData("⬅️ Back to All Releases", $"{ReleasesCallbackPrefix}:1")
            ]);
            }

            await _messageSender.EditMessageTextAsync(chatId, messageId, sb.ToString(), ParseMode.MarkdownV2, new InlineKeyboardMarkup(keyboardRows), cancellationToken);
        }


        /// <summary>
        /// Asynchronously handles the incoming callback query by routing it to the appropriate method.
        /// </summary>
        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            CallbackQuery? callbackQuery = update.CallbackQuery;
            if (callbackQuery?.Message == null)
            {
                _logger.LogWarning("EconomicCalendarCallbackHandler received an update without a valid CallbackQuery or Message.");
                return;
            }

            try
            {
                await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);

                string data = callbackQuery.Data!;

                if (data.StartsWith(SearchSeriesCallback))
                {
                    await HandleSearchSeriesInitiationAsync(callbackQuery, cancellationToken);
                }
                else if (data.StartsWith(ReleasesCallbackPrefix))
                {
                    await HandleReleasesViewAsync(callbackQuery, cancellationToken);
                }
                else if (data.StartsWith(ExploreReleasePrefix))
                {
                    await HandleExploreReleaseAsync(callbackQuery, cancellationToken);
                }
                else if (data == MenuCommandHandler.AnalysisCallbackData) // Assuming you have Main Menu
                {
                    _logger.LogInformation("Back to Menu button pressed from FredSearch. UserID: {UserId}", update.CallbackQuery.From.Id);
                    // Clear any user state if necessary.
                    await _stateMachine.ClearStateAsync(update.CallbackQuery.From.Id, cancellationToken);

                    // Set the state to the main menu:
                    await _stateMachine.SetStateAsync(update.CallbackQuery.From.Id, "MainMenuState", update, cancellationToken);  // Replace "MainMenuState" with the actual state name.

                    // Send Main Menu
                    // (Assuming you have a method like this)
                    // await SendMainMenu(update.CallbackQuery.Message.Chat.Id, cancellationToken); // Send the menu
                    return; // Important:  Exit the handler here
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred in EconomicCalendarCallbackHandler for UpdateID {UpdateId}.", update.Id);
                // Inform the user of the error
                long? chatId = callbackQuery.Message?.Chat?.Id;
                if (chatId.HasValue)
                {
                    await _messageSender.SendTextMessageAsync(chatId.Value, "An unexpected error occurred. Please try again later.", cancellationToken: cancellationToken);
                }
            }
        }
        #endregion

        #region Private Handler Methods



        /// <summary>
        /// Handles the request to view the list of economic releases with pagination.
        /// This method retrieves and displays economic release data,
        /// including impact levels, links, and pagination controls.
        /// </summary>
        private async Task HandleReleasesViewAsync(CallbackQuery callbackQuery, CancellationToken cancellationToken)
        {
            // Extract chat and message IDs. Using null-conditional operator for safety.
            long? chatId = callbackQuery.Message?.Chat.Id;
            int? messageId = callbackQuery.Message?.MessageId;

            // Ensure essential data is available before proceeding.
            if (chatId == null || messageId == null)
            {
                // Log an error if chat or message ID is missing from the callback.
                // _logger.LogError("CallbackQuery missing Message or MessageId. Callback ID: {CallbackId}", callbackQuery.Id);
                // Optionally answer the callback query here to remove the loading spinner.
                // await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
                return; // Exit the handler if crucial data is missing.
            }

            try
            {
                // Parse page number from callback data, e.g., "menu_econ_calendar:2"
                // Using null-conditional operator and TryParse for safe parsing.
                string[]? parts = callbackQuery.Data?.Split(':');
                int page = parts != null && parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : 1;

                // Inform the user that releases are being loaded by editing the message.
                // This EditMessageTextAsync call is a potential point of failure (Telegram API).
                await _messageSender.EditMessageTextAsync(
                    chatId.Value,
                    messageId.Value,
                    "🗓️ Loading Economic Releases... ⏳",
                    cancellationToken: cancellationToken); // Improved loading message

                // Fetch economic releases from the external service.
                // This call is a potential point of failure (external service/database).
                Shared.Results.Result<List<global::Application.DTOs.Fred.FredReleaseDto>> result = await _calendarService.GetReleasesAsync(page, PageSize, cancellationToken);

                // Check if fetching releases failed or returned no data.
                if (!result.Succeeded || result.Data == null || !result.Data.Any())
                {
                    // Build a keyboard for the error message.
                    InlineKeyboardMarkup errorKeyboard = GetPaginationKeyboard(page, false);

                    // Edit the message to show an error to the user.
                    // This EditMessageTextAsync call is another potential point of failure.
                    await _messageSender.EditMessageTextAsync(
                        chatId.Value,
                        messageId.Value,
                        "❌ Could not retrieve economic releases at this time. 😔",
                        replyMarkup: errorKeyboard,
                        cancellationToken: cancellationToken);

                    // Log the specific reason for failure (e.g., external service error).
                    // _logger.LogWarning("Failed to retrieve economic releases. Service result not successful or data empty. Page: {Page}, ChatId: {ChatId}", page, chatId);

                    // Optionally answer the callback query here if not already done.
                    // await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Failed to load data.", cancellationToken: cancellationToken);

                    return; // Exit after handling the functional error.
                }

                // Build the message text using StringBuilder for efficiency.
                StringBuilder sb = new();
                _ = sb.AppendLine("🗓️ *Upcoming Economic Releases* 📅 - *Key Indicators for Forex Trading:*"); // Changed text and added emoji and context.
                _ = sb.AppendLine("*Impact Levels: 🔴 High | 🟠 Medium | 🟢 Low*");
                _ = sb.AppendLine("`-----------------------------------`");

                // Loop through the retrieved releases and format them for the message.
                int counter = 1 + ((page - 1) * PageSize); // Start from the correct number for pagination
                                                           // Added Where(r => r != null) for defensive check if list contains nulls
                foreach (global::Application.DTOs.Fred.FredReleaseDto? release in result.Data.Where(r => r != null).ToList()) // Added ToList() if needed, or just iterate
                {
                    // Generate numeric emoji for the item number (supports up to 100).
                    string emoji = "";
                    if (counter is >= 1 and <= 10) // Handle 1-10 with single emoji code
                    {
                        emoji = $"{counter}\u20E3";
                    }
                    else if (counter is > 10 and < 100) // Handle 11-99 (two digits)
                    {
                        // Added null check for ToString() result just to be extremely defensive, though highly unlikely
                        string counterString = counter.ToString() ?? "";
                        emoji = counterString.Length == 2
                            ? $"{counterString[0]}\u20E3{counterString[1]}\u20E3"
                            : counterString.Length == 3
                                ? $"{counterString[0]}\u20E3{counterString[1]}\u20E3{counterString[2]}\u20E3"
                                : counter.ToString() + ".";
                    }
                    else // Fallback for numbers >= 100 or unusual cases
                    {
                        emoji = counter.ToString() + ".";
                    }


                    // Determine Impact Level and Emoji based on release name content.
                    // Using OrdinalIgnoreCase for case-insensitive comparison without locale issues.
                    string impactEmoji = "🟢"; // Default: Low Impact
                    string releaseNameForImpactCheck = release.Name?.Trim() ?? ""; // Defensive null check
                    if (releaseNameForImpactCheck.Contains("H.", StringComparison.OrdinalIgnoreCase))
                    {
                        impactEmoji = "🔴";  // High Impact
                    }
                    else if (releaseNameForImpactCheck.Contains("M.", StringComparison.OrdinalIgnoreCase))
                    {
                        impactEmoji = "🟠";  // Medium Impact
                    }

                    // --- START REPLACED LINK LOGIC ---
                    // Append formatted release information (excluding link for now)
                    string releaseName = release.Name?.Trim() ?? "Untitled Release"; // Defensive null check
                    _ = sb.AppendLine($"\n{emoji} *{TelegramMessageFormatter.EscapeMarkdownV2(releaseName)}* {impactEmoji}"); // Make title bold consistently

                    // Add official source link if available and valid.
                    // Add official source link if available and valid.
                    if (!string.IsNullOrWhiteSpace(release.Link))
                    {
                        string rawLink = release.Link.Trim();
                        // Validate if the link is a valid absolute URI (http or https)
                        if (Uri.TryCreate(rawLink, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                        {
                            // **CORRECTED MarkdownV2 URL ESCAPING:**
                            // Only characters '(' ')' and '\' need to be escaped with '\' inside a MarkdownV2 URL link part.
                            // This replaces the problematic TelegramMessageFormatter.EscapeMarkdownV2(rawLink)
                            string escapedLinkUrl = rawLink
                                .Replace("(", "\\(")
                                .Replace(")", "\\)")
                                .Replace("\\", "\\\\"); // Escape backslashes themselves if they can appear in the URL

                            // Escape the *text* part of the link for MarkdownV2.
                            string linkText = "🔗 Official Source"; // Text to display for the link
                                                                    // Use your existing EscapeMarkdownV2 method for the *text* part.
                            string escapedLinkText = TelegramMessageFormatter.EscapeMarkdownV2(linkText);

                            // Append the formatted MarkdownV2 link: [Escaped Text](Escaped URL)
                            _ = sb.AppendLine($"[{escapedLinkText}]({escapedLinkUrl})");

                            // Log that a link was added (optional, but helpful for debugging)
                            _logger.LogTrace("Added valid link for release '{ReleaseName}' (ID: {ReleaseId}): [{LinkText}]({LinkUrl})", (release.Name ?? "Untitled").Truncate(30), release.Id, linkText, rawLink.Truncate(50));
                        }
                        else
                        {
                            // Log if the link exists but is invalid/not absolute (optional, but helpful for debugging)
                            _logger.LogWarning("Skipping invalid or non-absolute link for release '{ReleaseName}' (ID: {ReleaseId}): '{Link}'", (release.Name ?? "Untitled").Truncate(30), release.Id, rawLink.Truncate(100));
                            // Optionally append the raw link as text (escaped) so user can see it (useful for debugging API data)
                            // sb.AppendLine($"🔗 Source Link: {TelegramMessageFormatter.EscapeMarkdownV2(rawLink.Truncate(100))}");
                        }
                    }
                    else
                    {
                        // Log if the link is missing
                        _logger.LogTrace("Link is null or whitespace for release '{ReleaseName}' (ID: {ReleaseId}). Skipping link.", releaseName.Truncate(30), release.Id);
                    }
                    // --- END REPLACED LINK LOGIC ---

                    _ = sb.AppendLine("‐‐‐‐‐‐‐‐‐‐‐‐‐‐‐‐‐‐"); // Add a separator after each item

                    // Increment counter after processing each item
                    counter++;
                }


                // Build the pagination keyboard based on whether there are more pages.
                InlineKeyboardMarkup releasesKeyboard = GetPaginationKeyboard(page, result.Data.Count == PageSize);

                // Edit the message with the generated list of releases and pagination keyboard.
                // This is the final potential point of failure related to Telegram API.
                await _messageSender.EditMessageTextAsync(
                    chatId.Value,
                    messageId.Value,
                    sb.ToString().TrimEnd('\n', '\r'), // Trim trailing newlines/carriage returns
                    ParseMode.MarkdownV2, // Ensure correct parsing mode is used
                    releasesKeyboard,
                    cancellationToken);


                // Optionally answer the callback query upon success to remove the loading spinner.
                // await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
            }
            catch (Exception)
            {
                // --- Exception Handling ---
                // Catching a general Exception to handle any unexpected errors during the process.

                // 1. Log the exception details for debugging.
                // _logger.LogError(ex, "An unexpected error occurred while handling releases view. ChatId: {ChatId}, MessageId: {MessageId}", chatId, messageId);

                // 2. Inform the user that an error occurred.
                // This should be done carefully, as this EditMessageTextAsync could also fail.
                try
                {
                    await _messageSender.EditMessageTextAsync(
                        chatId.Value,
                        messageId.Value,
                        "An unexpected error occurred while loading releases. Please try again later. 😢",
                        cancellationToken: cancellationToken);
                }
                catch (Exception)
                {
                    // If editing fails, maybe the message was deleted or bot was blocked.
                    // Log this secondary error but don't re-throw, as the primary issue is logged.
                    // _logger.LogError(editEx, "Failed to send error message to user {ChatId} after primary error in releases view.", chatId);
                }

                // 3. Crucially, answer the callback query to dismiss the loading indicator on the button.
                // This provides feedback to the user even if a message cannot be sent.
                // This requires access to the underlying Telegram.Bot client or a specific method in _messageSender.
                // Example using the raw client:
                // try
                // {
                //     await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Error loading data.", true, cancellationToken: cancellationToken);
                // }
                // catch (Exception answerEx)
                // {
                //      // Log if answering the callback fails as well (less common).
                //     // _logger.LogError(answerEx, "Failed to answer callback query {CallbackId} after handling releases view error.", callbackQuery.Id);
                // }
            }
        }









        /// <summary>
        /// Handles the request to initiate a search for an economic data series.
        /// </summary>
        private async Task HandleSearchSeriesInitiationAsync(CallbackQuery callbackQuery, CancellationToken cancellationToken)
        {
            // این ID ها را بیرون از try-catch می‌گیریم تا در صورت بروز خطا، بتوانیم لاگ دقیقی ثبت کنیم
            long? chatId = callbackQuery.Message?.Chat.Id;
            long userId = callbackQuery.From.Id;
            int? messageId = callbackQuery.Message?.MessageId;

            try
            {
                // اطمینان از اینکه مقادیر اصلی null نیستند
                if (chatId == null || messageId == null || string.IsNullOrEmpty(callbackQuery.Data))
                {
                    // لاگ کردن یک خطای غیرمنتظره در ساختار callbackQuery
                    // و خروج از متد
                    return;
                }

                string[] parts = callbackQuery.Data.Split(new[] { ':' }, 2);
                string? prefilledSearch = parts.Length > 1 ? parts[1] : null;

                Update triggerUpdate = new() { Id = 0, CallbackQuery = callbackQuery };
                const string stateName = "WaitingForFredSearch";
                await _stateMachine.SetStateAsync(userId, stateName, triggerUpdate, cancellationToken);

                ITelegramState? newState = _stateMachine.GetState(stateName);
                string? entryMessage = await newState!.GetEntryMessageAsync(chatId.Value, triggerUpdate, cancellationToken);

                if (!string.IsNullOrWhiteSpace(prefilledSearch))
                {
                    entryMessage += $"\n\n*Suggested search:* `{TelegramMessageFormatter.EscapeMarkdownV2(prefilledSearch)}`";
                }

                InlineKeyboardMarkup? searchKeyboard = MarkupBuilder.CreateInlineKeyboard(
                    new[] { InlineKeyboardButton.WithCallbackData("⬅️ Back to Calendar", $"{ReleasesCallbackPrefix}:1") }
                );

                await _messageSender.EditMessageTextAsync(chatId.Value, messageId.Value, entryMessage!, ParseMode.MarkdownV2, searchKeyboard, cancellationToken);
            }
            catch (Exception ex)
            {

                _logger.LogError(ex, "Failed to handle search series initiation for user {UserId}", userId);
            }
        }

        #endregion

        #region UI Generation
        // ... (GetPaginationKeyboard is the same)
        /// <summary>
        /// Builds the pagination keyboard for the economic releases view.
        /// </summary>
        /// <param name="currentPage">The current page number.</param>
        /// <param name="hasMore">Indicates if there are more pages of data available.</param>
        /// <returns>An <see cref="InlineKeyboardMarkup"/> for navigation.</returns>
        private InlineKeyboardMarkup GetPaginationKeyboard(int currentPage, bool hasMore)
        {
            List<InlineKeyboardButton> paginationRow = new();
            if (currentPage > 1)
            {
                paginationRow.Add(InlineKeyboardButton.WithCallbackData("⬅️ Previous", $"{ReleasesCallbackPrefix}:{currentPage - 1}"));
            }
            if (hasMore)
            {
                paginationRow.Add(InlineKeyboardButton.WithCallbackData("Next ➡️", $"{ReleasesCallbackPrefix}:{currentPage + 1}"));
            }

            List<List<InlineKeyboardButton>> keyboardLayout = new();
            if (paginationRow.Any())
            {
                keyboardLayout.Add(paginationRow);
            }

            keyboardLayout.Add([
                InlineKeyboardButton.WithCallbackData("📈 Search Data Series", SearchSeriesCallback)
            ]);

            keyboardLayout.Add([
                InlineKeyboardButton.WithCallbackData("🏠 Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral)
            ]);

            return new InlineKeyboardMarkup(keyboardLayout);
        }
        #endregion
    }
}