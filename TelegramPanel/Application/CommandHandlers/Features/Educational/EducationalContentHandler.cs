// --- START OF PLATINUM TIER FILE: EducationalContentHandler.cs ---

using Application.Common.Interfaces;
using Hangfire;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.DTO.Ui;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters;
using static TelegramPanel.Infrastructure.ActualTelegramMessageActions;

namespace TelegramPanel.Application.CommandHandlers.Features.Educational
{
    /// <summary>
    /// A high-performance, dynamic, and secure handler for serving multi-level, multi-language
    /// educational content from the server's file system.
    /// </summary>
    public class EducationalContentHandler : ITelegramCallbackQueryHandler
    {
        #region Configuration
        private readonly ILogger<EducationalContentHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly ITelegramBotClient _botClient;
        private readonly IMemoryCacheService<CachedMenuDto> _menuCache;
        private readonly IMemoryCacheService<string> _filePathCache;
        private const string BaseLearningPath = @"C:\ForexBotContent";
        private const int PageSize = 8;
        private static readonly TimeSpan MenuCacheDuration = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan FilePathCacheDuration = TimeSpan.FromHours(2);
        private static readonly string[] SupportedExtensions = { ".mp3", ".wav", ".mp4", ".mov", ".mkv" };

        private const string HandlerPrefix = "edu_"; // Single definition for the handler's prefix
        private const string NavPrefix = HandlerPrefix + "nav_";
        private const string PagePrefix = HandlerPrefix + "page_";
        private const string SendFilePrefix = HandlerPrefix + "send_";
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
        private static readonly Dictionary<string, string> FolderEmojiMap = new()
{
    // --- Custom Category Emojis ---
    { "podcasts", "🎧" },
    { "risk_management", "🛡️" },
    { "video_tutorials", "🎬" },
    { "market_basics", "📈" },
    { "advanced_strategies", "🚀" },
    { "trader_psychology", "🧠" },

    // --- Language Flag Emojis (Tiered) ---
    // Tier 1
    { "en", "🇬🇧" }, { "es", "🇪🇸" }, { "ru", "🇷🇺" }, { "de", "🇩🇪" }, { "fr", "🇫🇷" }, { "zh", "🇨🇳" },
    // Tier 2
    { "pt", "🇵🇹" }, { "ar", "🇸🇦" }, { "ja", "🇯🇵" }, { "ko", "🇰🇷" }, { "hi", "🇮🇳" }, { "tr", "🇹🇷" }, { "fa", "🇮🇷" },
    // Tier 3
    { "it", "🇮🇹" }, { "nl", "🇳🇱" }, { "pl", "🇵🇱" }, { "id", "🇮🇩" }, { "vi", "🇻🇳" },
    // Tier 4
    { "sv", "🇸🇪" }, { "no", "🇳🇴" }, { "da", "🇩🇰" }, { "fi", "🇫🇮" }, { "uk", "🇺🇦" }, { "bg", "🇧🇬" },
    { "ms", "🇲🇾" }, { "th", "🇹🇭" }, { "he", "🇮🇱" }, { "el", "🇬🇷" }, { "ro", "🇷🇴" }, { "hu", "🇭🇺" },
    { "cs", "🇨🇿" }, { "sk", "🇸🇰" }, { "sl", "🇸🇮" }, { "hr", "🇭🇷" }, { "sr", "🇷🇸" }, { "lt", "🇱🇹" },
    { "lv", "🇱🇻" }, { "et", "🇪🇪" }, { "is", "🇮🇸" },

    // --- Comprehensive List of Languages (A-Z by Code) ---
    { "af", "🇿🇦" }, // Afrikaans
    { "am", "🇪🇹" }, // Amharic
    { "sq", "🇦🇱" }, // Albanian
    { "hy", "🇦🇲" }, // Armenian
    { "az", "🇦🇿" }, // Azerbaijani
    { "eu", "🇪🇸" }, // Basque
    { "be", "🇧🇾" }, // Belarusian
    { "bn", "🇧🇩" }, // Bengali
    { "bs", "🇧🇦" }, // Bosnian
    { "ca", "🇪🇸" }, // Catalan
    { "ceb", "🇵🇭" },// Cebuano
    { "ny", "🇲🇼" }, // Chichewa
    { "co", "🇫🇷" }, // Corsican
    { "eo", "🏳️" }, // Esperanto
    { "fil", "🇵🇭" },// Filipino
    { "fy", "🇳🇱" }, // Frisian
    { "gl", "🇪🇸" }, // Galician
    { "ka", "🇬🇪" }, // Georgian
    { "gu", "🇮🇳" }, // Gujarati
    { "ht", "🇭🇹" }, // Haitian Creole
    { "ha", "🇳🇬" }, // Hausa
    { "haw", "🇺🇸" },// Hawaiian
    { "hmn", "🇨🇳" },// Hmong
    { "ig", "🇳🇬" }, // Igbo
    { "ga", "🇮🇪" }, // Irish
    { "jv", "🇮🇩" }, // Javanese
    { "kn", "🇮🇳" }, // Kannada
    { "kk", "🇰🇿" }, // Kazakh
    { "km", "🇰🇭" }, // Khmer
    { "rw", "🇷🇼" }, // Kinyarwanda
    { "ku", "🇮🇶" }, // Kurdish (Kurmanji)
    { "ky", "🇰🇬" }, // Kyrgyz
    { "lo", "🇱🇦" }, // Lao
    { "la", "🇻🇦" }, // Latin
    { "lb", "🇱🇺" }, // Luxembourgish
    { "mk", "🇲🇰" }, // Macedonian
    { "mg", "🇲🇬" }, // Malagasy
    { "ml", "🇮🇳" }, // Malayalam
    { "mt", "🇲🇹" }, // Maltese
    { "mi", "🇳🇿" }, // Maori
    { "mr", "🇮🇳" }, // Marathi
    { "mn", "🇲🇳" }, // Mongolian
    { "my", "🇲🇲" }, // Myanmar (Burmese)
    { "ne", "🇳🇵" }, // Nepali
    { "or", "🇮🇳" }, // Odia (Oriya)
    { "ps", "🇦🇫" }, // Pashto
    { "pa", "🇮🇳" }, // Punjabi
    { "sm", "🇼🇸" }, // Samoan
    { "gd", "🇬🇧" }, // Scots Gaelic
    { "st", "🇱🇸" }, // Sesotho
    { "sn", "🇿🇼" }, // Shona
    { "sd", "🇵🇰" }, // Sindhi
    { "si", "🇱🇰" }, // Sinhala
    { "so", "🇸🇴" }, // Somali
    { "su", "🇮🇩" }, // Sundanese
    { "sw", "🇹🇿" }, // Swahili
    { "tg", "🇹🇯" }, // Tajik
    { "ta", "🇱🇰" }, // Tamil
    { "tt", "🇷🇺" }, // Tatar
    { "te", "🇮🇳" }, // Telugu
    { "tk", "🇹🇲" }, // Turkmen
    { "ug", "🇨🇳" }, // Uyghur
    { "ur", "🇵🇰" }, // Urdu
    { "uz", "🇺🇿" }, // Uzbek
    { "cy", "🇬🇧" }, // Welsh
    { "xh", "🇿🇦" }, // Xhosa
    { "yi", "🏳️" }, // Yiddish
    { "yo", "🇳🇬" }, // Yoruba
    { "zu", "🇿🇦" }, // Zulu
};

