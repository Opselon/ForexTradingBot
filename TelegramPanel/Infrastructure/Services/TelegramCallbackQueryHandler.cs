using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.Interfaces;

namespace TelegramPanel.Infrastructure.Services
{
    public class TelegramCallbackQueryHandler : ITelegramCallbackQueryHandler
    {
        private readonly ILogger<TelegramCallbackQueryHandler> _logger;
        private readonly ITelegramBotClient _botClient;
        private readonly IMarketDataService _marketDataService;

        public TelegramCallbackQueryHandler(
            ILogger<TelegramCallbackQueryHandler> logger,
            ITelegramBotClient botClient,
            IMarketDataService marketDataService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
            _marketDataService = marketDataService ?? throw new ArgumentNullException(nameof(marketDataService));
        }

        private string EscapeMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            // For legacy Markdown (ParseMode.Markdown), characters _ * ` [ ] ( ) ~ > # + - = | { } . ! must be escaped.
            // More restrictive than MarkdownV2.
            return Regex.Replace(text, @"([_*`\[\]()~>#+\-=|{}\.\!])", "\\$1");
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken)
        {
            var callbackQuery = update.CallbackQuery;
            if (callbackQuery == null || callbackQuery.Message == null)
            {
                _logger.LogWarning("Received callback query without message context. UpdateID: {UpdateId}", update.Id);
                return;
            }

            var callbackData = callbackQuery.Data;
            if (string.IsNullOrEmpty(callbackData))
            {
                _logger.LogWarning("Received callback query with empty data. UpdateID: {UpdateId}, CallbackQueryID: {CallbackQueryId}", update.Id, callbackQuery.Id);
                // It's good practice to still answer the callback query to remove the loading spinner on the client.
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
                    await _botClient.SendMessage(
                        chatId: callbackQuery.Message.Chat.Id,
                        text: "Please select a new currency (feature coming soon!)",
                        parseMode: ParseMode.Markdown,
                        cancellationToken: cancellationToken);
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                }
                // Add other callback handlers here as needed
                // else if (callbackData.StartsWith("my_profile")) { ... }
                // else if (callbackData == "settings") { ... }
                else
                {
                    _logger.LogWarning("Received unsupported callback data: {CallbackData}. CallbackQueryID: {CallbackQueryId}", callbackData, callbackQuery.Id);
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Action not currently supported.", cancellationToken: cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling callback query for data: {CallbackData}. CallbackQueryID: {CallbackQueryId}", callbackQuery.Data, callbackQuery.Id);
                try
                {
                    // Try to notify the user about the error.
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id, "An error occurred, please try again.", cancellationToken: CancellationToken.None); // Use CancellationToken.None for critical cleanup
                }
                catch (Exception ackEx)
                {
                    _logger.LogError(ackEx, "Failed to acknowledge callback query after error. CallbackQueryID: {CallbackQueryId}", callbackQuery.Id);
                }
            }
        }

        private async Task HandleMarketAnalysisCallback(CallbackQuery callbackQuery, string callbackData, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Handling 'market_analysis' callback. CallbackData: {CallbackData}, CallbackQueryID: {CallbackQueryId}", callbackData, callbackQuery.Id);
            var parts = callbackData.Split(':');
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
            {
                _logger.LogWarning("Invalid market_analysis callback data format: {CallbackData}. CallbackQueryID: {CallbackQueryId}", callbackData, callbackQuery.Id);
                await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Invalid request data for market analysis.", cancellationToken: cancellationToken);
                return;
            }
            string symbol = parts[1].ToUpperInvariant(); // Normalize symbol

            try
            {
                MarketData marketData = await _marketDataService.GetMarketDataAsync(symbol, cancellationToken);
                if (marketData == null)
                {
                     _logger.LogWarning("Market data not found for symbol {Symbol}. CallbackQueryID: {CallbackQueryId}", symbol, callbackQuery.Id);
                     await _botClient.AnswerCallbackQuery(callbackQuery.Id, $"Market data not available for {symbol}.", cancellationToken: cancellationToken);
                     // Optionally, edit the message to indicate data is not found.
                     await _botClient.EditMessageText(
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

                await _botClient.EditMessageText(
                    chatId: callbackQuery.Message.Chat.Id,
                    messageId: callbackQuery.Message.MessageId,
                    text: message,
                    replyMarkup: keyboard,
                    parseMode: ParseMode.Markdown,
                    cancellationToken: cancellationToken);
                
                await _botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                 _logger.LogInformation("Successfully updated market analysis for {Symbol}. CallbackQueryID: {CallbackQueryId}", symbol, callbackQuery.Id);
            }
            catch (MarketDataService.MarketDataException mde)
            {
                _logger.LogWarning(mde, "MarketDataService error for symbol {Symbol}. CallbackQueryID: {CallbackQueryId}", symbol, callbackQuery.Id);
                 await _botClient.AnswerCallbackQuery(callbackQuery.Id, $"Could not fetch market data for {symbol}: {mde.Message}", cancellationToken: cancellationToken);
                 await _botClient.EditMessageText(
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
                await _botClient.AnswerCallbackQuery(callbackQuery.Id, "An error occurred while fetching market data.", cancellationToken: cancellationToken);
                // Consider editing message to reflect the error state if appropriate
            }
        }

        private string FormatMarketAnalysisMessage(MarketData data)
        {
            var trendEmoji = data.Change24h >= 0 ? "📈" : "📉"; 
            var sentimentEmoji = data.MarketSentiment?.ToLowerInvariant() switch // Added null check and ToLowerInvariant
            {
                "bullish" => "🟢",
                "bearish" => "🔴",
                _ => "⚪" // Neutral or unknown
            };

            // Escape dynamic data for Markdown
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

            var sb = new StringBuilder();
            sb.AppendLine($"*__{currencyName} ({symbol}) Analysis__*");
            sb.AppendLine(description);
            sb.AppendLine(); // Extra blank line for spacing

            sb.AppendLine("*Current Market Status*");
            sb.AppendLine($"Price: *{data.Price:F2}* {trendEmoji}"); // Price is double
            sb.AppendLine($"24h Change: *{data.Change24h:F2}%*");    // Change24h is double
            sb.AppendLine($"Volume 24h: *{data.Volume:N0}*");        // Volume is double
            sb.AppendLine($"Volatility: *{data.Volatility:P2}*");    // Volatility is double
            sb.AppendLine();

            sb.AppendLine("*Technical Analysis*");
            sb.AppendLine($"RSI: *{data.RSI:F2}* ({escapedRsiInterpretation})");
            sb.AppendLine($"MACD: *{macd}*");
            sb.AppendLine($"Support: *{data.Support:F2}*");       // Support is decimal
            sb.AppendLine($"Resistance: *{data.Resistance:F2}*"); // Resistance is decimal
            sb.AppendLine();

            sb.AppendLine("*Market Insights*");
            sb.AppendLine($"Trend: *{trend}*");
            sb.AppendLine($"Market Sentiment: {sentimentEmoji} *{marketSentiment}*");
            sb.AppendLine();
            sb.AppendLine($"_Insights: {insights}_");
            sb.AppendLine();

            sb.AppendLine("*Last Updated*");
            sb.AppendLine($"{escapedFormattedLastUpdated} UTC");

            return sb.ToString();
        }

        private string GetRSIInterpretation(double rsi) // RSI is double
        {
            return rsi switch
            {
                > 70 => "Overbought",
                < 30 => "Oversold",
                _ => "Neutral"
            };
        }

        private InlineKeyboardMarkup GetMarketAnalysisKeyboard(string currentSymbol)
        {
             // Define a list of common forex pairs and XAUUSD
            var forexPairs = new[] 
            { 
                "EURUSD", "USDJPY", "GBPUSD", "USDCHF", 
                "AUDUSD", "USDCAD", "NZDUSD", "XAUUSD" 
            };

            var buttons = new System.Collections.Generic.List<System.Collections.Generic.List<InlineKeyboardButton>>();
            var row = new System.Collections.Generic.List<InlineKeyboardButton>();

            foreach (var pair in forexPairs)
            {
                // Add a star if it's the currently displayed symbol
                var buttonText = (pair == currentSymbol) ? $"⭐ {pair}" : pair;
                row.Add(InlineKeyboardButton.WithCallbackData(buttonText, $"market_analysis:{pair}"));
                if (row.Count == 2) // 2 buttons per row
                {
                    buttons.Add(row);
                    row = new System.Collections.Generic.List<InlineKeyboardButton>();
                }
            }
            if (row.Any()) // Add any remaining buttons in the last row
            {
                buttons.Add(row);
            }
            
            // Add a refresh button for the current symbol as the last row, separate for prominence
            buttons.Add(new System.Collections.Generic.List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData($"🔄 Refresh {currentSymbol}", $"market_analysis:{currentSymbol}")
            });


            return new InlineKeyboardMarkup(buttons);
        }
    }
} 