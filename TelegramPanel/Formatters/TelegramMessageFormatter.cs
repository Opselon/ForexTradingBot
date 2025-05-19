// File: TelegramPanel/Formatters/TelegramMessageFormatter.cs
namespace TelegramPanel.Formatters
{
    public static class TelegramMessageFormatter
    {
        // Special characters that need to be escaped in Markdown
        private static readonly char[] MarkdownSpecialChars = { '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!' };

        /// <summary>
        /// Escapes text for Markdown formatting
        /// </summary>
        public static string EscapeMarkdownV2(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var result = text;
            // First escape backslash to avoid interference with other escapes
            result = result.Replace("\\", "\\\\");

            // Then escape other special characters
            foreach (var escChar in MarkdownSpecialChars)
            {
                result = result.Replace(escChar.ToString(), "\\" + escChar);
            }
            return result;
        }

        /// <summary>
        /// Formats text as bold using Markdown syntax
        /// </summary>
        public static string Bold(string text, bool escapePlainText = true)
        {
            var content = escapePlainText ? EscapeMarkdownV2(text) : text;
            return $"*{content}*";
        }

        /// <summary>
        /// Formats text as italic using Markdown syntax
        /// </summary>
        public static string Italic(string text, bool escapePlainText = true)
        {
            var content = escapePlainText ? EscapeMarkdownV2(text) : text;
            return $"_{content}_";
        }

        /// <summary>
        /// Formats text as code using Markdown syntax
        /// </summary>
        public static string Code(string text)
        {
            return $"`{text}`";
        }

        /// <summary>
        /// Formats text as strikethrough using Markdown syntax
        /// </summary>
        public static string Strikethrough(string text, bool escapePlainText = true)
        {
            var content = escapePlainText ? EscapeMarkdownV2(text) : text;
            return $"~{content}~";
        }

        /// <summary>
        /// Creates a Markdown link
        /// </summary>
        public static string Link(string text, string url, bool escapeLinkText = true)
        {
            var linkText = escapeLinkText ? EscapeMarkdownV2(text) : text;
            string escapedUrl = url.Replace(")", "\\)");
            return $"[{linkText}]({escapedUrl})";
        }

        /// <summary>
        /// Creates a spoiler text using Markdown syntax
        /// </summary>
        public static string Spoiler(string text, bool escapePlainText = true)
        {
            var content = escapePlainText ? EscapeMarkdownV2(text) : text;
            return $"||{content}||";
        }
    }
}
