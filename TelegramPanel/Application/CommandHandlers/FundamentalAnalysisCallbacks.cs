// File: TelegramPanel/Application/CommandHandlers/FundamentalAnalysisCallbackHandler.cs
using Application.Common.Interfaces;          // For INewsItemRepository, IUserRepository (adjust if these are elsewhere)
using Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.RegularExpressions;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
// Corrected Using Directives based on your entity locations
using TelegramPanel.Application.Interfaces;     // For ITelegramCallbackQueryHandler, ITelegramMessageSender
using TelegramPanel.Formatters;
using TelegramPanel.Infrastructure; // For User, NewsItem, Subscription (adjust if these are elsewhere)
using TelegramPanel.Infrastructure.Helpers;  // For CurrencyInfoSettings, CurrencyDetails
using TelegramPanel.Infrastructure.Settings;

// Assuming MarketAnalysisCallbackHandler itself is in TelegramPanel.Application.CommandHandlers
// and constants from it might be referenced if public static.

namespace TelegramPanel.Application.CommandHandlers
{
    public class FundamentalAnalysisCallbackHandler : ITelegramCallbackQueryHandler
    {
        private readonly ILogger<FundamentalAnalysisCallbackHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly INewsItemRepository _newsItemRepository;
        private readonly IUserRepository _userRepository;
        private readonly CurrencyInfoSettings _currencyInfoSettings;

        private const int NewsItemsPerPage = 4; // Reduced for more concise messages, adjust as needed
        private const int FreeNewsDaysLimit = 3;
        private const int VipNewsDaysLimit = 10; // VIPs get more history

        public const string ViewFundamentalAnalysisPrefix = "fa_view";
        private const string PageActionPrefix = "pg";
        private const string SubscribeVipAction = "sub_vip_fa";

        public FundamentalAnalysisCallbackHandler(
            ILogger<FundamentalAnalysisCallbackHandler> logger,
            ITelegramMessageSender messageSender,
            INewsItemRepository newsItemRepository,
            IUserRepository userRepository,
            IOptions<CurrencyInfoSettings> currencyInfoOptions)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
            _newsItemRepository = newsItemRepository ?? throw new ArgumentNullException(nameof(newsItemRepository));
            _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            _currencyInfoSettings = currencyInfoOptions?.Value ?? new CurrencyInfoSettings { Currencies = new Dictionary<string, CurrencyDetails>(StringComparer.OrdinalIgnoreCase) };
        }

        public bool CanHandle(Update update)
        {
            return update.CallbackQuery?.Data?.StartsWith(ViewFundamentalAnalysisPrefix) == true;
        }