        private static readonly List<(string Code, string Name)> PrioritizedLanguages =
[
    // Tier 1: Major Global Reach
    ("en", "🇬🇧 English"),
    ("es", "🇪🇸 Español (Spanish)"),
    ("ru", "🇷🇺 Русский (Russian)"),
    ("de", "🇩🇪 Deutsch (German)"),
    ("fr", "🇫🇷 Français (French)"),
    ("zh", "🇨🇳 中文 (Chinese)"),
    
    // Tier 2: Significant Regional & Economic
    ("pt", "🇵🇹 Português (Portuguese)"),
    ("ar", "🇸🇦 العربية (Arabic)"),
    ("ja", "🇯🇵 日本語 (Japanese)"),
    ("ko", "🇰🇷 한국어 (Korean)"),
    ("hi", "🇮🇳 हिन्दी (Hindi)"),
    ("tr", "🇹🇷 Türkçe (Turkish)"),
    ("fa", "🇮🇷 فارسی (Persian)"),

    // Tier 3: European & Southeast Asian
    ("it", "🇮🇹 Italiano (Italian)"),
    ("nl", "🇳🇱 Nederlands (Dutch)"),
    ("pl", "🇵🇱 Polski (Polish)"),
    ("id", "🇮🇩 Bahasa Indonesia"),
    ("vi", "🇻🇳 Tiếng Việt (Vietnamese)"),
    ("ms", "🇲🇾 Bahasa Melayu (Malay)"),
    ("th", "🇹🇭 ภาษาไทย (Thai)"),

    // Tier 4: Other European
    ("sv", "🇸🇪 Svenska (Swedish)"),
    ("no", "🇳🇴 Norsk (Norwegian)"),
    ("da", "🇩🇰 Dansk (Danish)"),
    ("fi", "🇫🇮 Suomi (Finnish)"),
    ("uk", "🇺🇦 Українська (Ukrainian)"),
    ("ro", "🇷🇴 Română (Romanian)"),
    ("hu", "🇭🇺 Magyar (Hungarian)"),
    ("cs", "🇨🇿 Čeština (Czech)"),
    ("el", "🇬🇷 Ελληνικά (Greek)"),
    ("he", "🇮🇱 עברית (Hebrew)"),

    // --- Comprehensive List of Additional Languages (A-Z by English Name) ---
    ("af", "🇿🇦 Afrikaans"),
    ("sq", "🇦🇱 Shqip (Albanian)"),
    ("am", "🇪🇹 አማርኛ (Amharic)"),
    ("hy", "🇦🇲 Հայերեն (Armenian)"),
    ("az", "🇦🇿 Azərbaycan dili (Azerbaijani)"),
    ("eu", "🇪🇸 Euskara (Basque)"),
    ("be", "🇧🇾 Беларуская (Belarusian)"),
    ("bn", "🇧🇩 বাংলা (Bengali)"),
    ("bs", "🇧🇦 Bosanski (Bosnian)"),
    ("bg", "🇧🇬 Български (Bulgarian)"),
    ("ca", "🇪🇸 Català (Catalan)"),
    ("ceb", "🇵🇭 Cebuano"),
    ("ny", "🇲🇼 Chichewa"),
    ("co", "🇫🇷 Corsu (Corsican)"),
    ("hr", "🇭🇷 Hrvatski (Croatian)"),
    ("eo", "🏳️ Esperanto"),
    ("et", "🇪🇪 Eesti (Estonian)"),
    ("fil", "🇵🇭 Filipino"),
    ("fy", "🇳🇱 Frysk (Frisian)"),
    ("gl", "🇪🇸 Galego (Galician)"),
    ("ka", "🇬🇪 ქართული (Georgian)"),
    ("gu", "🇮🇳 ગુજરાતી (Gujarati)"),
    ("ht", "🇭🇹 Kreyòl ayisyen (Haitian Creole)"),
    ("ha", "🇳🇬 Hausa"),
    ("haw", "🇺🇸 ʻŌlelo Hawaiʻi (Hawaiian)"),
    ("hmn", "🇨🇳 Hmong"),
    ("ig", "🇳🇬 Igbo"),
    ("is", "🇮🇸 Íslenska (Icelandic)"),
    ("ga", "🇮🇪 Gaeilge (Irish)"),
    ("jv", "🇮🇩 Basa Jawa (Javanese)"),
    ("kn", "🇮🇳 ಕನ್ನಡ (Kannada)"),
    ("kk", "🇰🇿 Қазақ тілі (Kazakh)"),
    ("km", "🇰🇭 ខ្មែរ (Khmer)"),
    ("rw", "🇷🇼 Kinyarwanda"),
    ("ku", "🇮🇶 Kurdî (Kurmanji)"),
    ("ky", "🇰🇬 Кыргызча (Kyrgyz)"),
    ("lo", "🇱🇦 ພາສາລາວ (Lao)"),
    ("la", "🇻🇦 Latina (Latin)"),
    ("lv", "🇱🇻 Latviešu (Latvian)"),
    ("lt", "🇱🇹 Lietuvių (Lithuanian)"),
    ("lb", "🇱🇺 Lëtzebuergesch (Luxembourgish)"),
    ("mk", "🇲🇰 Македонски (Macedonian)"),
    ("mg", "🇲🇬 Malagasy"),
    ("ml", "🇮🇳 മലയാളം (Malayalam)"),
    ("mt", "🇲🇹 Malti (Maltese)"),
    ("mi", "🇳🇿 Te Reo Māori (Maori)"),
    ("mr", "🇮🇳 मराठी (Marathi)"),
    ("mn", "🇲🇳 Монгол (Mongolian)"),
    ("my", "🇲🇲 မြန်မာ (Myanmar/Burmese)"),
    ("ne", "🇳🇵 नेपाली (Nepali)"),
    ("or", "🇮🇳 ଓଡିଆ (Odia/Oriya)"),
    ("ps", "🇦🇫 پښتو (Pashto)"),
    ("pa", "🇮🇳 ਪੰਜਾਬੀ (Punjabi)"),
    ("sm", "🇼🇸 Gagana fa'a Sāmoa (Samoan)"),
    ("gd", "🇬🇧 Gàidhlig (Scots Gaelic)"),
    ("sr", "🇷🇸 Српски (Serbian)"),
    ("st", "🇱🇸 Sesotho"),
    ("sn", "🇿🇼 Shona"),
    ("sd", "🇵🇰 سنڌي (Sindhi)"),
    ("si", "🇱🇰 සිංහල (Sinhala)"),
    ("sk", "🇸🇰 Slovenčina (Slovak)"),
    ("sl", "🇸🇮 Slovenščina (Slovenian)"),
    ("so", "🇸🇴 Soomaali (Somali)"),
    ("su", "🇮🇩 Basa Sunda (Sundanese)"),
    ("sw", "🇹🇿 Kiswahili (Swahili)"),
    ("tg", "🇹🇯 Тоҷикӣ (Tajik)"),
    ("ta", "🇱🇰 தமிழ் (Tamil)"),
    ("tt", "🇷🇺 Татар (Tatar)"),
    ("te", "🇮🇳 తెలుగు (Telugu)"),
    ("tk", "🇹🇲 Türkmençe (Turkmen)"),
    ("ug", "🇨🇳 ئۇيغۇرچە (Uyghur)"),
    ("ur", "🇵🇰 اردو (Urdu)"),
    ("uz", "🇺🇿 O‘zbekcha (Uzbek)"),
    ("cy", "🇬🇧 Cymraeg (Welsh)"),
    ("xh", "🇿🇦 isiXhosa (Xhosa)"),
    ("yi", "🏳️ ייִדיש (Yiddish)"),
    ("yo", "🇳🇬 Yorùbá (Yoruba)"),
    ("zu", "🇿🇦 isiZulu (Zulu)"),
];
        #endregion

