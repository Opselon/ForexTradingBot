// File: TelegramPanel/Application/CommandHandlers/Features/Analysis/AnalysisCallbackHandler.cs
using Application.Common.Interfaces;
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

namespace TelegramPanel.Application.CommandHandlers.Features.Analysis
{
    public class AnalysisCallbackHandler : ITelegramCallbackQueryHandler
    {
        private readonly ILogger<AnalysisCallbackHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly ITelegramStateMachine _stateMachine;
        private readonly INewsItemRepository _newsRepository;

        // Callbacks this handler is responsible for
        private const string CbWatchPrefix = "analysis_cb_watch";
        private const string SearchKeywordsCallback = "analysis_search_keywords";
        private const string ShowCbNewsPrefix = "cb_news_"; // e.g., cb_news_FED

        private static readonly Dictionary<string, (string Name, string[] Keywords)> CentralBankKeywords = new()
        {
            { "FED", ("Federal Reserve (USA)", new[] { "Federal Reserve", "Fed", "FOMC", "Jerome Powell", "rate hike", "rate cut", "monetary policy" }) },
            { "ECB", ("European Central Bank", new[] { "ECB", "European Central Bank", "Christine Lagarde", "Governing Council" }) },
            { "BOJ", ("Bank of Japan", new[] { "BoJ", "Bank of Japan", "Kazuo Ueda", "yield curve control" }) },
            { "BOE", ("Bank of England", new[] { "BoE", "Bank of England", "Andrew Bailey", "MPC", "Monetary Policy Committee" }) }
        };

        public AnalysisCallbackHandler(
            ILogger<AnalysisCallbackHandler> logger,
            ITelegramMessageSender messageSender,
            ITelegramStateMachine stateMachine,
            INewsItemRepository newsRepository)
        {
            _logger = logger;
            _messageSender = messageSender;
            _stateMachine = stateMachine;
            _newsRepository = newsRepository;
        }

        public bool CanHandle(Update update)
        {
            if (update.Type != UpdateType.CallbackQuery || update.CallbackQuery?.Data == null)
                return false;

            var data = update.CallbackQuery.Data;
            return data.StartsWith(CbWatchPrefix) ||
                   data.StartsWith(SearchKeywordsCallback) ||
                   data.StartsWith(ShowCbNewsPrefix);
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            var callbackQuery = update.CallbackQuery!;
            await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);

            var data = callbackQuery.Data!;
            var chatId = callbackQuery.Message!.Chat.Id;
            var messageId = callbackQuery.Message.MessageId;

            if (data.StartsWith(CbWatchPrefix))
            {
                await ShowCentralBankSelectionMenuAsync(chatId, messageId, cancellationToken);
            }
            else if (data.StartsWith(SearchKeywordsCallback))
            {
                await InitiateKeywordSearchAsync(chatId, messageId, callbackQuery.From.Id, update, cancellationToken);
            }
            else if (data.StartsWith(ShowCbNewsPrefix))
            {
                var bankCode = data.Substring(ShowCbNewsPrefix.Length);
                await ShowCentralBankNewsAsync(chatId, messageId, bankCode, cancellationToken);
            }
        }


        /// <summary>
        /// Initiates the keyword search state for the user, setting the appropriate state and sending an entry message.
        /// </summary>
        /// <param name="chatId"></param>
        /// <param name="messageId"></param>
        /// <param name="userId"></param>
        /// <param name="triggerUpdate"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task InitiateKeywordSearchAsync(long chatId, int messageId, long userId, Update triggerUpdate, CancellationToken cancellationToken)
        {
            _logger.LogInformation("User {UserId} initiated news search by keyword.", userId);

            var stateName = "WaitingForNewsKeywords";
            await _stateMachine.SetStateAsync(userId, stateName, triggerUpdate, cancellationToken);

            var state = _stateMachine.GetState(stateName);
            if (state == null) return;

            var entryMessage = await state.GetEntryMessageAsync(chatId, triggerUpdate, cancellationToken);

            var keyboard = MarkupBuilder.CreateInlineKeyboard(
                new[] { InlineKeyboardButton.WithCallbackData("⬅️ Cancel Search", MenuCallbackQueryHandler.BackToMainMenuGeneral) });

            await _messageSender.EditMessageTextAsync(chatId, messageId, entryMessage!, ParseMode.MarkdownV2, keyboard, cancellationToken);
        }

