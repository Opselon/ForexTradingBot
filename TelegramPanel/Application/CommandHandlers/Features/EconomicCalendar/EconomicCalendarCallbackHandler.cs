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

            await _messageSender.EditMessageTextAsync(chatId, messageId, "⏳ Fetching economic calendar...", cancellationToken: cancellationToken);

            var result = await _calendarService.GetReleasesAsync(page, PageSize, cancellationToken);

            if (!result.Succeeded || result.Data == null || !result.Data.Any())
            {
                var errorKeyboard = GetPaginationKeyboard(page, false);
                await _messageSender.EditMessageTextAsync(chatId, messageId, "❌ Could not retrieve economic releases at this time.", replyMarkup: errorKeyboard, cancellationToken: cancellationToken);
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("🗓️ *Recent & Upcoming Economic Releases*");
            sb.AppendLine("`-----------------------------------`");

            foreach (var release in result.Data)
            {
                sb.AppendLine($"\n*{TelegramMessageFormatter.EscapeMarkdownV2(release.Name)}*");
                if (!string.IsNullOrWhiteSpace(release.Link))
                {
                    sb.AppendLine($"[Official Source]({release.Link})");
                }
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

        // VVVVVV MODIFIED METHOD VVVVVV
        /// <summary>
        /// Handles the request to initiate a search for an economic data series.
        /// </summary>
        private async Task HandleSearchSeriesInitiationAsync(CallbackQuery callbackQuery, CancellationToken cancellationToken)
        {
            var chatId = callbackQuery.Message!.Chat.Id;
            var messageId = callbackQuery.Message.MessageId;
            var userId = callbackQuery.From.Id;

            // FIX: Manually construct an Update object to pass context to the state machine.
            var triggerUpdate = new Update { Id = 0, CallbackQuery = callbackQuery };

            const string stateName = "WaitingForFredSearch";
            await _stateMachine.SetStateAsync(userId, stateName, triggerUpdate, cancellationToken);

            var newState = _stateMachine.GetState(stateName);
            if (newState == null)
            {
                _logger.LogError("Could not retrieve state object for '{StateName}'.", stateName);
                await _messageSender.EditMessageTextAsync(chatId, messageId, "An internal error occurred. Please try again.", cancellationToken: cancellationToken);
                return;
            }

            var entryMessage = await newState.GetEntryMessageAsync(chatId, triggerUpdate, cancellationToken);
            if (string.IsNullOrWhiteSpace(entryMessage))
            {
                _logger.LogError("State '{StateName}' returned a null or empty entry message.", stateName);
                await _messageSender.EditMessageTextAsync(chatId, messageId, "An internal error occurred. Please try again.", cancellationToken: cancellationToken);
                return;
            }

            var searchKeyboard = MarkupBuilder.CreateInlineKeyboard(
                new[] { InlineKeyboardButton.WithCallbackData("⬅️ Back to Calendar", ReleasesCallbackPrefix) }
            );

            await _messageSender.EditMessageTextAsync(chatId, messageId, entryMessage, ParseMode.MarkdownV2, searchKeyboard, cancellationToken);
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