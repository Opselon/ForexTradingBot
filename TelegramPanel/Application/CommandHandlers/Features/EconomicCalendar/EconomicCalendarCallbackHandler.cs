// File: TelegramPanel/Application/CommandHandlers/Features/EconomicCalendar/EconomicCalendarCallbackHandler.cs
using Application.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.CommandHandlers.MainMenu;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters;
using TelegramPanel.Infrastructure;
using TelegramPanel.Infrastructure.Helpers;

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
        public bool CanHandle(Update update) =>

           update.CallbackQuery?.Data?.StartsWith(ReleasesCallbackPrefix) == true ||
           update.CallbackQuery?.Data == SearchSeriesCallback ||

           update.CallbackQuery?.Data == MenuCommandHandler.BackToMainMenuGeneral;
           


        private async Task HandleExploreReleaseAsync(CallbackQuery callbackQuery, CancellationToken cancellationToken)
        {
            var chatId = callbackQuery.Message!.Chat.Id;
            var messageId = callbackQuery.Message.MessageId;

            // Callback data format: "econ_explore:{releaseId}:{elementId}:{parentName}"
            var parts = callbackQuery.Data!.Split(':', 4);
            if (!int.TryParse(parts[1], out int releaseId) || !int.TryParse(parts[2], out int elementId)) return;

            string parentName = parts.Length > 3 ? parts[3] : "Root";

            await _messageSender.EditMessageTextAsync(chatId, messageId, "Loading data tree...", cancellationToken: cancellationToken);

            var result = await _calendarService.GetReleaseTableTreeAsync(releaseId, elementId == 0 ? null : elementId, cancellationToken);

            if (!result.Succeeded || !result.Data.Elements.Any())
            {
                await _messageSender.EditMessageTextAsync(chatId, messageId, "❌ No data tables found for this release.", cancellationToken: cancellationToken);
                return;
            }

            var sb = new StringBuilder();
            var keyboardRows = new List<List<InlineKeyboardButton>>();

            var currentElement = result.Data.Elements.First(); // The API returns the parent as the first element

            sb.AppendLine("🗓️ *Release Explorer*");
            sb.AppendLine($"`Path: {TelegramMessageFormatter.EscapeMarkdownV2(parentName)} > {TelegramMessageFormatter.EscapeMarkdownV2(currentElement.Name)}`");
            sb.AppendLine();
            sb.AppendLine("Select a category or data series below:");

            foreach (var child in currentElement.Children)
            {
                // If it's a data series, show a different button
                if (child.Type == "series" && !string.IsNullOrWhiteSpace(child.SeriesId))
                {
                    var buttonText = $"📈 {child.Name}";
                    keyboardRows.Add(new List<InlineKeyboardButton> {
                    InlineKeyboardButton.WithCallbackData(buttonText, $"series_details:{child.SeriesId}") // To be handled by another handler
                });
                }
                // If it's a group with more children, allow further drilling
                else if (child.Type == "group" && child.Children.Any())
                {
                    var buttonText = $"📂 {child.Name}";
                    keyboardRows.Add(new List<InlineKeyboardButton> {
                    InlineKeyboardButton.WithCallbackData(buttonText, $"{ExploreReleasePrefix}:{child.ReleaseId}:{child.ElementId}:{currentElement.Name}")
                });
                }
            }

            // Add "Back" button
            if (currentElement.ParentId != 0)
            {
                keyboardRows.Add(new List<InlineKeyboardButton> {
                InlineKeyboardButton.WithCallbackData("⬅️ Back", $"{ExploreReleasePrefix}:{currentElement.ReleaseId}:{currentElement.ParentId}:{parentName}")
            });
            }
            else
            {
                keyboardRows.Add(new List<InlineKeyboardButton> {
                InlineKeyboardButton.WithCallbackData("⬅️ Back to All Releases", $"{ReleasesCallbackPrefix}:1")
            });
            }

            await _messageSender.EditMessageTextAsync(chatId, messageId, sb.ToString(), ParseMode.MarkdownV2, new InlineKeyboardMarkup(keyboardRows), cancellationToken);
        }


        /// <summary>
        /// Asynchronously handles the incoming callback query by routing it to the appropriate method.
        /// </summary>
        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            var callbackQuery = update.CallbackQuery;
            if (callbackQuery?.Message == null)
            {
                _logger.LogWarning("EconomicCalendarCallbackHandler received an update without a valid CallbackQuery or Message.");
                return;
            }

            try
            {
                await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);

                var data = callbackQuery.Data!;

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
            var chatId = callbackQuery.Message?.Chat.Id;
            var messageId = callbackQuery.Message?.MessageId;

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
                var parts = callbackQuery.Data?.Split(':');
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
                var result = await _calendarService.GetReleasesAsync(page, PageSize, cancellationToken);

                // Check if fetching releases failed or returned no data.
                if (!result.Succeeded || result.Data == null || !result.Data.Any())
                {
                    // Build a keyboard for the error message.
                    var errorKeyboard = GetPaginationKeyboard(page, false);

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
                var sb = new StringBuilder();
                sb.AppendLine("🗓️ *Upcoming Economic Releases* 📅 - *Key Indicators for Forex Trading:*"); // Changed text and added emoji and context.
                sb.AppendLine("*Impact Levels: 🔴 High | 🟠 Medium | 🟢 Low*");
                sb.AppendLine("`-----------------------------------`");

                // Loop through the retrieved releases and format them for the message.
                int counter = 1 + (page - 1) * PageSize; // Start from the correct number for pagination
                foreach (var release in result.Data)
                {
                    // Generate numeric emoji for the item number (supports up to 100).
                    string emoji = "";
                    if (counter >= 1 && counter <= 10) // Handle 1-10 with single emoji code
                    {
                        emoji = $"{counter}\u20E3";
                    }
                    else if (counter > 10 && counter < 100) // Handle 11-99 (two digits)
                    {
                        emoji = $"{counter.ToString()[0]}\u20E3{counter.ToString()[1]}\u20E3";
                    }
                    else // Fallback for numbers >= 100 or unusual cases
                    {
                        emoji = counter.ToString() + ".";
                    }


                    // Determine Impact Level and Emoji based on release name content.
                    // Using OrdinalIgnoreCase for case-insensitive comparison without locale issues.
                    string impactEmoji = "🟢"; // Default: Low Impact
                    if (release.Name.Contains("H.", StringComparison.OrdinalIgnoreCase))
                    {
                        impactEmoji = "🔴";  // High Impact
                    }
                    else if (release.Name.Contains("M.", StringComparison.OrdinalIgnoreCase))
                    {
                        impactEmoji = "🟠";  // Medium Impact
                    }

                    // Append formatted release information to the message.
                    sb.AppendLine($"\n{emoji} {TelegramMessageFormatter.EscapeMarkdownV2(release.Name)} {impactEmoji}");  // Added emoji and enhanced formatting
                                                                                                                          // Add official source link if available.
                    if (!string.IsNullOrWhiteSpace(release.Link))
                    {
                        sb.AppendLine($"🔗 [Official Source]({TelegramMessageFormatter.EscapeMarkdownV2(release.Link)})"); // More engaging link and escape link in MarkdownV2
                    }
                    counter++;
                }

                // Build the pagination keyboard based on whether there are more pages.
                var releasesKeyboard = GetPaginationKeyboard(page, result.Data.Count == PageSize);

                // Edit the message with the generated list of releases and pagination keyboard.
                // This is the final potential point of failure related to Telegram API.
                await _messageSender.EditMessageTextAsync(
                    chatId.Value,
                    messageId.Value,
                    sb.ToString(),
                    ParseMode.MarkdownV2, // Ensure correct parsing mode is used
                    releasesKeyboard,
                    cancellationToken);

                // Optionally answer the callback query upon success to remove the loading spinner.
                // await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
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
                catch (Exception editEx)
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
            var chatId = callbackQuery.Message?.Chat.Id;
            var userId = callbackQuery.From.Id;
            var messageId = callbackQuery.Message?.MessageId;

            try
            {
                // اطمینان از اینکه مقادیر اصلی null نیستند
                if (chatId == null || messageId == null || string.IsNullOrEmpty(callbackQuery.Data))
                {
                    // لاگ کردن یک خطای غیرمنتظره در ساختار callbackQuery
                    // و خروج از متد
                    return;
                }

                var parts = callbackQuery.Data.Split(new[] { ':' }, 2);
                string? prefilledSearch = parts.Length > 1 ? parts[1] : null;

                var triggerUpdate = new Update { Id = 0, CallbackQuery = callbackQuery };
                const string stateName = "WaitingForFredSearch";
                await _stateMachine.SetStateAsync(userId, stateName, triggerUpdate, cancellationToken);

                var newState = _stateMachine.GetState(stateName);
                var entryMessage = await newState!.GetEntryMessageAsync(chatId.Value, triggerUpdate, cancellationToken);

                if (!string.IsNullOrWhiteSpace(prefilledSearch))
                {
                    entryMessage += $"\n\n*Suggested search:* `{TelegramMessageFormatter.EscapeMarkdownV2(prefilledSearch)}`";
                }

                var searchKeyboard = MarkupBuilder.CreateInlineKeyboard(
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
            var paginationRow = new List<InlineKeyboardButton>();
            if (currentPage > 1)
            {
                paginationRow.Add(InlineKeyboardButton.WithCallbackData("⬅️ Previous", $"{ReleasesCallbackPrefix}:{currentPage - 1}"));
            }
            if (hasMore)
            {
                paginationRow.Add(InlineKeyboardButton.WithCallbackData("Next ➡️", $"{ReleasesCallbackPrefix}:{currentPage + 1}"));
            }

            var keyboardLayout = new List<List<InlineKeyboardButton>>();
            if (paginationRow.Any())
            {
                keyboardLayout.Add(paginationRow);
            }

            keyboardLayout.Add(new List<InlineKeyboardButton> {
                InlineKeyboardButton.WithCallbackData("📈 Search Data Series", SearchSeriesCallback)
            });

            keyboardLayout.Add(new List<InlineKeyboardButton> {
                InlineKeyboardButton.WithCallbackData("🏠 Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral)
            });

            return new InlineKeyboardMarkup(keyboardLayout);
        }
        #endregion
    }
}