using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.Interfaces;

namespace TelegramPanel.Infrastructure.Services
{
    /// <summary>
    /// Handles incoming callback queries from Telegram, routing them to appropriate services
    /// and formatting responses for the user.
    /// </summary>
    public class TelegramCallbackQueryHandler : ITelegramCallbackQueryHandler
    {
        #region Fields

        private readonly ILogger<TelegramCallbackQueryHandler> _logger;
        private readonly ITelegramBotClient _botClient;
        private readonly IMarketDataService _marketDataService;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="TelegramCallbackQueryHandler"/> class.
        /// </summary>
        /// <param name="logger">The logger for logging messages.</param>
        /// <param name="botClient">The Telegram bot client for interacting with the Telegram API.</param>
        /// <param name="marketDataService">The service for retrieving market data.</param>
        /// <exception cref="ArgumentNullException">Thrown if logger, botClient, or marketDataService is null.</exception>
        public TelegramCallbackQueryHandler(
            ILogger<TelegramCallbackQueryHandler> logger,
            ITelegramBotClient botClient,
            IMarketDataService marketDataService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
            _marketDataService = marketDataService ?? throw new ArgumentNullException(nameof(marketDataService));
        }

        #endregion

        #region Private Helper Methods

        /// <summary>
        /// Escapes special characters in a string for Markdown V1 (legacy) compatibility in Telegram.
        /// Telegram's legacy Markdown requires specific characters like '_', '*', '`', '[', etc., to be escaped.
        /// </summary>
        /// <param name="text">The text to escape.</param>
        /// <returns>The escaped text, or an empty string if the input is null or empty.</returns>
        private string EscapeMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            // For legacy Markdown (ParseMode.Markdown), characters _ * ` [ ] ( ) ~ > # + - = | { } . ! must be escaped.
            // More restrictive than MarkdownV2.
            return Regex.Replace(text, @"([_*`\[\]()~>#+\-=|{}\.\!])", "\\$1");
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Handles an incoming update containing a callback query.
        /// </summary>
        /// <param name="update">The Telegram update.</param>
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task HandleAsync(Update update, CancellationToken cancellationToken)
        {
            CallbackQuery? callbackQuery = update.CallbackQuery;
            if (callbackQuery == null || callbackQuery.Message == null)
            {
                _logger.LogWarning("Received callback query without message context. UpdateID: {UpdateId}", update.Id);
                return;
            }

            string? callbackData = callbackQuery.Data;
            if (string.IsNullOrEmpty(callbackData))
            {
                _logger.LogWarning("Received callback query with empty data. UpdateID: {UpdateId}, CallbackQueryID: {CallbackQueryId}", update.Id, callbackQuery.Id);
                // It's important to answer the callback query to remove the loading spinner on the client side, even if there's an issue.
                try
                {
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Received empty callback data.", cancellationToken: cancellationToken);
                }
                catch (Exception ackEx)
                {
                    _logger.LogError(ackEx, "Failed to acknowledge callback query with empty data. CallbackQueryID: {CallbackQueryId}", callbackQuery.Id);
                }
                return;
            }

            _logger.LogInformation("Handling callback query. UpdateID: {UpdateId}, CallbackQueryID: {CallbackQueryId}, Data: {CallbackData}, ChatID: {ChatId}, MessageID: {MessageId}",
                update.Id, callbackQuery.Id, callbackData, callbackQuery.Message.Chat.Id, callbackQuery.Message.MessageId);

            try
            {
                if (callbackData.StartsWith("market_analysis:"))
                {
                    await HandleMarketAnalysisCallback(callbackQuery, callbackData, cancellationToken);
                }
                else if (callbackData == "change_currency")
                {
                    _logger.LogInformation("Handling 'change_currency' callback. CallbackQueryID: {CallbackQueryId}", callbackQuery.Id);
                    // Send a message to the user indicating the feature status.
                    _ = await _botClient.SendMessage(
                        chatId: callbackQuery.Message.Chat.Id,
                        text: "Please select a new currency (feature coming soon!)",
                        parseMode: ParseMode.Markdown,
                        cancellationToken: cancellationToken);
                    // Acknowledge the callback query to remove the loading spinner.
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                }
                // Add other callback handlers here as needed
                // else if (callbackData.StartsWith("my_profile")) { ... }
                // else if (callbackData == "settings") { ... }
                else
                {
                    _logger.LogWarning("Received unsupported callback data: {CallbackData}. CallbackQueryID: {CallbackQueryId}", callbackData, callbackQuery.Id);
                    // Inform the user that the action is not supported and acknowledge the query.
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Action not currently supported.", cancellationToken: cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling callback query for data: {CallbackData}. CallbackQueryID: {CallbackQueryId}", callbackQuery.Data, callbackQuery.Id);
                try
                {
                    // Attempt to notify the user about the error.
                    // Using CancellationToken.None here for critical cleanup/notification, ensuring this runs even if the original token was cancelled.
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id, "An error occurred, please try again.", cancellationToken: CancellationToken.None); // Use CancellationToken.None for critical cleanup
                }
                catch (Exception ackEx)
                {
                    _logger.LogError(ackEx, "Failed to acknowledge callback query after error. CallbackQueryID: {CallbackQueryId}", callbackQuery.Id);
                }
            }
        }

