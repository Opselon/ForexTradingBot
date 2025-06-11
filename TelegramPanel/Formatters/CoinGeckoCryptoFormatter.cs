// -----------------
// OVERHAULED FILE
// -----------------
using Application.DTOs.CoinGecko;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TelegramPanel.Formatters
{
    /// <summary>
    /// Provides static methods for formatting CoinGecko cryptocurrency data into user-friendly strings for Telegram.
    /// This version focuses on a rich, emoji-heavy, and well-structured UI.
    /// </summary>
    public static class CoinGeckoCryptoFormatter
    {
        /// <summary>
        /// Formats the detailed information of a single cryptocurrency into a visually appealing MarkdownV2 string.
        /// </summary>
        public static string FormatCoinDetails(CoinDetailsDto crypto)
        {
            var sb = new StringBuilder();
            var culture = CultureInfo.InvariantCulture;

            // --- HEADER ---
            sb.AppendLine(TelegramMessageFormatter.Bold($"💎 {crypto.Name} ({crypto.Symbol.ToUpper()})"));
            sb.AppendLine();

            // --- DESCRIPTION ---
            if (crypto.Description?.TryGetValue("en", out var description) == true && !string.IsNullOrWhiteSpace(description))
            {
                var cleanDescription = Regex.Replace(description, "<.*?>", "").Trim();
                sb.AppendLine(TelegramMessageFormatter.Italic(TelegramMessageFormatter.EscapeMarkdownV2(
                    cleanDescription.Length > 250 ? cleanDescription.Substring(0, 250).Trim() + "..." : cleanDescription
                )));
                sb.AppendLine();
            }

            // --- MARKET DATA SECTION ---
            if (crypto.MarketData != null)
            {
                var md = crypto.MarketData;
                string priceEmoji = md.PriceChangePercentage24h.HasValue && md.PriceChangePercentage24h >= 0 ? "📈" : "📉";

                sb.AppendLine("`----------------------------------`");
                sb.AppendLine(TelegramMessageFormatter.Bold("📊 Market Snapshot (USD)"));
                sb.AppendLine();

                double? currentPrice = null;
                md.CurrentPrice?.TryGetValue("usd", out currentPrice);
                string priceFormat = (currentPrice.HasValue && currentPrice < 0.01 && currentPrice > 0) ? "N8" : "N4";

                sb.AppendLine($"💰 *Price:* `{currentPrice?.ToString(priceFormat, culture) ?? "N/A"}`");

                var change24h = md.PriceChangePercentage24h;
                string changeText = change24h.HasValue
                    ? (change24h >= 0 ? "+" : "") + $"{change24h:F2}%"
                    : "N/A";
                sb.AppendLine($"{priceEmoji} *24h Change:* `{changeText}`");
                sb.AppendLine();

                sb.AppendLine(TelegramMessageFormatter.Bold("Key Metrics"));

                double? marketCap = null;
                md.MarketCap?.TryGetValue("usd", out marketCap);
                sb.AppendLine($"🧢 *Market Cap:* `${marketCap?.ToString("N0", culture) ?? "N/A"}`");

                double? totalVolume = null;
                md.TotalVolume?.TryGetValue("usd", out totalVolume);
                sb.AppendLine($"🔄 *Volume (24h):* `${totalVolume?.ToString("N0", culture) ?? "N/A"}`");
                sb.AppendLine();

                sb.AppendLine(TelegramMessageFormatter.Bold("Daily Range"));
                double? high24h = null;
                double? low24h = null;
                md.High24h?.TryGetValue("usd", out high24h);
                md.Low24h?.TryGetValue("usd", out low24h);
                sb.AppendLine($"🔼 *High:* `{high24h?.ToString(priceFormat, culture) ?? "N/A"}`");
                sb.AppendLine($"🔽 *Low:* `{low24h?.ToString(priceFormat, culture) ?? "N/A"}`");
            }

            return sb.ToString();
        }
    }
}