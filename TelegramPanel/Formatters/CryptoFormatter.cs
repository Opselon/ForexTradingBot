using Application.DTOs.FinancialModelingPrep;
using System.Globalization;
using System.Text;

namespace TelegramPanel.Formatters
{
    /// <summary>
    /// Provides static methods for formatting cryptocurrency data into user-friendly strings for Telegram messages.
    /// This keeps the presentation logic separate from the command handlers.
    /// </summary>
    public static class CryptoFormatter
    {
        /// <summary>
        /// Formats the detailed information of a single cryptocurrency into a MarkdownV2-ready string.
        /// </summary>
        /// <param name="crypto">The CryptoQuoteDto object containing the details.</param>
        /// <returns>A formatted string ready to be sent to Telegram.</returns>
        public static string FormatCryptoDetails(CryptoQuoteDto crypto)
        {
            var sb = new StringBuilder();
            var culture = CultureInfo.InvariantCulture; // Use invariant culture for consistent number formatting

            // Header: Name and Symbol
            sb.AppendLine(TelegramMessageFormatter.Bold($"🪙 {crypto.Name ?? "Unknown"} ({crypto.Symbol})"));
            sb.AppendLine("`----------------------------------`");

            // Price and Change
            string priceEmoji = crypto.Change.HasValue && crypto.Change >= 0 ? "📈" : "📉";
            sb.AppendLine(TelegramMessageFormatter.Bold("Price Details"));
            sb.AppendLine($"Current Price: `{crypto.Price?.ToString("N4", culture)} USD` {priceEmoji}");
            sb.AppendLine($"24h Change: `{crypto.Change?.ToString("N4", culture)}` (`{crypto.ChangesPercentage?.ToString("F2", culture)}%`)");
            sb.AppendLine($"Day Range: `{crypto.DayLow?.ToString("N4", culture)}` - `{crypto.DayHigh?.ToString("N4", culture)}`");
            sb.AppendLine($"Year Range: `{crypto.YearLow?.ToString("N4", culture)}` - `{crypto.YearHigh?.ToString("N4", culture)}`");
            sb.AppendLine();

            // Market Data
            sb.AppendLine(TelegramMessageFormatter.Bold("Market Data"));
            sb.AppendLine($"Market Cap: `{crypto.MarketCap?.ToString("N0", culture)}`");
            sb.AppendLine($"Volume (24h): `{crypto.Volume?.ToString("N0", culture)}`");
            sb.AppendLine($"Avg Volume: `{crypto.AvgVolume?.ToString("N0", culture)}`");
            sb.AppendLine();

            // Technical Averages
            sb.AppendLine(TelegramMessageFormatter.Bold("Technical Averages"));
            sb.AppendLine($"50-Day Avg Price: `{crypto.PriceAvg50?.ToString("N4", culture)}`");
            sb.AppendLine($"200-Day Avg Price: `{crypto.PriceAvg200?.ToString("N4", culture)}`");
            sb.AppendLine();

            // Footer with timestamp
            if (crypto.Timestamp.HasValue)
            {
                var updateTime = DateTimeOffset.FromUnixTimeSeconds(crypto.Timestamp.Value).UtcDateTime;
                sb.AppendLine(TelegramMessageFormatter.Italic($"Data as of: {updateTime:yyyy-MM-dd HH:mm:ss} UTC"));
            }

            return sb.ToString();
        }
    }
}