        #endregion

        #region Private Market Analysis Handlers

        /// <summary>
        /// Handles callback queries related to market analysis for a specific symbol.
        /// </summary>
        /// <param name="callbackQuery">The callback query.</param>
        /// <param name="callbackData">The data associated with the callback.</param>
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task HandleMarketAnalysisCallback(CallbackQuery callbackQuery, string callbackData, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Handling 'market_analysis' callback. CallbackData: {CallbackData}, CallbackQueryID: {CallbackQueryId}", callbackData, callbackQuery.Id);
            string[] parts = callbackData.Split(':');
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
            {
                _logger.LogWarning("Invalid market_analysis callback data format: {CallbackData}. CallbackQueryID: {CallbackQueryId}", callbackData, callbackQuery.Id);
                // Inform user of invalid data and acknowledge query.
                await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Invalid request data for market analysis.", cancellationToken: cancellationToken);
                return;
            }
            string symbol = parts[1].ToUpperInvariant(); // Normalize symbol to uppercase for consistent processing.

            try
            {
                // Corrected call in TelegramCallbackQueryHandler.cs (and MarketAnalysisCallbackHandler.cs if applicable)
                MarketData marketData = await _marketDataService.GetMarketDataAsync(symbol, forceRefresh: false, cancellationToken: cancellationToken);
                if (marketData == null)
                {
                    _logger.LogWarning("Market data not found for symbol {Symbol}. CallbackQueryID: {CallbackQueryId}", symbol, callbackQuery.Id);
                    // Inform user that data is not available and acknowledge query.
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id, $"Market data not available for {symbol}.", cancellationToken: cancellationToken);
                    // Optionally, edit the original message to indicate data is not found.
                    _ = await _botClient.EditMessageText(
                       chatId: callbackQuery.Message.Chat.Id,
                       messageId: callbackQuery.Message.MessageId,
                       text: $"Sorry, market data is currently unavailable for *{EscapeMarkdown(symbol)}*.",
                       parseMode: ParseMode.Markdown,
                       cancellationToken: cancellationToken
                    );
                    return;
                }

                string message = FormatMarketAnalysisMessage(marketData);
                InlineKeyboardMarkup keyboard = GetMarketAnalysisKeyboard(symbol);

                // Edit the original message with the new market analysis data and keyboard.
                _ = await _botClient.EditMessageText(
                    chatId: callbackQuery.Message.Chat.Id,
                    messageId: callbackQuery.Message.MessageId,
                    text: message,
                    replyMarkup: keyboard,
                    parseMode: ParseMode.Markdown,
                    cancellationToken: cancellationToken);

                // Acknowledge the callback query successfully.
                await _botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                _logger.LogInformation("Successfully updated market analysis for {Symbol}. CallbackQueryID: {CallbackQueryId}", symbol, callbackQuery.Id);
            }
            catch (MarketDataService.MarketDataException mde)
            {
                _logger.LogWarning(mde, "MarketDataService error for symbol {Symbol}. CallbackQueryID: {CallbackQueryId}", symbol, callbackQuery.Id);
                // Inform user about the specific market data error and acknowledge query.
                await _botClient.AnswerCallbackQuery(callbackQuery.Id, $"Could not fetch market data for {symbol}: {mde.Message}", cancellationToken: cancellationToken);
                // Update the message to reflect the error.
                _ = await _botClient.EditMessageText(
                   chatId: callbackQuery.Message.Chat.Id,
                   messageId: callbackQuery.Message.MessageId,
                   text: $"Failed to retrieve data for *{EscapeMarkdown(symbol)}*. Reason: {EscapeMarkdown(mde.Message)}\nPlease try again later.",
                   parseMode: ParseMode.Markdown,
                   cancellationToken: cancellationToken
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling market analysis for symbol {Symbol}. CallbackQueryID: {CallbackQueryId}", symbol, callbackQuery.Id);
                // Inform user about a general error and acknowledge query.
                await _botClient.AnswerCallbackQuery(callbackQuery.Id, "An error occurred while fetching market data.", cancellationToken: cancellationToken);
                // Consider editing message to reflect the error state if appropriate
            }
        }



