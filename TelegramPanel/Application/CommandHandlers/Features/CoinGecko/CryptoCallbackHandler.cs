// -----------------
// OVERHAULED FILE
// -----------------
using Application.Features.CoinGecko.Interfaces;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.CommandHandlers.MainMenu;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters;
using TelegramPanel.Infrastructure;
using TelegramPanel.Infrastructure.Helpers;

namespace TelegramPanel.Application.CommandHandlers.Features.CoinGecko
{
    /// <summary>
    /// Handles all callback queries for the paginated CoinGecko cryptocurrency market list with an enhanced UI.
    /// </summary>
    public class CryptoCallbackHandler : ITelegramCallbackQueryHandler
    {
        private readonly ILogger<CryptoCallbackHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly ICoinGeckoService _coinGeckoService;

        public const string CallbackPrefix = "coingecko";
        private const string ListAction = "list";
        private const string DetailsAction = "details";
        private const int CoinsPerPage = 5; // Reduced for a more compact and readable list view

        private static readonly Dictionary<string, string> CoinEmojis = new()
        {
            { "btc", "🟠" }, { "eth", "🔷" }, { "usdt", "💲" }, { "bnb", "🟡" }, { "sol", "🟣" },
            { "xrp", "🔵" }, { "usdc", "💲" }, { "doge", "🐕" }, { "ada", "🟪" }, { "trx", "🔴" },
        };

        public CryptoCallbackHandler(
            ILogger<CryptoCallbackHandler> logger,
            ITelegramMessageSender messageSender,
            ICoinGeckoService coinGeckoService)
        {
            _logger = logger;
            _messageSender = messageSender;
            _coinGeckoService = coinGeckoService;
        }

        public bool CanHandle(Update update) => update.CallbackQuery?.Data?.StartsWith(CallbackPrefix) == true;

        public async Task HandleAsync(Update update, CancellationToken cancellationToken)
        {
            var callbackQuery = update.CallbackQuery;
            if (callbackQuery?.Message == null || callbackQuery.Data == null) return;

            try
            {
                await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);

                var parts = callbackQuery.Data.Split('_');
                string action = parts.Length > 1 ? parts[1] : ListAction;
                string payload = parts.Length > 2 ? parts[2] : "1";

                switch (action)
                {
                    case ListAction:
                        int.TryParse(payload, out int page);
                        await HandleShowCoinListAsync(callbackQuery, Math.Max(1, page), cancellationToken);
                        break;
                    case DetailsAction:
                        await HandleShowDetailsAsync(callbackQuery, payload, cancellationToken);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in CoinGeckoCallbackHandler for data: {CallbackData}", callbackQuery.Data);
                var safeError = TelegramMessageFormatter.EscapeMarkdownV2("An unexpected error occurred. Please try again.");
                await _messageSender.SendTextMessageAsync(callbackQuery.Message.Chat.Id, safeError, ParseMode.MarkdownV2, cancellationToken: cancellationToken);
            }
        }

