// -----------------
// CORRECTED FILE
// -----------------
using Application.DTOs.CoinGecko;
using Application.DTOs.Fmp;
using Application.Features.CoinGecko.Interfaces;
using Application.Features.Fmp.Interfaces;
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

namespace TelegramPanel.Application.CommandHandlers.Features.Crypto
{
    public class CryptoCallbackHandler : ITelegramCallbackQueryHandler
    {
        private readonly ILogger<CryptoCallbackHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly ICoinGeckoService _coinGeckoService;
        private readonly IFmpService _fmpService;

        public const string CallbackPrefix = "crypto_unified";
        private const string ListAction = "list";
        private const string DetailsAction = "details";
        private const int CoinsPerPage = 5;

        private static readonly Dictionary<string, string> CoinEmojis = new()
        {
            { "btc", "🟠" }, { "eth", "🔷" }, { "usdt", "💲" }, { "bnb", "🟡" }, { "sol", "🟣" },
            { "xrp", "🔵" }, { "usdc", "💲" }, { "doge", "🐕" }, { "ada", "🟪" }, { "trx", "🔴" },
        };

        public CryptoCallbackHandler(
            ILogger<CryptoCallbackHandler> logger,
            ITelegramMessageSender messageSender,
            ICoinGeckoService coinGeckoService,
            IFmpService fmpService)
        {
            _logger = logger;
            _messageSender = messageSender;
            _coinGeckoService = coinGeckoService;
            _fmpService = fmpService;
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
                // --- FIX START: Correctly parse the callback data ---
                // Expected format: "crypto_unified_action_payload"
                // parts[0] = "crypto"
                // parts[1] = "unified"
                // parts[2] = "list" or "details" (the action)
                // parts[3] = "1" or "bitcoin" (the payload)

                string action = parts.Length > 2 ? parts[2] : ListAction;
                string payload = parts.Length > 3 ? parts[3] : "1"; // Default to page 1 for list actions

                switch (action)
                {
                    case ListAction:
                        if (int.TryParse(payload, out int page))
                        {
                            await HandleShowCoinListAsync(callbackQuery, Math.Max(1, page), cancellationToken);
                        }
                        else
                        {
                            _logger.LogWarning("Invalid page number in callback data: {CallbackData}", callbackQuery.Data);
                        }
                        break;

                    case DetailsAction:
                        // The payload is the coinId string, e.g., "bitcoin"
                        await HandleShowDetailsAsync(callbackQuery, payload, cancellationToken);
                        break;

                    default:
                        _logger.LogWarning("Unknown action in callback data: {CallbackData}", callbackQuery.Data);
                        break;
                }
                // --- FIX END ---
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in CryptoCallbackHandler for data: {CallbackData}", callbackQuery.Data);
                var safeError = TelegramMessageFormatter.EscapeMarkdownV2("An unexpected error occurred. Please try again.");
                await _messageSender.SendTextMessageAsync(callbackQuery.Message.Chat.Id, safeError, ParseMode.MarkdownV2, cancellationToken: cancellationToken);
            }
        }

