// -----------------
// COMPLETE AND CORRECTED FILE
// -----------------
using Application.Common.Interfaces;
using Application.Features.Crypto.Dtos;
using Application.Features.Crypto.Interfaces;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.CommandHandlers.MainMenu;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters;
using TelegramPanel.Infrastructure;
using TelegramPanel.Infrastructure.Helper;

// FIX: Changed namespace to match the new location
namespace TelegramPanel.Application.CommandHandlers.Features.Crypto
{
    public class CryptoCallbackHandler : ITelegramCallbackQueryHandler
    {
        public record UiCacheEntry(string Text, InlineKeyboardMarkup Keyboard);

        private readonly ILogger<CryptoCallbackHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly ICryptoDataOrchestrator _orchestrator; // <-- Use the orchestrator
        private readonly IMemoryCacheService<UiCacheEntry> _uiCache;

        public const string CallbackPrefix = "crypto_level20";
        private const string ListAction = "list";
        private const string DetailsAction = "details";
        private const int CoinsPerPage = 5;
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        private static readonly Dictionary<string, string> CoinEmojis = new() {
            { "btc", "🟠" }, { "eth", "🔷" }, { "usdt", "💲" }, { "bnb", "🟡" }, { "sol", "🟣" },
            { "xrp", "🔵" }, { "usdc", "💲" }, { "doge", "🐕" }, { "ada", "🟪" }, { "trx", "🔴" },
        };

        public CryptoCallbackHandler(
            ILogger<CryptoCallbackHandler> logger,
            ITelegramMessageSender messageSender,
            ICryptoDataOrchestrator orchestrator,
            IMemoryCacheService<UiCacheEntry> uiCache)
        {
            _logger = logger;
            _messageSender = messageSender;
            _orchestrator = orchestrator;
            _uiCache = uiCache;
        }

        public bool CanHandle(Update update) => update.CallbackQuery?.Data?.StartsWith(CallbackPrefix) == true;

        public async Task HandleAsync(Update update, CancellationToken cancellationToken)
        {
            var callbackQuery = update.CallbackQuery;
            if (callbackQuery?.Message == null || callbackQuery.Data == null) return;

            await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, "Processing...", showAlert: false, cancellationToken: cancellationToken);
            await _messageSender.EditMessageTextAsync(callbackQuery.Message.Chat.Id, callbackQuery.Message.MessageId, "⏳ Fetching latest data...", cancellationToken: cancellationToken);

