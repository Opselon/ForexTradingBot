// File: TelegramPanel/Formatters/TelegramMessageFormatter.cs
namespace TelegramPanel.Formatters
{
    public static class TelegramMessageFormatter
    {
        // برای MarkdownV2، کاراکترهای خاص باید escape شوند.
        private static readonly char[] MarkdownV2EscapeChars = { '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!' };

        private static string EscapeMarkdownV2(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var result = text;
            foreach (var escChar in MarkdownV2EscapeChars)
            {
                result = result.Replace(escChar.ToString(), "\\" + escChar);
            }
            return result;
        }

        public static string Bold(string text, bool escape = true) => $"*{(escape ? EscapeMarkdownV2(text) : text)}*";
        public static string Italic(string text, bool escape = true) => $"_{(escape ? EscapeMarkdownV2(text) : text)}_";
        public static string Code(string text, bool escape = true) => $"`{(escape ? EscapeMarkdownV2(text) : text)}`"; // برای کد، escape کردن متفاوت است
        public static string Strikethrough(string text, bool escape = true) => $"~{(escape ? EscapeMarkdownV2(text) : text)}~";
        public static string Underline(string text, bool escape = true) => $"__{(escape ? EscapeMarkdownV2(text) : text)}__"; // MarkdownV2 underline

        public static string Link(string text, string url, bool escapeText = true)
        {
            var escapedText = escapeText ? EscapeMarkdownV2(text) : text;
            // URL ها نباید escape شوند مگر اینکه کاراکترهای خاصی داشته باشند که تلگرام نیاز دارد
            return $"[{escapedText}]({url})";
        }

        public static string Spoiler(string text, bool escape = true) => $"||{(escape ? EscapeMarkdownV2(text) : text)}||";

        //  می‌توانید متدهای بیشتری برای لیست‌ها، نقل قول‌ها و ... اضافه کنید.
    }
}