// File: TelegramPanel/Application/CommandHandlers/FundamentalAnalysisCallbackHandler.cs

using Application.Common.Interfaces;
using Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.CommandHandlers.MainMenu;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Infrastructure;
using TelegramPanel.Infrastructure.Helpers;
using TelegramPanel.Infrastructure.Settings;

namespace TelegramPanel.Application.CommandHandlers.Features.News
{
    public class FundamentalAnalysisCallbackHandler : ITelegramCallbackQueryHandler
    {
        // --- Class Members and Constants ---
        private readonly ILogger<FundamentalAnalysisCallbackHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly INewsItemRepository _newsItemRepository;
        private readonly IUserRepository _userRepository;
        private readonly CurrencyInfoSettings _currencyInfoSettings;

        private record SmartKeywordSet(List<string> HighPrecisionTerms, List<string> GeneralTerms, Dictionary<string, List<string>> TermsByCurrency);
        private readonly Dictionary<string, (string Name, string[] CoreTerms, string[] Aliases)> _currencyKnowledgeBase = new()
{
    {
        "USD", ("US Dollar",
        new[] { 
            // Tier 1 Economic Data
            "US CPI", "Consumer Price Index", "Core CPI", "PCE Price Index", "Personal Consumption Expenditures", "Core PCE", "US GDP", "Gross Domestic Product", "NFP", "Non-Farm Payrolls", "US Unemployment Rate", "Labor Force Participation", "Average Hourly Earnings", "Retail Sales", "Advance Retail Sales", "Control Group",
            
            // Tier 2 Economic Data
            "Durable Goods Orders", "ISM Manufacturing", "ISM Non-Manufacturing", "ISM Services", "S&P Global Composite PMI", "Manufacturing PMI", "Services PMI", "PPI", "Producer Price Index", "Core PPI", "Trade Balance", "Current Account", "Consumer Confidence", "The Conference Board", "Housing Starts", "Building Permits", "New Home Sales", "Pending Home Sales", "Existing Home Sales", "Initial Jobless Claims", "Continuing Jobless Claims", "ADP Employment Report", "Factory Orders", "University of Michigan Sentiment", "UoM", "Empire State Manufacturing Index", "Philly Fed Survey", "Capacity Utilization", "Industrial Production", "JOLTS Job Openings",
            
            // Tier 3: Central Bank - The Fed (Maximum Detail)
            "Federal Reserve", "Fed", "FOMC", "Federal Open Market Committee", "FOMC minutes", "FOMC Statement", "FOMC Press Conference", "Fed Chair", "Jerome Powell", "Fed Vice Chair", "Philip Jefferson", "John Williams (NY Fed)", "Lisa Cook", "Christopher Waller", "Loretta Mester (Cleveland Fed)", "Neel Kashkari (Minneapolis Fed)", "Raphael Bostic (Atlanta Fed)", "monetary policy", "federal funds rate", "interest rate decision", "rate hike", "rate cut", "hawkish", "dovish", "accommodative", "restrictive", "data-dependent", "quantitative easing", "QE", "quantitative tightening", "QT", "tapering", "balance sheet reduction", "dot plot", "Summary of Economic Projections (SEP)", "Beige Book", "Jackson Hole Economic Symposium",
            
            // Tier 4: Government, Politics & Treasury
            "US Treasury", "Treasury Department", "Secretary of the Treasury", "Janet Yellen", "US government shutdown", "debt ceiling", "budget deficit", "fiscal policy", "stimulus", "White House", "US election", "geopolitics", "US-China relations",
            
            // Tier 5: Related Markets & Instruments
            "Treasury yields", "10-year Treasury note yield", "T-bond", "2-year yield", "yield curve inversion", "bond market", "corporate bonds", "junk bonds", "US bonds", "DXY", "Dollar Index", "S&P 500", "SPX", "NASDAQ Composite", "COMP", "Dow Jones Industrial Average", "DJIA", "Russell 2000", "VIX", "volatility index", "risk sentiment"
        },
        new[] { "USD", "Dollar", "Greenback", "Buck", "U.S. Dollar", "US$", "American Dollar" })
    },
    {
        "EUR", ("Euro",
        new[] { 
            // Tier 1 Economic Data
            "Eurozone CPI", "Harmonised Index of Consumer Prices", "HICP", "Eurozone GDP", "German IFO Business Climate", "ZEW Economic Sentiment", "Eurozone Unemployment", "Eurozone Manufacturing PMI", "Eurozone Services PMI", "Eurozone Composite PMI",
            
            // Tier 2 Economic Data
            "German industrial production", "French industrial production", "Italian industrial production", "Spanish industrial production", "German factory orders", "French GDP", "Italian GDP", "Spanish GDP", "Eurozone Retail Sales", "economic sentiment", "consumer confidence", "Eurozone trade balance", "Sentix Investor Confidence",
            
            // Tier 3: Central Bank (ECB)
            "ECB", "European Central Bank", "ECB President", "Christine Lagarde", "ECB Vice President", "Luis de Guindos", "Isabel Schnabel", "Philip Lane", "Fabio Panetta", "Joachim Nagel (Bundesbank)", "Francois Villeroy de Galhau (Banque de France)", "Governing Council", "ECB press conference", "ECB monetary policy account", "interest rate decision", "Main Refinancing Rate", "Deposit Facility Rate", "Marginal Lending Facility", "monetary policy", "APP", "Asset Purchase Programme", "PEPP", "Pandemic Emergency Purchase Programme", "TLTROs", "Transmission Protection Instrument (TPI)",
            
            // Tier 4: Politics & Institutions
            "EU summit", "Eurogroup meetings", "European Commission", "Ursula von der Leyen", "Charles Michel", "Olaf Scholz", "Emmanuel Macron", "Giorgia Meloni", "Pedro Sanchez", "EU fiscal rules", "Stability and Growth Pact", "NextGenerationEU",
            
            // Tier 5: Related Markets & Instruments
            "German Bunds", "10-year Bund yield", "French OATs", "Italian BTPs", "BTP-Bund spread", "sovereign debt", "Eonia", "Euribor", "DAX", "CAC 40", "Euro Stoxx 50", "FTSE MIB", "IBEX 35"
        },
        new[] { "EUR", "Euro", "single currency" })
    },
    {
        "JPY", ("Japanese Yen",
        new[] { 
            // Tier 1 Economic Data
            "Japan CPI", "National Core CPI", "Tokyo CPI", "Japan GDP", "Tankan Survey", "Large Manufacturers Index", "industrial production", "Retail Trade", "Retail Sales", "Unemployment Rate",
            
            // Tier 2 Economic Data
            "Machinery Orders", "household spending", "Tertiary Industry Index", "Trade Balance", "Coincident Index", "Leading Economic Index", "PPI", "Corporate Goods Price Index",
            
            // Tier 3: Central Bank (BoJ)
            "BoJ", "Bank of Japan", "BoJ Governor", "Kazuo Ueda", "former governor Haruhiko Kuroda", "yield curve control", "YCC", "monetary policy", "BoJ Summary of Opinions", "BoJ Outlook Report", "ultra-loose policy", "quantitative and qualitative easing (QQE)", "negative interest rates", "NIRP", "asset purchases",
            
            // Tier 4: Government & Policy
            "currency intervention", "Ministry of Finance", "MoF", "Fumio Kishida", "Shunichi Suzuki", "yen intervention", "jawboning", "fiscal policy",
            
            // Tier 5: Related Markets & Concepts
            "JGBs", "Japanese Government Bonds", "10-year JGB yield", "Nikkei 225", "Topix", "carry trade", "funding currency", "safe haven"
        },
        new[] { "JPY", "Yen", "Japanese Yen" })
    },
    {
        "GBP", ("British Pound",
        new[] { 
            // Tier 1 Economic Data
            "UK CPI", "UK GDP", "UK unemployment rate", "Claimant Count Change", "Retail Sales", "UK Manufacturing PMI", "UK Services PMI", "GfK Consumer Confidence", 
            
            // Tier 2 Economic Data
            "Average Earnings Index", "Labour Productivity", "Manufacturing Production", "Industrial Production", "Construction Output", "Halifax HPI", "Nationwide HPI", "BRC Shop Price Index", "Trade Balance", "Current Account",
            
            // Tier 3: Central Bank (BoE)
            "BoE", "Bank of England", "BoE Governor", "Andrew Bailey", "Chief Economist", "Huw Pill", "MPC", "Monetary Policy Committee", "BoE minutes", "BoE Inflation Report", "Super Thursday", "Bank Rate", "interest rate decision",
            
            // Tier 4: Government & Politics
            "Chancellor of the Exchequer", "HM Treasury", "UK budget", "Autumn Statement", "Spring Statement", "Jeremy Hunt", "Prime Minister", "Rishi Sunak", "Labour Party", "Keir Starmer", "UK election", "Brexit", "trade deal", "Northern Ireland Protocol", "Windsor Framework", "fiscal policy", "austerity", "Truss", "Kwarteng",
            
            // Tier 5: Related Markets & Instruments
            "Gilts", "10-year Gilt yield", "UK government bonds", "FTSE 100", "Footsie", "UK stock market"
        },
        new[] { "GBP", "Pound", "Sterling", "Cable", "Quid", "British Pound" })
    },
    //... (Similar maximum exhaustive lists for AUD, CAD, CHF, NZD) ...
    {
        "AUD", ("Australian Dollar",
        new[] { "Australian CPI", "Trimmed Mean CPI", "Australian GDP", "Employment Change", "Unemployment Rate", "Trade Balance", "Retail Sales", "Building Approvals", "Capital Expenditure", "NAB Business Confidence", "Westpac Consumer Sentiment", "AiG Performance Index", "RBA", "Reserve Bank of Australia", "Governor Michele Bullock", "RBA minutes", "Statement on Monetary Policy", "SoMP", "cash rate decision", "Iron ore", "Coal prices", "Copper prices", "China data", "Chinese PMI", "Chinese GDP", "Caixin PMI", "ASX 200", "risk-on", "risk-off", "commodity currency", "Aussie Dollar" },
        new[] { "AUD", "Aussie", "Australian Dollar" })
    },
    {
        "CAD", ("Canadian Dollar",
        new[] { "Canadian CPI", "Core CPI", "Canadian GDP", "Employment Change", "Unemployment Rate", "Ivey PMI", "Retail Sales", "Trade Balance", "Housing Starts", "Building Permits", "Manufacturing Sales", "BoC", "Bank of Canada", "Governor Tiff Macklem", "overnight rate", "BOC Rate Statement", "BOC Monetary Policy Report", "Business Outlook Survey", "Oil Prices", "WTI", "Crude oil", "WCS", "Western Canadian Select", "Brent", "natural gas", "OPEC", "OPEC+", "IEA", "S&P/TSX", "Keystone pipeline", "US-Canada trade" },
        new[] { "CAD", "Loonie", "Canadian Dollar" })
    },
    {
        "CHF", ("Swiss Franc",
        new[] { "Swiss CPI", "Swiss GDP", "Unemployment Rate", "Retail Sales", "Manufacturing PMI", "procure.ch PMI", "SECO Economic Forecasts", "KOF Economic Barometer", "Industrial Production", "Trade Balance", "SNB", "Swiss National Bank", "Chairman Thomas Jordan", "SNB press conference", "sight deposits", "policy rate", "foreign currency reserves", "safe-haven", "risk-off", "geopolitical risk", "flight to safety", "European geopolitical stability", "SMI", "Swiss Market Index", "banking secrecy" },
        new[] { "CHF", "Swissy", "Swiss Franc" })
    },
    {
        "NZD", ("New Zealand Dollar",
        new[] { "New Zealand CPI", "New Zealand GDP", "Employment Change", "Unemployment Rate", "Trade Balance", "Retail Sales", "Building Consents", "Electronic Card Retail Sales", "BusinessNZ PMI", "ANZ Business Confidence", "GDT Price Index", "Global Dairy Trade", "Visitor Arrivals", "RBNZ", "Reserve Bank of New Zealand", "Governor Adrian Orr", "Official Cash Rate", "OCR", "RBNZ press conference", "Monetary Policy Statement", "Financial Stability Report", "Dairy prices", "Fonterra", "whole milk powder", "NZX 50", "risk sentiment", "China trade relations" },
        new[] { "NZD", "Kiwi", "New Zealand Dollar" })
    },
    {
        "XAU", ("Gold",
        new[] { 
            // Core Terms & Instruments
            "Gold", "XAU", "Bullion", "Precious Metals", "spot gold", "gold futures", "GC=F", "COMEX", "LBMA", "gold standard", "XAG", "Silver", "Platinum", "Palladium",
            // Primary Drivers & Concepts
            "safe-haven asset", "store of value", "inflation", "disinflation", "stagflation", "hyperinflation", "inflation hedge", "inflation expectations", "TIPS", "Treasury Inflation-Protected Securities", "breakeven rates", "geopolitical tension", "war", "conflict", "risk aversion", "real yields", "real interest rates", "opportunity cost", "central bank buying", "central bank gold reserves", "physical demand", "jewelry demand", "industrial demand", "recession fears", "economic slowdown", "debt crisis", "market volatility", "equity market sell-off"
        },
        new[] { "XAU" })
    }
};
        private const int NewsItemsPerPage = 4;
        private const int FreeNewsDaysLimit = 3;
        private const int VipNewsDaysLimit = 10;
        public const string ViewFundamentalAnalysisPrefix = "fa_view";
        private const string PageActionPrefix = "pg";
        private const string SubscribeVipAction = "sub_vip_fa";

