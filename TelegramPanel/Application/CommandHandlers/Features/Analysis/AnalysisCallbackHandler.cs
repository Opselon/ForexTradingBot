// File: TelegramPanel/Application/CommandHandlers/Features/Analysis/AnalysisCallbackHandler.cs
using Application.Common.Interfaces;
using Domain.Entities;
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
        private const string SentimentAnalysisCallback = "analysis_sentiment";
        private const string SelectSentimentCurrencyPrefix = "sentiment_curr_";
        private const string CbWatchPrefix = "analysis_cb_watch";
        private const string SearchKeywordsCallback = "analysis_search_keywords";
        private const string ShowCbNewsPrefix = "cb_news_"; // e.g., cb_news_FED
        private static readonly List<string> BullishKeywords = new() { "strong", "hike", "beats", "optimistic", "hawkish", "robust", "upgrade", "rally", "surges", "upbeat", "better-than-expected", "gains" };
        private static readonly List<string> BearishKeywords = new() { "weak", "cut", "misses", "pessimistic", "dovish", "recession", "slump", "downgrade", "plunges", "fears", "worse-than-expected", "concerns" };
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

        /// <summary>
        /// Determines whether this handler can handle a given Telegram Update.
        /// </summary>
        /// <param name="update">The Telegram Update to check.</param>
        /// <returns>True if the handler can handle the update; otherwise, false.</returns>
        public bool CanHandle(Telegram.Bot.Types.Update update)
        {
            try
            {
                if (update.Type != UpdateType.CallbackQuery || update.CallbackQuery?.Data == null)
                    return false;

                var data = update.CallbackQuery.Data;
                return data.StartsWith(CbWatchPrefix) ||
                   data.StartsWith(SearchKeywordsCallback) ||
                   data.StartsWith(ShowCbNewsPrefix) ||
                   data.StartsWith(SentimentAnalysisCallback) ||
                   data.StartsWith(SelectSentimentCurrencyPrefix) ||
                   data == MenuCallbackQueryHandler.BackToMainMenuGeneral; // <<< ADD THIS CHECK
            }
            catch (Exception ex)
            {
                // Log the exception. Use your preferred logging mechanism.
                Console.Error.WriteLine($"Error in CanHandle: {ex}");  // Replace with proper logging.

                // You might also want to consider:
                // 1. Returning false to prevent the handler from incorrectly handling the update.
                // 2. Re-throwing the exception if it's critical and you want to crash the application (use with caution).
                return false; // Or, rethrow;  decision depends on the application's requirements.
            }
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            var callbackQuery = update.CallbackQuery!;
            await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);

            var data = callbackQuery.Data!;
            var chatId = callbackQuery.Message!.Chat.Id;
            var messageId = callbackQuery.Message.MessageId;
            var userId = callbackQuery.From.Id;

            // VVVVVV FIX: CONVERT TO A PROPER IF/ELSE IF CHAIN VVVVVV
            if (data == MenuCallbackQueryHandler.BackToMainMenuGeneral)
            {
                _logger.LogInformation("User {UserId} is cancelling an operation and returning to main menu.", userId);
                await _stateMachine.ClearStateAsync(userId, cancellationToken);

                var (text, keyboard) = MenuCommandHandler.GetMainMenuMarkup();
                await _messageSender.EditMessageTextAsync(chatId, messageId, text, ParseMode.Markdown, keyboard, cancellationToken: cancellationToken);
            }
            else if (data.StartsWith(SentimentAnalysisCallback))
            {
                await ShowSentimentCurrencySelectionMenuAsync(chatId, messageId, cancellationToken);
            }
            else if (data.StartsWith(SelectSentimentCurrencyPrefix))
            {
                var currencyCode = data.Substring(SelectSentimentCurrencyPrefix.Length);
                await HandleSentimentCurrencySelectionAsync(chatId, messageId, currencyCode, cancellationToken);
            }
            else if (data.StartsWith(CbWatchPrefix))
            {
                await ShowCentralBankSelectionMenuAsync(chatId, messageId, cancellationToken);
            }
            else if (data.StartsWith(SearchKeywordsCallback))
            {
                await InitiateKeywordSearchAsync(chatId, messageId, userId, update, cancellationToken);
            }
            else if (data.StartsWith(ShowCbNewsPrefix))
            {
                var bankCode = data.Substring(ShowCbNewsPrefix.Length);
                await ShowCentralBankNewsAsync(chatId, messageId, bankCode, cancellationToken);
            }
            // ^^^^^^ FIX: CONVERTED TO A PROPER IF/ELSE IF CHAIN ^^^^^^
        }


        private async Task ShowSentimentCurrencySelectionMenuAsync(long chatId, int messageId, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Showing currency selection menu for sentiment analysis to ChatID {ChatId}", chatId);

                var text = "📊 *Market Sentiment*\n\nPlease select a currency to analyze the sentiment of its recent news coverage.";

                // Consider handling CentralBankKeywords being null or empty.  Log a warning if so.
                if (CentralBankKeywords == null || CentralBankKeywords.Count == 0)
                {
                    _logger.LogWarning("CentralBankKeywords is null or empty.  Cannot show currency selection menu.");
                    // Optionally, send an error message to the user, or take other corrective action.
                    await _messageSender.SendTextMessageAsync(chatId, "An error occurred: Currency data unavailable. Please try again later.", cancellationToken: cancellationToken);
                    return; // Exit the method, since there's nothing to display.
                }

                var buttons = CentralBankKeywords.Select(kvp =>
                    InlineKeyboardButton.WithCallbackData($"{(kvp.Key == "USD" ? "🇺🇸" : kvp.Key == "EUR" ? "🇪🇺" : kvp.Key == "GBP" ? "🇬🇧" : "🇯🇵")} {kvp.Value.Name}", $"{SelectSentimentCurrencyPrefix}{kvp.Key}")
                ).ToList();

                // Handle buttons being empty.
                if (buttons.Count == 0)
                {
                    _logger.LogWarning("No currency buttons generated.  CentralBankKeywords may be empty or improperly formatted.");
                    await _messageSender.SendTextMessageAsync(chatId, "An error occurred: No currencies available. Please try again later.", cancellationToken: cancellationToken);
                    return; // Exit the method.
                }

                var keyboardRows = new List<List<InlineKeyboardButton>>();
                for (int i = 0; i < buttons.Count; i += 2)
                {
                    keyboardRows.Add(buttons.Skip(i).Take(2).ToList());
                }

                //Handle keyboardRows being null. It is unlikely, but good to be robust.
                if (keyboardRows == null)
                {
                    _logger.LogWarning("keyboardRows is null");
                    await _messageSender.SendTextMessageAsync(chatId, "An error occurred: could not create the currency buttons", cancellationToken: cancellationToken);
                    return;
                }

                keyboardRows.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("⬅️ Back to Analysis Menu", MenuCommandHandler.AnalysisCallbackData) });

                var keyboard = new InlineKeyboardMarkup(keyboardRows);

                await _messageSender.EditMessageTextAsync(chatId, messageId, text, ParseMode.Markdown, keyboard, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ShowSentimentCurrencySelectionMenuAsync for ChatID {ChatId}", chatId); // Use LogError for errors
                                                                                                                      // Consider sending an error message to the user.
                await _messageSender.SendTextMessageAsync(chatId, "An error occurred while displaying the currency selection menu. Please try again later.", cancellationToken: cancellationToken);
                // Consider additional error handling, like retrying, or potentially resetting bot state.
            }
        }

        private async Task HandleSentimentCurrencySelectionAsync(long chatId, int messageId, string currencyCode, CancellationToken cancellationToken)
        {
            try
            {
                // Input Validation -  Check for invalid currencyCode *first*.
                if (string.IsNullOrEmpty(currencyCode))
                {
                    _logger.LogWarning("Currency code is null or empty in HandleSentimentCurrencySelectionAsync for ChatID {ChatId}", chatId);
                    await _messageSender.SendTextMessageAsync(chatId, "Invalid currency code.  Please select a currency again.", cancellationToken: cancellationToken);
                    return;
                }

                if (!CentralBankKeywords.TryGetValue(currencyCode, out var currencyInfo))
                {
                    _logger.LogWarning("Currency code {CurrencyCode} not found in CentralBankKeywords for ChatID {ChatId}", currencyCode, chatId);
                    await _messageSender.SendTextMessageAsync(chatId, "Invalid currency selection. Please try again.", cancellationToken: cancellationToken);
                    return;
                }

                await _messageSender.EditMessageTextAsync(chatId, messageId, $"⏳ Analyzing sentiment for the *{currencyInfo.Name}*...", ParseMode.Markdown, cancellationToken: cancellationToken);

                var (sentimentText, topPositive, topNegative, positiveScore, negativeScore) = await PerformSentimentAnalysisAsync(currencyInfo.Keywords, cancellationToken);

                // Handle null results from PerformSentimentAnalysisAsync (defensive programming)
                if (sentimentText == null && topPositive == null && topNegative == null && positiveScore == 0 && negativeScore == 0) // or however the failure is represented.
                {
                    _logger.LogError("Sentiment analysis returned null results for {CurrencyCode} for ChatID {ChatId}", currencyCode, chatId);
                    await _messageSender.SendTextMessageAsync(chatId, $"Sentiment analysis failed for {currencyInfo.Name}. Please try again.", cancellationToken: cancellationToken);
                    return;
                }

                var message = FormatSentimentMessage(currencyInfo.Name, sentimentText, topPositive, topNegative, positiveScore, negativeScore);
                var keyboard = MarkupBuilder.CreateInlineKeyboard(new[] {
                InlineKeyboardButton.WithCallbackData("⬅️ Back to Currency Selection", SentimentAnalysisCallback)
            });

                await _messageSender.EditMessageTextAsync(chatId, messageId, message, ParseMode.MarkdownV2, keyboard, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in HandleSentimentCurrencySelectionAsync for ChatID {ChatId}, CurrencyCode: {CurrencyCode}", chatId, currencyCode);
                await _messageSender.SendTextMessageAsync(chatId, "An error occurred while processing the sentiment analysis. Please try again later.", cancellationToken: cancellationToken);
                // Consider more advanced error handling (retries, etc.)
            }
        }

        private async Task<(string? Sentiment, List<NewsItem>? TopPositive, List<NewsItem>? TopNegative, int PositiveScore, int NegativeScore)> PerformSentimentAnalysisAsync(string[] currencyKeywords, CancellationToken cancellationToken)
        {
            try
            {
                // Input validation - currencyKeywords check
                if (currencyKeywords == null || currencyKeywords.Length == 0)
                {
                    _logger.LogWarning("Currency keywords array is null or empty in PerformSentimentAnalysisAsync.");
                    // It's important to return a sensible value to indicate an issue.
                    return (null, null, null, 0, 0); // Indicate failure.
                }

                var (newsItems, _) = await _newsRepository.SearchNewsAsync(currencyKeywords, DateTime.UtcNow.AddDays(-3), DateTime.UtcNow, 1, 100, cancellationToken: cancellationToken);

                // Handle null or empty newsItems from the repository.
                if (newsItems == null)
                {
                    _logger.LogWarning("News items are null from _newsRepository.SearchNewsAsync.");
                    return (null, null, null, 0, 0); // Indicate failure.
                }
                if (newsItems.Count == 0)
                {
                    _logger.LogInformation("No news items found for the given keywords."); // Log this as information rather than an error.
                    return ("Not enough data", null, null, 0, 0); // Or return a specific "not enough data" result.
                }


                int positiveScore = 0;
                int negativeScore = 0;
                var positiveArticles = new List<(NewsItem, int)>();
                var negativeArticles = new List<(NewsItem, int)>();

                foreach (var item in newsItems)
                {
                    // Defensive programming - check if item is null.
                    if (item == null)
                    {
                        _logger.LogWarning("NewsItem is null within the newsItems list. Skipping.");
                        continue; // Skip the current item and continue with the loop.
                    }

                    // Defensive programming: check for null Title or Summary
                    string content = ($"{item.Title ?? ""} {item.Summary ?? ""}".ToLowerInvariant()).Trim(); // Use null-coalescing and trim.

                    // Additional defensive programming - content can be empty.
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        _logger.LogInformation("Empty content found for a NewsItem. Skipping.");
                        continue; // Skip to the next item
                    }


                    int currentPositive = BullishKeywords.Count(k => content.Contains(k));
                    int currentNegative = BearishKeywords.Count(k => content.Contains(k));

                    if (currentPositive > 0)
                    {
                        positiveScore += currentPositive;
                        positiveArticles.Add((item, currentPositive));
                    }
                    if (currentNegative > 0)
                    {
                        negativeScore += currentNegative;
                        negativeArticles.Add((item, currentNegative));
                    }
                }

                string sentiment;
                if (positiveScore > negativeScore * 1.5) sentiment = "Bullish 🟢";
                else if (negativeScore > positiveScore * 1.5) sentiment = "Bearish 🔴";
                else if (positiveScore > 0 || negativeScore > 0) sentiment = "Neutral/Mixed ⚪️";
                else sentiment = "Not enough data"; // Modified to be clearer.

                var topPositive = positiveArticles.OrderByDescending(a => a.Item2).Take(2).Select(a => a.Item1).ToList();
                var topNegative = negativeArticles.OrderByDescending(a => a.Item2).Take(2).Select(a => a.Item1).ToList();

                return (sentiment, topPositive, topNegative, positiveScore, negativeScore);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in PerformSentimentAnalysisAsync with keywords: {Keywords}", string.Join(",", currencyKeywords)); // Log the keywords too.
                                                                                                                                              // It is crucial to handle the exception and return a sensible result.
                                                                                                                                              // You have a few choices, depending on how you want the calling method to behave:
                                                                                                                                              // 1. Return a default/error value:
                return (null, null, null, 0, 0); // Most common: Indicate a failure.

                // 2. Re-throw the exception (use with caution, only if the error is truly unrecoverable at this level):
                // throw; // Re-throw the exception.  Use if the calling method cannot proceed.

                // 3.  Handle the exception and return the result, using a default value for each return variable
            }
        }

        private string FormatSentimentMessage(string currencyName, string? sentiment, List<NewsItem>? topPositive, List<NewsItem>? topNegative, int positiveScore, int negativeScore)
        {
            try
            {
                var sb = new StringBuilder();

                // Handle null sentiment
                sentiment ??= "No Sentiment Available"; // Provide a default value if sentiment is null

                sb.AppendLine(TelegramMessageFormatter.Bold($"Sentiment for {currencyName}: {sentiment}"));
                sb.AppendLine($"`Score: [Positive: {positiveScore}] [Negative: {negativeScore}]`");
                sb.AppendLine();

                if (topPositive != null && topPositive.Any()) // Check for null and empty
                {
                    sb.AppendLine(TelegramMessageFormatter.Bold("Key Positive News:"));
                    foreach (var item in topPositive)
                    {
                        // Defensive programming: Check for null item
                        if (item == null)
                        {
                            _logger.LogWarning("Null NewsItem encountered in topPositive. Skipping.");
                            continue; // Skip the null item.
                        }
                        sb.AppendLine($"▫️ {TelegramMessageFormatter.EscapeMarkdownV2(item.Title)} [↗]({item.Link})");
                    }
                    sb.AppendLine();
                }

                if (topNegative != null && topNegative.Any()) // Check for null and empty
                {
                    sb.AppendLine(TelegramMessageFormatter.Bold("Key Negative News:"));
                    foreach (var item in topNegative)
                    {
                        // Defensive programming: Check for null item
                        if (item == null)
                        {
                            _logger.LogWarning("Null NewsItem encountered in topNegative. Skipping.");
                            continue; // Skip the null item.
                        }
                        sb.AppendLine($"▪️ {TelegramMessageFormatter.EscapeMarkdownV2(item.Title)} [↘]({item.Link})");
                    }
                }

                sb.AppendLine("_Analysis based on keyword frequency in news from the last 3 days._");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error formatting sentiment message for {CurrencyName}", currencyName);
                // In case of an error during formatting, return a default/error message
                return $"Error formatting sentiment message for {currencyName}."; // Or a more user-friendly error.
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
            try
            {
                _logger.LogInformation("User {UserId} initiated news search by keyword.", userId);

                var stateName = "WaitingForNewsKeywords";
                await _stateMachine.SetStateAsync(userId, stateName, triggerUpdate, cancellationToken);

                var state = _stateMachine.GetState(stateName);

                // Defensive programming: check for null state
                if (state == null)
                {
                    _logger.LogWarning("State is null after setting the state to {StateName} for user {UserId}", stateName, userId);
                    await _messageSender.SendTextMessageAsync(chatId, "An error occurred while initiating the keyword search. Please try again.", cancellationToken: cancellationToken);
                    return;
                }

                var entryMessage = await state.GetEntryMessageAsync(chatId, triggerUpdate, cancellationToken);

                // Defensive programming: Check for null entryMessage (and handle).
                if (entryMessage == null)
                {
                    _logger.LogError("GetEntryMessageAsync returned null for user {UserId} in state {StateName}.", userId, stateName);
                    await _messageSender.SendTextMessageAsync(chatId, "An error occurred while retrieving the search instructions. Please try again.", cancellationToken: cancellationToken);
                    return;
                }

                var keyboard = MarkupBuilder.CreateInlineKeyboard(
                    new[] { InlineKeyboardButton.WithCallbackData("⬅️ Cancel Search", MenuCommandHandler.AnalysisCallbackData) });

                await _messageSender.EditMessageTextAsync(chatId, messageId, entryMessage, ParseMode.MarkdownV2, keyboard, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initiating keyword search for user {UserId}", userId);
                await _messageSender.SendTextMessageAsync(chatId, "An error occurred while initiating the keyword search. Please try again later.", cancellationToken: cancellationToken);
            }
        }

        /// <summary>
        /// Displays the Central Bank selection menu with buttons for each bank.
        /// </summary>
        /// <param name="chatId"></param>
        /// <param name="messageId"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task ShowCentralBankSelectionMenuAsync(long chatId, int messageId, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Showing Central Bank selection menu to ChatID {ChatId}", chatId);

                var text = "🏛️ *Central Bank Watch*\n\nSelect a central bank to view the latest related news and announcements.";

                // Input Validation - Check if CentralBankKeywords is null or empty
                if (CentralBankKeywords == null || CentralBankKeywords.Count == 0)
                {
                    _logger.LogWarning("CentralBankKeywords is null or empty. Cannot show Central Bank menu.");
                    await _messageSender.SendTextMessageAsync(chatId, "An error occurred: Central Bank data unavailable. Please try again later.", cancellationToken: cancellationToken);
                    return; // Exit the method.
                }

                var buttons = CentralBankKeywords.Select(kvp =>
                    InlineKeyboardButton.WithCallbackData($"🏦 {kvp.Value.Name}", $"{ShowCbNewsPrefix}{kvp.Key}")
                ).ToList();

                // Handle buttons being empty.
                if (buttons.Count == 0)
                {
                    _logger.LogWarning("No Central Bank buttons generated. CentralBankKeywords may be improperly formatted.");
                    await _messageSender.SendTextMessageAsync(chatId, "An error occurred: No central banks available. Please try again later.", cancellationToken: cancellationToken);
                    return; // Exit the method.
                }

                // Ensure all rows are of the same concrete type: List<InlineKeyboardButton>
                var keyboardRows = new List<List<InlineKeyboardButton>>(); // Changed to List<List<...>> for type safety
                for (int i = 0; i < buttons.Count; i += 2)
                {
                    keyboardRows.Add(buttons.Skip(i).Take(2).ToList());
                }

                // The code *already* correctly uses a List<InlineKeyboardButton> for each row. The fix was to ensure type safety in the original.  Adding input validation
                if (keyboardRows == null)
                {
                    _logger.LogWarning("keyboardRows is null");
                    await _messageSender.SendTextMessageAsync(chatId, "An error occurred: could not create the bank buttons", cancellationToken: cancellationToken);
                    return;
                }

                keyboardRows.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData("⬅️ Back to Analysis Menu", MenuCommandHandler.AnalysisCallbackData)
            });

                var keyboard = new InlineKeyboardMarkup(keyboardRows);

                await _messageSender.EditMessageTextAsync(chatId, messageId, text, ParseMode.Markdown, keyboard, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ShowCentralBankSelectionMenuAsync for ChatID {ChatId}", chatId); // Use LogError for errors
                await _messageSender.SendTextMessageAsync(chatId, "An error occurred while displaying the Central Bank menu. Please try again later.", cancellationToken: cancellationToken);
            }
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