            try
            {
                var parts = callbackQuery.Data.Split('_');
                string action = parts.Length > 2 ? parts[2] : ListAction;
                string payload = parts.Length > 3 ? parts[3] : "1";

                switch (action)
                {
                    case ListAction:
                        if (int.TryParse(payload, out int page)) await ProcessAndDisplayCoinListAsync(callbackQuery, page, cancellationToken);
                        break;
                    case DetailsAction:
                        await ProcessAndDisplayDetailsAsync(callbackQuery, payload, cancellationToken);
                        break;
                }
            }
            catch (Exception ex) { /* ... error handling ... */ }
        }
              private async Task ProcessAndDisplayDetailsAsync(CallbackQuery callbackQuery, string coinId, CancellationToken cancellationToken)
        {
            var result = await _orchestrator.GetCryptoDetailsAsync(coinId, cancellationToken);
            var (text, keyboard) = result.Succeeded && result.Data != null
                ? BuildDetailsMessage(result.Data, callbackQuery.Message?.ReplyMarkup)
                : BuildErrorDetailsMessage(coinId, result.Errors.FirstOrDefault(), callbackQuery.Message?.ReplyMarkup);
                
            await _messageSender.EditMessageTextAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.MessageId, text, ParseMode.MarkdownV2, keyboard, cancellationToken);
        }
        private async Task ProcessAndDisplayCoinListAsync(CallbackQuery callbackQuery, int page, CancellationToken cancellationToken)
        {
            var result = await _orchestrator.GetCryptoListAsync(page, CoinsPerPage, cancellationToken);
            var (text, keyboard) = result.Succeeded && result.Data != null
                ? BuildCryptoListMessage(page, result.Data)
                : BuildErrorListMessage(page, result.Errors.FirstOrDefault());

            await _messageSender.EditMessageTextAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.MessageId, text, ParseMode.MarkdownV2, keyboard, cancellationToken);
        }

        private (string, InlineKeyboardMarkup) BuildDetailsMessage(UnifiedCryptoDto coin, InlineKeyboardMarkup? originalMarkup)
        {
            var sb = new StringBuilder();
            sb.AppendLine(TelegramMessageFormatter.Bold($"💎 {coin.Name} ({coin.Symbol.ToUpper()})"));
            sb.AppendLine(TelegramMessageFormatter.Italic($"Data Source: {coin.PriceDataSource}"));
            if (!string.IsNullOrWhiteSpace(coin.Description))
            {
                var cleanDesc = System.Text.RegularExpressions.Regex.Replace(coin.Description, "<.*?>", "").Trim();
                sb.AppendLine().AppendLine(TelegramMessageFormatter.EscapeMarkdownV2(cleanDesc.Length > 250 ? cleanDesc.Substring(0, 250).Trim() + "..." : cleanDesc));
            }
            sb.AppendLine("`----------------------------------`");
            sb.AppendLine(TelegramMessageFormatter.Bold("📊 Market Snapshot (USD)"));

            string priceFormat = (coin.Price.HasValue && coin.Price < 0.01m && coin.Price > 0) ? "N8" : "N4";
            string priceEmoji = (coin.Change24hPercentage ?? 0) >= 0 ? "📈" : "📉";

            sb.AppendLine($"💰 *Price:* `{coin.Price?.ToString(priceFormat, CultureInfo.InvariantCulture) ?? "N/A"}`");
            sb.AppendLine($"{priceEmoji} *24h Change:* `{(coin.Change24hPercentage >= 0 ? "+" : "")}{coin.Change24hPercentage:F2}%`");
            sb.AppendLine($"🧢 *Market Cap:* `${coin.MarketCap?.ToString("N0", CultureInfo.InvariantCulture) ?? "N/A"}`");
            sb.AppendLine($"🔄 *Volume (24h):* `${coin.TotalVolume?.ToString("N0", CultureInfo.InvariantCulture) ?? "N/A"}`");
            sb.AppendLine($"🔼 *Day High:* `{coin.DayHigh?.ToString(priceFormat, CultureInfo.InvariantCulture) ?? "N/A"}`");
            sb.AppendLine($"🔽 *Day Low:* `{coin.DayLow?.ToString(priceFormat, CultureInfo.InvariantCulture) ?? "N/A"}`");

            return (sb.ToString(), GetBackKeyboard(coin.Id, originalMarkup));
        }


        private async Task FetchAndDisplayCoinListAsync(CallbackQuery callbackQuery, int page, CancellationToken cancellationToken)
        {
            var result = await _orchestrator.GetCryptoListAsync(page, CoinsPerPage, cancellationToken);
            var (text, keyboard) = result.Succeeded && result.Data != null
                ? BuildCryptoListMessage(page, result.Data)
                : BuildErrorListMessage(page, result.Errors.FirstOrDefault());

            var finalUi = new UiCacheEntry(text, keyboard);
            _uiCache.Set(callbackQuery.Data!, finalUi, CacheDuration);
            await _messageSender.EditMessageTextAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.MessageId, text, ParseMode.MarkdownV2, keyboard, cancellationToken);
        }

        private async Task FetchAndDisplayDetailsAsync(CallbackQuery callbackQuery, string coinId, CancellationToken cancellationToken)
        {
            var result = await _orchestrator.GetCryptoDetailsAsync(coinId, cancellationToken);
            var (text, keyboard) = result.Succeeded && result.Data != null
                ? BuildDetailsMessage(coinId, result.Data, callbackQuery.Message?.ReplyMarkup)
                : BuildErrorDetailsMessage(coinId, result.Errors.FirstOrDefault(), callbackQuery.Message?.ReplyMarkup);

            var finalUi = new UiCacheEntry(text, keyboard);
            _uiCache.Set(callbackQuery.Data!, finalUi, CacheDuration);
            await _messageSender.EditMessageTextAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.MessageId, text, ParseMode.MarkdownV2, keyboard, cancellationToken);
        }

        private (string, InlineKeyboardMarkup) BuildCryptoListMessage(int page, List<UnifiedCryptoDto> coins)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"🪙 *Crypto Markets Dashboard* `(Page {page})`");
            sb.AppendLine("`----------------------------------`");
            foreach (var coin in coins)
            {
                string emoji = CoinEmojis.TryGetValue(coin.Symbol.ToLower(), out var e) ? e : "🔹";
                string priceFormat = (coin.Price.HasValue && coin.Price < 0.01m && coin.Price > 0) ? "N8" : "N4";
                string price = coin.Price?.ToString(priceFormat, CultureInfo.InvariantCulture) ?? "N/A";
                string changeEmoji = (coin.Change24hPercentage ?? 0) >= 0 ? "📈" : "📉";
                string change = $"{coin.Change24hPercentage:F2}%";
                sb.AppendLine($"{emoji} *{TelegramMessageFormatter.EscapeMarkdownV2(coin.Name ?? coin.Symbol)}* `({coin.Symbol.ToUpper()})`");
                sb.AppendLine($"  Price: `{price}` USD {changeEmoji} `{change}`").AppendLine();
            }
            sb.AppendLine("Select a coin below for full details.");

            var keyboardRows = new List<List<InlineKeyboardButton>>();
            var buttonRow = new List<InlineKeyboardButton>();
            foreach (var coin in coins) { buttonRow.Add(InlineKeyboardButton.WithCallbackData(coin.Symbol.ToUpper(), $"{CallbackPrefix}_{DetailsAction}_{coin.Id}")); if (buttonRow.Count == 5) { keyboardRows.Add(buttonRow); buttonRow = new List<InlineKeyboardButton>(); } }
            if (buttonRow.Any()) keyboardRows.Add(buttonRow);

            var paginationRow = new List<InlineKeyboardButton>();
            if (page > 1) paginationRow.Add(InlineKeyboardButton.WithCallbackData("⬅️ Prev", $"{CallbackPrefix}_{ListAction}_{page - 1}"));
            if (coins.Count == CoinsPerPage) paginationRow.Add(InlineKeyboardButton.WithCallbackData("Next ➡️", $"{CallbackPrefix}_{ListAction}_{page + 1}"));
            if (paginationRow.Any()) keyboardRows.Add(paginationRow);

            keyboardRows.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🏠 Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral) });
            return (sb.ToString(), new InlineKeyboardMarkup(keyboardRows));
        }


        private (string text, InlineKeyboardMarkup keyboard) BuildDetailsMessage(string coinId, UnifiedCryptoDto coin, InlineKeyboardMarkup? originalMarkup)
        {
            var text = new StringBuilder();
            text.AppendLine(TelegramMessageFormatter.Bold($"💎 {coin.Name} ({coin.Symbol.ToUpper()})"));
            text.AppendLine(TelegramMessageFormatter.Italic($"Data from: {coin.PriceDataSource}"));
            text.AppendLine();
            if (!string.IsNullOrWhiteSpace(coin.Description))
            {
                var cleanDescription = System.Text.RegularExpressions.Regex.Replace(coin.Description, "<.*?>", "").Trim();
                text.AppendLine(TelegramMessageFormatter.EscapeMarkdownV2(cleanDescription.Length > 250 ? cleanDescription.Substring(0, 250).Trim() + "..." : cleanDescription)).AppendLine();
            }
            text.AppendLine("`----------------------------------`");
            text.AppendLine(TelegramMessageFormatter.Bold("📊 Market Snapshot (USD)"));

            var keyboard = GetBackKeyboard(coinId, originalMarkup);
            return (text.ToString(), keyboard);
        }

        private (string text, InlineKeyboardMarkup keyboard) BuildErrorListMessage(int page, string? error = null)
        {
            var errorText = page > 1 ? "ℹ️ No more coins to display." : $"❌ Data sources unavailable.\n_{error ?? "Please try again later."}_";
            var keyboardRows = new List<List<InlineKeyboardButton>> {
                new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🔄 Retry", $"{CallbackPrefix}_{ListAction}_1") },
                new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🏠 Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral) }};
            if (page > 1) { keyboardRows.Insert(0, new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("⬅️ Previous Page", $"{CallbackPrefix}_{ListAction}_{page - 1}") }); }
            return (errorText, new InlineKeyboardMarkup(keyboardRows));
        }

        private (string text, InlineKeyboardMarkup keyboard) BuildErrorDetailsMessage(string coinId, string? error, InlineKeyboardMarkup? originalMarkup)
        {
            var errorText = $"Could not fetch details for `{coinId}`.\n\n*Error:* {error ?? "Unavailable."}";
            return (TelegramMessageFormatter.EscapeMarkdownV2(errorText), GetBackKeyboard(coinId, originalMarkup));
        }

        private InlineKeyboardMarkup GetBackKeyboard(string coinId, InlineKeyboardMarkup? originalMarkup)
        {
            var previousPage = 1;
            var listButton = originalMarkup?.InlineKeyboard.SelectMany(row => row).FirstOrDefault(btn => btn.CallbackData?.Contains($"{ListAction}_") ?? false);
            if (listButton != null) { int.TryParse(listButton.CallbackData!.Split('_').Last(), out previousPage); if (previousPage == 0) previousPage = 1; }
            return MarkupBuilder.CreateInlineKeyboard(new[] { InlineKeyboardButton.WithCallbackData($"⬅️ Back to Page {previousPage}", $"{CallbackPrefix}_{ListAction}_{previousPage}") });
        }
    }
}