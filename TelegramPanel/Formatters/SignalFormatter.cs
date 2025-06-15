using Application.DTOs; // برای SignalDto
using System.Text;
using Telegram.Bot.Types.Enums;

namespace TelegramPanel.Formatters
{
    public static class SignalFormatter
    {
        public static string FormatSignal(SignalDto signal, ParseMode parseMode = ParseMode.MarkdownV2)
        {
            StringBuilder sb = new();

            //  از ایموجی‌ها و فرمت‌بندی حرفه‌ای استفاده کنید
            string typeEmoji = signal.Type.Equals("Buy", StringComparison.OrdinalIgnoreCase) ? "🟢" : "🔴";
            _ = sb.AppendLine($"{typeEmoji} *{signal.Symbol}* - {signal.Type.ToUpper()} Signal"); // استفاده از MarkdownV2

            if (signal.Category != null)
            {
                _ = sb.AppendLine($"Category: _{signal.Category.Name}_");
            }

            _ = sb.AppendLine($"Entry Price: `{signal.EntryPrice:F4}`"); // F4 برای نمایش با 4 رقم اعشار
            _ = sb.AppendLine($"Stop Loss: `{signal.StopLoss:F4}`");
            _ = sb.AppendLine($"Take Profit: `{signal.TakeProfit:F4}`");

            if (!string.IsNullOrWhiteSpace(signal.Source))
            {
                _ = sb.AppendLine($"Source: {signal.Source}");
            }

            //  مثال برای لینک چارت (باید URL واقعی را جایگزین کنید)
            // string chartSymbol = signal.Symbol.Replace("/", "").Replace("USDT", "PERP"); // تبدیل برای لینک بایننس
            // sb.AppendLine($"[View Chart](https://www.tradingview.com/chart/?symbol=BINANCE:{chartSymbol})");
            //  یا برای فارکس:
            // string forexSymbol = signal.Symbol.Replace("/", "");
            // sb.AppendLine($"[View Chart](https://www.tradingview.com/chart/?symbol=FX:{forexSymbol})");


            if (signal.Analyses != null && signal.Analyses.Any())
            {
                _ = sb.AppendLine("\n*Analysis:*");
                foreach (SignalAnalysisDto? analysis in signal.Analyses.Take(1)) // نمایش اولین تحلیل به عنوان مثال
                {
                    _ = sb.AppendLine($"- _{analysis.AnalystName}_: {analysis.Notes[..Math.Min(analysis.Notes.Length, 100)]}...");
                }
            }
            else
            {
                _ = sb.AppendLine("\n_This signal is based on automated analysis or direct feed._");
            }

            _ = sb.AppendLine($"\nPosted: {signal.CreatedAt:yyyy-MM-dd HH:mm} UTC");

            //  فراموش نکنید که کاراکترهای خاص MarkdownV2 را escape کنید اگر از آن استفاده می‌کنید
            //  مثلاً با یک متد کمکی:
            //  return EscapeMarkdownV2(sb.ToString());
            return sb.ToString();
        }

        //  متد کمکی برای escape کردن کاراکترهای MarkdownV2 (اگر ParseMode.MarkdownV2 استفاده می‌شود)
        public static string EscapeMarkdownV2(string text)
        {
            string[] escapeChars = new[] { "_", "*", "[", "]", "(", ")", "~", "`", ">", "#", "+", "-", "=", "|", "{", "}", ".", "!" };
            string result = text;
            foreach (string? esc in escapeChars)
            {
                result = result.Replace(esc, "\\" + esc);
            }
            return result;
        }
    }
}