        // The rest of the file (HandleShowCoinListAsync, HandleShowDetailsAsync, Display... methods) remains unchanged.
        // ... (all other methods from the previous correct version) ...
        private async Task HandleShowCoinListAsync(CallbackQuery callbackQuery, int page, CancellationToken cancellationToken)
        {
            var chatId = callbackQuery.Message!.Chat.Id;
            var messageId = callbackQuery.Message.MessageId;

            await _messageSender.EditMessageTextAsync(chatId, messageId, "⏳ Fetching cryptocurrency markets...", cancellationToken: cancellationToken);

            var primaryResult = await _coinGeckoService.GetCoinMarketsAsync(page, CoinsPerPage, cancellationToken);

            if (primaryResult.Succeeded && primaryResult.Data != null && primaryResult.Data.Any())
            {
                _logger.LogInformation("Primary data source (CoinGecko) succeeded for page {Page}. Displaying results.", page);
                await DisplayCoinGeckoList(chatId, messageId, page, primaryResult.Data);
                return;
            }

            if (page == 1)
            {
                _logger.LogWarning("Primary source (CoinGecko) failed. Reason: {Error}. Attempting fallback to FMP.", primaryResult.Errors.FirstOrDefault());
                await _messageSender.EditMessageTextAsync(chatId, messageId, "⚠️ Primary data source is busy. Trying backup source...", cancellationToken: cancellationToken);

                var fallbackResult = await _fmpService.GetTopCryptosAsync(20, cancellationToken);
                if (fallbackResult.Succeeded && fallbackResult.Data != null && fallbackResult.Data.Any())
                {
                    _logger.LogInformation("Fallback source (FMP) succeeded. Displaying top results.");
                    await DisplayFmpList(chatId, messageId, fallbackResult.Data);
                    return;
                }
            }

            _logger.LogError("Could not retrieve data. Primary error: {PrimaryError}", primaryResult.Errors.FirstOrDefault());
            var errorText = page > 1 ? "ℹ️ No more coins to display from primary source." : "❌ Both data sources failed to respond. Please try again later.";
            var errorKeyboardRows = new List<List<InlineKeyboardButton>>
            {
                new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🔄 Retry", $"{CallbackPrefix}_{ListAction}_1") }
            };
            if (page > 1)
            {
                errorKeyboardRows.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("⬅️ Previous Page", $"{CallbackPrefix}_{ListAction}_{page - 1}") });
            }
            errorKeyboardRows.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🏠 Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral) });
            await _messageSender.EditMessageTextAsync(chatId, messageId, errorText, replyMarkup: new InlineKeyboardMarkup(errorKeyboardRows), cancellationToken: cancellationToken);
        }

        private async Task DisplayFmpList(long chatId, int messageId, List<FmpQuoteDto> coins)
        {
            var sb = new StringBuilder();
            sb.AppendLine("🪙 *Crypto Markets Dashboard* `(Source: FMP)`");
            sb.AppendLine("`----------------------------------`");

            var culture = CultureInfo.InvariantCulture;

            foreach (var coin in coins)
            {
                string emoji = CoinEmojis.TryGetValue(coin.Symbol.Replace("USD", "").ToLower(), out var e) ? e : "🔸";
                string priceFormat = (coin.Price.HasValue && coin.Price < 0.01m && coin.Price > 0) ? "N8" : "N4";
                string price = coin.Price?.ToString(priceFormat, culture) ?? "N/A";
                string changeEmoji = (coin.ChangesPercentage ?? 0) >= 0 ? "📈" : "📉";
                string change = $"{coin.ChangesPercentage:F2}%";

                sb.AppendLine($"{emoji} *{TelegramMessageFormatter.EscapeMarkdownV2(coin.Name ?? coin.Symbol)}* `({coin.Symbol.ToUpper()})`");
                sb.AppendLine($"  Price: `{price}` USD {changeEmoji} `{change}`");
                sb.AppendLine();
            }
            sb.AppendLine("Details view is available from our primary data source.");

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new [] { InlineKeyboardButton.WithCallbackData("🔄 Refresh (try primary)", $"{CallbackPrefix}_{ListAction}_1") },
                new [] { InlineKeyboardButton.WithCallbackData("🏠 Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral) },
            });

            await _messageSender.EditMessageTextAsync(chatId, messageId, sb.ToString(), ParseMode.MarkdownV2, keyboard);
        }

        private async Task DisplayCoinGeckoList(long chatId, int messageId, int page, List<CoinMarketDto> coins)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"🪙 *Crypto Markets Dashboard* `(Page {page})`");
            sb.AppendLine("`----------------------------------`");

            var culture = CultureInfo.InvariantCulture;

            foreach (var coin in coins)
            {
                string emoji = CoinEmojis.TryGetValue(coin.Symbol.ToLower(), out var e) ? e : "🔹";
                string priceFormat = (coin.CurrentPrice.HasValue && coin.CurrentPrice < 0.01 && coin.CurrentPrice > 0) ? "N8" : "N4";
                string price = coin.CurrentPrice?.ToString(priceFormat, culture) ?? "N/A";
                string changeEmoji = (coin.PriceChangePercentage24h ?? 0) >= 0 ? "📈" : "📉";
                string change = $"{coin.PriceChangePercentage24h:F2}%";

                sb.AppendLine($"{emoji} *{TelegramMessageFormatter.EscapeMarkdownV2(coin.Name)}* `({coin.Symbol.ToUpper()})`");
                sb.AppendLine($"  Price: `{price}` USD {changeEmoji} `{change}`");
                sb.AppendLine();
            }
            sb.AppendLine("Select a coin below for full details.");

            var keyboardRows = new List<List<InlineKeyboardButton>>();
            var buttonRow = new List<InlineKeyboardButton>();
            foreach (var coin in coins)
            {
                buttonRow.Add(InlineKeyboardButton.WithCallbackData(coin.Symbol.ToUpper(), $"{CallbackPrefix}_{DetailsAction}_{coin.Id}"));
                if (buttonRow.Count == 5)
                {
                    keyboardRows.Add(buttonRow);
                    buttonRow = new List<InlineKeyboardButton>();
                }
            }
            if (buttonRow.Any()) keyboardRows.Add(buttonRow);

            var paginationRow = new List<InlineKeyboardButton>();
            if (page > 1) paginationRow.Add(InlineKeyboardButton.WithCallbackData("⬅️ Prev", $"{CallbackPrefix}_{ListAction}_{page - 1}"));
            if (coins.Count == CoinsPerPage) paginationRow.Add(InlineKeyboardButton.WithCallbackData("Next ➡️", $"{CallbackPrefix}_{ListAction}_{page + 1}"));
            if (paginationRow.Any()) keyboardRows.Add(paginationRow);

            keyboardRows.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🏠 Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral) });

            await _messageSender.EditMessageTextAsync(chatId, messageId, sb.ToString(), ParseMode.MarkdownV2, new InlineKeyboardMarkup(keyboardRows));
        }

        private async Task HandleShowDetailsAsync(CallbackQuery callbackQuery, string coinId, CancellationToken cancellationToken)
        {
            var chatId = callbackQuery.Message!.Chat.Id;
            var messageId = callbackQuery.Message.MessageId;

            var previousPage = 1;
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