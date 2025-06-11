// File: TelegramPanel/Application/States/FredSearchState.cs
using Application.Common.Interfaces.Fred;
using Microsoft.Extensions.Logging;
using System.Text;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters;
using TelegramPanel.Infrastructure;
using TelegramPanel.Infrastructure.Helper;

namespace TelegramPanel.Application.States
{
    public class FredSearchState : ITelegramState
    {
        private readonly ILogger<FredSearchState> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly IEconomicCalendarService _calendarService;

        public string Name => "WaitingForFredSearch";

        public FredSearchState(
            ILogger<FredSearchState> logger,
            ITelegramMessageSender messageSender,
            IEconomicCalendarService calendarService)
        {
            _logger = logger;
            _messageSender = messageSender;
            _calendarService = calendarService;
        }
        public Task<string?> GetEntryMessageAsync(long chatId, Update? triggerUpdate = null, CancellationToken cancellationToken = default)
        {
            try
            {
                // Add a bit more detail to the entry message.
                var entryMessage = new StringBuilder();
                _ = entryMessage.AppendLine("📈 *Search for Economic Data Series* 📊"); // Add emoji to emphasize the topic.
                _ = entryMessage.AppendLine(); // Add some space.
                _ = entryMessage.AppendLine("🔍 Enter the *exact* name or a *partial* name of the data series you want to find.");
                _ = entryMessage.AppendLine("💡 *Tip:*  Use common abbreviations (e.g., `CPI` for Consumer Price Index).");
                _ = entryMessage.AppendLine("Example search terms:");
                _ = entryMessage.AppendLine("• `GDP`");
                _ = entryMessage.AppendLine("• `Unemployment Rate`");
                _ = entryMessage.AppendLine("• `Inflation - All items`");
                _ = entryMessage.AppendLine();
                _ = entryMessage.AppendLine("⌨️  Just type your search term and send it! 👇");  // Encourage action
                return Task.FromResult<string?>(entryMessage.ToString());
            }
            catch (Exception ex)
            {
                // Log any errors during message creation - important!
                _logger.LogError(ex, "Error creating entry message for FredSearchState for ChatID {ChatId}", chatId);
                return Task.FromResult<string?>("⚠️  Sorry, there was an error preparing the search. Please try again later. 🤖"); // A user-friendly fallback.
            }
        }

        public async Task<string?> ProcessUpdateAsync(Update update, CancellationToken cancellationToken = default)
        {
            long? chatId = update.Message?.Chat.Id ?? update.CallbackQuery?.Message?.Chat.Id;

            if (update.Type != UpdateType.Message || string.IsNullOrWhiteSpace(update.Message?.Text))
            {
                if (chatId.HasValue)
                {
                    _logger.LogWarning("Invalid update type received in FredSearchState for ChatID {ChatId}. Expected text message.", chatId.Value);
                    await _messageSender.SendTextMessageAsync(chatId.Value, "Invalid input. Please send your search as a text message.", cancellationToken: cancellationToken);
                }
                return Name;
            }

            // FIX: Use the 'chatId' variable declared at the top of the method.
            var message = update.Message;
            var userId = message.From!.Id;
            var searchText = message.Text.Trim();

            if (searchText.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("User {UserId} cancelled FRED search.", userId);
                await _messageSender.SendTextMessageAsync(chatId.Value, "Search cancelled.", cancellationToken: cancellationToken);
                return null;
            }

            try
            {
                _logger.LogInformation("User {UserId} is searching FRED for: '{SearchText}'", userId, searchText);
                await _messageSender.SendTextMessageAsync(chatId.Value, $"⏳ Searching FRED for data series matching *{TelegramMessageFormatter.EscapeMarkdownV2(searchText)}*...", ParseMode.MarkdownV2, cancellationToken: cancellationToken);

                var result = await _calendarService.SearchSeriesAsync(searchText, cancellationToken);

                if (!result.Succeeded || result.Data == null || !result.Data.Any())
                {
                    var notFoundText = $"❌ No data series found for *{TelegramMessageFormatter.EscapeMarkdownV2(searchText)}*.\n\nPlease try a different search term or use `/cancel` to exit.";
                    await _messageSender.SendTextMessageAsync(chatId.Value, notFoundText, ParseMode.MarkdownV2, cancellationToken: cancellationToken);
                    return Name;
                }

                var (responseText, responseKeyboard) = BuildResponseMessage(searchText, result.Data);

                await _messageSender.SendTextMessageAsync(chatId.Value, responseText, ParseMode.MarkdownV2, responseKeyboard, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A critical error occurred during FRED search for user {UserId} with search text '{SearchText}'", userId, searchText);
                await _messageSender.SendTextMessageAsync(chatId.Value, "🚨 An unexpected error occurred. The operation has been cancelled. Please try again later.", cancellationToken: cancellationToken);
            }

            return null;
        }

        /// <summary>
        /// A private helper to build the formatted results message.
        /// </summary>
        private (string, InlineKeyboardMarkup?) BuildResponseMessage(string searchText, List<FredSeriesDto> seriesList)
        {
            var singleMessageSb = new StringBuilder();
            _ = singleMessageSb.AppendLine($"✅ Found *{seriesList.Count}* results for `{TelegramMessageFormatter.EscapeMarkdownV2(searchText)}`:");

            foreach (var series in seriesList.OrderByDescending(s => s.Popularity).Take(5))
            {
                _ = singleMessageSb.AppendLine();
                _ = singleMessageSb.AppendLine($"📈 *{TelegramMessageFormatter.EscapeMarkdownV2(series.Title)}*");
                _ = singleMessageSb.AppendLine($"`ID:` [{series.Id}](https://fred.stlouisfed.org/series/{series.Id}) `| Freq: {series.FrequencyShort} | Units: {series.UnitsShort}`");
            }

            var finalKeyboard = MarkupBuilder.CreateInlineKeyboard(
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("⬅️ New Search", "econ_search_series"),
                    InlineKeyboardButton.WithCallbackData("🗓️ Back to Calendar", "menu_econ_calendar")
                }
            );

            return (singleMessageSb.ToString(), finalKeyboard);
        }

    }
}