        // --- Constructor ---
        public FundamentalAnalysisCallbackHandler(
            ILogger<FundamentalAnalysisCallbackHandler> logger,
            ITelegramMessageSender messageSender,
            INewsItemRepository newsItemRepository,
            IUserRepository userRepository,
            IOptions<CurrencyInfoSettings> currencyInfoOptions)
        {
            _logger = logger;
            _messageSender = messageSender;
            _newsItemRepository = newsItemRepository;
            _userRepository = userRepository;
            _currencyInfoSettings = currencyInfoOptions?.Value ?? new CurrencyInfoSettings();
        }

        // --- Main Handler Logic ---
        public bool CanHandle(Update update) => update.CallbackQuery?.Data?.StartsWith(ViewFundamentalAnalysisPrefix) == true;

        public async Task HandleAsync(Update update, CancellationToken cancellationToken)
        {
            var callbackQuery = update.CallbackQuery;
            if (callbackQuery?.Message == null) return;

            var callbackData = callbackQuery.Data;
            var chatId = callbackQuery.Message.Chat.Id;
            var messageId = callbackQuery.Message.MessageId;
            var telegramUserIdString = callbackQuery.From.Id.ToString();

            _logger.LogInformation("FA_CBQ Handling: Data={Data}, Chat={ChatId}", callbackData, chatId);

            try
            {
                await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);

                var parts = callbackData.Split(':', 4);
                if (parts.Length < 2) return;

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

                var user = await _userRepository.GetByTelegramIdAsync(telegramUserIdString, cancellationToken);
                bool isVipUser = user?.Subscriptions?.Any(s => s.IsCurrentlyActive) ?? false;
                int daysToFetch = isVipUser ? VipNewsDaysLimit : FreeNewsDaysLimit;
                DateTime startDate = DateTime.UtcNow.Date.AddDays(-daysToFetch);

                await UpdateNewsMessageAsync(chatId, messageId, symbol, startDate, pageNumber, NewsItemsPerPage, isVipUser, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FA_CBQ Error for Data={CallbackData}", callbackData);
            }
        }

