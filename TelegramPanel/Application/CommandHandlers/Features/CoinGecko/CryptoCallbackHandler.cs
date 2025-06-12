// -----------------
// UPGRADED UI UX FILE - VERY PRETTY EDITION (Error Fix + Refinement)
// -----------------
using Application.Common.Interfaces;
using Application.Features.Crypto.Dtos;
using Application.Features.Crypto.Interfaces;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions; // Added for clearer regex usage
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.CommandHandlers.MainMenu;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters;
using TelegramPanel.Infrastructure; // Needed for TelegramMessageFormatter etc.

// FIX: Changed namespace to match the new location
namespace TelegramPanel.Application.CommandHandlers.Features.CoinGecko
{
    public class CryptoCallbackHandler : ITelegramCallbackQueryHandler
    {
        // Record to cache generated UI (text and keyboard)
        // This matches the type expected by IMemoryCacheService<UiCacheEntry>
        public record UiCacheEntry(string Text, InlineKeyboardMarkup Keyboard);

        private readonly ILogger<CryptoCallbackHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly ICryptoDataOrchestrator _orchestrator;
        private readonly IMemoryCacheService<UiCacheEntry> _uiCache; // Cache for UI elements

        // Constants for callback data structure
        public const string CallbackPrefix = "crypto_level20";
        private const string ListAction = "list";
        private const string DetailsAction = "details";
        private const string PageParam = "page"; // Parameter name for page number
        private const string IdParam = "id";   // Parameter name for coin ID

        private const int CoinsPerPage = 8; // Increased coins per page slightly for potentially more buttons per view
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5); // Cache UI for 5 minutes

