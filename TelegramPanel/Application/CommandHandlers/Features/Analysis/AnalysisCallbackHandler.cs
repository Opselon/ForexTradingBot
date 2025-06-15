// File: TelegramPanel/Application/CommandHandlers/Features/Analysis/AnalysisCallbackHandler.cs
using Application.Common.Interfaces;
using Domain.Entities;
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
        private static readonly List<string> BullishKeywords = ["strong", "hike", "beats", "optimistic", "hawkish", "robust", "upgrade", "rally", "surges", "upbeat", "better-than-expected", "gains"];
        private static readonly List<string> BearishKeywords = ["weak", "cut", "misses", "pessimistic", "dovish", "recession", "slump", "downgrade", "plunges", "fears", "worse-than-expected", "concerns"];
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
                {
                    return false;
                }

                string data = update.CallbackQuery.Data;
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

        /// <summary>
        /// Handles incoming CallbackQuery updates from Telegram.
        /// This method processes button clicks from inline keyboards, directing them
        /// to appropriate handlers based on the callback data prefix.
        /// Includes logging and basic error handling for Telegram API interactions and potential exceptions from sub-handlers.
        /// </summary>
        /// <param name="update">The Telegram Update object containing the CallbackQuery.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            // Using null-conditional operator and checking for null instead of null-forgiving operator (!)
            CallbackQuery? callbackQuery = update.CallbackQuery;
            if (callbackQuery?.Data == null || callbackQuery.Message == null)
            {
                // Log a warning if essential callback data is missing.
                _logger.LogWarning("Received CallbackQuery with missing data or message. UpdateID: {UpdateId}, CallbackID: {CallbackId}", update.Id, callbackQuery?.Id);
                // Optionally answer the callback query here to remove the loading spinner, though without ID it might be impossible.
                // if (callbackQuery?.Id != null) { await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken); }
                return; // Exit early if essential data is missing.
            }

            string data = callbackQuery.Data;
            long chatId = callbackQuery.Message.Chat.Id;
            int messageId = callbackQuery.Message.MessageId;
            long userId = callbackQuery.From.Id;

            // Log the incoming callback query for traceability
            _logger.LogInformation("Handling CallbackQuery from UserID {UserId} in ChatID {ChatId}, MessageID {MessageId}. Data: {CallbackData}",
                userId, chatId, messageId, data.Truncate(100));

            try
            {
                // --- Answer Callback Query ---
                // Answer the callback query *before* starting potentially long operations
                // to dismiss the loading indicator on the user's button.
                // This should be done *inside* the try block, as answering can also fail.
                await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
                _logger.LogTrace("Answered CallbackQuery {CallbackId}.", callbackQuery.Id);

                // --- Dispatch Logic ---
                // Use a proper if/else if chain to handle different callback data prefixes.
                if (data == MenuCallbackQueryHandler.BackToMainMenuGeneral)
                {
                    _logger.LogInformation("User {UserId} triggered Back to Main Menu.", userId);
                    await _stateMachine.ClearStateAsync(userId, cancellationToken);

                    // Assuming MenuCommandHandler.GetMainMenuMarkup returns (string text, InlineKeyboardMarkup keyboard)
                    (string text, InlineKeyboardMarkup keyboard) = MenuCommandHandler.GetMainMenuMarkup();
                    // Edit the message to show the main menu. Potential Telegram API call failure.
                    await _messageSender.EditMessageTextAsync(chatId, messageId, text, ParseMode.MarkdownV2, keyboard, cancellationToken: cancellationToken);
                    _logger.LogDebug("Sent Main Menu to ChatID {ChatId}.", chatId);
                }
                else if (data.StartsWith(SentimentAnalysisCallback))
                {
                    _logger.LogInformation("User {UserId} triggered Sentiment Analysis.", userId);
                    // Call the handler for sentiment analysis menu. This method might have its own try-catch, but errors from it should be caught here too.
                    await ShowSentimentCurrencySelectionMenuAsync(chatId, messageId, cancellationToken);
                }
                else if (data.StartsWith(SelectSentimentCurrencyPrefix))
                {
                    string currencyCode = data[SelectSentimentCurrencyPrefix.Length..];
                    _logger.LogInformation("User {UserId} selected currency '{CurrencyCode}' for sentiment.", userId, currencyCode);
                    // Call the handler for currency selection. This method might have its own try-catch.
                    await HandleSentimentCurrencySelectionAsync(chatId, messageId, currencyCode, cancellationToken);
                }
                else if (data.StartsWith(CbWatchPrefix))
                {
                    _logger.LogInformation("User {UserId} triggered CB Watch menu.", userId);
                    // Call the handler for central bank selection menu. This method might have its own try-catch.
                    await ShowCentralBankSelectionMenuAsync(chatId, messageId, cancellationToken);
                }
                else if (data.StartsWith(SearchKeywordsCallback))
                {
                    _logger.LogInformation("User {UserId} triggered Keyword Search initiation.", userId);
                    // Call the handler to initiate keyword search. This method might have its own try-catch.
                    // Note: Passing 'update' might be problematic if it's not serializable/deserializable by JobScheduler if this initiates a job.
                    await InitiateKeywordSearchAsync(chatId, messageId, userId, update, cancellationToken);
                }
                else if (data.StartsWith(ShowCbNewsPrefix))
                {
                    string bankCode = data[ShowCbNewsPrefix.Length..];
                    _logger.LogInformation("User {UserId} triggered Central Bank News for '{BankCode}'.", userId, bankCode);
                    // Call the handler for central bank news. This method might have its own try-catch.
                    await ShowCentralBankNewsAsync(chatId, messageId, bankCode, cancellationToken);
                }
                // --- Add other callback data handlers here ---
                else
                {
                    _logger.LogWarning("Received unhandled CallbackQuery data from UserID {UserId} in ChatID {ChatId}: {CallbackData}",
                        userId, chatId, data.Truncate(100));
                    // Optionally inform the user about the unhandled command or show a default menu.
                    // await _messageSender.EditMessageTextAsync(chatId, messageId, "Unknown command.", cancellationToken: cancellationToken);
                }

                _logger.LogInformation("Successfully processed CallbackQuery from UserID {UserId} for data: {CallbackData}", userId, data.Truncate(100));
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically.
                _logger.LogInformation(ex, "CallbackQuery handling was cancelled for UserID {UserId}, ChatID {ChatId}.", userId, chatId);
                // No need to send an error message as the operation was cancelled.
                // Attempt to answer the callback query again just in case the first one failed.
                // try { await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, "Operation cancelled.", true, cancellationToken: cancellationToken); } catch { }
            }
            // Catch specific exceptions thrown by your sub-handlers if you need to handle them differently.
            // catch (InvalidOperationException ioEx) // Example: If a sub-handler throws InvalidOperationException for business logic
            // {
            //     _logger.LogWarning(ioEx, "Business logic error handling CallbackQuery for UserID {UserId}, ChatID {ChatId}. Data: {CallbackData}",
            //         userId, chatId, data.Truncate(100));
            //     // Inform the user about the business logic error.
            //     try
            //     {
            //          await _messageSender.EditMessageTextAsync(chatId, messageId, $"Error: {ioEx.Message}", cancellationToken: cancellationToken);
            //          // Or send a new message
            //          // await _messageSender.SendMessageAsync(chatId, $"Error: {ioEx.Message}", cancellationToken: cancellationToken);
            //     }
            //     catch { /* Log secondary send error if needed */ }
            // Attempt to answer the callback query again to remove spinner.
            // try { await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, "Action failed.", true, cancellationToken: cancellationToken); } catch { }
            // }
            catch (Exception ex)
            {
                // Catch any other unexpected exceptions from sub-handlers or Telegram API calls.
                // Log the critical technical error.
                _logger.LogError(ex, "An unexpected error occurred while handling CallbackQuery for UserID {UserId}, ChatID {ChatId}. Data: {CallbackData}",
                    userId, chatId, data.Truncate(100));

                // Inform the user about the unexpected error.
                // This EditMessageTextAsync might also fail, hence the potential need for robust error messaging.
                try
                {
                    await _messageSender.EditMessageTextAsync(
                        chatId,
                        messageId,
                        "An unexpected error occurred while processing your request. Please try again later. 😢",
                        cancellationToken: cancellationToken);
                    // Or send a new message if editing the original isn't appropriate or fails.
                    // await _messageSender.SendMessageAsync(chatId, "An unexpected error occurred...", cancellationToken: cancellationToken);
                }
                catch (Exception sendErrorEx)
                {
                    // Log if sending the error message also fails.
                    _logger.LogError(sendErrorEx, "Failed to send fallback error message to ChatID {ChatId} after CallbackQuery handling failure.", chatId);
                }

                // **CRUCIAL:** Attempt to answer the callback query again as a last resort
                // to dismiss the loading indicator on the button, even if message sending failed.
                try
                {
                    await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, "An error occurred!", true, cancellationToken: cancellationToken); // true means show alert
                }
                catch (Exception answerEx)
                {
                    // Log if answering the callback fails as well (less common but possible).
                    _logger.LogError(answerEx, "Failed to answer callback query {CallbackId} after handling error.", callbackQuery.Id);
                }
            }
        }

        private async Task ShowSentimentCurrencySelectionMenuAsync(long chatId, int messageId, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Showing currency selection menu for sentiment analysis to ChatID {ChatId}", chatId);

                string text = "📊 *Market Sentiment*\n\nPlease select a currency to analyze the sentiment of its recent news coverage.";

                // Consider handling CentralBankKeywords being null or empty.  Log a warning if so.
                if (CentralBankKeywords == null || CentralBankKeywords.Count == 0)
                {
                    _logger.LogWarning("CentralBankKeywords is null or empty.  Cannot show currency selection menu.");
                    // Optionally, send an error message to the user, or take other corrective action.
                    await _messageSender.SendTextMessageAsync(chatId, "An error occurred: Currency data unavailable. Please try again later.", cancellationToken: cancellationToken);
                    return; // Exit the method, since there's nothing to display.
                }

                List<InlineKeyboardButton> buttons = CentralBankKeywords.Select(kvp =>
                    InlineKeyboardButton.WithCallbackData($"{(kvp.Key == "USD" ? "🇺🇸" : kvp.Key == "EUR" ? "🇪🇺" : kvp.Key == "GBP" ? "🇬🇧" : "🇯🇵")} {kvp.Value.Name}", $"{SelectSentimentCurrencyPrefix}{kvp.Key}")
                ).ToList();

                // Handle buttons being empty.
                if (buttons.Count == 0)
                {
                    _logger.LogWarning("No currency buttons generated.  CentralBankKeywords may be empty or improperly formatted.");
                    await _messageSender.SendTextMessageAsync(chatId, "An error occurred: No currencies available. Please try again later.", cancellationToken: cancellationToken);
                    return; // Exit the method.
                }

                List<List<InlineKeyboardButton>> keyboardRows = new();
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

                keyboardRows.Add([InlineKeyboardButton.WithCallbackData("⬅️ Back to Analysis Menu", MenuCommandHandler.AnalysisCallbackData)]);

                InlineKeyboardMarkup keyboard = new(keyboardRows);

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

                if (!CentralBankKeywords.TryGetValue(currencyCode, out (string Name, string[] Keywords) currencyInfo))
                {
                    _logger.LogWarning("Currency code {CurrencyCode} not found in CentralBankKeywords for ChatID {ChatId}", currencyCode, chatId);
                    await _messageSender.SendTextMessageAsync(chatId, "Invalid currency selection. Please try again.", cancellationToken: cancellationToken);
                    return;
                }

                await _messageSender.EditMessageTextAsync(chatId, messageId, $"⏳ Analyzing sentiment for the *{currencyInfo.Name}*...", ParseMode.Markdown, cancellationToken: cancellationToken);

                (string sentimentText, List<NewsItem> topPositive, List<NewsItem> topNegative, int positiveScore, int negativeScore) = await PerformSentimentAnalysisAsync(currencyInfo.Keywords, cancellationToken);

                // Handle null results from PerformSentimentAnalysisAsync (defensive programming)
                if (sentimentText == null && topPositive == null && topNegative == null && positiveScore == 0 && negativeScore == 0) // or however the failure is represented.
                {
                    _logger.LogError("Sentiment analysis returned null results for {CurrencyCode} for ChatID {ChatId}", currencyCode, chatId);
                    await _messageSender.SendTextMessageAsync(chatId, $"Sentiment analysis failed for {currencyInfo.Name}. Please try again.", cancellationToken: cancellationToken);
                    return;
                }

                string message = FormatSentimentMessage(currencyInfo.Name, sentimentText, topPositive, topNegative, positiveScore, negativeScore);
                InlineKeyboardMarkup? keyboard = MarkupBuilder.CreateInlineKeyboard(new[] {
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

                (List<NewsItem> newsItems, int _) = await _newsRepository.SearchNewsAsync(currencyKeywords, DateTime.UtcNow.AddDays(-3), DateTime.UtcNow, 1, 100, cancellationToken: cancellationToken);

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
                List<(NewsItem, int)> positiveArticles = new();
                List<(NewsItem, int)> negativeArticles = new();

                foreach (NewsItem item in newsItems)
                {
                    // Defensive programming - check if item is null.
                    if (item == null)
                    {
                        _logger.LogWarning("NewsItem is null within the newsItems list. Skipping.");
                        continue; // Skip the current item and continue with the loop.
                    }

                    // Defensive programming: check for null Title or Summary
                    string content = $"{item.Title ?? ""} {item.Summary ?? ""}".ToLowerInvariant().Trim(); // Use null-coalescing and trim.

                    // Additional defensive programming - content can be empty.
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        _logger.LogInformation("Empty content found for a NewsItem. Skipping.");
                        continue; // Skip to the next item
                    }


                    int currentPositive = BullishKeywords.Count(content.Contains);
                    int currentNegative = BearishKeywords.Count(content.Contains);

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
                if (positiveScore > negativeScore * 1.5)
                {
                    sentiment = "Bullish 🟢";
                }
                else if (negativeScore > positiveScore * 1.5)
                {
                    sentiment = "Bearish 🔴";
                }
                else if (positiveScore > 0 || negativeScore > 0)
                {
                    sentiment = "Neutral/Mixed ⚪️";
                }
                else
                {
                    sentiment = "Not enough data"; // Modified to be clearer.
                }

                List<NewsItem> topPositive = positiveArticles.OrderByDescending(a => a.Item2).Take(2).Select(a => a.Item1).ToList();
                List<NewsItem> topNegative = negativeArticles.OrderByDescending(a => a.Item2).Take(2).Select(a => a.Item1).ToList();

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
                StringBuilder sb = new();

                // Handle null sentiment
                sentiment ??= "No Sentiment Available"; // Provide a default value if sentiment is null

                _ = sb.AppendLine(TelegramMessageFormatter.Bold($"Sentiment for {currencyName}: {sentiment}"));
                _ = sb.AppendLine($"`Score: [Positive: {positiveScore}] [Negative: {negativeScore}]`");
                _ = sb.AppendLine();

                if (topPositive != null && topPositive.Any()) // Check for null and empty
                {
                    _ = sb.AppendLine(TelegramMessageFormatter.Bold("Key Positive News:"));
                    foreach (NewsItem item in topPositive)
                    {
                        // Defensive programming: Check for null item
                        if (item == null)
                        {
                            _logger.LogWarning("Null NewsItem encountered in topPositive. Skipping.");
                            continue; // Skip the null item.
                        }
                        _ = sb.AppendLine($"▫️ {TelegramMessageFormatter.EscapeMarkdownV2(item.Title)} [↗]({item.Link})");
                    }
                    _ = sb.AppendLine();
                }

                if (topNegative != null && topNegative.Any()) // Check for null and empty
                {
                    _ = sb.AppendLine(TelegramMessageFormatter.Bold("Key Negative News:"));
                    foreach (NewsItem item in topNegative)
                    {
                        // Defensive programming: Check for null item
                        if (item == null)
                        {
                            _logger.LogWarning("Null NewsItem encountered in topNegative. Skipping.");
                            continue; // Skip the null item.
                        }
                        _ = sb.AppendLine($"▪️ {TelegramMessageFormatter.EscapeMarkdownV2(item.Title)} [↘]({item.Link})");
                    }
                }

                _ = sb.AppendLine("_Analysis based on keyword frequency in news from the last 3 days._");
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

                string stateName = "WaitingForNewsKeywords";
                await _stateMachine.SetStateAsync(userId, stateName, triggerUpdate, cancellationToken);

                ITelegramState? state = _stateMachine.GetState(stateName);

                // Defensive programming: check for null state
                if (state == null)
                {
                    _logger.LogWarning("State is null after setting the state to {StateName} for user {UserId}", stateName, userId);
                    await _messageSender.SendTextMessageAsync(chatId, "An error occurred while initiating the keyword search. Please try again.", cancellationToken: cancellationToken);
                    return;
                }

                string? entryMessage = await state.GetEntryMessageAsync(chatId, triggerUpdate, cancellationToken);

                // Defensive programming: Check for null entryMessage (and handle).
                if (entryMessage == null)
                {
                    _logger.LogError("GetEntryMessageAsync returned null for user {UserId} in state {StateName}.", userId, stateName);
                    await _messageSender.SendTextMessageAsync(chatId, "An error occurred while retrieving the search instructions. Please try again.", cancellationToken: cancellationToken);
                    return;
                }

                InlineKeyboardMarkup? keyboard = MarkupBuilder.CreateInlineKeyboard(
                    new[] { InlineKeyboardButton.WithCallbackData("⬅️ Back to Analysis Menu", MenuCommandHandler.AnalysisCallbackData) });

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

                string text = "🏛️ *Central Bank Watch*\n\nSelect a central bank to view the latest related news and announcements.";

                // Input Validation - Check if CentralBankKeywords is null or empty
                if (CentralBankKeywords == null || CentralBankKeywords.Count == 0)
                {
                    _logger.LogWarning("CentralBankKeywords is null or empty. Cannot show Central Bank menu.");
                    await _messageSender.SendTextMessageAsync(chatId, "An error occurred: Central Bank data unavailable. Please try again later.", cancellationToken: cancellationToken);
                    return; // Exit the method.
                }

                List<InlineKeyboardButton> buttons = CentralBankKeywords.Select(kvp =>
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
                List<List<InlineKeyboardButton>> keyboardRows = new(); // Changed to List<List<...>> for type safety
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

                keyboardRows.Add(
            [
                InlineKeyboardButton.WithCallbackData("⬅️ Back to Analysis Menu", MenuCommandHandler.AnalysisCallbackData)
            ]);

                InlineKeyboardMarkup keyboard = new(keyboardRows);

                await _messageSender.EditMessageTextAsync(chatId, messageId, text, ParseMode.Markdown, keyboard, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ShowCentralBankSelectionMenuAsync for ChatID {ChatId}", chatId); // Use LogError for errors
                await _messageSender.SendTextMessageAsync(chatId, "An error occurred while displaying the Central Bank menu. Please try again later.", cancellationToken: cancellationToken);
            }
        }


        /// <summary>
        /// Handles the request to show recent news for a selected central bank.
        /// Fetches news from the repository, formats it, and updates the message.
        /// Handles potential data access and Telegram API errors.
        /// </summary>
        /// <param name="chatId">The chat ID.</param>
        /// <param name="messageId">The message ID to edit.</param>
        /// <param name="bankCode">The code of the central bank.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        public async Task ShowCentralBankNewsAsync(long chatId, int messageId, string bankCode, CancellationToken cancellationToken)
        {
            // Basic validation for bankCode (already present)
            if (!CentralBankKeywords.TryGetValue(bankCode, out (string Name, string[] Keywords) bankInfo))
            {
                _logger.LogWarning("Invalid bank code received: {BankCode}. Exiting handler for ChatID {ChatId}, MessageID {MessageId}.", bankCode, chatId, messageId);
                // Optionally inform the user or edit the message to indicate invalid input.
                // await _messageSender.EditMessageTextAsync(chatId, messageId, "Invalid central bank selected.", cancellationToken: cancellationToken);
                return; // Exit if bank code is invalid
            }

            _logger.LogInformation("Fetching news for Central Bank: {BankName} (Code: {BankCode}) for ChatID {ChatId}, MessageID {MessageId}.", bankInfo.Name, bankCode, chatId, messageId);

            try
            {
                // --- Step 1: Show Loading Message ---
                // Potential Telegram API call failure.
                await _messageSender.EditMessageTextAsync(
                    chatId,
                    messageId,
                    $"⏳ Fetching news for *{bankInfo.Name}*...", // Use MarkdownV2 escaping if needed for bankInfo.Name
                    ParseMode.MarkdownV2, // Use MarkdownV2 consistently
                    cancellationToken: cancellationToken);
                _logger.LogDebug("Loading message sent for CB news fetch.");

                // --- Step 2: Search News ---
                // Potential database/repository interaction failure.
                (List<NewsItem> results, int totalCount) = await _newsRepository.SearchNewsAsync(
                    keywords: bankInfo.Keywords,
                    sinceDate: DateTime.UtcNow.AddDays(-14),
                    untilDate: DateTime.UtcNow,
                    pageNumber: 1,
                    pageSize: 5,
                    matchAllKeywords: false,
                    isUserVip: true, // Assume search logic uses this
                    cancellationToken: cancellationToken);
                _logger.LogDebug("News search completed for {BankName}. Found {ResultCount} results out of {TotalCount}.", bankInfo.Name, results.Count(), totalCount);

                // --- Step 3: Build and Send Result Message ---
                StringBuilder sb = new();
                if (results == null || !results.Any()) // Check for null results from repository too
                {
                    _ = sb.AppendLine($"No recent news found for the *{TelegramMessageFormatter.EscapeMarkdownV2(bankInfo.Name)}*."); // Use EscapeMarkdownV2
                }
                else
                {
                    _ = sb.AppendLine($"🏛️ *Top {results.Count()} News Results for: {TelegramMessageFormatter.EscapeMarkdownV2(bankInfo.Name)}*"); // Use EscapeMarkdownV2
                    _ = sb.AppendLine();
                    // Check if any news items are null or have null properties before processing
                    foreach (NewsItem? item in results.Where(i => i != null))
                    {
                        // Ensure properties are not null before using them
                        string title = item.Title?.Trim() ?? "Untitled";
                        string sourceName = item.SourceName?.Trim() ?? "Unknown Source";
                        string link = item.Link?.Trim() ?? string.Empty;
                        DateTime publishedDate = item.PublishedDate; // Assuming PublishedDate is non-nullable DateTime

                        _ = sb.AppendLine($"🔸 *{TelegramMessageFormatter.EscapeMarkdownV2(title)}*"); // Use EscapeMarkdownV2
                        _ = sb.AppendLine($"_{TelegramMessageFormatter.EscapeMarkdownV2(sourceName)}_ at _{publishedDate:yyyy-MM-dd HH:mm} UTC_"); // Use EscapeMarkdownV2 and correct date format

                        // Validate link format before creating link in Markdown
                        if (!string.IsNullOrWhiteSpace(link) && Uri.TryCreate(link, UriKind.Absolute, out Uri? uri))
                        {
                            // Escape parentheses in the URL itself for MarkdownV2 links
                            string escapedLink = link.Replace("(", "\\(").Replace(")", "\\)");
                            _ = sb.AppendLine($"[Read More]({escapedLink})"); // MarkdownV2 link syntax
                        }
                        else
                        {
                            _logger.LogWarning("Invalid or missing URL format for news item link. NewsItemID: {NewsItemId}, Link: {Link}", item.Id, link);
                            // Optionally append just the link text without the markdown link
                            // sb.AppendLine($"Read More: {TelegramMessageFormatter.EscapeMarkdownV2(link)}");
                        }
                        _ = sb.AppendLine("‐‐‐‐‐‐‐‐‐‐‐‐‐‐‐‐‐‐"); // Separator
                    }
                }

                // Build the keyboard. Assumes MarkupBuilder and CbWatchPrefix are defined.
                InlineKeyboardMarkup? keyboard = MarkupBuilder.CreateInlineKeyboard(new[] {
            InlineKeyboardButton.WithCallbackData("⬅️ Back to Bank Selection", CbWatchPrefix)
        });

                // Edit the message with the search results and keyboard. Potential Telegram API call failure.
                await _messageSender.EditMessageTextAsync(
                    chatId,
                    messageId,
                    sb.ToString().Trim(), // Trim trailing newlines
                    ParseMode.MarkdownV2, // Use MarkdownV2 consistently
                    keyboard,
                    cancellationToken: cancellationToken);
                _logger.LogInformation("CB news results sent for {BankName}.", bankInfo.Name);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically.
                _logger.LogInformation(ex, "CB news fetch for {BankName} was cancelled for ChatID {ChatId}.", bankInfo.Name, chatId);
                // Optionally inform the user about cancellation
                // try { await _messageSender.EditMessageTextAsync(chatId, messageId, "Operation cancelled.", cancellationToken: cancellationToken); } catch { }
            }
            // Catch specific repository/DB or mapping exceptions if you want more granular logging or handling.
            // catch (DbException dbEx) // Example: Database error during search
            // {
            //     _logger.LogError(dbEx, "Database error during news search for {BankName} for ChatID {ChatId}.", bankInfo.Name, chatId);
            //      // Inform the user about the database error
            //     try { await _messageSender.EditMessageTextAsync(chatId, messageId, "A database error occurred while fetching news. Please try again later.", cancellationToken: cancellationToken); } catch { }
            // }
            // catch (AutoMapperMappingException mapEx) // Example: Mapping error inside SearchNewsAsync
            // {
            //      _logger.LogError(mapEx, "Mapping error during news processing for {BankName} for ChatID {ChatId}.", bankInfo.Name, chatId);
            //       // Inform the user about the processing error
            //      try { await _messageSender.EditMessageTextAsync(chatId, messageId, "An error occurred while processing news data. Please try again later.", cancellationToken: cancellationToken); } catch { }
            // }
            catch (Exception ex)
            {
                // Catch any other unexpected technical exceptions (e.g., Telegram API errors, other unhandled issues).
                // Log the critical technical error.
                _logger.LogError(ex, "An unexpected error occurred while showing CB news for {BankName} for ChatID {ChatId}.", bankInfo.Name, chatId);

                // Inform the user about the unexpected error.
                // This EditMessageTextAsync might also fail, hence the potential need for robustness.
                try
                {
                    await _messageSender.EditMessageTextAsync(
                        chatId,
                        messageId,
                        "An unexpected error occurred while fetching news. Please try again later. 😢",
                        cancellationToken: cancellationToken);
                }
                catch (Exception sendErrorEx)
                {
                    // Log if sending the error message also fails.
                    _logger.LogError(sendErrorEx, "Failed to send fallback error message to ChatID {ChatId} after CB news failure.", chatId);
                }
                // Note: This is a message Edit, not a CallbackQuery Answer. If this was triggered
                // by a CallbackQuery, the AnswerCallbackQueryAsync should have been done in the
                // calling handler method to dismiss the spinner.
            }
        }
    }
}