using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Infrastructure;

namespace TelegramPanel.Application.CommandHandlers
{
    public class MarketAnalysisCallbackHandler : ITelegramCallbackQueryHandler, ITelegramCommandHandler
    {
        private readonly ILogger<MarketAnalysisCallbackHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly IMarketDataService _marketDataService;

        private const string MarketAnalysisCallback = "market_analysis";
        private const string RefreshMarketDataCallback = "refresh_market_data";
        private const string SelectCurrencyCallback = "select_currency";

        // 13+ popular forex pairs + gold
        private static readonly (string Symbol, string Label)[] SupportedSymbols = new[]
        {
            ("EURUSD", "🇪🇺 EUR/USD"),
            ("GBPUSD", "🇬🇧 GBP/USD"),
            ("USDJPY", "🇺🇸 USD/JPY"),
            ("AUDUSD", "🇦🇺 AUD/USD"),
            ("USDCAD", "🇺🇸 USD/CAD"),
            ("USDCHF", "🇺🇸 USD/CHF"),
            ("NZDUSD", "🇳🇿 NZD/USD"),
            ("EURGBP", "🇪🇺 EUR/GBP"),
            ("EURJPY", "🇪🇺 EUR/JPY"),
            ("GBPJPY", "🇬🇧 GBP/JPY"),
            ("AUDJPY", "🇦🇺 AUD/JPY"),
            ("CHFJPY", "🇨🇭 CHF/JPY"),
            ("EURAUD", "🇪🇺 EUR/AUD"),
            ("EURCAD", "🇪🇺 EUR/CAD"),
            ("GBPAUD", "🇬🇧 GBP/AUD"),
            ("XAUUSD", "🥇 Gold (XAU/USD)")
        };

        public MarketAnalysisCallbackHandler(
            ILogger<MarketAnalysisCallbackHandler> logger,
            ITelegramMessageSender messageSender,
            IMarketDataService marketDataService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
            _marketDataService = marketDataService ?? throw new ArgumentNullException(nameof(marketDataService));
        }

        public bool CanHandle(Update update)
        {
            return update.CallbackQuery?.Data?.StartsWith(MarketAnalysisCallback) == true ||
                   update.CallbackQuery?.Data?.StartsWith(RefreshMarketDataCallback) == true ||
                   update.CallbackQuery?.Data?.StartsWith(SelectCurrencyCallback) == true;
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken)
        {
            if (update.CallbackQuery == null || update.CallbackQuery.Message == null)
            {
                _logger.LogWarning("CallbackQuery or its Message is null.");
                return;
            }

            var callbackQuery = update.CallbackQuery;
            var callbackData = callbackQuery.Data;
            var chatId = callbackQuery.Message.Chat.Id;
            var messageId = callbackQuery.Message.MessageId;

            if (string.IsNullOrEmpty(callbackData))
            {
                _logger.LogWarning("Callback query with empty data. UpdateID: {UpdateId}", update.Id);
                await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, "Empty callback.", cancellationToken: cancellationToken);
                return;
            }

            _logger.LogInformation("Handling CBQ. Data:{Data}, Chat:{ChatId}, Msg:{MsgId}, User:{UserId}",
                callbackData, chatId, messageId, callbackQuery.From.Id);

