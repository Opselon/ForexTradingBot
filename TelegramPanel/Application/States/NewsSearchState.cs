// File: TelegramPanel/Application/States/NewsSearchState.cs
using Application.Common.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters;
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions;

namespace TelegramPanel.Application.States
{
    public class NewsSearchState : ITelegramState
    {
        private readonly ILogger<NewsSearchState> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly INewsItemRepository _newsRepository;

        public string Name => "WaitingForNewsKeywords";

        public NewsSearchState(
            ILogger<NewsSearchState> logger,
            ITelegramMessageSender messageSender,
            INewsItemRepository newsRepository)
        {
            _logger = logger;
            _messageSender = messageSender;
            _newsRepository = newsRepository;
        }

        public Task<string?> GetEntryMessageAsync(long chatId, Update? triggerUpdate = null, CancellationToken cancellationToken = default)
        {
            string text = TelegramMessageFormatter.Bold("🔎 Search News by Keyword") + "\n\n" +
                       "Please enter the keywords you want to search for. You can enter multiple words separated by a space or comma.\n\n" +
                       "_Example: `inflation interest rates`_";

            return Task.FromResult<string?>(text);
        }

        public async Task<string?> ProcessUpdateAsync(Update update, CancellationToken cancellationToken = default)
        {
            // VVVVVV THE PRIMARY FIX IS HERE VVVVVV

            // 1. Determine the ChatId regardless of update type.
            long? chatId = update.Message?.Chat.Id ?? update.CallbackQuery?.Message?.Chat.Id;

            // 2. Validate the update type. We only want to process new text messages.
            if (update.Type != UpdateType.Message || update.Message?.Text == null)
            {
                if (chatId.HasValue)
                {
                    _logger.LogWarning("Received a non-text message update in NewsSearchState for ChatID {ChatId}.", chatId.Value);
                    // Inform the user what is expected.
                    await _messageSender.SendTextMessageAsync(chatId.Value, "Invalid input. Please send your search keywords as a text message.", cancellationToken: cancellationToken);
                }
                else
                {
                    _logger.LogError("Could not determine ChatID in NewsSearchState from a non-message update. UpdateID: {UpdateId}", update.Id);
                }

                // Stay in the same state to await valid input.
                return Name;
            }

            // 3. We can now safely use the message object.
            Message message = update.Message;
            long userId = message.From!.Id;
            string keywords = message.Text.Trim();

            // ^^^^^^ END OF THE PRIMARY FIX ^^^^^^

            if (string.IsNullOrWhiteSpace(keywords))
            {
                await _messageSender.SendTextMessageAsync(chatId.Value, "Search cannot be empty. Please enter some keywords or use the menu to cancel.", cancellationToken: cancellationToken);
                return Name; // Stay in the same state
            }

            _logger.LogInformation("User {UserId} is searching for news with keywords: '{Keywords}'", userId, keywords);

            string searchingMessage = $"⏳ Searching for news related to `{TelegramMessageFormatter.EscapeMarkdownV2(keywords)}`...";
            await _messageSender.SendTextMessageAsync(chatId.Value, searchingMessage, ParseMode.MarkdownV2, cancellationToken: cancellationToken);

            List<string> keywordList = keywords.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();

            (List<Domain.Entities.NewsItem> results, int _) = await _newsRepository.SearchNewsAsync(keywordList, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow, 1, 5, false, true, cancellationToken);

            if (!results.Any())
            {
                string notFoundMessage = $"No news articles found for your keywords: `{TelegramMessageFormatter.EscapeMarkdownV2(keywords)}`\\. Try a different search\\.";
                await _messageSender.SendTextMessageAsync(chatId.Value, notFoundMessage, ParseMode.MarkdownV2, cancellationToken: cancellationToken);
            }
            else
            {
                StringBuilder sb = new();
                _ = sb.AppendLine(TelegramMessageFormatter.Bold($"📰 Top {results.Count} News Results for: `{TelegramMessageFormatter.EscapeMarkdownV2(keywords)}`"));
                _ = sb.AppendLine();

                foreach (Domain.Entities.NewsItem item in results)
                {
                    _ = sb.AppendLine($"🔸 *{TelegramMessageFormatter.EscapeMarkdownV2(item.Title)}*");
                    _ = sb.AppendLine($"_{TelegramMessageFormatter.EscapeMarkdownV2(item.SourceName)}_ at _{item.PublishedDate:yyyy-MM-dd HH:mm} UTC_");
                    if (!string.IsNullOrWhiteSpace(item.Summary))
                    {
                        string summary = item.Summary.Length > 200 ? item.Summary[..200] + "..." : item.Summary;
                        _ = sb.AppendLine(TelegramMessageFormatter.EscapeMarkdownV2(summary));
                    }
                    if (Uri.TryCreate(item.Link, UriKind.Absolute, out Uri? validUri))
                    {
                        _ = sb.AppendLine($"[Read More]({validUri})");
                    }
                    _ = sb.AppendLine("--------------------");
                }

                await _messageSender.SendTextMessageAsync(chatId.Value, sb.ToString(), ParseMode.MarkdownV2, cancellationToken: cancellationToken);
            }

            // Return null to signify that the conversation for this state is complete.
            return null;
        }
    }
}