        public EducationalContentHandler(
      ILogger<EducationalContentHandler> logger,
      ITelegramMessageSender messageSender,
      ITelegramBotClient botClient,
      IMemoryCacheService<CachedMenuDto> menuCache,
      IMemoryCacheService<string> filePathCache)
        {
            _logger = logger;
            _messageSender = messageSender;
            _botClient = botClient;
            _menuCache = menuCache; // Use this name consistently
            _filePathCache = filePathCache;

            InitializeContentDirectories();
        }


        /// <summary>
        /// A robust, one-time startup procedure to ensure the entire required
        /// directory structure exists, preventing runtime errors. This is idempotent.
        /// </summary>
        private void InitializeContentDirectories()
        {
            _logger.LogInformation("Verifying educational content directory structure at '{BasePath}'...", BaseLearningPath);
            try
            {
                string[] contentCategories = new[] { "podcasts", "risk_management", "video_tutorials" };
                IEnumerable<string> languagesToSupport = PrioritizedLanguages.Select(lang => lang.Code);

                foreach (string? category in contentCategories)
                {
                    foreach (string? langCode in languagesToSupport)
                    {
                        // Directory.CreateDirectory is smart: it does nothing if the path already exists.
                        _ = Directory.CreateDirectory(Path.Combine(BaseLearningPath, category, langCode));
                    }
                }
                _logger.LogInformation("Content directory structure verified successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "FATAL: Could not create educational content directories. Check file system permissions for the application's user account at '{Path}'.", BaseLearningPath);
            }
        }

        //================================================================================
        // SECTION 4: CORE HANDLER LOGIC (CanHandle & HandleAsync)
        //================================================================================
        #region Core Handler Logic


        public bool CanHandle(Update update)
        {
            return update.Type == UpdateType.CallbackQuery && update.CallbackQuery?.Data?.StartsWith(HandlerPrefix) == true;
        }

        /// <summary>
        /// The main entry point for all callbacks. It acts as a secure, high-level router,
        /// decoding the callback data and dispatching to the appropriate internal processor.
        /// </summary>
        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            CallbackQuery callbackQuery = update.CallbackQuery!;
            await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);

