// File: TelegramPanel/Infrastructure/Helpers/MarkupBuilder.cs (یا یک مسیر مناسب دیگر)
using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramPanel.Infrastructure.Helper // یا یک namespace مناسب دیگر
{
    public static class MarkupBuilder
    {
        /// <summary>
        /// Creates an InlineKeyboardMarkup from an array of button rows,
        /// ensuring it uses List<List<InlineKeyboardButton>> internally
        /// for better Hangfire serialization.
        /// </summary>
        /// <param name="buttonRows">An array where each element is an array of InlineKeyboardButtons representing a row.</param>
        /// <returns>An InlineKeyboardMarkup.</returns>
        public static InlineKeyboardMarkup? CreateInlineKeyboard(params InlineKeyboardButton[][] buttonRows)
        {
            if (buttonRows == null || !buttonRows.Any())
            {
                return null; // or new InlineKeyboardMarkup(new List<List<InlineKeyboardButton>>());
            }

            var rowsAsList = new List<List<InlineKeyboardButton>>();
            foreach (var rowArray in buttonRows)
            {
                if (rowArray != null)
                {
                    rowsAsList.Add(new List<InlineKeyboardButton>(rowArray));
                }
            }
            return new InlineKeyboardMarkup(rowsAsList);
        }

        /// <summary>
        /// Overload to create an InlineKeyboardMarkup from a single row of buttons.
        /// </summary>
        public static InlineKeyboardMarkup? CreateInlineKeyboard(params InlineKeyboardButton[] singleRowButtons)
        {
            return singleRowButtons == null || !singleRowButtons.Any() ? null : CreateInlineKeyboard(new[] { singleRowButtons });
        }

        /// <summary>
        /// Overload to handle IEnumerable of IEnumerables, converting them to List of Lists.
        /// This is useful if you are dynamically building rows.
        /// </summary>
        public static InlineKeyboardMarkup? CreateInlineKeyboard(IEnumerable<IEnumerable<InlineKeyboardButton>> buttonRows)
        {
            if (buttonRows == null || !buttonRows.Any())
            {
                return null;
            }

            var rowsAsList = new List<List<InlineKeyboardButton>>();
            foreach (var rowEnum in buttonRows)
            {
                if (rowEnum != null)
                {
                    rowsAsList.Add(new List<InlineKeyboardButton>(rowEnum));
                }
            }
            return new InlineKeyboardMarkup(rowsAsList);
        }
    }
}