        public bool CanHandle(Update update)
        {
            // Determine what callbacks THIS specific handler is responsible for.
            // If it's a generic fallback, this logic might be different or
            // it might rely on being ordered last in the DI registration.
            // For example, if it handles "change_currency" directly (which it was):
            if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery?.Data != null)
            {
                string callbackData = update.CallbackQuery.Data;
                // Example: if this handler is *only* for a specific set of callbacks
                // that are NOT handled by MarketAnalysisCallbackHandler or
                // .
                // Let's assume it was handling "change_currency" as per its original code.
                if (callbackData == "change_currency") // Example prefix
                {
                    // _logger.LogTrace("TelegramCallbackQueryHandler (Infrastructure): CanHandle returning true for 'change_currency'");
                    return true;
                }
                // Add other prefixes this specific infrastructure handler is responsible for.
            }
            // _logger.LogTrace("TelegramCallbackQueryHandler (Infrastructure): CanHandle returning false for data: {Data}", update.CallbackQuery?.Data);
            return false;
        }

        #endregion

        /// <summary>
        /// Formats the market data into a user-friendly string with Markdown.
        /// </summary>
        /// <param name="data">The market data to format.</param>
        /// <returns>A Markdown formatted string representing the market analysis.</returns>
        private string FormatMarketAnalysisMessage(MarketData data)
        {
            string trendEmoji = data.Change24h >= 0 ? "📈" : "📉"; // Assign emoji based on 24h price change.
            string sentimentEmoji = data.MarketSentiment?.ToLowerInvariant() switch // Assign emoji based on market sentiment.
            {
                "bullish" => "🟢",
                "bearish" => "🔴",
                _ => "⚪" // Neutral or unknown
            };

            // Escape dynamic data for Markdown to prevent formatting issues or injection.
            string currencyName = EscapeMarkdown(data.CurrencyName ?? "N/A");
            string symbol = EscapeMarkdown(data.Symbol ?? "N/A");
            string description = EscapeMarkdown(data.Description ?? "No description available.");
            string macd = EscapeMarkdown(data.MACD ?? "N/A");
            string trend = EscapeMarkdown(data.Trend ?? "N/A");
            string marketSentiment = EscapeMarkdown(data.MarketSentiment ?? "N/A");
            string insights = EscapeMarkdown(data.Insights != null && data.Insights.Any() ? string.Join("; ", data.Insights) : "No specific insights.");

            string rsiRawInterpretation = GetRSIInterpretation((double)data.RSI); // Cast data.RSI to double
            string escapedRsiInterpretation = EscapeMarkdown(rsiRawInterpretation);

            string formattedLastUpdated = data.LastUpdated.ToString("yyyy-MM-dd HH:mm:ss"); // LastUpdated is DateTime, not nullable
            string escapedFormattedLastUpdated = EscapeMarkdown(formattedLastUpdated);

            StringBuilder sb = new();
            _ = sb.AppendLine($"*__{currencyName} ({symbol}) Analysis__*");
            _ = sb.AppendLine(description);
            _ = sb.AppendLine(); // Extra blank line for visual spacing.

            _ = sb.AppendLine("*Current Market Status*");
            _ = sb.AppendLine($"Price: *{data.Price:F2}* {trendEmoji}"); // Price is double
            _ = sb.AppendLine($"24h Change: *{data.Change24h:F2}%*");    // Change24h is double
            _ = sb.AppendLine($"Volume 24h: *{data.Volume:N0}*");        // Volume is double
            _ = sb.AppendLine($"Volatility: *{data.Volatility:P2}*");    // Volatility is double
            _ = sb.AppendLine();

            _ = sb.AppendLine("*Technical Analysis*");
            _ = sb.AppendLine($"RSI: *{data.RSI:F2}* ({escapedRsiInterpretation})");
            _ = sb.AppendLine($"MACD: *{macd}*");
            _ = sb.AppendLine($"Support: *{data.Support:F2}*");       // Support is decimal
            _ = sb.AppendLine($"Resistance: *{data.Resistance:F2}*"); // Resistance is decimal
            _ = sb.AppendLine();

            _ = sb.AppendLine("*Market Insights*");
            _ = sb.AppendLine($"Trend: *{trend}*");
            _ = sb.AppendLine($"Market Sentiment: {sentimentEmoji} *{marketSentiment}*");
            _ = sb.AppendLine();
            _ = sb.AppendLine($"_Insights: {insights}_");
            _ = sb.AppendLine();

            _ = sb.AppendLine("*Last Updated*");
            _ = sb.AppendLine($"{escapedFormattedLastUpdated} UTC");

            return sb.ToString();
        }

        /// <summary>
        /// Provides a textual interpretation of the RSI value.
        /// </summary>
        /// <param name="rsi">The RSI value.</param>
        /// <returns>A string indicating whether the asset is "Overbought", "Oversold", or "Neutral".</returns>
        private string GetRSIInterpretation(double rsi) // RSI is double
        {
            return rsi switch
            {
                > 70 => "Overbought",
                < 30 => "Oversold",
                _ => "Neutral"
            };
        }

        /// <summary>
        /// Generates an inline keyboard markup for market analysis actions.
        /// Includes buttons for common forex pairs and a refresh button for the current symbol.
        /// </summary>
        /// <param name="currentSymbol">The currently displayed symbol, to highlight it and set refresh context.</param>
        /// <returns>An <see cref="InlineKeyboardMarkup"/> for Telegram.</returns>
        private InlineKeyboardMarkup GetMarketAnalysisKeyboard(string currentSymbol)
        {
            // Define a list of common forex pairs and XAUUSD for quick selection.
            // If this list were to become very large and button generation complex,
            // PLINQ or other parallel processing *could* be considered, but is an overkill for this size.
            string[] forexPairs = new[]
            {
                "EURUSD", "USDJPY", "GBPUSD", "USDCHF",
                "AUDUSD", "USDCAD", "NZDUSD", "XAUUSD"
            };

            List<List<InlineKeyboardButton>> buttons = new();
            List<InlineKeyboardButton> row = new();

            foreach (string? pair in forexPairs)
            {
                // Add a star emoji to the button text if it's the currently displayed symbol for better UX.
                string buttonText = (pair == currentSymbol) ? $"⭐ {pair}" : pair;
                row.Add(InlineKeyboardButton.WithCallbackData(buttonText, $"market_analysis:{pair}"));
                if (row.Count == 2) // Arrange buttons in rows of 2 for a cleaner layout.
                {
                    buttons.Add(row);
                    row = [];
                }
            }
            if (row.Any()) // Add any remaining buttons in the last row if the total count is odd.
            {
                buttons.Add(row);
            }

            // Add a dedicated refresh button for the current symbol as the last row for easy access.
            buttons.Add(
            [
                InlineKeyboardButton.WithCallbackData($"🔄 Refresh {currentSymbol}", $"market_analysis:{currentSymbol}")
            ]);

            return new InlineKeyboardMarkup(buttons);
        }
    }
}