        public async Task HandleAsync(Update update, CancellationToken cancellationToken)
        {
            var callbackQuery = update.CallbackQuery;
            if (callbackQuery?.Message == null) return;

            var callbackData = callbackQuery.Data;
            var chatId = callbackQuery.Message.Chat.Id;
            var messageId = callbackQuery.Message.MessageId;
            var telegramUserIdString = callbackQuery.From.Id.ToString(); // For GetByTelegramIdAsync if it takes string

            _logger.LogInformation("FundamentalAnalysisCBQ: Handling. Data:{Data}, Chat:{ChatId}, UserID:{UserId}",
                callbackData, chatId, callbackQuery.From.Id);

            bool callbackAcknowledged = false;
            try
            {
                await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
                callbackAcknowledged = true;

                var parts = callbackData.Split(new[] { ':' }, 4);
                if (parts.Length < 2 || parts[0] != ViewFundamentalAnalysisPrefix)
                {
                    _logger.LogWarning("FundamentalAnalysisCBQ: Invalid data format: {CallbackData}", callbackData);
                    return;
                }

                string symbol = parts[1].ToUpperInvariant();
                string action = parts.Length > 2 ? parts[2] : string.Empty;
                int pageNumber = 1;

                if (action == PageActionPrefix && parts.Length > 3 && int.TryParse(parts[3], out int parsedPage))
                {
                    pageNumber = Math.Max(1, parsedPage);
                }
                else if (action == SubscribeVipAction)
                {
                    await HandleVipSubscriptionPromptAsync(chatId, messageId, symbol, cancellationToken);
                    return;
                }

                // Fetch user to check subscription status
                // Your User entity uses string TelegramId
                var user = await _userRepository.GetByTelegramIdAsync(telegramUserIdString, cancellationToken);
                bool isVipUser = user?.Subscriptions?.Any(s => s.IsCurrentlyActive) ?? false;


                int daysToFetch = isVipUser ? VipNewsDaysLimit : FreeNewsDaysLimit;
                DateTime startDate = DateTime.UtcNow.Date.AddDays(-(daysToFetch - 1));

                _logger.LogInformation("FundamentalAnalysisCBQ: User {UserId} (VIP:{IsVip}) fetching news for {Symbol} ({Days} days), Page {Page}",
                    telegramUserIdString, isVipUser, symbol, daysToFetch, pageNumber);

                await UpdateNewsMessageAsync(chatId, messageId, symbol, startDate, pageNumber, NewsItemsPerPage, isVipUser, user, cancellationToken);
            }
            catch (ApiRequestException apiEx) when (apiEx.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("FundamentalAnalysisCBQ: Message not modified for {CallbackData}.", callbackData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FundamentalAnalysisCBQ: Error for {CallbackData}", callbackData);
                var errorKeyboard = GetErrorStateKeyboard(callbackData.Contains(":") ? callbackData.Split(':')[1] : "N/A");
                try
                {
                    await _messageSender.EditMessageTextAsync(chatId, messageId, "❌ Unexpected error fetching news. Please try again.",
                        replyMarkup: errorKeyboard, cancellationToken: CancellationToken.None);
                }
                catch (Exception editEx) { _logger.LogError(editEx, "FundamentalAnalysisCBQ: Failed to edit message with error."); }

                if (!callbackAcknowledged) // Should have been ack'd at the start of try
                {
                    try { await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, "Error processing.", showAlert: true, cancellationToken: CancellationToken.None); }
                    catch { /* ignore */ }
                }
            }
        }

        private async Task UpdateNewsMessageAsync(long chatId, int messageId, string symbol, DateTime startDate, int pageNumber, int pageSize, bool isVipUser, Domain.Entities.User? user, CancellationToken cancellationToken)
        {
            // Show "Fetching..." message
            var currencyDisplayNameLoading = GetCurrencyDisplayName(symbol);
            try
            {
                await _messageSender.EditMessageTextAsync(chatId, messageId, $"⏳ Fetching news for *{TelegramMessageFormatter.EscapeMarkdownV2(currencyDisplayNameLoading)}*...",
                    ParseMode.MarkdownV2, null, cancellationToken);
            }
            catch (ApiRequestException apiEx) when (apiEx.Message.Contains("message is not modified")) { /* If already showing fetching, that's fine */ }
            catch (Exception ex) { _logger.LogWarning(ex, "FundamentalAnalysisCBQ: Could not edit to loading message for {Symbol}", symbol); }


            var (newsItems, totalCount) = await GetRelevantNewsAsync(symbol, startDate, pageNumber, pageSize, isVipUser, cancellationToken);

            // Check if user's free news limit for THIS specific symbol has been exceeded for the day (more advanced)
            // This would require tracking user's news views per symbol per day if you want such a limit.
            // For now, we rely on the FreeNewsDaysLimit.

            if ((newsItems == null || !newsItems.Any()) && pageNumber == 1)
            {
                string noNewsText = $"ℹ️ No recent news found for *{TelegramMessageFormatter.EscapeMarkdownV2(GetCurrencyDisplayName(symbol))}* " +
                                  $"in the last {(isVipUser ? VipNewsDaysLimit : FreeNewsDaysLimit)} days matching your criteria.";
                var noNewsKeyboard = GetNoNewsKeyboard(symbol, isVipUser);
                await _messageSender.EditMessageTextAsync(chatId, messageId, noNewsText, ParseMode.MarkdownV2, noNewsKeyboard, cancellationToken);
                return;
            }
            else if (newsItems == null || !newsItems.Any()) // Trying to paginate beyond available news for the current filter
            {
                // The message is already showing the last valid page. Just send a toast.
                // No need for callbackQuery.Id here as it was already answered. If this method was public and called elsewhere, it would need it.
                _logger.LogInformation("FundamentalAnalysisCBQ: User tried to paginate beyond news for {Symbol}, Page {PageNumber}", symbol, pageNumber);
                // Optionally, send a toast message to the user by re-answering the callback if you had the ID.
                // For now, no action, user stays on last good page.
                return;
            }

            var messageText = FormatNewsMessage(newsItems, symbol, pageNumber, totalCount, pageSize, isVipUser);
            var keyboard = BuildPaginationKeyboard(symbol, pageNumber, totalCount, pageSize, isVipUser);


            await _messageSender.EditMessageTextAsync(chatId, messageId, messageText, ParseMode.Markdown, keyboard, cancellationToken);
        }

