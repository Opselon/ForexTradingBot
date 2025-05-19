using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TelegramPanel.Application.Interfaces
{
    public interface IMarketDataService
    {
        Task<MarketData> GetMarketDataAsync(string symbol, CancellationToken cancellationToken = default);
    }

    public class MarketData
    {
        public List<string> Insights { get; set; }
        public string Symbol { get; set; }
        public string CurrencyName { get; set; }
        public string Description { get; set; }
        public decimal Price { get; set; }
        public decimal Change24h { get; set; } // Percentage
        public decimal High24h { get; set; }
        public decimal Low24h { get; set; }
        public decimal Volume { get; set; }
        public decimal MarketCap { get; set; }
        public decimal RSI { get; set; }
        public string MACD { get; set; }
        public decimal Volatility { get; set; } // Percentage
        public decimal Support { get; set; }
        public decimal Resistance { get; set; }
        public string Trend { get; set; }
        public string MarketSentiment { get; set; }
        public decimal PriceChangePercentage7d { get; set; }
        public decimal PriceChangePercentage30d { get; set; }
        public string? CoinGeckoId { get; set; }
        public DateTime LastUpdated { get; set; }
        public string DataSource { get; set; }
        public bool IsPriceLive { get; set; }
        public List<string> Remarks { get; set; } = new List<string>();
    }

    // CurrencyDetails class should NOT be defined here if it causes ambiguity
    // It is expected to be used from TelegramPanel.Infrastructure.Settings.CurrencyDetails
} 