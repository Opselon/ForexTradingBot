using System.Text;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters;
using TelegramPanel.Infrastructure;
using TelegramPanel.Infrastructure.Services;

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
            if (update.CallbackQuery == null) return;

            try
            {
                var callbackData = update.CallbackQuery.Data;
                var chatId = update.CallbackQuery.Message?.Chat.Id;
                var messageId = update.CallbackQuery.Message?.MessageId;

                if (chatId == null || messageId == null)
                {
                    _logger.LogWarning("Missing chat ID or message ID in callback query");
                    return;
                }

                // Handle currency selection
                if (callbackData.StartsWith(SelectCurrencyCallback))
                {
                    var selectedSymbol = callbackData.Contains(":") ? callbackData.Split(':')[1] : null;
                    if (!string.IsNullOrEmpty(selectedSymbol))
                    {
                        await ShowMarketAnalysis(chatId.Value, messageId.Value, selectedSymbol, cancellationToken);
                        return;
                    }
                }

                // Handle refresh request
                if (callbackData.StartsWith(RefreshMarketDataCallback))
                {
                    var symbol = callbackData.Split(':')[1];
                    await ShowMarketAnalysis(chatId.Value, messageId.Value, symbol, cancellationToken);
                    return;
                }

                // Show currency selection menu
                await ShowCurrencySelectionMenu(chatId.Value, messageId.Value, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling market analysis callback");
                if (update.CallbackQuery?.Message?.Chat.Id != null)
                {
                    await _messageSender.SendTextMessageAsync(
                        update.CallbackQuery.Message.Chat.Id,
                        "❌ Sorry, there was an error processing your request. Please try again later.",
                        cancellationToken: cancellationToken);
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

        private async Task ShowMarketAnalysis(long chatId, int messageId, string symbol, CancellationToken cancellationToken)
        {
            try
            {
                var marketData = await _marketDataService.GetMarketDataAsync(symbol, cancellationToken);
                var message = FormatMarketAnalysisMessage(marketData);
                var keyboard = GetMarketAnalysisKeyboard(symbol);

                await _messageSender.EditMessageTextAsync(
                    chatId,
                    messageId,
                    message,
                    ParseMode.Markdown,
                    keyboard,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error showing market analysis for {Symbol}", symbol);
                await _messageSender.EditMessageTextAsync(
                    chatId,
                    messageId,
                    $"❌ Error fetching market data for {symbol}. Please try again later.",
                    ParseMode.Markdown,
                    null,
                    cancellationToken);
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
                        "💱 Change Currency",
                        SelectCurrencyCallback),
                    InlineKeyboardButton.WithCallbackData(
                        "📊 Technical View",
                        $"technical_view:{symbol}")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        "🏠 Back to Main Menu",
                        "show_main_menu")
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