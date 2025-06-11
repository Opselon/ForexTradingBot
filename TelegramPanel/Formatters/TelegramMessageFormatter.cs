// File: TelegramPanel/Formatters/TelegramMessageFormatter.cs
namespace TelegramPanel.Formatters
{
    public static class TelegramMessageFormatter
    {
        // کاراکترهایی که در MarkdownV2 باید escape شوند (به جز خود بک‌اسلش که اولویت دارد)
        private static readonly char[] MarkdownV2SpecialChars = { '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!' };

        /// <summary>
        /// Escapes text for Telegram MarkdownV2.
        /// IMPORTANT: This method should be applied to plain text BEFORE applying Markdown formatting characters like *, _, `.
        /// </summary>
        public static string EscapeMarkdownV2(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var result = text;
            // 1. ابتدا خود بک‌اسلش را escape کنید تا در مراحل بعد با escape های دیگر تداخل پیدا نکند.
            result = result.Replace("\\", "\\\\");

            // 2. سپس سایر کاراکترهای خاص را escape کنید.
            foreach (var escChar in MarkdownV2SpecialChars)
            {
                result = result.Replace(escChar.ToString(), "\\" + escChar);
            }
            return result;
        }


        // ... (EscapeMarkdownV2 و MarkdownV2SpecialChars) ...

        public static string Bold(string text, bool escapePlainText = true) // ✅ نام پارامTR صحیح است
        {
            var content = escapePlainText ? EscapeMarkdownV2(text) : text;
            return $"*{content}*";
        }

        public static string Italic(string text, bool escapePlainText = true) // ✅ نام پارامتر صحیح است
        {
            var content = escapePlainText ? EscapeMarkdownV2(text) : text;
            return $"_{content}_";
        }

        public static string Code(string text)
        {
            return $"`{text}`";
        }

        public static string Strikethrough(string text, bool escapePlainText = true) // ✅ نام پارامتر صحیح است
        {
            var content = escapePlainText ? EscapeMarkdownV2(text) : text;
            return $"~{content}~";
        }

        public static string Underline(string text, bool escapePlainText = true) // ✅ نام پارامتر صحیح است
        {
            var content = escapePlainText ? EscapeMarkdownV2(text) : text;
            return $"__{content}__";
        }

        public static string Link(string text, string url, bool escapeLinkText = true) // ✅ نام پارامتر صحیح است
        {
            var linkText = escapeLinkText ? EscapeMarkdownV2(text) : text;
            string escapedUrl = url.Replace(")", "\\)");
            return $"[{linkText}]({escapedUrl})";
        }

        public static string Spoiler(string text, bool escapePlainText = true) // ✅ نام پارامتر صحیح است
        {
            var content = escapePlainText ? EscapeMarkdownV2(text) : text;
            return $"||{content}||";
        }
    }
}