        private async Task UpdateNewsMessageAsync(long chatId, int messageId, string symbol, DateTime startDate, int pageNumber, int pageSize, bool isVipUser, CancellationToken cancellationToken)
        {
            await _messageSender.EditMessageTextAsync(chatId, messageId, $"⏳ Fetching news for *{GetCurrencyDisplayName(symbol)}*...", ParseMode.Markdown, null, cancellationToken);

            var (newsItems, totalCount) = await GetRelevantNewsAsync(symbol, startDate, pageNumber, pageSize, isVipUser, cancellationToken);

            if (!newsItems.Any() && pageNumber == 1)
            {
                string noNewsText = $"ℹ️ No news found for *{GetCurrencyDisplayName(symbol)}* in the last {(isVipUser ? VipNewsDaysLimit : FreeNewsDaysLimit)} days.";
                await _messageSender.EditMessageTextAsync(chatId, messageId, noNewsText, ParseMode.Markdown, GetNoNewsKeyboard(symbol, isVipUser), cancellationToken);
                return;
            }

            var messageText = FormatNewsMessage(newsItems, symbol, pageNumber, totalCount, pageSize);
            var keyboard = BuildPaginationKeyboard(symbol, pageNumber, totalCount, pageSize, isVipUser);
            await _messageSender.EditMessageTextAsync(chatId, messageId, messageText, ParseMode.Markdown, keyboard, cancellationToken);
        }

