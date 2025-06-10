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
        /// </summary>
        private async Task HandleReleasesViewAsync(CallbackQuery callbackQuery, CancellationToken cancellationToken)
        {
            var chatId = callbackQuery.Message!.Chat.Id;
            var messageId = callbackQuery.Message.MessageId;

            // Parse page number from callback data, e.g., "menu_econ_calendar:2"
            var parts = callbackQuery.Data!.Split(':');
            int page = parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : 1;

            await _messageSender.EditMessageTextAsync(chatId, messageId, "🗓️ Loading Economic Releases... ⏳", cancellationToken: cancellationToken); // Improved loading message

            var result = await _calendarService.GetReleasesAsync(page, PageSize, cancellationToken);

            if (!result.Succeeded || result.Data == null || !result.Data.Any())
            {
                var errorKeyboard = GetPaginationKeyboard(page, false);
                await _messageSender.EditMessageTextAsync(chatId, messageId, "❌ Could not retrieve economic releases at this time. 😔", replyMarkup: errorKeyboard, cancellationToken: cancellationToken);
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("🗓️ *Upcoming Economic Releases* 📅 - *Key Indicators for Forex Trading:*"); // Changed text and added emoji and context.
            sb.AppendLine("*Impact Levels: 🔴 High | 🟠 Medium | 🟢 Low*");
            sb.AppendLine("`-----------------------------------`");

            // Improved Loop & Emoji Logic
            int counter = 1 + (page - 1) * PageSize; // Start from the correct number for pagination
            foreach (var release in result.Data)
            {
                // Numeric Emoji logic (supports up to 100)
                string emoji = "";
                if (counter <= 10)
                {
                    emoji = $"{counter}\u20E3";  // 1-10
                }
                else
                {
                    string counterString = counter.ToString();
                    if (counterString.Length == 2)
                    {
                        emoji = $"{counterString[0]}\u20E3{counterString[1]}\u20E3";
                    }
                    else if (counterString.Length == 3)
                    {
                        emoji = $"{counterString[0]}\u20E3{counterString[1]}\u20E3{counterString[2]}\u20E3";
                    }
                    else
                    {
                        emoji = counter.ToString() + ".";
                    }
                }

                // Determine Impact Level and Emoji
                string impactEmoji = "🟢"; // Default: Low
                if (release.Name.Contains("H.", StringComparison.OrdinalIgnoreCase)) // Check by the names.
                {
                    impactEmoji = "🔴";  // High
                }
                else if (release.Name.Contains("M.", StringComparison.OrdinalIgnoreCase))
                {
                    impactEmoji = "🟠";  // Medium
                }

                sb.AppendLine($"\n{emoji} {TelegramMessageFormatter.EscapeMarkdownV2(release.Name)} {impactEmoji}");  // Added emoji and enhanced formatting
                if (!string.IsNullOrWhiteSpace(release.Link))
                {
                    sb.AppendLine($"🔗 [Official Source]({release.Link})");  // More engaging link
                }
                counter++;
            }

            var releasesKeyboard = GetPaginationKeyboard(page, result.Data.Count == PageSize);

            await _messageSender.EditMessageTextAsync(
                chatId,
                messageId,
                sb.ToString(),
                ParseMode.MarkdownV2,
                releasesKeyboard,
                cancellationToken);
        }











        /// <summary>
        /// Handles the request to initiate a search for an economic data series.
        /// </summary>
        private async Task HandleSearchSeriesInitiationAsync(CallbackQuery callbackQuery, CancellationToken cancellationToken)
        {
            var chatId = callbackQuery.Message!.Chat.Id;
            var messageId = callbackQuery.Message.MessageId;
            var userId = callbackQuery.From.Id;

            var parts = callbackQuery.Data!.Split(new[] { ':' }, 2);
            string? prefilledSearch = parts.Length > 1 ? parts[1] : null;

            var triggerUpdate = new Update { Id = 0, CallbackQuery = callbackQuery };
            const string stateName = "WaitingForFredSearch";
            await _stateMachine.SetStateAsync(userId, stateName, triggerUpdate, cancellationToken);

            var newState = _stateMachine.GetState(stateName);
            var entryMessage = await newState!.GetEntryMessageAsync(chatId, triggerUpdate, cancellationToken);

            if (!string.IsNullOrWhiteSpace(prefilledSearch))
            {
                entryMessage += $"\n\n*Suggested search:* `{TelegramMessageFormatter.EscapeMarkdownV2(prefilledSearch)}`";
            }

            var searchKeyboard = MarkupBuilder.CreateInlineKeyboard(
                new[] { InlineKeyboardButton.WithCallbackData("⬅️ Back to Calendar", $"{ReleasesCallbackPrefix}:1") }
            );

            await _messageSender.EditMessageTextAsync(chatId, messageId, entryMessage!, ParseMode.MarkdownV2, searchKeyboard, cancellationToken);
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