        /// <summary>
        /// Displays the Central Bank selection menu with buttons for each bank.
        /// </summary>
        /// <param name="chatId"></param>
        /// <param name="messageId"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private Task ShowCentralBankSelectionMenuAsync(long chatId, int messageId, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Showing Central Bank selection menu to ChatID {ChatId}", chatId);

            var text = "🏛️ *Central Bank Watch*\n\nSelect a central bank to view the latest related news and announcements.";

            var buttons = CentralBankKeywords.Select(kvp =>
                InlineKeyboardButton.WithCallbackData($"🏦 {kvp.Value.Name}", $"{ShowCbNewsPrefix}{kvp.Key}")
            ).ToList();

            // VVVVVV FIX IS HERE VVVVVV
            // Ensure all rows are of the same concrete type: List<InlineKeyboardButton>
            var keyboardRows = new List<List<InlineKeyboardButton>>(); // Changed to List<List<...>> for type safety
            for (int i = 0; i < buttons.Count; i += 2)
            {
                // We use .ToList() to convert the LINQ result to a List.
                keyboardRows.Add(buttons.Skip(i).Take(2).ToList());
            }

            // We explicitly create a new List for the final row.
            keyboardRows.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData("⬅️ Back to Analysis Menu", MenuCommandHandler.AnalysisCallbackData)
            });
            // ^^^^^^ FIX IS HERE ^^^^^^

            var keyboard = new InlineKeyboardMarkup(keyboardRows);

            return _messageSender.EditMessageTextAsync(chatId, messageId, text, ParseMode.Markdown, keyboard, cancellationToken);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="chatId"></param>
        /// <param name="messageId"></param>
        /// <param name="bankCode"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task ShowCentralBankNewsAsync(long chatId, int messageId, string bankCode, CancellationToken cancellationToken)
        {
            if (!CentralBankKeywords.TryGetValue(bankCode, out var bankInfo))
            {
                _logger.LogWarning("Invalid bank code received: {BankCode}", bankCode);
                return;
            }

            _logger.LogInformation("Fetching news for Central Bank: {BankName}", bankInfo.Name);
            await _messageSender.EditMessageTextAsync(chatId, messageId, $"⏳ Fetching news for *{bankInfo.Name}*...", ParseMode.Markdown, cancellationToken: cancellationToken);

            var (results, totalCount) = await _newsRepository.SearchNewsAsync(
                keywords: bankInfo.Keywords,
                sinceDate: DateTime.UtcNow.AddDays(-14), // Search last 2 weeks for relevance
                untilDate: DateTime.UtcNow,
                pageNumber: 1,
                pageSize: 5, // Show top 5
                matchAllKeywords: false,
                isUserVip: true,
                cancellationToken: cancellationToken);

            var sb = new StringBuilder();
            if (!results.Any())
            {
                sb.AppendLine($"No recent news found for the *{bankInfo.Name}*.");
            }
            else
            {
                sb.AppendLine(TelegramMessageFormatter.Bold($"🏛️ Top {results.Count} News Results for: {bankInfo.Name}"));
                sb.AppendLine();
                foreach (var item in results)
                {
                    sb.AppendLine($"🔸 *{TelegramMessageFormatter.EscapeMarkdownV2(item.Title)}*");
                    sb.AppendLine($"_{TelegramMessageFormatter.EscapeMarkdownV2(item.SourceName)}_ at _{item.PublishedDate:yyyy-MM-dd HH:mm} UTC_");
                    if (!string.IsNullOrWhiteSpace(item.Link) && Uri.TryCreate(item.Link, UriKind.Absolute, out var uri))
                    {
                        sb.AppendLine($"[Read More]({uri})");
                    }
                    sb.AppendLine("‐‐‐‐‐‐‐‐‐‐‐‐‐‐‐‐‐‐");
                }
            }

            var keyboard = MarkupBuilder.CreateInlineKeyboard(new[] {
                InlineKeyboardButton.WithCallbackData("⬅️ Back to Bank Selection", CbWatchPrefix)
            });

            await _messageSender.EditMessageTextAsync(chatId, messageId, sb.ToString(), ParseMode.MarkdownV2, keyboard, cancellationToken: cancellationToken);
        }
    }
}