        // --- ✅ SINGLE, CORRECT IMPLEMENTATION OF GetRelevantNewsAsync ---
        private async Task<(List<NewsItem> News, int TotalCount)> GetRelevantNewsAsync(
            string symbol, DateTime startDate, int page, int pageSize, bool isVipUser, CancellationToken cancellationToken)
        {
            var keywordSet = GenerateSmartKeywords(symbol);
            if (keywordSet == null) return (new List<NewsItem>(), 0);

            var (highPrecisionItems, _) = await _newsItemRepository.SearchNewsAsync(keywordSet.HighPrecisionTerms, startDate, DateTime.UtcNow, 1, 100, false, isVipUser, cancellationToken);
            var (generalItems, _) = await _newsItemRepository.SearchNewsAsync(keywordSet.GeneralTerms, startDate, DateTime.UtcNow, 1, 100, false, isVipUser, cancellationToken);

            var combinedNews = new Dictionary<Guid, NewsItem>();
            foreach (var item in highPrecisionItems) combinedNews[item.Id] = item;

            string baseCode = symbol == "XAUUSD" ? "XAU" : symbol.Substring(0, 3);
            string quoteCode = symbol == "XAUUSD" ? "USD" : symbol.Substring(3, 3);

            if (keywordSet.TermsByCurrency.TryGetValue(baseCode, out var baseTerms) && keywordSet.TermsByCurrency.TryGetValue(quoteCode, out var quoteTerms))
            {
                foreach (var item in generalItems)
                {
                    if (combinedNews.ContainsKey(item.Id)) continue;
                    var content = $"{item.Title} {item.Summary}".ToLowerInvariant();
                    if (baseTerms.Any(k => content.Contains(k.ToLowerInvariant())) && quoteTerms.Any(k => content.Contains(k.ToLowerInvariant())))
                    {
                        combinedNews[item.Id] = item;
                    }
                }
            }

            var finalRelevantNews = combinedNews.Values.OrderByDescending(n => n.PublishedDate).ThenByDescending(n => n.CreatedAt).ToList();
            int totalCount = finalRelevantNews.Count;
            var pagedNews = finalRelevantNews.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            return (pagedNews, totalCount);
        }