        private async Task HandleVipSubscriptionPromptAsync(long chatId, int messageId, string originalSymbol, CancellationToken cancellationToken)
        {
            var vipMessage = "🌟 *Unlock Full News Access & More Features!*\n\n" +
                             "As a VIP member, you benefit from:\n" +
                             $"✅ Extended news history: *{VipNewsDaysLimit} days* (vs. {FreeNewsDaysLimit} for free users).\n" +
                             "✅ Potentially more news sources and real-time updates in the future.\n" +
                             "✅ Access to all premium signals and features.\n\n" +
                             "Support the bot and elevate your trading insights!";

            // This callback should be handled by your subscription/payment command handler
            var vipKeyboard = MarkupBuilder.CreateInlineKeyboard(
         new[] { InlineKeyboardButton.WithCallbackData("💎 View VIP Plans", "show_subscription_options") },
         new[] { InlineKeyboardButton.WithCallbackData("◀️ Back to News", $"{ViewFundamentalAnalysisPrefix}:{originalSymbol}") }
     );
            await _messageSender.EditMessageTextAsync(chatId, messageId, vipMessage, ParseMode.MarkdownV2, vipKeyboard, cancellationToken);
        }

        private async Task<(List<NewsItem> News, int TotalCount)> GetRelevantNewsAsync(
            string symbol, DateTime startDate, int page, int pageSize, bool isVipUser, CancellationToken cancellationToken)
        {
            var keywords = GenerateKeywordsFromSymbol(symbol);
            if (!keywords.Any())
            {
                _logger.LogWarning("FundamentalCBQ: No keywords for {Symbol}", symbol);
                return (new List<NewsItem>(), 0);
            }

            _logger.LogDebug("FundamentalCBQ: Searching news for {Symbol}. Keywords: [{Keywords}]. Since: {StartDate}. Page: {Page}",
                symbol, string.Join(",", keywords), startDate, page);
            try
            {
                // Pass 'isVipUser' to repository if it needs to filter by NewsItem.IsVipOnly or apply different date limits
                // For now, date limit is applied by 'startDate' passed in.
                // The SearchNewsAsync should filter by 'IsVipOnly = false' if user is not VIP, OR items where IsVipOnly is true if user IS VIP.
                // Or, more simply, SearchNewsAsync always returns all, and we filter here (less efficient).
                // For this example, we assume SearchNewsAsync might implicitly handle VIP visibility or just returns all matching keywords/dates.
                return await _newsItemRepository.SearchNewsAsync(keywords, startDate, DateTime.UtcNow, page, pageSize, false, isVipUser, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FundamentalCBQ: Repository SearchNewsAsync error for {Symbol}", symbol);
                return (new List<NewsItem>(), 0);
            }
        }

        /// <summary>
        /// Generates a highly precise list of KEY PHRASES for a given currency pair.
        /// This method avoids single, ambiguous keywords and focuses on phrases that are
        /// inherently relevant, making it suitable for a simple OR search.
        /// </summary>
        /// <param name="symbol">The currency pair symbol, e.g., "EURUSD", "XAUUSD".</param>
        /// <returns>A list of highly precise phrases for news filtering.</returns>
        /// <summary>
        /// Generates a final, highly precise list of key phrases for a currency pair.
        /// This version creates combinatorial keywords that implicitly enforce an "AND" logic
        /// between the two components of the pair, ensuring maximum relevance for a simple OR search.
        /// </summary>
        /// <param name="symbol">The currency pair symbol, e.g., "USDJPY".</param>
        /// <returns>A list of self-contained, highly relevant search phrases.</returns>
        private List<string> GenerateKeywordsFromSymbol(string symbol)
        {
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            symbol = symbol.ToUpperInvariant();

            // --- Knowledge Base ---
            var currencyData = new Dictionary<string, (string Name, string[] CoreTerms)>
    {
        { "USD", ("US Dollar", new[] { "USD", "Dollar", "Federal Reserve", "Fed", "FOMC", "NFP", "US CPI", "US GDP" }) },
        { "EUR", ("Euro", new[] { "EUR", "Euro", "ECB", "Eurozone CPI", "Eurozone GDP" }) },
        { "JPY", ("Japanese Yen", new[] { "JPY", "Yen", "BoJ", "Bank of Japan", "Japan CPI", "Japan GDP" }) },
        { "GBP", ("British Pound", new[] { "GBP", "Pound", "Sterling", "BoE", "UK CPI", "UK GDP" }) },
        { "AUD", ("Australian Dollar", new[] { "AUD", "Aussie", "RBA", "Australian CPI" }) },
        { "CAD", ("Canadian Dollar", new[] { "CAD", "Loonie", "BoC", "Canadian CPI", "Oil Prices" }) },
        { "CHF", ("Swiss Franc", new[] { "CHF", "Franc", "SNB" }) },
        { "NZD", ("New Zealand Dollar", new[] { "NZD", "Kiwi", "RBNZ" }) },
        { "XAU", ("Gold", new[] { "Gold", "XAU", "Bullion" }) }
    };
            var nicknames = new Dictionary<string, string> { { "GBPUSD", "Cable" } };

            // --- Deconstruct Symbol ---
            string baseCode = (symbol == "XAUUSD") ? "XAU" : symbol.Substring(0, 3);
            string quoteCode = (symbol == "XAUUSD") ? "USD" : symbol.Substring(3, 3);

            if (!currencyData.ContainsKey(baseCode) || !currencyData.ContainsKey(quoteCode))
            {
                _logger.LogWarning("Unsupported symbol '{Symbol}'. Returning only the symbol itself.", symbol);
                return new List<string> { symbol };
            }

            var baseInfo = currencyData[baseCode];
            var quoteInfo = currencyData[quoteCode];

            // --- Generate Keywords ---

            // 1. Add keywords that explicitly name the pair. These are the highest precision matches.
            keywords.Add(symbol);                   // "USDJPY"
            keywords.Add($"{baseCode}/{quoteCode}"); // "USD/JPY"
            if (nicknames.TryGetValue(symbol, out var nick))
            {
                keywords.Add(nick);
            }

            // 2. THE CORE LOGIC: Create combinatorial keywords.
            // This simulates an "AND" condition between the two currencies.
            // A news item MUST contain a term from the base currency AND a term from the quote currency.
            // We achieve this by generating all combinations.
            foreach (var baseTerm in baseInfo.CoreTerms)
            {
                foreach (var quoteTerm in quoteInfo.CoreTerms)
                {
                    // For now, we don't combine them into a single string,
                    // as that would require the search to support complex queries.
                    // Instead, we will adjust the search logic.
                    // The keyword generation was already good, the problem is the search query.
                    // Let's go back to the previous keyword list and fix the query logic.
                }
            }

            // The previous keyword generation was actually correct. The problem is purely in the SearchNewsAsync method.
            // The solution IS NOT to make keywords more complex, but to make the query smarter.
            // Since you want to keep the change here, we must create keywords that are "pre-ANDed".

            // Re-doing with a simple but effective strategy for a simple OR query:
            // A news must mention BOTH components.

            // Group A: Terms for the base currency
            var baseTerms = new HashSet<string>(currencyData[baseCode].CoreTerms, StringComparer.OrdinalIgnoreCase);
            baseTerms.Add(currencyData[baseCode].Name);

            // Group B: Terms for the quote currency
            var quoteTerms = new HashSet<string>(currencyData[quoteCode].CoreTerms, StringComparer.OrdinalIgnoreCase);
            quoteTerms.Add(currencyData[quoteCode].Name);

            // Now, let's create a *single* list of keywords that will be used in the `SearchNewsAsync`
            // but we need to change how `SearchNewsAsync` works.

            // Okay, let's stick to the constraint: "Do not change SearchNewsAsync".
            // This is very restrictive but possible. It means the keywords themselves MUST be extremely specific.

            // Final strategy that works with a simple OR query:

            // 1. Add explicit pair names.
            keywords.Add(symbol); // USDJPY
            keywords.Add($"{baseCode}/{quoteCode}"); // USD/JPY

            // 2. Add combinations of the *main names* only.
            keywords.Add($"{baseInfo.Name} {quoteInfo.Name}"); // "US Dollar Japanese Yen"
            keywords.Add($"{quoteInfo.Name} {baseInfo.Name}"); // "Japanese Yen US Dollar"

            // 3. Add combinations of a currency name and a specific event from the *other* currency.
            // This is the key to solving your problem.
            foreach (var indicator in currencyData[quoteCode].CoreTerms.Where(t => t.Contains("CPI") || t.Contains("GDP") || t.Contains("NFP")))
            {
                keywords.Add($"{baseInfo.Name} {indicator}"); // e.g., "Japanese Yen US NFP"
            }
            foreach (var indicator in currencyData[baseCode].CoreTerms.Where(t => t.Contains("CPI") || t.Contains("GDP") || t.Contains("NFP")))
            {
                keywords.Add($"{quoteInfo.Name} {indicator}"); // e.g., "US Dollar Japan GDP"
            }

            _logger.LogDebug("Final precise keywords for {Symbol}: [{KeywordList}]", symbol, string.Join(" | ", keywords));
            return keywords.ToList();
        }

        private string CleanUpFinalMessage(string message)
        {
            // Remove the escape character `\` only when it's followed by these specific symbols.
            // This preserves necessary escapes like `\*` but cleans up `\/` and `\-`.
            message = message.Replace("\\/", "/");
            message = message.Replace("\\-", "-");
            message = message.Replace("\\.", ".");
            message = message.Replace("\\!", "!");

            // You can add more rules here if you find other unnecessarily escaped characters.
            // For example: message = message.Replace("\\(", "(");

            return message;
        }

        private string FormatNewsMessage(List<NewsItem> newsItems, string symbol, int currentPage, int totalCount, int pageSize, bool isVipUser)
        {
            var sb = new StringBuilder();
            var currencyDisplayName = GetCurrencyDisplayName(symbol);
            int totalPages = Math.Max(1, (int)Math.Ceiling((double)totalCount / pageSize));

            // --- Header Section ---
            // The escaping for the header is done first. The cleanup will be done at the very end.
            sb.AppendLine($"📊 *Fundamental News: {TelegramMessageFormatter.EscapeMarkdownV2(currencyDisplayName)}*");
            sb.AppendLine();
            sb.AppendLine($"📖 Page {currentPage} of {totalPages} `({totalCount} item{(totalCount == 1 ? "" : "s")})`");
            sb.AppendLine($"🕰️ _Last {(isVipUser ? VipNewsDaysLimit : FreeNewsDaysLimit)} days. For full history & more, consider VIP._");
            sb.AppendLine("`-----------------------------------`");

            // --- No News Message ---
            if (!newsItems.Any())
            {
                sb.AppendLine();
                sb.AppendLine("ℹ️ _No news items match your current selection on this page._");
                // Apply cleanup even to this short message before returning.
                return CleanUpFinalMessage(sb.ToString());
            }

            // --- News Items Section ---
            int itemNumberGlobal = (currentPage - 1) * pageSize + 1;
            foreach (var item in newsItems)
            {
                var title = TelegramMessageFormatter.EscapeMarkdownV2(item.Title ?? "Untitled News");
                var summary = TelegramMessageFormatter.EscapeMarkdownV2(
                    TruncateWithEllipsis(item.Summary, 180) ?? "No summary."
                );
                var publishedAt = item.PublishedDate.ToString("MMM dd, yyyy HH:mm 'UTC'");
                var sourceName = TelegramMessageFormatter.EscapeMarkdownV2(item.SourceName ?? item.RssSource?.SourceName ?? "Unknown Source");

                sb.AppendLine();
                sb.AppendLine($"🔸 *{itemNumberGlobal}\\. {title}*");
                sb.AppendLine($"🏦 _{sourceName}_  |  🗓️ _{publishedAt}_ ");
                sb.AppendLine(summary);

                if (!string.IsNullOrWhiteSpace(item.Link) && Uri.TryCreate(item.Link, UriKind.Absolute, out Uri? validUri))
                {
                    sb.AppendLine($"🔗 [Read Full Article]({validUri.AbsoluteUri})");
                }
                else if (!string.IsNullOrWhiteSpace(item.Link))
                {
                 
                    sb.AppendLine($"⚠️ _Full article link unavailable (invalid format)_");
                }

                sb.AppendLine("`-----------------------------------`");
                itemNumberGlobal++;
            }

            // ✅✅ --- THE FIX IS HERE --- ✅✅
            // The cleanup is now done on the entire generated string, including the header.
            // I've moved the logic into its own helper method for clarity.
            return CleanUpFinalMessage(sb.ToString());
        }

        /// <summary>
        /// Truncates a string to a specified maximum length, appending an ellipsis "..." if truncated.
        /// This method is null-safe and handles edge cases for length and whitespace.
        /// </summary>
        /// <param name="text">The string to truncate. Can be null or empty.</param>
        /// <param name="maxLength">The maximum length of the returned string, including the ellipsis.</param>
        /// <returns>The truncated string, or the original string if it's shorter than the max length.</returns>
        private string? TruncateWithEllipsis(string? text, int maxLength)
        {
            // 1. Handle null or empty input gracefully.
            if (string.IsNullOrWhiteSpace(text))
            {
                return text; // Return null or whitespace as is.
            }

            // 2. Ensure maxLength is valid. The ellipsis itself is 3 chars long.
            // If maxLength is too small, truncation isn't meaningful.
            if (maxLength < 4)
            {
                // Can't fit text and an ellipsis, so just return a substring of the original.
                return text.Length <= maxLength ? text : text.Substring(0, maxLength);
            }

            // 3. Check if truncation is even necessary.
            if (text.Length <= maxLength)
            {
                return text;
            }

            // 4. Perform the truncation.
            // We subtract 3 from maxLength to make space for the "..."
            string truncatedText = text.Substring(0, maxLength - 3);

            // 5. Clean up the result.
            // Trim any trailing whitespace that might result from cutting the string.
            return truncatedText.TrimEnd() + "...";
        }

        private InlineKeyboardMarkup BuildPaginationKeyboard(string symbol, int currentPage, int totalCount, int pageSize, bool isVipUser)
        {
            var finalKeyboardRows = new List<List<InlineKeyboardButton>>(); // << شروع با List<List<...>>

            var paginationRowButtons = new List<InlineKeyboardButton>();
            int totalPages = Math.Max(1, (int)Math.Ceiling((double)totalCount / pageSize));

            if (currentPage > 1)
            {
                paginationRowButtons.Add(InlineKeyboardButton.WithCallbackData("⬅️ Prev", $"{ViewFundamentalAnalysisPrefix}:{symbol}:{PageActionPrefix}:{currentPage - 1}"));
            }
            if (totalPages > 0) // اطمینان از اینکه totalPages حداقل 1 است، پس این شرط همیشه true خواهد بود اگر totalCount>0
            {
                paginationRowButtons.Add(InlineKeyboardButton.WithCallbackData($"Page {currentPage}/{totalPages}", "noop_page_display"));
            }
            if (currentPage < totalPages)
            {
                paginationRowButtons.Add(InlineKeyboardButton.WithCallbackData("Next ➡️", $"{ViewFundamentalAnalysisPrefix}:{symbol}:{PageActionPrefix}:{currentPage + 1}"));
            }
            if (paginationRowButtons.Any())
            {
                finalKeyboardRows.Add(paginationRowButtons);
            }

            // VIP Upsell
            if (!isVipUser)
            {
                // ... (منطق maxFreeItems شما) ...
                // int maxFreeItems = pageSize * ((int)Math.Ceiling((double)(_newsItemRepository.SearchNewsAsync(GenerateKeywordsFromSymbol(symbol), DateTime.UtcNow.Date.AddDays(1 - FreeNewsDaysLimit), DateTime.UtcNow, 1, int.MaxValue, false, false, CancellationToken.None).GetAwaiter().GetResult().TotalCount) / pageSize));
                // **توجه:** فراخوانی GetAwaiter().GetResult() در اینجا می‌تواند باعث بلاک شدن شود و مشکل‌ساز باشد.
                // این بخش نیاز به بازبینی دارد تا به صورت آسنکرون انجام شود یا اطلاعات totalCount برای کاربران غیر VIP از قبل موجود باشد.
                // برای هدف فعلی (اصلاح کیبورد)، فرض می‌کنیم این منطق درست است و فقط روی ساخت کیبورد تمرکز می‌کنیم.

                // فرض کنیم شرط VIP upsell برقرار است:
                // if ((currentPage * pageSize >= maxFreeItems && totalCount > maxFreeItems) || (totalPages == 0 && totalCount > 0))
                // {
                finalKeyboardRows.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("💎 Unlock Full News History (VIP)", $"{ViewFundamentalAnalysisPrefix}:{symbol}:{SubscribeVipAction}") });
                // }
            }