        // Emojis for visual flair - Expanded for more "prettiness"
        private static readonly Dictionary<string, string> CoinEmojis = new() {
            { "btc", "🟠" }, { "eth", "🔷" }, { "usdt", "💲" }, { "bnb", "🟡" }, { "sol", "🟣" },
            { "xrp", "🔵" }, { "usdc", "💲" }, { "doge", "🐕" }, { "ada", "🟪" }, { "trx", "🔴" },
            { "dot", "⚪" }, { "matic", "🔶" }, { "shib", "🐕" }, { "dai", "💲" }, { "bch", "💚" },
            { "link", "⛓️" }, { "ltc", "🩶" }, { "avax", "🅰️" }, { "uni", "🦄" }, { "xmr", "Ⓜ️" },
            { "atom", "⚛️" }, { "fil", "💾" }, { "etc", "🔸" }, { "vet", "🧪" }, { "xlm", "✨" },
            { "egld", "👑" }, { "icp", "🧠" }, { "sand", "🏖️" }, { "mana", "🎭" }, { "axs", "axies" },
            { "near", "🇳" }, { "ftm", "👻" }, { "algo", "🅰️" }, { "hbar", "ℏ" }, { "vtho", "🔋" }
            // Add more as needed
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

        public bool CanHandle(Update update)
        {
            return update.CallbackQuery?.Data?.StartsWith(CallbackPrefix) == true;
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken)
        {
            var callbackQuery = update.CallbackQuery;
            if (callbackQuery?.Message == null || callbackQuery.Data == null)
            {
                _logger.LogWarning("Callback query or message data is null.");
                return;
            }

            _logger.LogInformation("Handling crypto callback: {CallbackData}", callbackQuery.Data);

            await _messageSender.AnswerCallbackQueryAsync(
                callbackQuery.Id,
                "Processing...",
                showAlert: false,
                cancellationToken: cancellationToken
            );

            long chatId = callbackQuery.Message.Chat.Id;
            int messageId = callbackQuery.Message.MessageId;
            var currentMessageText = callbackQuery.Message.Text;
            var currentMessageMarkup = callbackQuery.Message.ReplyMarkup;

            var cacheKey = callbackQuery.Data;

            // 1. Check cache first
            if (_uiCache.TryGetValue(cacheKey, out var cachedUi))
            {
                _logger.LogInformation("Serving callback {CallbackData} from cache.", cacheKey);
                try
                {
                    // Only edit if the cached UI is different from the current message
                    if (cachedUi.Text != currentMessageText || !cachedUi.Keyboard.Equals(currentMessageMarkup))
                    {
                        await _messageSender.EditMessageTextAsync(
                           chatId,
                           messageId,
                           cachedUi.Text,
                           ParseMode.MarkdownV2,
                           cachedUi.Keyboard,
                           cancellationToken
                        );
                        _logger.LogInformation("Edited message with cached UI for callback {CallbackData}", cacheKey);
                    }
                    else
                    {
                        _logger.LogInformation("Cached UI is identical to current message for {CallbackData}, no edit needed.", cacheKey);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to edit message with cached UI for callback {CallbackData}. Message might be gone or changed.", cacheKey);
                }
                return; // Exit if served from cache
            }

            // Cache miss: Show loading, fetch data, build UI, cache, and send
            _logger.LogInformation("Cache miss for {CallbackData}. Fetching data.", cacheKey);
            try
            {
                // Attempt to update the message to indicate loading (only if not already loading)
                if (currentMessageText?.Trim() != "⏳ Fetching latest data...")
                {
                    await _messageSender.EditMessageTextAsync(
                        chatId,
                        messageId,
                        "⏳ Fetching latest data...",
                        cancellationToken: cancellationToken
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to edit message to loading state for callback {CallbackData}. Proceeding with fetch.", cacheKey);
            }


            try
            {
                // --- Fetch data and build UI (can result in success or specific data error UI) ---
                var parts = callbackQuery.Data.Split('_');
                string action = parts.Length > 2 ? parts[2] : string.Empty;
                var paramDict = ParseParameters(parts.Skip(3).ToArray());

                (string text, InlineKeyboardMarkup keyboard) builtUi; // Result of fetching and building

                switch (action)
                {
                    case ListAction:
                        int page = paramDict.TryGetValue(PageParam, out var pageStr) && int.TryParse(pageStr, out int parsedPage) ? parsedPage : 1;
                        if (page < 1) page = 1;
                        builtUi = await FetchAndBuildCryptoListAsync(page, cancellationToken); // Returns success or list error UI
                        break;
                    case DetailsAction:
                        string coinId = paramDict.TryGetValue(IdParam, out var idStr) ? idStr : null!;
                        int returnPage = paramDict.TryGetValue(PageParam, out var returnPageStr) && int.TryParse(returnPageStr, out int parsedReturnPage) ? parsedReturnPage : 1;
                        if (returnPage < 1) returnPage = 1;
                        if (string.IsNullOrEmpty(coinId)) throw new ArgumentException("Missing coin ID in details callback data.");
                        builtUi = await FetchAndBuildDetailsAsync(coinId, returnPage, cancellationToken); // Returns success or details error UI
                        break;
                    default:
                        _logger.LogWarning("Unknown crypto action received: {Action}", action);
                        builtUi = BuildErrorListMessage(1, $"Unknown action: {action}"); // Returns list error UI
                        break;
                }

                // --- Cache the generated UI (Success or specific Error) ---
                // This creates a UiCacheEntry from the built text and keyboard.
                var finalUi = new UiCacheEntry(builtUi.text, builtUi.keyboard);

                // _uiCache.Set automatically overwrites any existing entry for the same cacheKey.
                // This ensures that if a previous request for this key resulted in an error UI being cached,
                // and the current request succeeded and built a success UI, the success UI
                // will replace the error UI in the cache.
                // If the current request failed and built a data-specific error UI (e.g., "No more coins"),
                // that error UI will replace any previous entry (success or another error) for this key.
                // The cache duration is applied here.
                _uiCache.Set(cacheKey, finalUi, CacheDuration);
                _logger.LogInformation("Cached UI (Type: {UiType}) for callback {CallbackData}.",
                                       builtUi.text.StartsWith("💔") || builtUi.text.StartsWith("ℹ️") ? "Error" : "Success", // Simple heuristic for logging cache type
                                       cacheKey);


                // --- Update the message with the final UI ---
                // Only edit if the fetched UI is different from the current message (might be different from loading state)
                if (finalUi.Text != currentMessageText || !finalUi.Keyboard.Equals(currentMessageMarkup))
                {
                    await _messageSender.EditMessageTextAsync(
                        chatId,
                        messageId,
                        finalUi.Text,
                        ParseMode.MarkdownV2,
                        finalUi.Keyboard,
                        cancellationToken
                    );
                    _logger.LogInformation("Edited message with fetched UI for callback {CallbackData}", cacheKey);
                }
                else
                {
                    _logger.LogInformation("Fetched UI is identical to current message for {CallbackData}, no edit needed.", cacheKey);
                }
            }
            catch (Exception ex)
            {
                // --- Handle unexpected exceptions during processing ---
                // Errors caught here are system errors (parsing, nulls, etc.), NOT API fetch failures handled by BuildError*Message.
                // The error UI generated in THIS catch block ("❌ An error occurred...") is *NOT* cached by design,
                // as it represents a transient processing issue rather than a specific data state.
                _logger.LogError(ex, "Error handling crypto callback {CallbackData}", callbackQuery.Data);

                var errorText = new StringBuilder();
                errorText.AppendLine("💔 Ops! Something went wrong while processing your request.");

                var parts = callbackQuery.Data.Split('_');
                string action = parts.Length > 2 ? parts[2] : "unknown";
                var paramDict = ParseParameters(parts.Skip(3).ToArray());

                errorText.AppendLine($"_Attempted action: *{EscapeTextForTelegramMarkup(action.ToUpper())}*_{EscapeTextForTelegramMarkup(paramDict.Any() ? " with params: " + string.Join(", ", paramDict.Select(kv => $"{kv.Key}={kv.Value}")) : "")}");

                // Include exception message details carefully
                if (!string.IsNullOrEmpty(ex.Message) && !ex.Message.Contains("Failed") && !ex.Message.Contains("Object reference not set")) // Simple heuristic to exclude common generic developer errors
                {
                    errorText.AppendLine($"_Details: {EscapeTextForTelegramMarkup(ex.Message)}_");
                }
                else
                {
                    errorText.AppendLine("_Please try again or return to the main menu._");
                }

                // Build context-aware error keyboard (similar logic as before)
                InlineKeyboardMarkup errorKeyboard;
                int contextPage = paramDict.TryGetValue(PageParam, out var pStr) && int.TryParse(pStr, out int parsedP) ? parsedP : 1;
                if (contextPage < 1) contextPage = 1;

                var keyboardRows = new List<List<InlineKeyboardButton>>();
                if (action == DetailsAction)
                {
                    keyboardRows.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData($"⬅️ Back to Page {contextPage}", $"{CallbackPrefix}_{ListAction}_{PageParam}_{contextPage}") });
                }
                else if (action == ListAction && contextPage > 1)
                {
                    keyboardRows.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("⬅️ Previous Page", $"{CallbackPrefix}_{ListAction}_{PageParam}_{contextPage - 1}") });
                }
                keyboardRows.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🔄 Retry Last Action", callbackQuery.Data!) });
                keyboardRows.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🏠 Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral) });
                errorKeyboard = new InlineKeyboardMarkup(keyboardRows);

                try
                {
                    // Attempt to edit the message to show the error
                    await _messageSender.EditMessageTextAsync(
                       chatId,
                       messageId,
                       EscapeTextForTelegramMarkup(errorText.ToString()), // Escape the error text
                       ParseMode.MarkdownV2,
                       errorKeyboard,
                       cancellationToken
                   );
                    _logger.LogError("Edited message with ERROR UI from catch block for callback {CallbackData}", cacheKey);
                }
                catch (Exception editEx)
                {
                    _logger.LogError(editEx, "Failed to edit message with error UI from catch block for callback {CallbackData}. Message might be gone.", cacheKey);
                }
            }
        }

        // Helper to parse key-value parameters from callback data parts
        // Expected format after action: paramName1_paramValue1_paramName2_paramValue2...
        private Dictionary<string, string> ParseParameters(string[] parts)
        {
            Dictionary<string, string> parameters = [];
            for (int i = 0; i < parts.Length - 1; i += 2)
            {
                parameters[parts[i]] = parts[i + 1];
            }
            return parameters;
        }

        // Renamed and consolidated fetching + building for list view
        private async Task<(string text, InlineKeyboardMarkup keyboard)> FetchAndBuildCryptoListAsync(int page, CancellationToken cancellationToken)
        {
            Shared.Results.Result<List<UnifiedCryptoDto>> result = await _orchestrator.GetCryptoListAsync(page, CoinsPerPage, cancellationToken);

            if (result.Succeeded && result.Data != null)
            {
                return BuildCryptoListMessage(page, result.Data);
            }
            else
            {
                _logger.LogError("Failed to fetch crypto list for page {Page}: {Error}", page, result.Errors.FirstOrDefault());
                // Pass the page number to the error message builder
                return BuildErrorListMessage(page, result.Errors.FirstOrDefault());
            }
        }

        // Renamed and consolidated fetching + building for details view
        private async Task<(string text, InlineKeyboardMarkup keyboard)> FetchAndBuildDetailsAsync(string coinId, int returnPage, CancellationToken cancellationToken)
        {
            Shared.Results.Result<UnifiedCryptoDto> result = await _orchestrator.GetCryptoDetailsAsync(coinId, cancellationToken);

            if (result.Succeeded && result.Data != null)
            {
                // Pass the page number we should return to
                return BuildDetailsMessage(coinId, result.Data, returnPage);
            }
            else
            {
                _logger.LogError("Failed to fetch crypto details for {CoinId}: {Error}", coinId, result.Errors.FirstOrDefault());
                // Pass the page number we should return to even on error
                return BuildErrorDetailsMessage(coinId, result.Errors.FirstOrDefault(), returnPage);
            }
        }

        // Builds the text and keyboard for the crypto list page
        // Builds the text and keyboard for the crypto list page
        private (string text, InlineKeyboardMarkup keyboard) BuildCryptoListMessage(int page, List<UnifiedCryptoDto> coins)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"📊 *Crypto Markets Dashboard* `(Page {page})`"); // Added emoji
            sb.AppendLine("`----------------------------------`");

            if (!coins.Any())
            {
                sb.AppendLine("🥹 No coins found on this page or unable to retrieve."); // Added emoji
            }
            else
            {
                foreach (var coin in coins)
                {
                    // Emoji for the list entry text
                    string listEntryEmoji = CoinEmojis.TryGetValue(coin.Symbol.ToLower(), out var e) ? e : "🔹";
                    // Use appropriate price format (more decimals for low prices)
                    string priceFormat = (coin.Price.HasValue && coin.Price > 0 && coin.Price < 0.01m) ? "N8" : "N4";
                    string price = coin.Price?.ToString(priceFormat, CultureInfo.InvariantCulture) ?? "N/A";

                    // Emojis for change
                    string changeEmoji = (coin.Change24hPercentage ?? 0) >= 0 ? "📈" : "📉";
                    string change = coin.Change24hPercentage.HasValue ? $"{coin.Change24hPercentage.Value:F2}%" : "N/A";

                    // Escape coin name and symbol minimally for Markdown V2
                    string escapedName = EscapeTextForTelegramMarkup(coin.Name ?? coin.Symbol);
                    string escapedSymbol = EscapeTextForTelegramMarkup(coin.Symbol.ToUpper());

                    sb.AppendLine().AppendLine($"{listEntryEmoji} {TelegramMessageFormatter.Bold(escapedName)} ({escapedSymbol})");
                    // Price and change are inside backticks, their content isn't escaped by this helper
                    sb.AppendLine($"{EscapeTextForTelegramMarkup("  Price:")} `${price}` USD {changeEmoji} `{change}`");
                    // sb.AppendLine("`-- -- -- -- -- -- -- -- -- -- -- --`"); // Smaller separator
                }
                // Remove the last separator line if using them
                // if (sb.Length > 0)
                // {
                //      var lastLineStart = sb.ToString().LastIndexOf("`-- --");
                //      if(lastLineStart > 0) sb.Remove(lastLineStart, sb.Length - lastLineStart);
                // }

                sb.AppendLine().AppendLine("👇 Select a coin below for full details."); // Added emoji
            }

            var keyboardRows = new List<List<InlineKeyboardButton>>();
            var buttonRow = new List<InlineKeyboardButton>();

            // Add buttons for each coin on the page
            int buttonsInRow = 0;
            foreach (var coin in coins)
            {
                // --- UI/UX Improvement: Include the current page number in the details callback ---
                // This allows the back button on the details page to return here

                // FIX: Use a safe identifier for coin ID in callback data.
                // Use coin.Symbol.ToUpper() as a temporary safe identifier.
                // This is NOT a robust solution if symbols are not unique across all displayable coins.
                // A proper solution requires an ID mapping layer.
                string safeCoinIdentifier = coin.Symbol.ToUpper(); // Assuming symbols are alphanumeric and <= 64 bytes

                // If coin.Symbol isn't guaranteed safe or unique, you MUST use a mapped ID or GUID here.
                // Example using a hypothetical SafeCoinId property:
                // string safeCoinIdentifier = coin.SafeCoinId; // Requires adding SafeCoinId to UnifiedCryptoDto

                // Check if the safe identifier is valid according to Telegram's rules just in case
                if (string.IsNullOrEmpty(safeCoinIdentifier) || safeCoinIdentifier.Length > 64 || !System.Text.RegularExpressions.Regex.IsMatch(safeCoinIdentifier, @"^[a-zA-Z0-9_]+$"))
                {
                    // Log a warning and skip this button, or use a generic identifier if safeCoinIdentifier isn't valid
                    _logger.LogWarning("Coin Symbol '{Symbol}' or ID '{Id}' generated invalid callback data identifier '{SafeId}'. Skipping button.", coin.Symbol, coin.Id, safeCoinIdentifier);
                    continue; // Skip button for this coin if the identifier is invalid
                }


                var callbackData = $"{CallbackPrefix}_{DetailsAction}_{IdParam}_{safeCoinIdentifier}_{PageParam}_{page}";


                // Get the emoji for the button
                string buttonEmoji = CoinEmojis.TryGetValue(coin.Symbol.ToLower(), out var e) ? e : "🔹";
                // Create the button text by prepending the emoji
                // The symbol itself is usually safe to use directly as button text
                string buttonText = $"{buttonEmoji} {coin.Symbol.ToUpper()}";

                buttonRow.Add(InlineKeyboardButton.WithCallbackData(buttonText, callbackData));
                buttonsInRow++;
                // Limit row width for better display
                if (buttonsInRow >= 4) // Adjusted button per row count slightly
                {
                    keyboardRows.Add(buttonRow);
                    buttonRow = new List<InlineKeyboardButton>();
                    buttonsInRow = 0;
                }
            }
            // Add any remaining buttons
            if (buttonRow.Any())
            {
                keyboardRows.Add(buttonRow);
            }

            // Pagination buttons
            var paginationRow = new List<InlineKeyboardButton>();
            if (page > 1)
            {
                // Callback for previous page - Uses only safe characters and integers
                paginationRow.Add(InlineKeyboardButton.WithCallbackData("⬅️ Prev", $"{CallbackPrefix}_{ListAction}_{PageParam}_{page - 1}"));
            }
            // Only show 'Next' if we got a full page of results
            if (coins.Count == CoinsPerPage)
            {
                // Callback for next page - Uses only safe characters and integers
                paginationRow.Add(InlineKeyboardButton.WithCallbackData("Next ➡️", $"{CallbackPrefix}_{ListAction}_{PageParam}_{page + 1}"));
            }
            if (paginationRow.Any())
            {
                keyboardRows.Add(paginationRow);
            }

            // Add a footer note for data source disclaimer (Plain text)
            sb.AppendLine().AppendLine("Data sources may vary and might be slightly delayed.");

            // Main menu button at the bottom - ASSUMING MenuCallbackQueryHandler.BackToMainMenuGeneral is a VALID callback data string ([a-zA-Z0-9_] only)
            keyboardRows.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🏠 Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral) });

            return (sb.ToString(), new InlineKeyboardMarkup(keyboardRows));
        }


        // Builds the keyboard for the details view, including the back button
        // Takes the page number to return to as input
        private InlineKeyboardMarkup GetBackKeyboard(int page)
        {
            // Create callback data to go back to the specific list page.
            // This only uses safe characters and the page number.
            var backCallbackData = $"{CallbackPrefix}_{ListAction}_{PageParam}_{page}";
            var keyboardRows = new List<List<InlineKeyboardButton>>();
            keyboardRows.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData($"⬅️ Back to Page {page}", backCallbackData) });
            // ASSUMING MenuCallbackQueryHandler.BackToMainMenuGeneral is a VALID callback data string
            keyboardRows.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🏠 Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral) }); // Add Main Menu button here too
            return new InlineKeyboardMarkup(keyboardRows);
        }

        // Builds the text and keyboard for the coin details view
        // Added 'currentPage' parameter to know where to go back
        private (string text, InlineKeyboardMarkup keyboard) BuildDetailsMessage(string coinId, UnifiedCryptoDto coin, int currentPage)
        {
            StringBuilder sb = new();

            // FIX UI/UX: Correct header formatting and apply less aggressive escaping.
            // Escape Name and Symbol minimally for literal inclusion, put the literal parentheses outside bold.
            // Example desired output: 💎 Tether (USDT) - Name is bold, symbol in parentheses is not.
            // Use the local, less aggressive escape helper for the text parts.
            string escapedName = EscapeTextForTelegramMarkup(coin.Name ?? coin.Symbol);
            string escapedSymbol = EscapeTextForTelegramMarkup(coin.Symbol.ToUpper());

            // Apply Bold formatting *after* escaping the name, then add the symbol in literal parentheses.
            _ = sb.AppendLine($"💎 {TelegramMessageFormatter.Bold(escapedName)} ({escapedSymbol})");


            // Escape data source name minimally for literal inclusion
            // Example: Data Source: CoinGecko - Italic, no extra escaping in "CoinGecko".
            _ = sb.AppendLine(TelegramMessageFormatter.Italic($"Data Source: {EscapeTextForTelegramMarkup(coin.PriceDataSource)}")); // Use local helper


            if (!string.IsNullOrWhiteSpace(coin.Description))
            {
                // Basic HTML tag removal and trimming
                string cleanDesc = Regex.Replace(coin.Description, "<.*?>", "").Trim();
                // Truncate description gracefully and add ellipsis
                if (cleanDesc.Length > 500)
                {
                    cleanDesc = cleanDesc[..500].Trim();
                    int lastSpace = cleanDesc.LastIndexOfAny(new[] { ' ', '.', ',', ';', ':', '!', '?' });
                    if (lastSpace > cleanDesc.Length * 0.7) // Only break if not too early
                    {
                        cleanDesc = cleanDesc[..lastSpace];
                    }
                    cleanDesc += "...";
                }
                // FIX UI/UX: Apply the less aggressive EscapeTextForTelegramMarkup to the description.
                // This should prevent escaping of literal () or . within the description.
                _ = sb.AppendLine().AppendLine(EscapeTextForTelegramMarkup(cleanDesc));
            }
            else
            {
                _ = sb.AppendLine().AppendLine("_No description available._"); // Italic fallback
            }

            // Separator - this seems fine as monospaced text
            _ = sb.AppendLine("`----------------------------------`");

            // Market Snapshot Header - Example: 📊 Market Snapshot (USD) - Bold, (USD) is literal.
            // FIX UI/UX: Manually build the bold part and literal part, using less aggressive escaping for "USD".
            _ = sb.AppendLine($"{TelegramMessageFormatter.Bold("📊 Market Snapshot")} ({EscapeTextForTelegramMarkup("USD")})");


            // Use appropriate price format
            string priceFormat = coin.Price.HasValue && coin.Price > 0 && coin.Price < 0.01m ? "N8" : "N4";

            // Price and other numerical data are inside backticks (` `` `), so their content doesn't need escaping *by this helper*.
            // FIX UI/UX: Apply the less aggressive EscapeTextForTelegramMarkup to the labels.
            // This should prevent escaping of literal () in labels like "Volume (24h):".
            string changeEmoji = (coin.Change24hPercentage ?? 0) >= 0 ? "📈" : "📉";

            _ = sb.AppendLine($"{EscapeTextForTelegramMarkup("💰 Price:")} `{coin.Price?.ToString(priceFormat, CultureInfo.InvariantCulture) ?? "N/A"}`");

            // Add a plus sign for positive change
            string changeSign = (coin.Change24hPercentage ?? 0) >= 0 ? "+" : "";

            // Use 'changeEmoji' and escape the label using the less aggressive helper
            _ = sb.AppendLine($"{changeEmoji} {EscapeTextForTelegramMarkup("24h Change:")} `{changeSign}{coin.Change24hPercentage:F2}%`");

            _ = sb.AppendLine($"{EscapeTextForTelegramMarkup("🧢 Market Cap:")} `${coin.MarketCap?.ToString("N0", CultureInfo.InvariantCulture) ?? "N/A"}`");
            // Label contains parentheses - use the helper that doesn't escape them
            _ = sb.AppendLine($"{EscapeTextForTelegramMarkup("🔄 Volume (24h):")} `${coin.TotalVolume?.ToString("N0", CultureInfo.InvariantCulture) ?? "N/A"}`");
            _ = sb.AppendLine($"{EscapeTextForTelegramMarkup("🔼 Day High:")} `{coin.DayHigh?.ToString(priceFormat, CultureInfo.InvariantCulture) ?? "N/A"}`");
            _ = sb.AppendLine($"{EscapeTextForTelegramMarkup("🔽 Day Low:")} `{coin.DayLow?.ToString(priceFormat, CultureInfo.InvariantCulture) ?? "N/A"}`");
            return (sb.ToString(), GetBackKeyboard(currentPage));
        }




        // FIX UI/UX: Modify the EscapeTextForTelegramMarkup helper to be LESS aggressive.
        // This version only escapes the characters that are absolutely necessary to prevent
        // Telegram from interpreting them as formatting in MOST plain text contexts,
        // while allowing common punctuation like (), ., -, :, etc., to appear literally.
        // This is a balance between strict V2 compliance and desired "pretty" output.
        private string EscapeTextForTelegramMarkup(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            StringBuilder sb = new(text.Length);
            foreach (char c in text)
            {
                // Escape characters that have special meaning in Telegram Markdown V2
                // and are likely to cause problems in common plain text scenarios.
                // We are INTENTIONALLY NOT escaping: ., !, -, +, =, {, }, (, ), etc.
                // as these frequently appear literally in text and look bad when escaped.
                // This relies on Telegram clients being lenient with these characters outside
                // of explicit Markdown syntax like links, bold, italic blocks, etc.
                // If you encounter parsing issues with specific characters later, you might need
                // to add them back here based on their context.
                switch (c)
                {
                    case '_': // Start/end italic
                    case '*': // Start/end bold
                    case '[': // Start inline link/keyboard button
                    case ']': // End inline link/keyboard button
                              // case '(': // Removed: Don't escape literal parentheses in plain text
                              // case ')': // Removed: Don't escape literal parentheses in plain text
                    case '~': // Strikethrough
                    case '`': // Code
                    case '>': // Blockquote
                    case '|': // Table column separator (if used)
                    case '\\': // Must escape the escape character itself!
                        _ = sb.Append('\\');
                        break;
                        // Characters like #, ., !, -, + are NOT escaped here to match desired output.
                }
                _ = sb.Append(c);
            }
            return sb.ToString();
        }


        // Builds an error message and keyboard for the list view
        // Added 'page' parameter to correctly link back to the same page retry/previous page if needed
        private (string text, InlineKeyboardMarkup keyboard) BuildErrorListMessage(int page, string? error = null)
        {
            StringBuilder sb = new();
            _ = sb.AppendLine("💔 Failed to load cryptocurrency list."); // Added emoji

            if (page > 1)
            {
                _ = sb.AppendLine("ℹ️ You may have reached the end, or data for this page is unavailable.");
            }

            _ = sb.AppendLine(); // Add spacing
            _ = sb.AppendLine($"_Error: {TelegramMessageFormatter.EscapeMarkdownV2(error ?? "Data sources unavailable or timed out.")}_");
            _ = sb.AppendLine("Please try again or return to the main menu.");

            List<List<InlineKeyboardButton>> keyboardRows = [];

            // Add 'Previous Page' button if applicable
            if (page > 1)
            {
                keyboardRows.Add([InlineKeyboardButton.WithCallbackData("⬅️ Previous Page", $"{CallbackPrefix}_{ListAction}_{PageParam}_{page - 1}")]);
            }

            // Add retry button for the current page
            keyboardRows.Add([InlineKeyboardButton.WithCallbackData("🔄 Retry Page", $"{CallbackPrefix}_{ListAction}_{PageParam}_{page}")]);

            // Add Main Menu button
            keyboardRows.Add([InlineKeyboardButton.WithCallbackData("🏠 Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral)]);

            return (sb.ToString(), new InlineKeyboardMarkup(keyboardRows));
        }

        // Builds an error message and keyboard for the details view
        // Added 'returnPage' parameter to know where to go back
        private (string text, InlineKeyboardMarkup keyboard) BuildErrorDetailsMessage(string coinId, string? error, int returnPage)
        {
            StringBuilder errorText = new();
            _ = errorText.AppendLine($"💔 Could not fetch details for `{TelegramMessageFormatter.EscapeMarkdownV2(coinId.ToUpper())}`."); // Added emoji, escaped ID, made uppercase
            _ = errorText.AppendLine();
            _ = errorText.AppendLine($"*Error:* {TelegramMessageFormatter.EscapeMarkdownV2(error ?? "Unavailable.")}"); // Escape error message
            _ = errorText.AppendLine("Please go back to the list or return to the main menu.");

            // --- UI/UX Improvement: Provide a back button even on error ---
            // The GetBackKeyboard function already handles adding the Main Menu button
            return (errorText.ToString(), GetBackKeyboard(returnPage));
        }

      
    }
}