        /// <summary>
        /// Fetches and displays a paginated, dashboard-style list of cryptocurrencies.
        /// </summary>
        private async Task HandleShowCoinListAsync(CallbackQuery callbackQuery, int page, CancellationToken cancellationToken)
        {
            var chatId = callbackQuery.Message!.Chat.Id;
            var messageId = callbackQuery.Message.MessageId;

            await _messageSender.EditMessageTextAsync(chatId, messageId, "⏳ Fetching cryptocurrency markets...", cancellationToken: cancellationToken);

            var result = await _coinGeckoService.GetCoinMarketsAsync(page, CoinsPerPage, cancellationToken);

            if (!result.Succeeded || result.Data == null || !result.Data.Any())
            {
                // Handle case where there are no results (either API error or end of list)
                var errorText = page > 1 ? "ℹ️ No more coins to display." : "❌ Could not retrieve crypto market data.";
                var errorKeyboardRows = new List<List<InlineKeyboardButton>>
                {
                    new List<InlineKeyboardButton>
                    {
                        InlineKeyboardButton.WithCallbackData("🔄 Retry", $"{CallbackPrefix}_{ListAction}_1"),
                        InlineKeyboardButton.WithCallbackData("🏠 Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral)
                    }
                };
                if (page > 1)
                {
                    errorKeyboardRows.Insert(0, new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("⬅️ Previous Page", $"{CallbackPrefix}_{ListAction}_{page - 1}") });
                }
                await _messageSender.EditMessageTextAsync(chatId, messageId, errorText, replyMarkup: new InlineKeyboardMarkup(errorKeyboardRows), cancellationToken: cancellationToken);
                return;
            }

            // --- UI Overhaul: Build the dashboard in the message text ---
            var sb = new StringBuilder();
            sb.AppendLine($"🪙 *Crypto Markets Dashboard* `(Page {page})`");
            sb.AppendLine("`----------------------------------`");

            var culture = CultureInfo.InvariantCulture;

            foreach (var coin in result.Data)
            {
                string emoji = CoinEmojis.TryGetValue(coin.Symbol.ToLower(), out var e) ? e : "🔹";
                string priceFormat = (coin.CurrentPrice.HasValue && coin.CurrentPrice < 0.01 && coin.CurrentPrice > 0) ? "N8" : "N4";
                string price = coin.CurrentPrice?.ToString(priceFormat, culture) ?? "N/A";
                string changeEmoji = (coin.PriceChangePercentage24h ?? 0) >= 0 ? "📈" : "📉";
                string change = $"{coin.PriceChangePercentage24h:F2}%";

                sb.AppendLine($"{emoji} *{TelegramMessageFormatter.EscapeMarkdownV2(coin.Name)}* `({coin.Symbol.ToUpper()})`");
                sb.AppendLine($"  Price: `{price}` USD {changeEmoji} `{change}`");
                sb.AppendLine(); // Add a blank line for spacing
            }
            sb.AppendLine("Select a coin below for full details.");

            // --- Button Logic: Buttons are now just for selection ---
            var keyboardRows = new List<List<InlineKeyboardButton>>();
            var buttonRow = new List<InlineKeyboardButton>();
            foreach (var coin in result.Data)
            {
                buttonRow.Add(InlineKeyboardButton.WithCallbackData(coin.Symbol.ToUpper(), $"{CallbackPrefix}_{DetailsAction}_{coin.Id}"));
                if (buttonRow.Count == 5) // 5 selection buttons per row
                {
                    keyboardRows.Add(buttonRow);
                    buttonRow = new List<InlineKeyboardButton>();
                }
            }
            if (buttonRow.Any()) keyboardRows.Add(buttonRow);


            // --- Pagination Buttons ---
            var paginationRow = new List<InlineKeyboardButton>();
            if (page > 1)
            {
                paginationRow.Add(InlineKeyboardButton.WithCallbackData("⬅️ Prev", $"{CallbackPrefix}_{ListAction}_{page - 1}"));
            }
            if (result.Data.Count == CoinsPerPage)
            {
                paginationRow.Add(InlineKeyboardButton.WithCallbackData("Next ➡️", $"{CallbackPrefix}_{ListAction}_{page + 1}"));
            }
            if (paginationRow.Any()) keyboardRows.Add(paginationRow);

            keyboardRows.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🏠 Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral) });

            var keyboard = new InlineKeyboardMarkup(keyboardRows);
            await _messageSender.EditMessageTextAsync(chatId, messageId, sb.ToString(), ParseMode.MarkdownV2, keyboard, cancellationToken);
        }

        private async Task HandleShowDetailsAsync(CallbackQuery callbackQuery, string coinId, CancellationToken cancellationToken)
        {
            var chatId = callbackQuery.Message!.Chat.Id;
            var messageId = callbackQuery.Message.MessageId;

            var previousPage = 1;
            // This logic to find the previous page number remains the same
            var listButton = callbackQuery.Message?.ReplyMarkup?.InlineKeyboard
                .SelectMany(row => row)
                .SelectMany(button => button.CallbackData?.Split('_') ?? Array.Empty<string>())
                .SkipWhile(part => part != ListAction)
                .Skip(1)
                .FirstOrDefault();

            if (listButton != null && int.TryParse(listButton, out int pageNum))
            {
                previousPage = pageNum;
            }

            await _messageSender.EditMessageTextAsync(chatId, messageId, $"⏳ Fetching details for `{coinId}`...", ParseMode.MarkdownV2, cancellationToken: cancellationToken);

            var result = await _coinGeckoService.GetCryptoDetailsAsync(coinId, cancellationToken);

            var backKeyboard = MarkupBuilder.CreateInlineKeyboard(
                new[] { InlineKeyboardButton.WithCallbackData($"⬅️ Back to Page {previousPage}", $"{CallbackPrefix}_{ListAction}_{previousPage}") });

            if (!result.Succeeded || result.Data == null)
            {
                var errorMessage = result.Errors.FirstOrDefault() ?? "An unknown error occurred.";
                var safeErrorMessage = TelegramMessageFormatter.EscapeMarkdownV2($"❌ {errorMessage}");
                await _messageSender.EditMessageTextAsync(chatId, messageId, safeErrorMessage, ParseMode.MarkdownV2, backKeyboard, cancellationToken);
                return;
            }

            var formattedDetails = CoinGeckoCryptoFormatter.FormatCoinDetails(result.Data);

            await _messageSender.EditMessageTextAsync(chatId, messageId, formattedDetails, ParseMode.MarkdownV2, backKeyboard, cancellationToken);
        }
    }
}