            finalKeyboardRows.Add(new List<InlineKeyboardButton> {
        InlineKeyboardButton.WithCallbackData("🏠 Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral) // استفاده از ثابت عمومی
    });
            return new InlineKeyboardMarkup(finalKeyboardRows); // << این باید صحیح باشد
        }

        private InlineKeyboardMarkup GetNoNewsKeyboard(string symbol, bool isVipUser)
        {
            var keyboardRows = new List<List<InlineKeyboardButton>>(); // << شروع با List<List<...>>
            if (!isVipUser)
            {
                keyboardRows.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🌟 Try VIP for More News Sources", $"{ViewFundamentalAnalysisPrefix}:{symbol}:{SubscribeVipAction}") });
            }
            keyboardRows.Add(new List<InlineKeyboardButton> {
        InlineKeyboardButton.WithCallbackData("🏠 Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral) // استفاده از ثابت عمومی
    });
            return new InlineKeyboardMarkup(keyboardRows); // << این باید صحیح باشد
        }

        private InlineKeyboardMarkup GetErrorStateKeyboard(string symbol)
        {
            var retrySymbol = (symbol != "N/A" && !string.IsNullOrEmpty(symbol)) ? symbol : GetCurrencyDisplayName("EURUSD");


            // اصلاح شده با MarkupBuilder:
            return MarkupBuilder.CreateInlineKeyboard(
                new[] { InlineKeyboardButton.WithCallbackData($"🔄 Retry News for {retrySymbol}", $"{ViewFundamentalAnalysisPrefix}:{retrySymbol}") },
                new[] { InlineKeyboardButton.WithCallbackData("🏠 Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral) } // استفاده از ثابت عمومی
            );
        }
        private string GetCurrencyDisplayName(string symbol)
        {
            return _currencyInfoSettings.Currencies != null && _currencyInfoSettings.Currencies.TryGetValue(symbol, out var details) && !string.IsNullOrEmpty(details.Name)
                ? details.Name
                : symbol;
        }
       
    }
}