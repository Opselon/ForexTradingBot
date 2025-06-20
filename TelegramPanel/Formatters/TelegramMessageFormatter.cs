
using System.Text;
using System.Text.RegularExpressions; // For ArgumentNullException (best practice though not strictly used in these methods)

namespace TelegramPanel.Formatters
{
    /// <summary>
    /// Provides robust and flexible methods for formatting text using Telegram MarkdownV2.
    /// Focuses on correctly escaping content within formatting and supporting various MarkdownV2 entities
    /// while aiming for a balance between strictness and readable output.
    /// </summary>
    public static class TelegramMessageFormatter
    {
        // These characters MUST be escaped when they appear literally in text,
        // as they are core MarkdownV2 syntax symbols or the escape character itself.
        // This list is curated for a 'prettier' output with less clutter from escaping common punctuation,
        // relying on Telegram client leniency.
        private static readonly char[] LessAggressiveMarkdownV2SpecialChars = { '_', '*', '[', ']', '~', '`', '>', '|', '\\' };
        private static readonly Regex _markdownV2EscapeRegex =
        new(@"([_\[\]()~`>#\+\-=|{}.!*])", RegexOptions.Compiled);
        private static readonly Regex _markdownV1EscapeRegex =
       new(@"([_*`\[])", RegexOptions.Compiled);


        public static string EscapeMarkdownV1(string text)
        {
            return string.IsNullOrEmpty(text) ? string.Empty : _markdownV1EscapeRegex.Replace(text, @"\$1");
        }


        /// <summary>
        /// Escapes characters in a plain text string that have special meaning in
        /// Telegram MarkdownV2 syntax symbols or are the escape character itself.
        /// This version avoids escaping common punctuation (like (), ., !, etc.)
        /// for a 'prettier' output, but still escapes core Markdown syntax characters and the escape character itself.
        /// This method should be applied to the *content* (the text inside formatting) or any standalone plain text segment.
        /// </summary>
        /// <param name="text">The plain text string to escape.</param>
        /// <returns>The escaped string, safe to be included within Markdown V2 formatting or as standalone text.</returns>

        public static string EscapeMarkdownV2(string text)
        {
            return string.IsNullOrEmpty(text) ? string.Empty : _markdownV2EscapeRegex.Replace(text, @"\$1");
        }

        /// <summary>
        /// Formats text as Bold. Optionally escapes the plain text content before applying markers.
        /// Use escapePlainText = true for raw text input (base layer).
        /// Use escapePlainText = false for input that is already formatted or pre-escaped (higher layers).
        /// Example: `*Hello World*`
        /// </summary>
        /// <param name="text">The text to format as bold.</param>
        /// <param name="escapePlainText">If true, the input text is escaped before applying bold markers.</param>
        /// <returns>MarkdownV2 formatted string.</returns>
        public static string Bold(string? text, bool escapePlainText = true)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string content = escapePlainText ? EscapeMarkdownV2(text) : text;
            return $"*{content}*";
        }

