// File: TelegramPanel/Application/CommandHandlers/FundamentalAnalysisCallbackHandler.cs
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

// Corrected Using Directives based on your entity locations
using TelegramPanel.Application.Interfaces;     // For ITelegramCallbackQueryHandler, ITelegramMessageSender
using Application.Common.Interfaces;          // For INewsItemRepository, IUserRepository (adjust if these are elsewhere)
using Domain.Entities;
using TelegramPanel.Infrastructure; // For User, NewsItem, Subscription (adjust if these are elsewhere)
using TelegramPanel.Infrastructure.Settings;  // For CurrencyInfoSettings, CurrencyDetails

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
                await _messageSender.EditMessageTextAsync(chatId, messageId, $"⏳ Fetching news for *{EscapeMarkdownV2(currencyDisplayNameLoading)}*...",
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
                string noNewsText = $"ℹ️ No recent news found for *{EscapeMarkdownV2(GetCurrencyDisplayName(symbol))}* " +
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

            await _messageSender.EditMessageTextAsync(chatId, messageId, messageText, ParseMode.MarkdownV2, keyboard, cancellationToken);
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
            var vipKeyboard = new InlineKeyboardMarkup(new[]
            {
                new [] { InlineKeyboardButton.WithCallbackData("💎 View VIP Plans", "show_subscription_options") }, // Generic VIP options callback
                new [] { InlineKeyboardButton.WithCallbackData("◀️ Back to News", $"{ViewFundamentalAnalysisPrefix}:{originalSymbol}") }
            });
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

        private List<string> GenerateKeywordsFromSymbol(string symbol)
        {
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            symbol = symbol.ToUpperInvariant();

            // Add specific symbol components
            if (symbol == "XAUUSD") { keywords.Add("XAU"); keywords.Add("GOLD"); keywords.Add("USD"); }
            else if (symbol == "XAGUSD") { keywords.Add("XAG"); keywords.Add("SILVER"); keywords.Add("USD"); }
            else if (symbol.Length == 6) { keywords.Add(symbol.Substring(0, 3)); keywords.Add(symbol.Substring(3, 3)); }
            else { keywords.Add(symbol); } // For single-word symbols like stock tickers, if any

            // Broader terms based on components - This list needs to be rich
            var componentMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                // Currencies & Central Banks
                {"USD", new[]{"US Dollar", "Dollar", "Federal Reserve", "Fed", "FOMC", "Greenback", "Buck", "Non-farm payroll", "NFP", "US CPI", "US GDP", "US Retail Sales", "Jerome Powell"}},
                {"EUR", new[]{"Euro", "ECB", "European Central Bank", "Eurozone CPI", "Eurozone GDP", "Christine Lagarde", "EU Summit"}},
                {"GBP", new[]{"British Pound", "Sterling", "Cable", "BoE", "Bank of England", "UK CPI", "UK GDP", "Andrew Bailey"}},
                {"JPY", new[]{"Japanese Yen", "Yen", "BoJ", "Bank of Japan", "Kazuo Ueda", "Japan CPI"}},
                {"AUD", new[]{"Australian Dollar", "Aussie", "RBA", "Reserve Bank of Australia", "Michele Bullock"}},
                {"CAD", new[]{"Canadian Dollar", "Loonie", "BoC", "Bank of Canada", "Tiff Macklem", "Canada CPI", "Oil Prices" /* CAD related */}},
                {"CHF", new[]{"Swiss Franc", "Franc", "SNB", "Swiss National Bank", "Thomas Jordan"}},
                {"NZD", new[]{"New Zealand Dollar", "Kiwi", "RBNZ", "Reserve Bank of New Zealand", "Adrian Orr"}},
                // Commodities
                {"XAU", new[]{"Gold", "Bullion", "Precious Metal", "Safe Haven", "Gold Price"}},
                {"XAG", new[]{"Silver", "Industrial Metal"}},
                {"OIL", new[]{"Crude Oil", "WTI", "Brent", "OPEC", "Oil Inventories", "Energy Prices"}}, // If you add OIL
                // General Economic Terms - these can be broad, use judiciously or with specific asset categories
                {"Interest Rate", new[]{"Interest Rate", "Monetary Policy", "Rate Hike", "Rate Cut", "Central Bank Rate"}},
                {"Inflation", new[]{"Inflation", "CPI", "Consumer Price Index", "PPI", "Producer Price Index", "Price Stability"}},
                {"GDP", new[]{"GDP", "Gross Domestic Product", "Economic Growth", "Recession", "Economic Activity"}},
                {"Employment", new[]{"Employment", "Unemployment Rate", "Jobs Report", "Labor Market", "Wage Growth"}},
                {"Trade Balance", new[]{"Trade Balance", "Exports", "Imports", "Tariffs"}},
                {"Manufacturing PMI", new[]{"PMI", "Manufacturing Index", "Factory Activity"}},
                {"Services PMI", new[]{"Services Index", "Non-Manufacturing Index"}},
                {"Consumer Confidence", new[]{"Consumer Sentiment", "Consumer Spending"}},
                {"Geopolitical", new[]{"Geopolitical Risk", "Tensions", "Elections", "War", "Conflict" /* Broad, use with care */}},
                {"Market Sentiment", new[]{"Risk Appetite", "Risk-on", "Risk-off", "Volatility Index", "VIX"}}
            };

            var symbolComponents = new List<string>();
            if (symbol == "XAUUSD") { symbolComponents.Add("XAU"); symbolComponents.Add("USD"); }
            else if (symbol == "XAGUSD") { symbolComponents.Add("XAG"); symbolComponents.Add("USD"); }
            else if (symbol.Length == 6) { symbolComponents.Add(symbol.Substring(0, 3)); symbolComponents.Add(symbol.Substring(3, 3)); }
            else { symbolComponents.Add(symbol); }


            foreach (var component in symbolComponents)
            {
                if (componentMap.TryGetValue(component, out var terms))
                {
                    foreach (var term in terms) keywords.Add(term);
                }
            }
            // Add the original symbol itself if not already present (it should be from the start)
            keywords.Add(symbol);


            // Add very generic terms applicable to most financial news, these are less specific.
            // Use these if the specific symbol/component search needs broadening.
            // For an OR search, these increase recall but might reduce precision.
            // keywords.Add("Market News"); keywords.Add("Financial Update"); keywords.Add("Economic Outlook");

            _logger.LogDebug("Generated keywords for {Symbol}: [{KeywordList}]", symbol, string.Join(", ", keywords));
            return keywords.ToList(); // Repository will handle ORing these keywords
        }


        private string FormatNewsMessage(List<NewsItem> newsItems, string symbol, int currentPage, int totalCount, int pageSize, bool isVipUser)
        {
            var sb = new StringBuilder();
            var currencyDisplayName = GetCurrencyDisplayName(symbol);
            int totalPages = Math.Max(1, (int)Math.Ceiling((double)totalCount / pageSize)); // Ensure totalPages is at least 1

            sb.AppendLine($"📰 *Fundamental News: {EscapeMarkdownV2(currencyDisplayName)}*");
            sb.AppendLine($"Page {currentPage} of {totalPages} ({totalCount} item{(totalCount == 1 ? "" : "s")})");
            sb.AppendLine($"_Last {(isVipUser ? VipNewsDaysLimit : FreeNewsDaysLimit)} days. For full history & more, consider VIP._");
            sb.AppendLine("---");

            if (!newsItems.Any())
            {
                sb.AppendLine("_No news items match your current selection on this page._");
                return sb.ToString();
            }

            int itemNumberGlobal = (currentPage - 1) * pageSize + 1;
            foreach (var item in newsItems)
            {
                var title = EscapeMarkdownV2(item.Title ?? "Untitled News");
                // Using item.Summary as per your entity (which was item.Description before)
                var summary = EscapeMarkdownV2(
                    !string.IsNullOrWhiteSpace(item.Summary) && item.Summary.Length > 180
                        ? item.Summary.Substring(0, 180).Trim() + "..."
                        : (item.Summary ?? "No summary.")
                );
                var publishedAt = item.PublishedDate.ToString("MMM dd, yyyy HH:mm 'UTC'"); // PublishedDate is not nullable in your entity
                var sourceName = EscapeMarkdownV2(item.SourceName ?? item.RssSource?.SourceName ?? "Unknown Source"); // Prioritize direct SourceName

                sb.AppendLine($"*{itemNumberGlobal}\\. {title}*");
                sb.AppendLine($" _{sourceName} \\| {publishedAt}_ "); // Use pipe for visual separation
                sb.AppendLine(summary);
                if (!string.IsNullOrWhiteSpace(item.Link))
                {
                    // Ensure the link is a valid absolute URL before creating a Markdown link
                    if (Uri.TryCreate(item.Link, UriKind.Absolute, out _))
                    {
                        sb.AppendLine($"[Read Full Article]({item.Link})");
                    }
                    else
                    {
                        _logger.LogWarning("FundamentalAnalysisCBQ: Invalid URI for news item link: {Link}", item.Link);
                        sb.AppendLine($"_Full article link unavailable (invalid format)_");
                    }
                }
                sb.AppendLine("---");
                itemNumberGlobal++;
            }
            return sb.ToString();
        }

        private InlineKeyboardMarkup BuildPaginationKeyboard(string symbol, int currentPage, int totalCount, int pageSize, bool isVipUser)
        {
            var keyboardRows = new List<List<InlineKeyboardButton>>();
            var paginationRow = new List<InlineKeyboardButton>();
            int totalPages = Math.Max(1, (int)Math.Ceiling((double)totalCount / pageSize));

            if (currentPage > 1)
            {
                paginationRow.Add(InlineKeyboardButton.WithCallbackData("⬅️ Prev", $"{ViewFundamentalAnalysisPrefix}:{symbol}:{PageActionPrefix}:{currentPage - 1}"));
            }

            if (totalPages > 0)
            {
                paginationRow.Add(InlineKeyboardButton.WithCallbackData($"Page {currentPage}/{totalPages}", "noop_page_display"));
            }

            if (currentPage < totalPages)
            {
                paginationRow.Add(InlineKeyboardButton.WithCallbackData("Next ➡️", $"{ViewFundamentalAnalysisPrefix}:{symbol}:{PageActionPrefix}:{currentPage + 1}"));
            }

            if (paginationRow.Any()) keyboardRows.Add(paginationRow);

            // VIP Upsell
            if (!isVipUser)
            {
                // This is a simplified check. A more accurate check would involve knowing the exact number of free items.
                // If current page is the last for free users AND there are more items available (implying VIP items)
                int maxFreeItems = pageSize * ((int)Math.Ceiling((double)(_newsItemRepository.SearchNewsAsync(GenerateKeywordsFromSymbol(symbol), DateTime.UtcNow.Date.AddDays(1 - FreeNewsDaysLimit), DateTime.UtcNow, 1, int.MaxValue, false).GetAwaiter().GetResult().TotalCount) / pageSize));

                if ((currentPage * pageSize >= maxFreeItems && totalCount > maxFreeItems) || (totalPages == 0 && totalCount > 0))
                {
                    keyboardRows.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("💎 Unlock Full News History (VIP)", $"{ViewFundamentalAnalysisPrefix}:{symbol}:{SubscribeVipAction}") });
                }
            }

            keyboardRows.Add(new List<InlineKeyboardButton> {
        
                InlineKeyboardButton.WithCallbackData("🏠 Main Menu", "show_main_menu")
            });
            return new InlineKeyboardMarkup(keyboardRows);
        }

        private InlineKeyboardMarkup GetNoNewsKeyboard(string symbol, bool isVipUser)
        {
            var rows = new List<List<InlineKeyboardButton>>();
            if (!isVipUser)
            {
                rows.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🌟 Try VIP for More News Sources", $"{ViewFundamentalAnalysisPrefix}:{symbol}:{SubscribeVipAction}") });
            }
            rows.Add(new List<InlineKeyboardButton> {
 
                InlineKeyboardButton.WithCallbackData("🏠 Main Menu", "show_main_menu")
            });
            return new InlineKeyboardMarkup(rows);
        }

        private InlineKeyboardMarkup GetErrorStateKeyboard(string symbol) // symbol can be "N/A"
        {
            var retrySymbol = (symbol != "N/A" && !string.IsNullOrEmpty(symbol)) ? symbol : GetCurrencyDisplayName("EURUSD"); // Default retry
            return new InlineKeyboardMarkup(new[] {
                new [] { InlineKeyboardButton.WithCallbackData($"🔄 Retry News for {retrySymbol}", $"{ViewFundamentalAnalysisPrefix}:{retrySymbol}") },
                new [] { InlineKeyboardButton.WithCallbackData("🏠 Main Menu", "show_main_menu") }
            });
        }

        private string GetCurrencyDisplayName(string symbol)
        {
            return _currencyInfoSettings.Currencies != null && _currencyInfoSettings.Currencies.TryGetValue(symbol, out var details) && !string.IsNullOrEmpty(details.Name)
                ? details.Name
                : symbol;
        }
        private (string Name, string Category) GetCurrencyDisplayNameAndCategory(string symbol)
        {
            if (_currencyInfoSettings.Currencies != null && _currencyInfoSettings.Currencies.TryGetValue(symbol, out var details))
            {
                return (details.Name ?? symbol, details.Category ?? "Unknown");
            }
            // Basic fallback for category inference if not in settings
            return (symbol, (symbol.Length == 6 && char.IsLetter(symbol[0])) ? "Forex" : (symbol.Contains("USD") ? "Commodity" : "Unknown"));
        }


        private string EscapeMarkdownV2(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var markdownEscapeRegex = new Regex(@"([_*\[\]()~`>#+\-=|{}.!])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
            return markdownEscapeRegex.Replace(text, @"\$1");
        }
    }
}