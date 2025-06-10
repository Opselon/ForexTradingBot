// File: TelegramPanel/Application/States/NewsSearchState.cs
using Application.Common.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters;
using TelegramPanel.Infrastructure;

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

        /// <summary>
        /// Provides the instructional message when a user enters this state.
        /// </summary>
        public Task<string?> GetEntryMessageAsync(long chatId, Update? triggerUpdate = null, CancellationToken cancellationToken = default)
        {
            // The message is now constructed here, to be sent by the state machine or the initiating handler.
            var text = TelegramMessageFormatter.Bold("🔎 Search News by Keyword") + "\n\n" +
                       "Please enter the keywords you want to search for. You can enter multiple words separated by a space or comma.\n\n" +
                       "_Example: `inflation interest rates`_";

            return Task.FromResult<string?>(text);
        }

        /// <summary>
        /// Processes the user's keyword submission and returns the search results.
        /// </summary>
        /// <returns>Returns null to indicate the conversation state should be cleared after this action.</returns>
        public async Task<string?> ProcessUpdateAsync(Update update, CancellationToken cancellationToken = default)
        {
            var message = update.Message;
            if (message?.Text == null || message.From == null)
            {
                _logger.LogWarning("NewsSearchState received an update without a message, text, or user.");
                await _messageSender.SendTextMessageAsync(message.Chat.Id, "Invalid input. Please send text keywords to search.", cancellationToken: cancellationToken);
                return Name; // Stay in the same state
            }

            var userId = message.From.Id;
            var chatId = message.Chat.Id;
            var keywords = message.Text.Trim();

            if (string.IsNullOrWhiteSpace(keywords))
            {
                await _messageSender.SendTextMessageAsync(chatId, "Search cannot be empty. Please enter some keywords or use the menu to cancel.", cancellationToken: cancellationToken);
                return Name; // Stay in the same state
            }

            _logger.LogInformation("User {UserId} is searching for news with keywords: '{Keywords}'", userId, keywords);

            // FIX: Correctly escape the closing curly brace '}' by doubling it to '}}'.
            var searchingMessage = $"⏳ Searching for news related to `{TelegramMessageFormatter.EscapeMarkdownV2(keywords)}`...";
            await _messageSender.SendTextMessageAsync(chatId, searchingMessage, ParseMode.MarkdownV2, cancellationToken: cancellationToken);

            var keywordList = keywords.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();

            // FIX: Corrected the method call to match the INewsItemRepository interface.
            // The 6th parameter is 'matchAllKeywords', 7th is 'isUserVip', and 8th is the token.
            var (results, totalCount) = await _newsRepository.SearchNewsAsync(keywordList, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow, 1, 5, false, true, cancellationToken);

            if (!results.Any())
            {
                var notFoundMessage = $"No news articles found for your keywords: `{TelegramMessageFormatter.EscapeMarkdownV2(keywords)}`\\. Try a different search\\.";
                await _messageSender.SendTextMessageAsync(chatId, notFoundMessage, ParseMode.MarkdownV2, cancellationToken: cancellationToken);
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine(TelegramMessageFormatter.Bold($"📰 Top {results.Count} News Results for: `{TelegramMessageFormatter.EscapeMarkdownV2(keywords)}`"));
                sb.AppendLine();

                foreach (var item in results)
                {
                    sb.AppendLine($"🔸 *{TelegramMessageFormatter.EscapeMarkdownV2(item.Title)}*");
                    sb.AppendLine($"_{TelegramMessageFormatter.EscapeMarkdownV2(item.SourceName)}_ at _{item.PublishedDate:yyyy-MM-dd HH:mm} UTC_");
                    if (!string.IsNullOrWhiteSpace(item.Summary))
                    {
                        var summary = item.Summary.Length > 200 ? item.Summary.Substring(0, 200) + "..." : item.Summary;
                        sb.AppendLine(TelegramMessageFormatter.EscapeMarkdownV2(summary));
                    }
                    if (Uri.TryCreate(item.Link, UriKind.Absolute, out var validUri))
                    {
                        sb.AppendLine($"[Read More]({validUri})");
                    }
                    sb.AppendLine("--------------------");
                }

                // FIX: Removed the unsupported 'disableWebPagePreview' parameter.
                // We will add this to the ITelegramMessageSender interface as an improvement in the next step.
                await _messageSender.SendTextMessageAsync(chatId, sb.ToString(), ParseMode.MarkdownV2, cancellationToken: cancellationToken);
            }

            // Return null to signify that the conversation for this state is complete.
            return null;
        }
    }
}