        /// <summary>
        /// Formats text as Italic. Optionally escapes the plain text content before applying markers.
        /// Use escapePlainText = true for raw text input (base layer).
        /// Use escapePlainText = false for input that is already formatted or pre-escaped (higher layers).
        /// Example: `_Hello World_`
        /// </summary>
        /// <param name="text">The text to format as italic.</param>
        /// <param name="escapePlainText">If true, the input text is escaped before applying italic markers.</param>
        /// <returns>MarkdownV2 formatted string.</returns>
        public static string Italic(string? text, bool escapePlainText = true)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string content = escapePlainText ? EscapeMarkdownV2(text) : text;
            return $"_{content}_";
        }

        /// <summary>
        /// Formats text as Underline. Optionally escapes the plain text content before applying markers.
        /// Use escapePlainText = true for raw text input (base layer).
        /// Use escapePlainText = false for input that is already formatted or pre-escaped (higher layers).
        /// Example: `__Hello World__`
        /// </summary>
        /// <param name="text">The text to format as underline.</param>
        /// <param name="escapePlainText">If true, the input text is escaped before applying underline markers.</param>
        /// <returns>MarkdownV2 formatted string.</returns>
        public static string Underline(string? text, bool escapePlainText = true)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string content = escapePlainText ? EscapeMarkdownV2(text) : text;
            return $"__{content}__";
        }

        /// <summary>
        /// Formats text as Strikethrough. Optionally escapes the plain text content before applying markers.
        /// Use escapePlainText = true for raw text input (base layer).
        /// Use escapePlainText = false for input that is already formatted or pre-escaped (higher layers).
        /// Example: `~Hello World~`
        /// </summary>
        /// <param name="text">The text to format as strikethrough.</param>
        /// <param name="escapePlainText">If true, the input text is escaped before applying strikethrough markers.</param>
        /// <returns>MarkdownV2 formatted string.</returns>
        public static string Strikethrough(string? text, bool escapePlainText = true)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string content = escapePlainText ? EscapeMarkdownV2(text) : text;
            return $"~{content}~";
        }

        /// <summary>
        /// Formats text as Spoiler (Hidden). Optionally escapes the plain text content before applying markers.
        /// Use escapePlainText = true for raw text input (base layer).
        /// Use escapePlainText = false for input that is already formatted or pre-escaped (higher layers).
        /// Example: `||Secret Message||`
        /// </summary>
        /// <param name="text">The text to format as spoiler.</param>
        /// <param name="escapePlainText">If true, the input text is escaped before applying spoiler markers.</param>
        /// <returns>MarkdownV2 formatted string.</returns>
        public static string Spoiler(string? text, bool escapePlainText = true)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string content = escapePlainText ? EscapeMarkdownV2(text) : text;
            return $"||{content}||";
        }

        /// <summary>
        /// Formats text as Inline Code. Content inside the code markers is NOT escaped by EscapeMarkdownV2.
        /// Instead, literal backticks within the code block must be escaped.
        /// Example: `int x = 1;`
        /// </summary>
        /// <param name="text">The text to format as code.</param>
        /// <returns>MarkdownV2 formatted string.</returns>
        public static string Code(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            // Content inside ` ` is literal in MarkdownV2.
            // Only literal backticks (`) within the text need to be escaped with a backslash.
            return $"`{text.Replace("`", "\\`")}`";
        }

        /// <summary>
        /// Formats text as a Preformatted Code Block. Content inside is NOT escaped by EscapeMarkdownV2.
        /// Instead, literal triple backticks (```) within the text must be escaped.
        /// Optional: Include a language tag on the first line.
        /// Example:
        /// ```
        /// block of code
        /// ```
        /// Or:
        /// ```csharp
        /// // code
        /// ```
        /// </summary>
        /// <param name="text">The text content of the code block.</param>
        /// <param name="language">Optional. The language identifier for syntax highlighting.</param>
        /// <returns>MarkdownV2 formatted string.</returns>
        public static string Pre(string? text, string? language = null)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            // Content inside ``` ``` is literal in MarkdownV2.
            // Only literal triple backticks (```) within the text need to be escaped with a backslash (\```).
            string escapedText = text.Replace("```", "\\`\\`\\`");

            StringBuilder sb = new();
            _ = sb.Append("```");
            if (!string.IsNullOrWhiteSpace(language))
            {
                // Language tag does not need escaping, but should be trimmed and on the first line.
                _ = sb.AppendLine(language.Trim());
            }
            else
            {
                _ = sb.AppendLine(); // Newline even if no language for correct block start
            }
            _ = sb.Append(escapedText);
            _ = sb.Append("```");
            return sb.ToString();
        }


        /// <summary>
        /// Creates an inline URL link. Link text is escaped, URL has specific minimal escaping.
        /// Example: `[Link Text](https://example.com)`
        /// </summary>
        /// <param name="text">The text displayed for the link.</param>
        /// <param name="url">The URL the link points to.</param>
        /// <param name="escapeLinkText">Optionally escapes the link text content (default: true).</param>
        /// <returns>MarkdownV2 formatted string.</returns>
        public static string Link(string? text, string? url, bool escapeLinkText = true)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(url))
            {
                return string.Empty;
            }

            // Escape the link text content based on the flag
            string escapedText = escapeLinkText ? EscapeMarkdownV2(text) : text;

            // URLs in MarkdownV2 need specific escaping for '(' and ')'.
            string escapedUrl = url.Replace("(", "\\(").Replace(")", "\\)");

            return $"[{escapedText}]({escapedUrl})";
        }

        /// <summary>
        /// Creates a mention link for a user by ID. Text displayed is escaped.
        /// Example: `[Mention Text](tg://user?id=12345)`
        /// </summary>
        /// <param name="text">The text displayed for the mention.</param>
        /// <param name="userId">The Telegram User ID (long).</param>
        /// <param name="escapeMentionText">Optionally escapes the mention text content (default: true).</param>
        /// <returns>MarkdownV2 formatted string.</returns>
        public static string TextMention(string? text, long userId, bool escapeMentionText = true)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            // Escape the mention text content based on the flag
            string escapedText = escapeMentionText ? EscapeMarkdownV2(text) : text;

            // tg://user?id=... URL format does NOT need () escaping, userId is just numbers.
            return $"[{escapedText}](tg://user?id={userId})";
        }

        /// <summary>
        /// Formats text using a Custom Emoji. Text displayed is escaped.
        /// Example: `<tg-emoji emoji-id="5368324170671202286">👍</tg-emoji>`
        /// </summary>
        /// <param name="text">The text displayed, typically the standard emoji fallback.</param>
        /// <param name="emojiId">The unique ID of the custom emoji string.</param>
        /// <param name="escapeText">Optionally escapes the text content (default: true).</param>
        /// <returns>MarkdownV2 formatted string.</returns>
        public static string CustomEmoji(string? text, string? emojiId, bool escapeText = true)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(emojiId))
            {
                return string.Empty;
            }

            // Escape the text content based on the flag
            string escapedText = escapeText ? EscapeMarkdownV2(text) : text;
            // emojiId should typically only contain digits. Escape it just in case.
            string escapedEmojiId = EscapeMarkdownV2(emojiId);

            return $"<tg-emoji emoji-id=\"{escapedEmojiId}\">{escapedText}</tg-emoji>";
        }

        // Note: Combining formats like Bold(Italic("text")) is done by calling methods sequentially,
        // using `escapePlainText: false` for outer formatting methods.
        // Example: `string boldItalic = TelegramMessageFormatter.Bold(TelegramMessageFormatter.Italic("Hello World", escapePlainText: true), escapePlainText: false);`
    }
}