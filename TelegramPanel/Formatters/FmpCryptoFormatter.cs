// -----------------
// FINAL VERSION
// -----------------
using Application.DTOs.Fmp;
using System.Globalization;
using System.Text;

namespace TelegramPanel.Formatters
{
    public static class FmpCryptoFormatter
    {
        /// <summary>
        /// Formats a full FmpQuoteDto into a rich, user-friendly string for Telegram.
        /// </summary>
        public static string FormatFmpQuoteDetails(FmpQuoteDto crypto)
        {
            StringBuilder sb = new();
            CultureInfo culture = CultureInfo.InvariantCulture;

            _ = sb.AppendLine(TelegramMessageFormatter.Bold($"💎 {crypto.Name ?? crypto.Symbol} ({crypto.Symbol.ToUpper()})"));
            _ = sb.AppendLine(TelegramMessageFormatter.Italic("(Data from Fallback Source: FMP)"));
            _ = sb.AppendLine("`----------------------------------`");

            string priceEmoji = (crypto.Change ?? 0) >= 0 ? "📈" : "📉";

            _ = sb.AppendLine(TelegramMessageFormatter.Bold("📊 Market Snapshot (USD)"));
            _ = sb.AppendLine();

            string priceFormat = (crypto.Price.HasValue && crypto.Price < 0.01m && crypto.Price > 0) ? "N8" : "N4";

            _ = sb.AppendLine($"💰 *Price:* `{crypto.Price?.ToString(priceFormat, culture) ?? "N/A"}`");

            decimal? change24h = crypto.ChangesPercentage;
            string changeText = change24h.HasValue ? (change24h >= 0 ? "+" : "") + $"{change24h:F2}%" : "N/A";
            _ = sb.AppendLine($"{priceEmoji} *24h Change:* `{changeText}`");
            _ = sb.AppendLine();

            _ = sb.AppendLine($"🧢 *Market Cap:* `${crypto.MarketCap?.ToString("N0", culture) ?? "N/A"}`");
            _ = sb.AppendLine($"🔄 *Volume (24h):* `{crypto.Volume?.ToString("N0", culture) ?? "N/A"}`");
            _ = sb.AppendLine();

            _ = sb.AppendLine(TelegramMessageFormatter.Bold("Daily Range"));
            _ = sb.AppendLine($"🔼 *High:* `{crypto.DayHigh?.ToString(priceFormat, culture) ?? "N/A"}`");
            _ = sb.AppendLine($"🔽 *Low:* `{crypto.DayLow?.ToString(priceFormat, culture) ?? "N/A"}`");

            return sb.ToString();
        }
    }
}