            long chatId = callbackQuery.Message!.Chat.Id;
            int messageId = callbackQuery.Message.MessageId;
            string data = callbackQuery.Data!;

            using IDisposable? logScope = _logger.BeginScope("EduHandler: User={UserId}, CbData={Data}", callbackQuery.From.Id, data);

            // --- FIX: Corrected routing logic to prevent fall-through ---
            if (data.StartsWith(SendFilePrefix))
            {
                await ProcessFileRequestAsync(chatId, data, cancellationToken);
            }
            else // It must be a navigation or pagination request
            {
                await ProcessNavigationRequestAsync(chatId, messageId, data, cancellationToken);
            }
        }

        #endregion

        //================================================================================
        // SECTION 5: INTERNAL PROCESSING LOGIC (The "How")
        //================================================================================
        #region Internal Processing Methods

        /// <summary>
        /// Processes navigation-related requests (showing folders, pages).
        /// </summary>
        private async Task ProcessNavigationRequestAsync(long chatId, int messageId, string callbackData, CancellationToken ct)
        {
            string encodedPath;
            int page = 1;

            if (callbackData.StartsWith(PagePrefix))
            {
                string[] parts = callbackData[PagePrefix.Length..].Split(new[] { "_page_" }, StringSplitOptions.None);
                encodedPath = parts[0];
                page = int.Parse(parts[1]);
            }
            else
            {
                encodedPath = callbackData[NavPrefix.Length..];
            }

            string relativePath = DecodeUrlSafeBase64(encodedPath);

            // --- SECURITY: Path Traversal Check ---
            if (relativePath.Contains("..") || Path.IsPathRooted(relativePath))
            {
                _logger.LogWarning("Directory traversal attempt blocked: '{Path}'", relativePath);
                return;
            }

            string absolutePath = Path.GetFullPath(Path.Combine(BaseLearningPath, relativePath));
            if (!absolutePath.StartsWith(BaseLearningPath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Path validation failed. Attempt to access outside base directory: '{Path}'", absolutePath);
                return;
            }

            if (Directory.Exists(absolutePath))
            {
                await ShowFolderContentsAsync(chatId, messageId, relativePath, page, ct);
            }
            else
            {
                _logger.LogWarning("User navigated to a directory that does not exist: {Path}", absolutePath);
                await _messageSender.SendTextMessageAsync(chatId, "Sorry, that category no longer exists.", cancellationToken: ct);
            }
        }

        /// <summary>
        /// Processes a request to send a specific file identified by its hash.
        /// </summary>
        private async Task ProcessFileRequestAsync(long chatId, string callbackData, CancellationToken ct)
        {
            string fileHash = callbackData[SendFilePrefix.Length..];

            if (!_filePathCache.TryGetValue(fileHash, out string? absolutePath) || string.IsNullOrEmpty(absolutePath))
            {
                _logger.LogWarning("File hash '{Hash}' not found in cache. It may have expired.", fileHash);
                await _messageSender.SendTextMessageAsync(chatId, "This link has expired. Please navigate the menu again.", cancellationToken: ct);
                return;
            }

            if (!File.Exists(absolutePath))
            {
                _logger.LogError("File path '{Path}' from cache hash '{Hash}' does not exist on disk.", absolutePath, fileHash);
                await _messageSender.SendTextMessageAsync(chatId, "Sorry, an error occurred and this file could not be found.", cancellationToken: ct);
                return;
            }

            await SendFileAsync(chatId, absolutePath, ct);
        }

        #endregion

        //================================================================================
        // SECTION 6: UI GENERATION & FILE SENDING (The "What")
        //================================================================================
        #region UI Generation & File Sending
        /// <summary>
        /// Retrieves a menu from the cache or generates it if not present.
        /// </summary>
        private async Task ShowFolderContentsAsync(long chatId, int messageId, string relativePath, int page, CancellationToken ct)
        {
            string encodedPath = EncodeUrlSafeBase64(relativePath); // Encode path for cache key
            string cacheKey = $"edu_menu_{encodedPath}_p{page}";

            if (!_menuCache.TryGetValue(cacheKey, out CachedMenuDto? cachedMenu))
            {
                _logger.LogInformation("CACHE MISS: Generating menu for path '{Path}', page {Page}.", relativePath, page);
                cachedMenu = GenerateFolderMenuDto(relativePath, page);
                _menuCache.Set(cacheKey, cachedMenu, CacheDuration);
            }
            else
            {
                _logger.LogInformation("CACHE HIT: Serving menu for path '{Path}', page {Page}.", relativePath, page);
            }

            await _messageSender.EditMessageTextAsync(chatId, messageId, cachedMenu!.Text, ParseMode.Markdown, cachedMenu.Keyboard, ct);
        }


        /// <summary>
        /// The core logic for building the menu DTO. This is a pure function with no I/O,
        /// making it fast and testable.
        /// </summary>
        private CachedMenuDto GenerateFolderMenuDto(string relativePath, int page)
        {
            string absolutePath = Path.Combine(BaseLearningPath, relativePath);
            string title = BuildBreadcrumbTitle(relativePath);

            List<InlineKeyboardButton> allItems = [];

            try
            {
                // Get sub-directories (ONLY IF NOT EMPTY)
                allItems.AddRange(Directory.GetDirectories(absolutePath)
                    .Where(dir => Directory.EnumerateFileSystemEntries(dir).Any()) // Filter out empty directories
                    .OrderBy(d => d)
                    .Select(dir => BuildDirectoryButton(relativePath, Path.GetFileName(dir))));

                // Get supported files
                allItems.AddRange(Directory.GetFiles(absolutePath)
                    .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f)
                    .Select(file => BuildFileButton(absolutePath, Path.GetFileName(file))));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate menu items for path: {Path}", absolutePath);
            }

            // --- Pagination Logic ---
            int totalItems = allItems.Count;
            int totalPages = (int)Math.Ceiling(totalItems / (double)PageSize);
            page = Math.Clamp(page, 1, totalPages);
            List<InlineKeyboardButton> itemsForPage = allItems.Skip((page - 1) * PageSize).Take(PageSize).ToList();

            // --- Text Formatting ---
            string finalTitle = totalPages > 1 ? $"{title} (Page {page}/{totalPages})" : title;
            string text = $"{TelegramMessageFormatter.Bold(finalTitle)}\n\n{TelegramMessageFormatter.Italic("Please choose an option below:")}";
            if (totalItems == 0)
            {
                text += "\n\n_This section is currently empty._";
            }

            // --- Keyboard Assembly ---
            List<List<InlineKeyboardButton>> keyboardRows = itemsForPage.Select(b => new List<InlineKeyboardButton> { b }).ToList();

            List<InlineKeyboardButton> navButtons = [];
            string encodedCurrentPath = EncodeUrlSafeBase64(relativePath);
            if (page > 1)
            {
                navButtons.Add(InlineKeyboardButton.WithCallbackData("⬅️ Previous", $"{PagePrefix}{encodedCurrentPath}_page_{page - 1}"));
            }

            if (page < totalPages)
            {
                navButtons.Add(InlineKeyboardButton.WithCallbackData("Next ➡️", $"{PagePrefix}{encodedCurrentPath}_page_{page + 1}"));
            }

            if (navButtons.Any())
            {
                keyboardRows.Add(navButtons);
            }

            string? parentRelativePath = Path.GetDirectoryName(relativePath)?.Replace('\\', '/');
            if (parentRelativePath != null)
            {
                keyboardRows.Add([InlineKeyboardButton.WithCallbackData("⬆️ Up One Level", NavPrefix + EncodeUrlSafeBase64(parentRelativePath))]);
            }
            else
            {
                keyboardRows.Add([InlineKeyboardButton.WithCallbackData("↩️ Main Menu", "show_main_menu")]);
            }

            return new CachedMenuDto(text, new InlineKeyboardMarkup(keyboardRows));
        }
        /// <summary>
        /// Sends a file to a chat.
        /// </summary>
        /// <param name="chatId"></param>
        /// <param name="absolutePath"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [AutomaticRetry(Attempts = 3)]
        private async Task SendFileAsync(long chatId, string absolutePath, CancellationToken ct)
        {
            string fileName = Path.GetFileName(absolutePath);
            _logger.LogInformation("User requested file: {FilePath}", absolutePath);

            // --- STEP 1: Send Chat Action (Typing...) ---
            try
            {
                await _botClient.SendChatAction(chatId, ChatAction.UploadDocument);
                _logger.LogDebug("Sent ChatAction 'UploadDocument' to ChatID {ChatId}.", chatId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred while sending chat action to ChatID {ChatId}.", chatId);
                // We can often continue even if this fails.
            }

            Task sendTask;

            try
            {
                // --- STEP 2: Open File Stream and Prepare InputFile ---
                FileStream? fileStream = new(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                InputFile? inputFile = InputFile.FromStream(fileStream, fileName);

                // --- FIX: Truncate caption to prevent exceeding Telegram's limits ---
                // Telegram has a caption limit of 1024 characters.
                string formattedFileName = GetFormattedName(fileName); // Cleaned up filename, e.g., "Trading Basics"

                // Create the base caption.
                StringBuilder captionBuilder = new($"{formattedFileName}\n\nShared by @trade_ai_helper_bot");

                // Append the summary if available and if there's space.
                // We'll need to fetch the summary here if it's not directly in the filename.
                // For now, let's assume the filename itself is the primary content.
                // If you have a separate way to get summaries, you'd fetch and append them here,
                // making sure to truncate them to leave room for the filename and bot signature.

                // Truncate the final caption to be safe, leaving room for the bot signature and ensuring it's not too long.
                // We'll leave some buffer for the bot signature and potential future additions.
                const int captionMaxLength = 1000; // Slightly less than Telegram's 1024 limit for safety.
                string? caption = captionBuilder.ToString();
                if (caption.Length > captionMaxLength)
                {
                    caption = caption[..(captionMaxLength - 3)] + "..."; // Truncate and add ellipsis
                }
                // --- END OF CAPTION FIX ---

                string? extension = Path.GetExtension(fileName).ToLowerInvariant();

                if (extension is ".mp3" or ".wav")
                {
                    sendTask = _botClient.SendAudio(chatId, inputFile, caption: caption, cancellationToken: ct);
                }
                else if (extension is ".mp4" or ".mov" or ".mkv")
                {
                    sendTask = _botClient.SendVideo(chatId, inputFile, caption: caption, cancellationToken: ct);
                }
                else
                {
                    _logger.LogWarning("User requested unsupported file type: {FileName}", fileName);
                    await _messageSender.SendTextMessageAsync(chatId, "Sorry, this file type is not supported for direct playback.", cancellationToken: ct);
                    return; // Do not proceed if file type is unsupported.
                }

                await sendTask;

                _logger.LogInformation("Successfully sent file '{FileName}' to ChatID {ChatId}.", fileName, chatId);
            }
            // --- SPECIFIC API EXCEPTIONS ---
            catch (OperationCanceledException)
            {
                _logger.LogInformation("File sending operation for '{FileName}' was cancelled.", fileName);
            }
            catch (ApiRequestException apiEx)
            {
                _logger.LogError(apiEx, "Telegram API Error while sending file '{FileName}' to ChatID {ChatId}. Error Code: {ErrorCode}, Message: {ApiMessage}",
                    fileName, chatId, apiEx.ErrorCode, apiEx.Message);

                if (apiEx.Message.Contains("file is too large") || apiEx.ErrorCode == 413) // Telegram Error Code 413 for too large file
                {
                    await _messageSender.SendTextMessageAsync(chatId, "The file is too large to send. Please check Telegram's file size limits.", cancellationToken: ct);
                }
                else if (apiEx.ErrorCode == 403 || apiEx.Message.Contains("bot was blocked by the user") || apiEx.Message.Contains("chat not found"))
                {
                    _logger.LogWarning("User {ChatId} blocked the bot or the bot lost access. Marking as unreachable.", chatId);
                    // Placeholder for marking user as unreachable
                    // await _userService.MarkUserAsUnreachableAsync(chatId.ToString(), $"ApiError_{apiEx.ErrorCode}", CancellationToken.None);
                }
                else if (apiEx.ErrorCode == 400 && apiEx.Message.Contains("chat not found"))
                {
                    _logger.LogWarning("Chat {ChatId} not found or bot lost access. Cannot send file.", chatId);
                }
                else
                {
                    await _messageSender.SendTextMessageAsync(chatId, $"Telegram API error: {apiEx.Message}. Please try again later.", cancellationToken: ct);
                }
                throw; // Re-throw to indicate failure to the caller/Hangfire
            }
            // --- GENERAL EXCEPTIONS ---
            catch (IOException ioEx)
            {
                _logger.LogError(ioEx, "File IO error while reading '{FileName}' for ChatID {ChatId}.", fileName, chatId);
                await _messageSender.SendTextMessageAsync(chatId, "Error reading the file. It might be corrupted or inaccessible.", cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred while processing or sending file '{FileName}' to ChatID {ChatId}.", fileName, chatId);
                await _messageSender.SendTextMessageAsync(chatId, "An unexpected error occurred. Please try again later.", cancellationToken: ct);
            }
            finally
            {
                // The 'await using var fileStream = ...' statement handles disposal automatically.
                // Explicitly disposing here is redundant but harmless if you prefer the pattern.
                // fileStream?.Dispose();
            }
        }
        #endregion

        //================================================================================
        // SECTION 7: PRIVATE STATIC HELPERS (Pure Functions)
        //================================================================================
        #region Private Static Helpers

        private InlineKeyboardButton BuildDirectoryButton(string relativePath, string dirName)
        {
            string newRelativePath = Path.Combine(relativePath, dirName).Replace('\\', '/');
            string encodedNewPath = EncodeUrlSafeBase64(newRelativePath);

            string buttonText = PrioritizedLanguages.FirstOrDefault(l => l.Code == dirName).Name ?? $"{GetEmojiForName(dirName)} {GetFormattedName(dirName)}";
            return InlineKeyboardButton.WithCallbackData(buttonText, NavPrefix + encodedNewPath);
        }
        private InlineKeyboardButton BuildFileButton(string absoluteDirPath, string fileName)
        {
            string fullPath = Path.Combine(absoluteDirPath, fileName);
            string fileHash = GenerateSha256Hash(fullPath);
            _filePathCache.Set(fileHash, fullPath, FilePathCacheDuration);
            return InlineKeyboardButton.WithCallbackData($"▶️ {GetFormattedName(fileName)}", SendFilePrefix + fileHash);
        }

        private List<List<InlineKeyboardButton>> BuildPaginatedKeyboard(List<InlineKeyboardButton> itemsForPage, string relativePath, int page, int totalPages)
        {
            List<List<InlineKeyboardButton>> keyboardRows = itemsForPage.Select(b => new List<InlineKeyboardButton> { b }).ToList();

            List<InlineKeyboardButton> navButtons = [];
            string encodedCurrentPath = EncodeUrlSafeBase64(relativePath);
            if (page > 1)
            {
                navButtons.Add(InlineKeyboardButton.WithCallbackData("⬅️ Previous", $"{PagePrefix}{encodedCurrentPath}_page_{page - 1}"));
            }

            if (page < totalPages)
            {
                navButtons.Add(InlineKeyboardButton.WithCallbackData("Next ➡️", $"{PagePrefix}{encodedCurrentPath}_page_{page + 1}"));
            }

            if (navButtons.Any())
            {
                keyboardRows.Add(navButtons);
            }

            string? parentRelativePath = Path.GetDirectoryName(relativePath)?.Replace('\\', '/');
            if (parentRelativePath != null)
            {
                keyboardRows.Add([InlineKeyboardButton.WithCallbackData("⬆️ Up One Level", NavPrefix + EncodeUrlSafeBase64(parentRelativePath))]);
            }
            else
            {
                keyboardRows.Add([InlineKeyboardButton.WithCallbackData("↩️ Main Menu", "show_main_menu")]);
            }

            return keyboardRows;
        }

        private string BuildBreadcrumbTitle(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                return "📚 Learning Center";
            }

            IEnumerable<string> pathParts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Select(part => $"{GetEmojiForName(part)} {GetFormattedName(part)}");
            return string.Join(" > ", pathParts);
        }

        private static string GenerateSha256Hash(string input)
        {
            return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(input))).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        }

        private static string EncodeUrlSafeBase64(string plainText)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText)).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        }

        // In TelegramPanel/Application/CommandHandlers/Features/EducationalContentHandler.cs
        // Inside the private static helpers section

        private static string DecodeUrlSafeBase64(string base64Url)
        {
            // 1. Replace URL-safe characters back to standard Base64 characters.
            base64Url = base64Url.Replace('-', '+').Replace('_', '/');

            // 2. Add the necessary padding. The length of a valid Base64 string
            //    (without padding) must be a multiple of 4.
            switch (base64Url.Length % 4)
            {
                case 2: base64Url += "=="; break;
                case 3: base64Url += "="; break;
            }

            // 3. Decode the now-valid Base64 string.
            byte[] decodedBytes = Convert.FromBase64String(base64Url);
            return Encoding.UTF8.GetString(decodedBytes);
        }
        private static string GetFormattedName(string name)
        {
            return string.IsNullOrEmpty(name) ? "Back" : Path.GetFileNameWithoutExtension(name).Replace("_", " ").Replace("-", " ");
        }

        private static string GetEmojiForName(string name)
        {
            return FolderEmojiMap.TryGetValue(name.ToLowerInvariant(), out string? emoji) ? emoji : "📁";
        }

        #endregion
    }
}