        // --- ✅ SINGLE, CORRECT IMPLEMENTATION OF GenerateSmartKeywords ---
        private SmartKeywordSet? GenerateSmartKeywords(string symbol)
        {
            var highPrecision = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var general = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var termsByCurrency = new Dictionary<string, List<string>>();
            symbol = symbol.ToUpperInvariant();

            var nicknames = new Dictionary<string, string> { { "GBPUSD", "Cable" }, { "XAUUSD", "Gold" } };
            string baseCode = symbol == "XAUUSD" ? "XAU" : symbol.Substring(0, 3);
            string quoteCode = symbol == "XAUUSD" ? "USD" : symbol.Substring(3, 3);

            if (!_currencyKnowledgeBase.TryGetValue(baseCode, out var baseInfo) || !_currencyKnowledgeBase.TryGetValue(quoteCode, out var quoteInfo))
            {
                return null;
            }

            highPrecision.Add(symbol);
            highPrecision.Add($"{baseCode}/{quoteCode}");
            if (nicknames.TryGetValue(symbol, out var nick)) highPrecision.Add(nick);

            termsByCurrency[baseCode] = baseInfo.CoreTerms.Concat(baseInfo.Aliases).ToList();
            termsByCurrency[quoteCode] = quoteInfo.CoreTerms.Concat(quoteInfo.Aliases).ToList();
            foreach (var term in termsByCurrency[baseCode]) general.Add(term);
            foreach (var term in termsByCurrency[quoteCode]) general.Add(term);

            return new SmartKeywordSet(highPrecision.ToList(), general.ToList(), termsByCurrency);
        }