            try
            {
                string[] parts = callbackData.Split(new[] { ':' }, 2); // Split only on the first colon
                string action = parts[0];
                string? payload = parts.Length > 1 ? parts[1] : null;

                // It's good practice to answer the callback query promptly.
                // We can do a general one here, and specific actions can override if they need to show specific text in the toast.
                // However, if a subsequent EditMessageTextAsync fails with "message not modified", answering again can be an issue.
                // Let's try answering within each specific action block or if no action is matched.
                bool callbackAcknowledged = false;

                if (action == MarketAnalysisCallback) // This is for the INITIAL entry point to show the menu
                {
                    _logger.LogInformation("Action: Initial MarketAnalysisCallback. Showing currency menu. ChatID:{ChatId}", chatId);
                    await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
                    callbackAcknowledged = true;
                    await ShowCurrencySelectionMenu(chatId, messageId, cancellationToken);
                }
                else if (action == SelectCurrencyCallback)
                {
                    if (!string.IsNullOrEmpty(payload)) // A specific currency was selected FROM THE MENU
                    {
                        _logger.LogInformation("Action: SelectCurrencyCallback for {Symbol}. ChatID:{ChatId}", payload, chatId);
                        // ShowMarketAnalysis will handle its own loading message and final ack for this interaction path
                        // The AnswerCallbackQuery here is just to acknowledge the button press if ShowMarketAnalysis takes time to start editing
                        await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, $"Loading {payload}...", cancellationToken: cancellationToken);
                        callbackAcknowledged = true; // Consider it acknowledged for now.
                        await ShowMarketAnalysis(chatId, messageId, payload, isRefresh: false, callbackQuery.Id, cancellationToken);
                    }
                    else // This is the "Change Currency" button on an *existing analysis message*
                    {
                        _logger.LogInformation("Action: SelectCurrencyCallback (Change Currency button). Showing currency menu. ChatID:{ChatId}", chatId);
                        await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
                        callbackAcknowledged = true;
                        await ShowCurrencySelectionMenu(chatId, messageId, cancellationToken);
                    }
                }
                else if (action == RefreshMarketDataCallback)
                {
                    if (!string.IsNullOrEmpty(payload))
                    {
                        _logger.LogInformation("Action: RefreshMarketDataCallback for {Symbol}. ChatID:{ChatId}", payload, chatId);
                        // ShowMarketAnalysis will handle its own loading message and final ack
                        await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, "Refreshing...", cancellationToken: cancellationToken);
                        callbackAcknowledged = true; // Consider it acknowledged for now.
                        await ShowMarketAnalysis(chatId, messageId, payload, isRefresh: true, callbackQuery.Id, cancellationToken);
                    }
                    else
                    {
                        _logger.LogWarning("RefreshMarketDataCallback missing symbol payload. CBQID:{CBQID}", callbackQuery.Id);
                        await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, "Invalid refresh request.", showAlert: true, cancellationToken: cancellationToken);
                        callbackAcknowledged = true;
                    }
                }
                // else if (action == ViewTechnicalsCallback) { /* ... */ }
                else
                {
                    _logger.LogWarning("Unhandled callback action: {Action} with payload {Payload}. CBQID:{CBQID}", action, payload, callbackQuery.Id);
                    if (!callbackAcknowledged) // Only answer if no other branch did
                    {
                        await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, "Action not recognized.", cancellationToken: cancellationToken);
                    }
                }
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException apiEx) when (apiEx.Message.Contains("message is not modified"))
            {
                // This catch block is important here if ShowMarketAnalysis's "message not modified" exception bubbles up
                // and we haven't answered the callback query from a *refresh* action yet.
                _logger.LogInformation("HandleAsync: Message not modified. CBQID: {CBQID}. This might be from a refresh with no new data.", callbackQuery.Id);
                // If the original action was a refresh, this is where the "no new data" ack should ideally happen IF ShowMarketAnalysis didn't handle it.
                // However, ShowMarketAnalysis was modified to handle this specific ack, so this catch here might be redundant for that case.
                // For safety, ensure callbacks are always answered.
                try { await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, "Data is up to date.", showAlert: false, cancellationToken: CancellationToken.None); }
                catch { /* Already answered or error */ }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in HandleAsync for market analysis callback. Data: {CallbackData}, CBQID:{CBQID}", callbackData, callbackQuery.Id);
                try
                {
                    await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, "An error occurred.", showAlert: true, cancellationToken: CancellationToken.None);
                }
                catch (Exception ackEx)
                {
                    _logger.LogError(ackEx, "Failed to ack callback query after error in HandleAsync. CBQID:{CBQID}", callbackQuery.Id);
                }
                // Optionally, edit the message to provide a "start over" option
                try
                {
                    var startOverKeyboard = new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("🔄 Start Over", MarketAnalysisCallback)); // Use the main menu callback
                    await _messageSender.EditMessageTextAsync(
                        chatId,
                        messageId,
                        "❌ An unexpected error occurred. Please try starting over.",
                        replyMarkup: startOverKeyboard,
                        cancellationToken: CancellationToken.None);
                }
                catch (Exception editEx)
                {
                    _logger.LogError(editEx, "Failed to edit message with generic error and start over. ChatID:{ChatId}, MsgID:{MsgId}", chatId, messageId);
                }

            }

        }


        private async Task ShowCurrencySelectionMenu(long chatId, int messageId, CancellationToken cancellationToken)
        {
            // 3 columns per row
            var rows = SupportedSymbols
                .Select((pair, i) => new { pair, i })
                .GroupBy(x => x.i / 3)
                .Select(g => g.Select(x => InlineKeyboardButton.WithCallbackData(x.pair.Label, $"{SelectCurrencyCallback}:{x.pair.Symbol}")).ToArray())
                .ToArray();

            var keyboard = new InlineKeyboardMarkup(rows);

            await _messageSender.EditMessageTextAsync(
                chatId,
                messageId,
                "💱 *Select a Forex Pair for Analysis:*\n\nChoose from the most popular currency pairs:",
                ParseMode.Markdown,
                keyboard,
                cancellationToken);
        }

        private async Task ShowMarketAnalysis(long chatId, int messageId, string symbol, bool isRefresh, string callbackQueryId, CancellationToken cancellationToken)
        {
            // ... (loading message edit as before)
            string loadingMessage = isRefresh
                ? $"🔄 _Refreshing data for {symbol}..._"
                : $"📊 _Fetching analysis for {symbol}..._";
            try
            {
                await _messageSender.EditMessageTextAsync(chatId, messageId, loadingMessage, ParseMode.Markdown, null, cancellationToken);
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException apiEx) when (apiEx.Message.Contains("message is not modified")) { /*Ignore if already loading*/ }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not edit to loading message for {Symbol}", symbol); }


            MarketData marketData;
            try
            {
                marketData = await _marketDataService.GetMarketDataAsync(symbol, forceRefresh: isRefresh, cancellationToken: cancellationToken);
            }
            catch (Exception serviceEx)
            {
                _logger.LogError(serviceEx, "MarketDataService threw an exception for {Symbol}", symbol);
                var errorKeyboard = GetMarketAnalysisKeyboard(symbol); // Get keyboard with refresh/change options
                await _messageSender.EditMessageTextAsync(chatId, messageId, $"❌ Error fetching data for *{symbol}*: Service error.", ParseMode.Markdown, errorKeyboard, cancellationToken);
                // No AnswerCallbackQuery here as we want the main HandleAsync to potentially show a generic error alert.
                // Or, if this is the final point for this specific error:
                // await _messageSender.AnswerCallbackQueryAsync(callbackQueryId, "Service error retrieving data.", showAlert: true, cancellationToken: CancellationToken.None);
                return;
            }

            if (marketData == null || (!marketData.IsPriceLive && marketData.DataSource == "Unavailable"))
            {
                _logger.LogWarning("Data unavailable for {Symbol}. IsLive:{IsLive}, Source:{Source}", symbol, marketData?.IsPriceLive, marketData?.DataSource);
                string errorText = $"⚠️ Data for *{symbol}* is currently unavailable.";
                if (marketData != null && marketData.Remarks.Any()) errorText += "\n\n*Notes:*\n• " + string.Join("\n• ", marketData.Remarks);

                var errorKeyboard = GetMarketAnalysisKeyboard(symbol); // Get keyboard with refresh/change options
                await _messageSender.EditMessageTextAsync(chatId, messageId, errorText, ParseMode.Markdown, errorKeyboard, cancellationToken);
                // Answer here if this is the final state for this callback.
                // await _messageSender.AnswerCallbackQueryAsync(callbackQueryId, $"Data for {symbol} unavailable.", cancellationToken: CancellationToken.None);
                return;
            }

            var newMessageText = FormatMarketAnalysisMessage(marketData);
            var newKeyboard = GetMarketAnalysisKeyboard(symbol); // This now generates "Change Currency" with MarketAnalysisCallback

            try
            {
                await _messageSender.EditMessageTextAsync(chatId, messageId, newMessageText, ParseMode.Markdown, newKeyboard, cancellationToken);
                // If edit was successful, and it was a refresh that *did* change content, no specific answer needed beyond the visual update.
                // The main HandleAsync already did an initial ack.
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException apiEx) when (apiEx.Message.Contains("message is not modified"))
            {
                _logger.LogInformation("Message for {Symbol} not modified (data likely unchanged).", symbol);
                if (isRefresh) // Only provide "up to date" feedback if it was an explicit refresh action
                {
                    try
                    {
                        // This is where we specifically answer for a refresh that yielded no change.
                        await _messageSender.AnswerCallbackQueryAsync(callbackQueryId, "Data is already up to date.", showAlert: false, cancellationToken: CancellationToken.None);
                    }
                    catch (Exception ackEx) { _logger.LogWarning(ackEx, "Failed to send 'no new data' ack for refresh."); }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error rendering market analysis for {Symbol}", symbol);
                var errorKeyboard = GetMarketAnalysisKeyboard(symbol); // Get keyboard with refresh/change options
                await _messageSender.EditMessageTextAsync(chatId, messageId, $"❌ Error displaying data for *{symbol}*.", ParseMode.Markdown, errorKeyboard, cancellationToken);
                // Consider if an AnswerCallbackQuery is needed here.
            }
        }
        private string FormatMarketAnalysisMessage(MarketData data)
        {
            var priceChangeEmoji = data.Change24h >= 0 ? "📈" : "📉";
            var trendEmoji = data.Trend switch
            {
                "Strong Uptrend" => "🚀",
                "Strong Downtrend" => "📉",
                "Weak Uptrend" => "↗️",
                "Weak Downtrend" => "↘️",
                _ => "➡️"
            };
            var sentimentEmoji = data.MarketSentiment switch
            {
                "Extremely Bullish" => "🟢🟢",
                "Extremely Bearish" => "🔴🔴",
                "Bullish" => "🟢",
                "Bearish" => "🔴",
                _ => "⚪"
            };

            return $"*{data.CurrencyName} Market Analysis*\n" +
                   $"_{data.Description}_\n\n" +
                   $"*Current Market Status:*\n" +
                   $"💰 Price: `{data.Price:N5}` {priceChangeEmoji}\n" +
                   $"📊 24h Change: `{data.Change24h:N2}%`\n" +
                   $"💎 Volume: `{data.Volume:N0}`\n" +
                   $"📈 Trend: {data.Trend} {trendEmoji}\n" +
                   $"🎯 Market Sentiment: {data.MarketSentiment} {sentimentEmoji}\n\n" +
                   $"*Technical Analysis:*\n" +
                   $"📊 RSI: `{data.RSI:N2}` ({GetRSIInterpretation(data.RSI)})\n" +
                   $"📈 MACD: {data.MACD}\n" +
                   $"🎯 Support: `{data.Support:N5}`\n" +
                   $"🎯 Resistance: `{data.Resistance:N5}`\n" +
                   $"📊 Volatility: `{data.Volatility:N2}%`\n\n" +
                   $"*Market Insights:*\n" +
                   string.Join("\n", data.Insights.Select(i => $"• {i}")) + "\n\n" +
                   $"*Last Updated:* {data.LastUpdated:g} UTC";
        }

        private InlineKeyboardMarkup GetMarketAnalysisKeyboard(string symbol)
        {
            return new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        "🔄 Refresh Analysis",
                        $"{RefreshMarketDataCallback}:{symbol}")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        "💱 Change Currency", // This button should take user back to the currency selection menu
                        MarketAnalysisCallback), // <<< --- THIS IS THE KEY CHANGE FOR "CHANGE CURRENCY" BUTTON
                    InlineKeyboardButton.WithCallbackData(
                        "📰 Fundamental News", // Changed from "Technical View" based on recent context
                        $"{FundamentalAnalysisCallbackHandler.ViewFundamentalAnalysisPrefix}:{symbol}")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        "🏠 Back to Main Menu",
                        "show_main_menu") // Assuming 'show_main_menu' is handled elsewhere
                }
            });
        }

        private string GetRSIInterpretation(decimal rsi)
        {
            return rsi switch
            {
                > 70 => "Overbought",
                < 30 => "Oversold",
                _ => "Neutral"
            };
        }
    }
}