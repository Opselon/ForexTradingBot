// File: TelegramPanel/Application/States/FredSearchState.cs
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
                entryMessage.AppendLine("📈 *Search for Economic Data Series* 📊"); // Add emoji to emphasize the topic.
                entryMessage.AppendLine(); // Add some space.
                entryMessage.AppendLine("🔍 Enter the *exact* name or a *partial* name of the data series you want to find.");
                entryMessage.AppendLine("💡 *Tip:*  Use common abbreviations (e.g., `CPI` for Consumer Price Index).");
                entryMessage.AppendLine("Example search terms:");
                entryMessage.AppendLine("• `GDP`");
                entryMessage.AppendLine("• `Unemployment Rate`");
                entryMessage.AppendLine("• `Inflation - All items`");
                entryMessage.AppendLine();
                entryMessage.AppendLine("⌨️  Just type your search term and send it! 👇");  // Encourage action
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
            try
            {
                if (update.Type != UpdateType.Message || string.IsNullOrWhiteSpace(update.Message?.Text))
                {
                    _logger.LogWarning("🚫 Invalid input (not a text message or empty) from user {UserId}", update.Message?.From?.Id);
                    await _messageSender.SendTextMessageAsync(update.Message.Chat.Id, "🚫 Invalid input! Please send your search as a text message. ✍️", cancellationToken: cancellationToken);
                    return Name; // Stay in state.
                }

                var message = update.Message;
                var searchText = message.Text.Trim();
                _logger.LogInformation("User {UserId} is searching FRED for: '{SearchText}'", message.From.Id, searchText);
                await _messageSender.SendTextMessageAsync(message.Chat.Id, $"🔍 Searching for *{searchText}*... ⏳", ParseMode.Markdown, cancellationToken: cancellationToken);

                var result = await _calendarService.SearchSeriesAsync(searchText, cancellationToken);

                // Defensive Programming - Check for null result.
                if (result == null)
                {
                    _logger.LogError("SearchSeriesAsync returned null for search term: '{SearchText}' for user {UserId}", searchText, message.From.Id);
                    await _messageSender.SendTextMessageAsync(message.Chat.Id, $"❌ Oops! Something went wrong during the search for *{searchText}*. Please try again. 🔄", ParseMode.Markdown, cancellationToken: cancellationToken);
                    return Name; // Stay in state.
                }

                if (!result.Succeeded || !result.Data.Any())
                {
                    _logger.LogInformation("No data series found for '{SearchText}' for user {UserId}", searchText, message.From.Id);
                    await _messageSender.SendTextMessageAsync(message.Chat.Id, $"😔 No data series found for *{searchText}*. Try another term! 🤓", ParseMode.Markdown, cancellationToken: cancellationToken);
                    return null; // Exit state.
                }

                var sb = new StringBuilder();
                sb.AppendLine($"✅ *Top results for '{searchText}':* 🎉");

                foreach (var series in result.Data.OrderByDescending(s => s.Popularity).Take(5))
                {
                    // Defensive programming: Check for null series item
                    if (series == null)
                    {
                        _logger.LogWarning("Null series item found in search results for '{SearchText}' for user {UserId}. Skipping.", searchText, message.From.Id);
                        continue; // Skip to the next series.
                    }

                    sb.AppendLine("`------------------------------`");
                    sb.AppendLine($"📈 *{TelegramMessageFormatter.EscapeMarkdownV2(series.Title)}*");
                    sb.AppendLine($"`🆔 ID:` {series.Id} | `📊 Freq:` {series.FrequencyShort} | `📏 Units:` {series.UnitsShort}");
                    sb.AppendLine($"🗓️ *Last Updated:* {TelegramMessageFormatter.EscapeMarkdownV2(series.LastUpdated)}");
                    if (!string.IsNullOrWhiteSpace(series.Notes))
                    {
                        // Defensive programming: Ensure Notes isn't too long.  Limits length.
                        // Escape markdown in the notes *and* add the elipsis
                        var notesToShow = series.Notes.Length <= 150 ? TelegramMessageFormatter.EscapeMarkdownV2(series.Notes) : TelegramMessageFormatter.EscapeMarkdownV2(series.Notes.Substring(0, 150)) + "...";
                        sb.AppendLine($"📝 *Notes:* _{notesToShow}_");
                    }
                }

                // Add "Back to Menu" button
                var keyboard = new InlineKeyboardMarkup(new[] {
                    InlineKeyboardButton.WithCallbackData("🔙 Back to Menu", MenuCommandHandler.EconomicCalendarCallbackData) // More expressive emoji
                });

                await _messageSender.SendTextMessageAsync(message.Chat.Id, sb.ToString(), ParseMode.MarkdownV2, keyboard, cancellationToken: cancellationToken);
                return null; // Exit state after showing results
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing keyword search for user {UserId}, search term: '{SearchText}'", update.Message?.From?.Id, update.Message?.Text);
                await _messageSender.SendTextMessageAsync(update.Message.Chat.Id, "🚨 Uh oh! There was an error processing your search. Please try again later. 🙏", cancellationToken: cancellationToken);
                return Name; // Stay in state (or potentially transition to an error state).
            }
        }
    }
}