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
        public string Symbol { get; set; }
        public string CurrencyName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public decimal Change24h { get; set; }
        public decimal Volume { get; set; }
        public decimal RSI { get; set; }
        public string MACD { get; set; } = string.Empty;
        public decimal Support { get; set; }
        public decimal Resistance { get; set; }
        public List<string> Insights { get; set; } = new List<string>();
        public DateTime LastUpdated { get; set; }
        public decimal Volatility { get; set; }
        public string Trend { get; set; } = string.Empty;
        public string MarketSentiment { get; set; } = string.Empty;
    }

    // CurrencyDetails class should NOT be defined here if it causes ambiguity
    // It is expected to be used from TelegramPanel.Infrastructure.Settings.CurrencyDetails
} 