        // --- ✅ SINGLE, CORRECT IMPLEMENTATION OF FormatNewsMessage ---
        private string FormatNewsMessage(List<NewsItem> newsItems, string symbol, int currentPage, int totalCount, int pageSize)
        {
            var sb = new StringBuilder();
            int totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

            sb.AppendLine($"📊 *Fundamental News: {GetCurrencyDisplayName(symbol)}*");
            sb.AppendLine($"📖 Page {currentPage} of {totalPages} `({totalCount} items)`");
            sb.AppendLine("`-----------------------------------`");

            if (!newsItems.Any())
            {
                sb.AppendLine("\nℹ️ _No more news items on this page._");
                return sb.ToString();
            }

            int itemNumber = (currentPage - 1) * pageSize + 1;
            foreach (var item in newsItems)
            {
                sb.AppendLine($"\n🔸 *{itemNumber++}. {item.Title}*");
                sb.AppendLine($"🏦 _{item.SourceName}_ | 🗓️ _{item.PublishedDate:MMM dd, yyyy HH:mm 'UTC'}_");
                sb.AppendLine(TruncateWithEllipsis(item.Summary, 180) ?? "_No summary available._");
                if (!string.IsNullOrWhiteSpace(item.Link)) sb.AppendLine($"🔗 [Read Full Article]({item.Link})");
                sb.AppendLine("`-----------------------------------`");
            }
            return sb.ToString();
        }

        // --- Other Helper Methods ---
        private string? TruncateWithEllipsis(string? text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength) return text;
            return text.Substring(0, maxLength - 3).TrimEnd() + "...";
        }

        private string GetCurrencyDisplayName(string symbol)
        {
            return _currencyInfoSettings.Currencies != null && _currencyInfoSettings.Currencies.TryGetValue(symbol, out var details) && !string.IsNullOrEmpty(details.Name)
                ? details.Name
                : symbol;
        }

        private InlineKeyboardMarkup BuildPaginationKeyboard(string symbol, int currentPage, int totalCount, int pageSize, bool isVipUser)
        {
            var rows = new List<List<InlineKeyboardButton>>();
            var paginationRow = new List<InlineKeyboardButton>();
            int totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

            if (currentPage > 1) paginationRow.Add(InlineKeyboardButton.WithCallbackData("⬅️ Prev", $"{ViewFundamentalAnalysisPrefix}:{symbol}:{PageActionPrefix}:{currentPage - 1}"));
            if (totalPages > 1) paginationRow.Add(InlineKeyboardButton.WithCallbackData($"Page {currentPage}/{totalPages}", "noop"));
            if (currentPage < totalPages) paginationRow.Add(InlineKeyboardButton.WithCallbackData("Next ➡️", $"{ViewFundamentalAnalysisPrefix}:{symbol}:{PageActionPrefix}:{currentPage + 1}"));

            if (paginationRow.Any()) rows.Add(paginationRow);
            if (!isVipUser) rows.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("💎 Unlock Full History (VIP)", $"{ViewFundamentalAnalysisPrefix}:{symbol}:{SubscribeVipAction}") });
            rows.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🏠 Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral) });

            return new InlineKeyboardMarkup(rows);
        }

        private InlineKeyboardMarkup GetNoNewsKeyboard(string symbol, bool isVipUser)
        {
            var rows = new List<List<InlineKeyboardButton>>();
            if (!isVipUser) rows.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🌟 Try VIP for More News Sources", $"{ViewFundamentalAnalysisPrefix}:{symbol}:{SubscribeVipAction}") });
            rows.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🏠 Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral) });
            return new InlineKeyboardMarkup(rows);
        }

        private async Task HandleVipSubscriptionPromptAsync(long chatId, int messageId, string originalSymbol, CancellationToken cancellationToken)
        {
            var vipMessage = "🌟 *Unlock Full News Access & More Features!*\n\nAs a VIP member, you benefit from:\n✅ Extended news history\n✅ More news sources\n✅ Access to all premium signals\n\nSupport the bot and elevate your trading insights!";
            var vipKeyboard = MarkupBuilder.CreateInlineKeyboard(
                new[] { InlineKeyboardButton.WithCallbackData("💎 View VIP Plans", "show_subscription_options") },
                new[] { InlineKeyboardButton.WithCallbackData("◀️ Back to News", $"{ViewFundamentalAnalysisPrefix}:{originalSymbol}") }
            );
            await _messageSender.EditMessageTextAsync(chatId, messageId, vipMessage, ParseMode.Markdown, vipKeyboard, cancellationToken);
        }

        // Dummy method for error keyboard, can be expanded.
        private InlineKeyboardMarkup GetErrorStateKeyboard(string symbol) => new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("🏠 Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral));
    }
}