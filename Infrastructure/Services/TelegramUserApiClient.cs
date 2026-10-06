// File: src/Infrastructure/Services/TelegramUserApiClient.cs
#region Usings
using Application.Common.Interfaces;
using Application.Common.Interfaces.Admin;
using Application.Interfaces;
using Infrastructure.Settings;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using TL;
#endregion

namespace Infrastructure.Services
{
    /// <summary>
    /// Implements ITelegramUserApiClient using WTelegramClient for user-level Telegram API interactions.
    /// This "Pro Version" focuses on robust connection management, resilience, and comprehensive logging.
    /// </summary>
    public class TelegramUserApiClient : ITelegramUserApiClient
    {
        private readonly INotificationToAdminService _notifToAdmin;
        public bool IsConnected { get; private set; } = false;
        #region Private Readonly Fields
        private readonly IMemoryCache _memoryCache;

        private readonly ILogger<TelegramUserApiClient> _logger;
        private readonly TelegramUserApiSettings _settings;
        private readonly IHashtagService _hashtagService;
        private readonly ConcurrentDictionary<long, (TL.User User, DateTime Expiry)> _userCacheWithExpiry = new();
        private readonly ConcurrentDictionary<long, (TL.ChatBase Chat, DateTime Expiry)> _chatCacheWithExpiry = new();
        private readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(30);
        private readonly MemoryCache _messageCache = new(new MemoryCacheOptions());
        private readonly MemoryCacheEntryOptions _cacheOptions = new MemoryCacheEntryOptions()
            .SetSlidingExpiration(TimeSpan.FromMinutes(5))
            .SetAbsoluteExpiration(TimeSpan.FromHours(1));
        private readonly TimeSpan _cacheCleanupInterval = TimeSpan.FromMinutes(10);
        private readonly IAdviceService _adviceService;
        private readonly IServiceProvider _serviceProvider;
        private readonly ResiliencePipeline _resiliencePipeline;
        private readonly TimeSpan[] _retryDelays = new TimeSpan[]
        {
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8)
        };

        // LEVEL 10: New Private Readonly Fields for Channel
        private readonly bool _useChannelForDispatch; // Configurable flag via constructor
        private readonly MarkdownParserService _markdownParserService; // Add markdown parser service
        private readonly IGeminiService _geminiService; // Inject GeminiService singleton
        #endregion

        #region Private Fields
        private WTelegram.Client? _client;
        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private System.Threading.Timer? _cacheCleanupTimer;
        // Internal caches used by WTelegramClient's update handler to populate main caches.
        // These remain Dictionary<long, ...> as WTelegramClient's CollectUsersChats populates these.
        private readonly Dictionary<long, TL.User> _internalWtcUserCache = [];
        private readonly Dictionary<long, TL.ChatBase> _internalWtcChatCache = [];
        private readonly string _sessionPath; // Explicit session file path for custom loader/saver

        // LEVEL 10: New Private Fields for Channel
        private Channel<TL.Update>? _updateChannel; // Using the new alias 'Channel'
        private Task? _channelConsumerTask;
        #endregion

        #region Public Properties & Events (Interface Implementation)
        public WTelegram.Client NativeClient => _client!;

        // --- CRITICAL FIX: RESTORED TO MATCH INTERFACE ---
        // Original: public event Action<TL.Update> OnCustomUpdateReceived = delegate { };
        // This line is now exactly as it was in your interface.
        public event Action<TL.Update> OnCustomUpdateReceived = delegate { };
        #endregion

        #region Constructor
        public TelegramUserApiClient(IAdviceService adviceService, IServiceProvider serviceProvider,
             ILogger<TelegramUserApiClient> logger, IHashtagService hashtagService, IMemoryCache memoryCache,
             IOptions<TelegramUserApiSettings> settingsOptions,
             MarkdownParserService markdownParserService,
             IGeminiService geminiService,
             INotificationToAdminService notificationToAdminService,
             bool useChannelForDispatch = false)
        {
            _memoryCache = memoryCache;
            _serviceProvider = serviceProvider;
            _geminiService = geminiService ?? throw new ArgumentNullException(nameof(geminiService));
            _hashtagService = hashtagService;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settingsOptions?.Value ?? throw new ArgumentNullException(nameof(settingsOptions));
            _useChannelForDispatch = useChannelForDispatch;
            _notifToAdmin = notificationToAdminService ?? throw new ArgumentNullException(nameof(notificationToAdminService));
            _markdownParserService = markdownParserService ?? throw new ArgumentNullException(nameof(markdownParserService));
            _adviceService = adviceService;

            // --- FIX: Robust Session Path Logic ---
            string defaultPath = Path.Combine(AppContext.BaseDirectory, _settings.SessionPath ?? "telegram_user.session");
            string? dir = Path.GetDirectoryName(defaultPath);

            // Check write permission by trying to create a dummy file
            bool isWritable = false;
            try
            {
                if (!string.IsNullOrEmpty(dir))
                {
                    if (!Directory.Exists(dir))
                    {
                        _ = Directory.CreateDirectory(dir);
                    }

                    string dummyPath = Path.Combine(dir, ".write_test_" + Guid.NewGuid());
                    using (File.Create(dummyPath, 1, FileOptions.DeleteOnClose)) { }
                    isWritable = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Permission check failed for path '{Path}': {Message}", defaultPath, ex.Message);
                isWritable = false;
            }

            if (isWritable)
            {
                _sessionPath = defaultPath;
                _logger.LogInformation("Session file will be stored at: {Path}", _sessionPath);
            }
            else
            {
                // Fallback to Temp directory if App directory is read-only (common in Docker)
                string fallbackPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(defaultPath));
                _logger.LogWarning("⚠️ Write permission denied for '{DefaultPath}'. Falling back to system temp directory: '{FallbackPath}'", defaultPath, fallbackPath);
                _sessionPath = fallbackPath;
            }
            // ---------------------------------------

            // Configure WTelegramClient's internal logging
            WTelegram.Helpers.Log = (level, message) =>
            {
                LogLevel msLevel = level switch
                {
                    0 => Microsoft.Extensions.Logging.LogLevel.Trace,
                    1 => Microsoft.Extensions.Logging.LogLevel.Debug,
                    2 => Microsoft.Extensions.Logging.LogLevel.Information,
                    3 => Microsoft.Extensions.Logging.LogLevel.Warning,
                    4 => Microsoft.Extensions.Logging.LogLevel.Error,
                    _ => Microsoft.Extensions.Logging.LogLevel.None,
                };
                // Avoid spamming logs with basic info unless debugging
                if (msLevel >= Microsoft.Extensions.Logging.LogLevel.Warning || _logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace))
                {
                    _logger.Log(msLevel, "[WTelegram] {Message}", message);
                }
            };

            // Define custom session loader
            byte[]? startSessionLoader()
            {
                _logger.LogTrace("Custom session loader: Attempting to load session from {SessionPath}...", _sessionPath);
                if (!File.Exists(_sessionPath))
                {
                    _logger.LogDebug("Custom session loader: Session file {SessionPath} does not exist. Returning null.", _sessionPath);
                    return null;
                }
                try
                {
                    byte[] data = File.ReadAllBytes(_sessionPath);
                    _logger.LogInformation("Custom session loader: Successfully loaded {BytesRead} bytes.", data.Length);
                    return data;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Custom session loader: Error reading session file {SessionPath}. Deleting corrupt file.", _sessionPath);
                    try { File.Delete(_sessionPath); } catch { }
                    return null;
                }
            }

            // Define custom session saver
            void saveSessionAction(byte[] bytes)
            {
                _logger.LogTrace("Custom session saver: Saving {BytesLength} bytes to {SessionPath}...", bytes.Length, _sessionPath);
                try
                {
                    string tempPath = _sessionPath + ".tmp";
                    File.WriteAllBytes(tempPath, bytes);
                    File.Move(tempPath, _sessionPath, overwrite: true);
                    _logger.LogInformation("Custom session saver: Saved successfully.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Custom session saver: Failed to save session to {SessionPath}.", _sessionPath);
                }
            }

            try
            {
                _client = new WTelegram.Client(ConfigProvider, startSessionLoader(), saveSessionAction);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create WTelegram.Client.");
                throw new InvalidOperationException("WTelegram.Client could not be initialized.", ex);
            }

            _client.OnUpdates += async updates =>
            {
                if (updates is TL.UpdatesBase updatesBase)
                {
                    await HandleUpdatesBaseAsync(updatesBase);
                }
            };

            try
            {
                _cacheCleanupTimer = new System.Threading.Timer(CacheCleanup, null, (int)_cacheCleanupInterval.TotalMilliseconds, (int)_cacheCleanupInterval.TotalMilliseconds);
            }
            catch { _logger.LogError("Failed to start cache cleanup timer."); }

            // Configure Polly
            _resiliencePipeline = new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
                {
                    ShouldHandle = new PredicateBuilder()
                        .Handle<HttpRequestException>()
                        .Handle<IOException>()
                        .Handle<RpcException>(rpcEx => rpcEx.Code is >= 500 and < 600 || rpcEx.Message.Contains("FLOOD_WAIT") || rpcEx.Message.Contains("TOO_MANY_REQUESTS")),
                    DelayGenerator = args =>
                    {
                        int attempt = args.AttemptNumber;
                        return attempt < _retryDelays.Length ? ValueTask.FromResult<TimeSpan?>(_retryDelays[attempt]) : ValueTask.FromResult<TimeSpan?>(null);
                    },
                    MaxRetryAttempts = _retryDelays.Length,
                    OnRetry = args =>
                    {
                        _logger.LogWarning("Polly Retry: Attempt {Attempt} failed. Retrying...", args.AttemptNumber + 1);
                        return default;
                    }
                })
                .Build();

            if (_useChannelForDispatch)
            {
                StartUpdateChannelPipeline();
            }
        }
        #endregion

        #region Configuration Provider


        // --- LEVEL 10: NEW PRIVATE METHODS FOR CHANNEL MANAGEMENT ---
        // Place these methods directly inside TelegramUserApiClient
        private void StartUpdateChannelPipeline()
        {
            if (_updateChannel != null)
            {
                return; // Ambiguity fix already applied via 'using Channel = ...' alias
            }

            // Use the 'Channel' alias from the using directive
            _updateChannel = System.Threading.Channels.Channel.CreateUnbounded<TL.Update>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });

            _channelConsumerTask = Task.Run(async () =>
            {
                await foreach (Update update in _updateChannel.Reader.ReadAllAsync()) // Reader is now correctly accessed
                {
                    try
                    {
                        // Level 7: Conditional logging for channel dispatch
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.LogTrace("Update channel consumer: Dispatching update of type {UpdateType}.", update.GetType().Name);
                        }
                        // Due to interface constraints (Action<TL.Update>), we must invoke synchronously.
                        // Subscribers must handle async work internally (e.g., Task.Run or async void).
                        OnCustomUpdateReceived?.Invoke(update);
                    }
                    catch (Exception ex) // Catch sync exceptions from handlers
                    {
                        _logger.LogError(ex, "Update channel consumer: Exception during synchronous OnCustomUpdateReceived invocation for update type {UpdateType}.",
                                          update.GetType().Name);
                    }
                }
            });
            _logger.LogInformation("Update processing channel pipeline started.");
        }



        public async Task StopUpdateChannelPipelineAsync()
        {
            if (_updateChannel != null)
            {
                _logger.LogInformation("Completing update processing channel pipeline...");
                _updateChannel.Writer.Complete();
                if (_channelConsumerTask != null)
                {
                    await _channelConsumerTask;
                }
                _updateChannel = null;
                _channelConsumerTask = null;
                _logger.LogInformation("Update processing channel pipeline stopped.");
            }
        }

        // --- LEVEL 1 & 7: NEW PRIVATE HELPER METHOD FOR LOGGING ---
        // Place this method directly inside YourExistingClass
        /// <summary>
        /// Helper method to truncate strings for logging, and guard against `ToString()` performance.
        /// Only calls `ToString()` if the specific log level is enabled.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private string GetUpdateContentForLogging(object? obj, int maxLength = 100)
        {
            if (obj == null)
            {
                return "null";
            }

            if (_logger.IsEnabled(LogLevel.Debug) || _logger.IsEnabled(LogLevel.Trace))
            {
                string value = obj.ToString() ?? string.Empty;
                return value.Length <= maxLength ? value : value[..maxLength] + "...";
            }
            return "Content not logged at current level.";
        }

        // --- LEVEL 4: NEW PRIVATE HELPER METHODS FOR MESSAGE CONSTRUCTION ---
        // Place these methods directly inside YourExistingClass
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Peer ResolvePeer(long id, bool isChat)
        {
            if (isChat)
            {
                if (_internalWtcChatCache.TryGetValue(id, out ChatBase? cachedChat))
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.LogTrace("ResolvePeer (Chat): Chat {ChatId} (Type: {ChatType}) found in WTC internal cache.", id, cachedChat.GetType().Name);
                    }
                    return cachedChat is TL.Channel channel ? new TL.PeerChannel { channel_id = channel.id } : new TL.PeerChat { chat_id = cachedChat.ID };
                }
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning("ResolvePeer (Chat): Chat {ChatId} not in WTC internal cache, using minimal PeerChat (ID only).", id);
                }
                return new TL.PeerChat { chat_id = id };
            }
            else // is User
            {
                if (_internalWtcUserCache.TryGetValue(id, out User? cachedUser))
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.LogTrace("ResolvePeer (User): User {UserId} found in WTC internal cache.", id);
                    }
                    return new TL.PeerUser { user_id = cachedUser.id };
                }
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning("ResolvePeer (User): User {UserId} not in WTC internal cache, using minimal PeerUser (ID only).", id);
                }
                return new TL.PeerUser { user_id = id };
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private TL.UpdateNewMessage ConvertShortMessageToNewMessage(TL.UpdateShortMessage usm)
        {
            Peer userPeer = ResolvePeer(usm.user_id, false);

            Message msg = new()
            {
                flags = 0,
                id = usm.id,
                peer_id = userPeer,
                from_id = userPeer,
                message = usm.message,
                date = usm.date,
                entities = usm.entities,
                media = null,
                reply_to = usm.reply_to,
                fwd_from = usm.fwd_from,
                via_bot_id = usm.via_bot_id,
                ttl_period = usm.ttl_period,
                grouped_id = 0,
            };

            TransferMessageFlags(ref msg.flags, usm.flags);

            return new TL.UpdateNewMessage { message = msg, pts = usm.pts, pts_count = usm.pts_count };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private TL.UpdateNewMessage ConvertShortChatMessageToNewMessage(TL.UpdateShortChatMessage uscm)
        {
            Peer chatPeer = ResolvePeer(uscm.chat_id, true);
            Peer fromPeer = ResolvePeer(uscm.from_id, false);

            Message msg = new()
            {
                flags = 0,
                id = uscm.id,
                peer_id = chatPeer,
                from_id = fromPeer,
                message = uscm.message,
                date = uscm.date,
                entities = uscm.entities,
                media = null,
                reply_to = uscm.reply_to,
                fwd_from = uscm.fwd_from,
                via_bot_id = uscm.via_bot_id,
                ttl_period = uscm.ttl_period,
                grouped_id = 0,
            };

            TransferMessageFlags(ref msg.flags, uscm.flags);

            return new TL.UpdateNewMessage { message = msg, pts = uscm.pts, pts_count = uscm.pts_count };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TransferMessageFlags<TFlags>(ref TL.Message.Flags messageFlags, TFlags sourceFlags) where TFlags : Enum
        {
            if (sourceFlags is TL.UpdateShortMessage.Flags usmFlags)
            {
                if (usmFlags.HasFlag(TL.UpdateShortMessage.Flags.out_))
                {
                    messageFlags |= TL.Message.Flags.out_;
                }

                if (usmFlags.HasFlag(TL.UpdateShortMessage.Flags.mentioned))
                {
                    messageFlags |= TL.Message.Flags.mentioned;
                }

                if (usmFlags.HasFlag(TL.UpdateShortMessage.Flags.silent))
                {
                    messageFlags |= TL.Message.Flags.silent;
                }

                if (usmFlags.HasFlag(TL.UpdateShortMessage.Flags.media_unread))
                {
                    messageFlags |= TL.Message.Flags.media_unread;
                }
            }
            else if (sourceFlags is TL.UpdateShortChatMessage.Flags uscmFlags)
            {
                if (uscmFlags.HasFlag(TL.UpdateShortChatMessage.Flags.out_))
                {
                    messageFlags |= TL.Message.Flags.out_;
                }

                if (uscmFlags.HasFlag(TL.UpdateShortChatMessage.Flags.mentioned))
                {
                    messageFlags |= TL.Message.Flags.mentioned;
                }

                if (uscmFlags.HasFlag(TL.UpdateShortChatMessage.Flags.silent))
                {
                    messageFlags |= TL.Message.Flags.silent;
                }

                if (uscmFlags.HasFlag(TL.UpdateShortChatMessage.Flags.media_unread))
                {
                    messageFlags |= TL.Message.Flags.media_unread;
                }
            }
        }



        /// <summary>
        /// Provides configuration values to WTelegramClient.
        /// Values are fetched from settings or interactively (for verification codes/passwords).
        /// </summary>
        private string? ConfigProvider(string key)
        {
            switch (key)
            {
                case "api_id":
                    return _settings.ApiId.ToString();
                case "api_hash":
                    return _settings.ApiHash;
                case "phone_number":
                    // Fix: Explicitly check and log if phone number is missing
                    if (string.IsNullOrWhiteSpace(_settings.PhoneNumber))
                    {
                        _logger.LogCritical("❌ Configuration Error: 'phone_number' is requested by Telegram but is missing in settings. Please set 'TelegramUserApi:PhoneNumber' in appsettings.json.");
                    }
                    return _settings.PhoneNumber;
                case "verification_code":
                    return AskCode("Telegram asks for verification code: ", _settings.VerificationCodeSource);
                case "password":
                    return AskCode("Telegram asks for 2FA password (if enabled): ", _settings.TwoFactorPasswordSource);
                default:
                    return null;
            }
        }

        /// <summary>
        /// Prompts for a verification code or password, potentially via console.
        /// This method is interactive and not suitable for headless production environments without a custom sourceMethod.
        /// </summary>
        private string? AskCode(string question, string? sourceMethod)
        {
            if (string.IsNullOrWhiteSpace(question))
            {
                _logger.LogWarning("AskCode called with an empty or null question. SourceMethod: {SourceMethod}", sourceMethod ?? "Unknown");
                return null;
            }

            string effectiveSourceMethod = string.IsNullOrWhiteSpace(sourceMethod) ? "console" : sourceMethod.Trim();
            string trimmedQuestion = question.Trim();

            _logger.LogInformation("WTC Input Request: \"{QuestionDisplay}\" (Source: {SourceMethod})",
                                   trimmedQuestion.Length > 50 ? trimmedQuestion[..50] + "..." : trimmedQuestion,
                                   effectiveSourceMethod);

            if (effectiveSourceMethod.Equals("console", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // Check if the application is running in an interactive user environment.
                    if (!Environment.UserInteractive)
                    {
                        _logger.LogWarning("AskCode (console): Application is not running in an interactive user environment. Cannot prompt for \"{QuestionDisplay}\". Returning null.",
                                           trimmedQuestion.Length > 50 ? trimmedQuestion[..50] + "..." : trimmedQuestion);
                        return null;
                    }
                    // Attempt to detect if console input is redirected (e.g., from a file or pipe).
                    try
                    {
                        if (Console.IsInputRedirected)
                        {
                            _logger.LogInformation("AskCode (console): Input is redirected. Reading from redirected input for \"{QuestionDisplay}\".",
                                                   trimmedQuestion.Length > 50 ? trimmedQuestion[..50] + "..." : trimmedQuestion);
                        }
                    }
                    catch (InvalidOperationException ioExConsoleCheck)
                    {
                        // This can happen if no console is attached (e.g., a service or non-interactive process).
                        _logger.LogWarning(ioExConsoleCheck, "AskCode (console): No console available or console operation failed during pre-check for \"{QuestionDisplay}\". Returning null.",
                                           trimmedQuestion.Length > 50 ? trimmedQuestion[..50] + "..." : trimmedQuestion);
                        return null;
                    }

                    Console.Out.Write(trimmedQuestion + " ");
                    string? userInput = Console.ReadLine();

                    if (userInput != null)
                    {
                        _logger.LogInformation("WTC Input Received: User provided input of length {InputLength} for \"{QuestionDisplay}\" from console.",
                                               userInput.Length,
                                               trimmedQuestion.Length > 50 ? trimmedQuestion[..50] + "..." : trimmedQuestion);
                        return userInput;
                    }
                    else
                    {
                        _logger.LogWarning("WTC Input Received: User cancelled input (EOF) or input stream ended for \"{QuestionDisplay}\" from console. Returning null.",
                                           trimmedQuestion.Length > 50 ? trimmedQuestion[..50] + "..." : trimmedQuestion);
                        return null;
                    }
                }
                catch (IOException ioEx)
                {
                    _logger.LogError(ioEx, "WTC Input Error: An IOException occurred while trying to read from console for \"{QuestionDisplay}\". Returning null.",
                                     trimmedQuestion.Length > 50 ? trimmedQuestion[..50] + "..." : trimmedQuestion);
                    return null;
                }
                catch (OperationCanceledException ocEx)
                {
                    _logger.LogWarning(ocEx, "WTC Input Warning: Console read operation was ostensibly cancelled for \"{QuestionDisplay}\". Returning null.",
                                       trimmedQuestion.Length > 50 ? trimmedQuestion[..50] + "..." : trimmedQuestion);
                    return null;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "WTC Input Error: An unexpected error occurred during console input for \"{QuestionDisplay}\". Returning null.",
                                     trimmedQuestion.Length > 50 ? trimmedQuestion[..50] + "..." : trimmedQuestion);
                    return null;
                }
            }
            else
            {
                // Implement other source methods here (e.g., fetching from a secure vault, external API).
                _logger.LogWarning("WTC Input Request: Source method '{SourceMethod}' is not implemented for question \"{QuestionDisplay}\". Returning null.",
                                   effectiveSourceMethod,
                                   trimmedQuestion.Length > 50 ? trimmedQuestion[..50] + "..." : trimmedQuestion);
                return null;
            }
        }

        /// <summary>
        /// Periodically cleans up expired entries from the user and chat expiry caches.
        /// </summary>
        private void CacheCleanup(object? state)
        {
            _logger.LogDebug("Running scheduled cache cleanup for user and chat caches...");
            int usersRemoved = 0;
            DateTime now = DateTime.UtcNow;

            // Use ToArray() to avoid modification during enumeration.
            foreach (KeyValuePair<long, (User User, DateTime Expiry)> entry in _userCacheWithExpiry.ToArray())
            {
                if (entry.Value.Expiry < now)
                {
                    if (_userCacheWithExpiry.TryRemove(entry.Key, out _))
                    {
                        usersRemoved++;
                        _logger.LogTrace("Removed expired user {UserId} from _userCacheWithExpiry.", entry.Key);
                    }
                }
            }
            _logger.LogInformation("Cache cleanup: Removed {UsersRemovedCount} expired users from _userCacheWithExpiry. Current user cache count: {CurrentUserCacheCount}", usersRemoved, _userCacheWithExpiry.Count);

            int chatsRemoved = 0;
            foreach (KeyValuePair<long, (ChatBase Chat, DateTime Expiry)> entry in _chatCacheWithExpiry.ToArray())
            {
                if (entry.Value.Expiry < now)
                {
                    if (_chatCacheWithExpiry.TryRemove(entry.Key, out _))
                    {
                        chatsRemoved++;
                        _logger.LogTrace("Removed expired chat {ChatId} from _chatCacheWithExpiry.", entry.Key);
                    }
                }
            }
            _logger.LogInformation("Cache cleanup: Removed {ChatsRemovedCount} expired chats from _chatCacheWithExpiry. Current chat cache count: {CurrentChatCacheCount}", chatsRemoved, _chatCacheWithExpiry.Count);
        }

        #endregion

        #region WTelegramClient Update Handler
        /// <summary>
        /// Handles incoming updates from WTelegramClient, populates internal caches,
        /// and dispatches updates via the OnCustomUpdateReceived event.
        /// </summary>
        private async Task HandleUpdatesBaseAsync(TL.UpdatesBase updatesBase) // Changed to async Task, using TL.UpdatesBase
        {
            // Level 7: Introduce Stopwatch/Metrics (uncomment for profiling)
            // var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Level 7: Conditional Logging for initial debug message using GetUpdateContentForLogging
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("HandleUpdatesBaseAsync: Received UpdatesBase of type {UpdatesBaseType}. Update content (partial): {UpdatesBaseContent}",
                                 updatesBase.GetType().Name,
                                 GetUpdateContentForLogging(updatesBase, 200));
            }

            // Clear internal WTC caches and collect users/chats from the new updates.
            // --- FIX: Using the direct extension method call for CollectUsersChats ---
            updatesBase.CollectUsersChats(_internalWtcUserCache, _internalWtcChatCache);

            DateTime expiryTime = DateTime.UtcNow.Add(_cacheExpiration);

            // Level 2: Smarter Cache Updates - Use ConcurrentDictionary's AddOrUpdate for efficiency
            // Level 1: Reduce String Allocations in Logging for cache updates by making logging conditional
            // Refactoring: Moved AddOrUpdate outside the conditional logging as it's efficient.
            // Only the LogTrace call is now conditional.
            foreach (KeyValuePair<long, User> userEntry in _internalWtcUserCache)
            {
                _ = _userCacheWithExpiry.AddOrUpdate(userEntry.Key,
                                                (userEntry.Value, expiryTime),
                                                (key, existingVal) => (userEntry.Value, expiryTime)); // Always update value and expiry
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("Cached user {UserId} with expiry {ExpiryTime}.", userEntry.Key, expiryTime);
                }
            }
            foreach (KeyValuePair<long, ChatBase> chatEntry in _internalWtcChatCache)
            {
                _ = _chatCacheWithExpiry.AddOrUpdate(chatEntry.Key,
                                                (chatEntry.Value, expiryTime),
                                                (key, existingVal) => (chatEntry.Value, expiryTime)); // Always update value and expiry
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("Cached chat {ChatId} with expiry {ExpiryTime}.", chatEntry.Key, expiryTime);
                }
            }

            // Level 1: Pre-allocate `updatesToDispatch` List with a reasonable capacity (e.g., 100 for UpdatesCombined).
            List<TL.Update> updatesToDispatch = new(100); // Use TL.Update

            // Extract specific Update types from UpdatesBase containers.
            if (updatesBase is TL.Updates updatesContainer && updatesContainer.updates != null) // Use TL.Updates
            {
                _logger.LogDebug("HandleUpdatesBaseAsync: Processing 'Updates' container with {UpdateCount} inner updates.", updatesContainer.updates.Length);
                updatesToDispatch.AddRange(updatesContainer.updates);
            }
            else if (updatesBase is TL.UpdatesCombined updatesCombinedContainer && updatesCombinedContainer.updates != null) // Use TL.UpdatesCombined
            {
                _logger.LogDebug("HandleUpdatesBaseAsync: Processing 'UpdatesCombined' container with {UpdateCount} inner updates.", updatesCombinedContainer.updates.Length);
                updatesToDispatch.AddRange(updatesCombinedContainer.updates);
            }
            // Special handling for UpdateShortMessage and UpdateShortChatMessage to convert them to UpdateNewMessage.
            // Level 4: Use centralized helper methods for message construction
            else if (updatesBase is TL.UpdateShortMessage usm) // Use TL.UpdateShortMessage
            {
                _logger.LogDebug("HandleUpdatesBaseAsync: Processing 'UpdateShortMessage'. MsgID: {MessageId}, UserID: {UserId}, PTS: {Pts}", usm.id, usm.user_id, usm.pts);
                updatesToDispatch.Add(ConvertShortMessageToNewMessage(usm));
                _logger.LogDebug("HandleUpdatesBaseAsync (USM): Converted to UpdateNewMessage for MsgID {MessageId} and added to dispatch list.", usm.id);
            }
            else if (updatesBase is TL.UpdateShortChatMessage uscm) // Use TL.UpdateShortChatMessage
            {
                _logger.LogDebug("HandleUpdatesBaseAsync: Processing 'UpdateShortChatMessage'. MsgID: {MessageId}, FromID: {FromId}, ChatID: {ChatId}, PTS: {Pts}", uscm.id, uscm.from_id, uscm.chat_id, uscm.pts);
                updatesToDispatch.Add(ConvertShortChatMessageToNewMessage(uscm));
                _logger.LogDebug("HandleUpdatesBaseAsync (USCM): Converted to UpdateNewMessage for MsgID {MessageId} and added to dispatch list.", uscm.id);
            }
            else
            {
                // Log if an unknown update type is received and not handled
                //  _logger.LogWarning("HandleUpdatesBaseAsync: Unhandled UpdatesBase type: {UpdatesBaseType}.", updatesBase.GetType().Name);
            }

            // Dispatch all collected updates to subscribers.
            if (updatesToDispatch.Count > 0)
            {
                _logger.LogInformation("HandleUpdatesBaseAsync: Preparing to dispatch {DispatchCount} TL.Update object(s).", updatesToDispatch.Count);

                // Level 10: Use Channel for dispatch if enabled (Recommended for high-throughput and decoupled processing)
                if (_useChannelForDispatch && _updateChannel != null)
                {
                    foreach (Update update in updatesToDispatch)
                    {
                        // Level 7: Conditional logging for channel writes
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.LogTrace("HandleUpdatesBaseAsync: Writing update of type {UpdateType} to channel.", update.GetType().Name);
                        }
                        // This WriteAsync is non-blocking and adds the update to the channel.
                        // A separate consumer task (StartUpdateChannelPipeline) will read and process these updates.
                        await _updateChannel.Writer.WriteAsync(update); // Writer is now correctly accessed
                    }
                    _logger.LogInformation("HandleUpdatesBaseAsync: Finished writing {DispatchCount} TL.Update object(s) to channel.", updatesToDispatch.Count);
                }
                else // Fallback to direct event dispatch if channel is not used (Potentially blocking if handlers are sync/long-running)
                {
                    // CRITICAL FIX: To prevent HandleUpdatesBaseAsync from blocking the main update reception thread,
                    // we offload the synchronous event invocation to the Thread Pool using Task.Run.
                    // This makes the invocation fire-and-forget from the perspective of this method,
                    // allowing it to quickly process the next incoming Telegram update.
                    foreach (Update update in updatesToDispatch)
                    {
                        // Capture the update variable for the lambda expression to avoid closure over loop variable,
                        // ensuring each Task operates on its specific 'update' object.
                        Update currentUpdate = update;

                        // Level 7: Conditional logging for direct dispatch
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.LogTrace("HandleUpdatesBaseAsync: Directly dispatching update of type {UpdateType}. Update content (partial): {UpdateContent} via Task.Run.",
                                             currentUpdate.GetType().Name, GetUpdateContentForLogging(currentUpdate));
                        }

                        // IMPORTANT CHANGE: Wrapping the event invocation in Task.Run()
                        // This allows HandleUpdatesBaseAsync to continue processing other updates
                        // without waiting for the event subscribers to complete their work.
                        // The '_' discards the Task, indicating a "fire-and-forget" pattern.
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                // Invoke the event handler. Any long-running or blocking operation here
                                // will now run on a Thread Pool thread, not blocking HandleUpdatesBaseAsync.
                                OnCustomUpdateReceived?.Invoke(currentUpdate);
                            }
                            catch (Exception ex)
                            {
                                // Log any exceptions that occur within the event handlers,
                                // as they won't propagate back to HandleUpdatesBaseAsync directly.
                                _logger.LogError(ex, "HandleUpdatesBaseAsync: Exception during OnCustomUpdateReceived invocation in Task.Run for update type {UpdateType}. Update content (partial): {UpdateContent}", currentUpdate.GetType().Name, GetUpdateContentForLogging(currentUpdate, 100));
                            }
                        });
                    }
                    // Adjusted log message: We have initiated the dispatch of tasks to the Thread Pool,
                    // but this method is no longer 'finished' waiting for their completion.
                    _logger.LogInformation("HandleUpdatesBaseAsync: Initiated direct dispatch for {DispatchCount} TL.Update object(s) via Task.Run.", updatesToDispatch.Count);
                }
            }
            else
            {
                _logger.LogDebug("HandleUpdatesBaseAsync: No TL.Update objects to dispatch from this UpdatesBase of type {UpdatesBaseType}.", updatesBase.GetType().Name);
            }

            // stopwatch.Stop();
            // _logger.LogInformation("HandleUpdatesBaseAsync completed in {ElapsedMs}ms.", stopwatch.Elapsed.TotalMilliseconds);
        }



        /// <summary>
        /// Truncates a string for logging purposes.
        /// </summary>
        private string TruncateString(string? str, int maxLength)
        {
            return string.IsNullOrEmpty(str) ? "[null_or_empty]" : str.Length <= maxLength ? str : str[..maxLength] + "...";
        }
        #endregion

        #region ITelegramUserApiClient Implementation

        /// <summary>
        /// Connects to Telegram and logs in the user if needed.
        /// Manages the connection lifecycle and initial data fetching.
        /// This method is designed to be resilient to transient network issues and
        /// specific Telegram API errors, gracefully handling them or propagating
        /// failures to the caller for higher-level retry mechanisms.
        /// </summary>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> that signals when the operation should be cancelled.</param>
        /// <returns>A <see cref="Task"/> that represents the asynchronous operation.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the WTelegram.Client instance is unexpectedly null.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the operation is cancelled via the <paramref name="cancellationToken"/>.</exception>
        /// <exception cref="TL.RpcException">Thrown for unhandled Telegram API (RPC) errors.</exception>
        /// <exception cref="NullReferenceException">Caught and re-thrown for unexpected NREs from the underlying WTelegram.Client, logged as a warning.</exception>
        /// <exception cref="Exception">Thrown for any other critical, unclassified errors.</exception>
        // In Infrastructure/Services/TelegramUserApiClient.cs

        /// <summary>
        /// Connects to Telegram and logs in the user if needed.
        /// Manages the connection lifecycle and initial data fetching.
        /// This method is designed to be resilient to transient network issues and
        /// specific Telegram API errors, gracefully handling them or propagating
        /// failures to the caller for higher-level retry mechanisms.
        /// </summary>
        public async Task ConnectAndLoginAsync(CancellationToken cancellationToken)
        {
            _logger.LogTrace("ConnectAndLoginAsync: Attempting to acquire connection lock.");
            await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("ConnectAndLoginAsync: Acquired connection lock.");

            try
            {
                if (_client is null)
                {
                    _logger.LogCritical("ConnectAndLoginAsync: WTelegram.Client instance is unexpectedly null. Cannot proceed.");
                    throw new InvalidOperationException("Telegram API client is not initialized.");
                }

                _logger.LogInformation("ConnectAndLoginAsync: Attempting to connect and login user API.");

                TL.User? loggedInUser = null;
                try
                {
                    loggedInUser = await _resiliencePipeline.ExecuteAsync(
                        async (context, token) => await _client.LoginUserIfNeeded().ConfigureAwait(false),
                        new Polly.Context(nameof(ConnectAndLoginAsync)),
                        cancellationToken
                    ).ConfigureAwait(false);
                }
                catch (TL.RpcException e) when (e.Code == 401 && (e.Message.Contains("SESSION_PASSWORD_NEEDED") || e.Message.Contains("account_password_input_needed")))
                {
                    // These are handled by WTelegram's internal logic asking for password via Config callback, 
                    // but if we get here, it means we might need to log it.
                    _logger.LogWarning("2FA Password needed but not handled automatically by Config callback.");
                }
                catch (TL.RpcException e) when (e.Message.StartsWith("PHONE_MIGRATE_"))
                {
                    _logger.LogInformation("DC Migration required ({Message}). WTelegramClient handles this automatically, but retrying login...", e.Message);
                    // The client usually handles migration, we just need to retry the login call.
                    // Since we are in a resilience pipeline, it might have already retried or we rely on outer loop.
                }
                // --- STRONG FIX START: Handle Missing Config Gracefully ---
                catch (WTelegram.WTException wtEx) when (wtEx.Message.Contains("phone_number"))
                {
                    _logger.LogCritical("🛑 FATAL CONFIGURATION ERROR: The Telegram Client requires a Phone Number to login, but it is missing in the settings. " +
                                        "Please update 'TelegramUserApi:PhoneNumber' in appsettings.json. " +
                                        "Auto-forwarding will be DISABLED until the configuration is fixed and the application is restarted.");

                    IsConnected = false;
                    // We return immediately to stop the crash loop. 
                    // By not throwing, the background service will assume 'work finished' and won't retry instantly.
                    return;
                }
                // --- STRONG FIX END ---

                if (loggedInUser is null)
                {
                    // If we reached here without an exception but user is null, it's a logic failure
                    _logger.LogError("ConnectAndLoginAsync: User API Login Failed: LoginUserIfNeeded returned null after all attempts.");
                    throw new InvalidOperationException("WTelegramClient failed to log in user.");
                }

                _logger.LogInformation("ConnectAndLoginAsync: User API Logged in successfully: ID {UserId}, Full Name: {FullName}",
                    loggedInUser.id, $"{loggedInUser.first_name} {loggedInUser.last_name}");

                await RefreshDialogsAndCachesAsync(cancellationToken).ConfigureAwait(false);
                IsConnected = true;
            }
            catch (NullReferenceException nre)
            {
                _logger.LogDebug(nre, "(Suppressed) ConnectAndLoginAsync: A transient NullReferenceException occurred inside WTelegram.Client. The resilience policy will now attempt to retry.");
                IsConnected = false;
                throw;
            }
            catch (IOException ioEx)
            {
                _logger.LogError(ioEx, "ConnectAndLoginAsync: A low-level network I/O error occurred. Check network connectivity.");
                IsConnected = false;
                throw;
            }
            catch (OperationCanceledException oce)
            {
                _logger.LogInformation(oce, "ConnectAndLoginAsync: Operation cancelled.");
                IsConnected = false;
                throw;
            }
            catch (TL.RpcException rpcEx)
            {
                _logger.LogError(rpcEx, "ConnectAndLoginAsync: Telegram API (RPC) error: {ErrorTypeString}, Code: {ErrorCode}.", rpcEx.Message, rpcEx.Code);
                IsConnected = false;
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "ConnectAndLoginAsync: Critical, unclassified error occurred.");
                IsConnected = false;
                throw;
            }
            finally
            {
                try
                {
                    _ = _connectionLock.Release();
                    _logger.LogDebug("ConnectAndLoginAsync: Connection lock released.");
                }
                catch (ObjectDisposedException)
                {
                    // Ignore
                }
            }
        }
        /// <summary>
        /// Helper method to fetch all dialogs and populate internal user/chat caches.
        /// This method uses the resilience pipeline.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <exception cref="InvalidOperationException">Thrown if the Telegram client is not initialized or fails to fetch dialogs.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the operation is cancelled.</exception>
        /// <exception cref="TL.RpcException">Thrown for Telegram API errors during dialog fetching.</exception>
        /// <exception cref="Exception">Thrown for any other unhandled errors.</exception>
        private async Task RefreshDialogsAndCachesAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("RefreshDialogsAndCachesAsync: Fetching all dialogs to refresh user/chat caches...");

            if (_client is null)
            {
                _logger.LogError("RefreshDialogsAndCachesAsync: Telegram client (_client) is not initialized. Cannot refresh dialogs.");
                throw new InvalidOperationException("Telegram API client is not initialized.");
            }

            try
            {
                // Level 4: Resilience Pipeline for Messages_GetAllDialogs.
                TL.Messages_DialogsBase dialogs = await _resiliencePipeline.ExecuteAsync(
                    async (context, token) => await _client!.Messages_GetAllDialogs().ConfigureAwait(false),
                    new Polly.Context(nameof(_client.Messages_GetAllDialogs)),
                    cancellationToken
                ).ConfigureAwait(false);

                if (dialogs is null)
                {
                    _logger.LogError("RefreshDialogsAndCachesAsync: Messages_GetAllDialogs returned null after Polly retries. This is unexpected.");
                    throw new InvalidOperationException("Telegram API call to Messages_GetAllDialogs unexpectedly returned null.");
                }

                // Level 5: Populate expiry caches with fresh data.
                // Clear internal WTC caches before collecting new data to prevent stale entries
                _internalWtcUserCache.Clear();
                _internalWtcChatCache.Clear();

                // Collect users and chats into the internal WTC caches.
                dialogs.CollectUsersChats(_internalWtcUserCache, _internalWtcChatCache);

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("RefreshDialogsAndCachesAsync: Populating user cache from dialogs. Found {UserCacheCount} users.", _internalWtcUserCache.Count);
                }

                int usersTransferred = 0;
                foreach (KeyValuePair<long, User> userEntry in _internalWtcUserCache)
                {
                    _userCacheWithExpiry[userEntry.Key] = (userEntry.Value, DateTime.UtcNow.Add(_cacheExpiration));
                    usersTransferred++;
                }
                _logger.LogInformation("RefreshDialogsAndCachesAsync: Successfully transferred {UsersTransferredCount} users to expiry cache.", usersTransferred);

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("RefreshDialogsAndCachesAsync: Populating chat cache from dialogs. Found {ChatCacheCount} chats.", _internalWtcChatCache.Count);
                }

                int chatsTransferred = 0;
                foreach (KeyValuePair<long, ChatBase> chatEntry in _internalWtcChatCache)
                {
                    _chatCacheWithExpiry[chatEntry.Key] = (chatEntry.Value, DateTime.UtcNow.Add(_cacheExpiration));
                    chatsTransferred++;
                }
                _logger.LogInformation("RefreshDialogsAndCachesAsync: Successfully transferred {ChatsTransferredCount} chats to expiry cache.", chatsTransferred);
            }
            catch (OperationCanceledException oce)
            {
                _logger.LogInformation(oce, "RefreshDialogsAndCachesAsync: Operation cancelled during dialogs fetch.");
                throw;
            }
            catch (TL.RpcException rpcEx)
            {
                _logger.LogError(rpcEx, "RefreshDialogsAndCachesAsync: Telegram API (RPC) error fetching dialogs: {ErrorTypeString}, Code: {ErrorCode}", rpcEx.Message, rpcEx.Code);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RefreshDialogsAndCachesAsync: An unexpected error occurred while fetching dialogs and refreshing caches.");
                throw;
            }
        }





        /// <summary>
        /// Retrieves specific messages by their IDs from a given peer.
        /// Uses caching and resilience policies.
        /// </summary>
        // --------------------------------------------------------------------------------------
        // REPLACE your existing GetMessagesAsync method with this upgraded version.
        // --------------------------------------------------------------------------------------
        /// <summary>
        /// Retrieves specific messages by their IDs from a given peer.
        /// Uses caching and resilience policies.
        /// </summary>
        public async Task<TL.Messages_MessagesBase> GetMessagesAsync(TL.InputPeer peer, int[] msgIds, CancellationToken cancellationToken)
        {
            // Level 1: Early Exit for invalid state/arguments. Use 'is null' for C# 9+ clarity.
            if (_client is null)
            {
                _logger.LogError("GetMessagesAsync: Telegram client (_client) is not initialized. Cannot get messages.");
                throw new InvalidOperationException("Telegram API client is not initialized.");
            }
            if (peer is null)
            {
                _logger.LogError("GetMessagesAsync: InputPeer cannot be null.");
                throw new ArgumentNullException(nameof(peer), "InputPeer cannot be null for getting messages.");
            }
            if (msgIds is null || msgIds.Length == 0) // Use .Length == 0 instead of !.Any() for arrays
            {
                _logger.LogError("GetMessagesAsync: Message IDs list cannot be null or empty.");
                throw new ArgumentException("Message IDs list cannot be null or empty for getting messages.", nameof(msgIds));
            }

            // Level 1: Optimize string formatting for logging (only if needed)
            string messageIdsString = _logger.IsEnabled(LogLevel.Debug) // Only build string if debug logging is enabled
                ? (msgIds.Length > 5
                    ? $"{string.Join(", ", msgIds.Take(5))}... (Total: {msgIds.Length})"
                    : string.Join(", ", msgIds))
                : "[...IDs...]"; // Placeholder if not logging verbosely

            // Level 4: Use centralized helper for peer ID extraction
            long peerIdForLog = GetPeerIdForLog(peer);
            string peerTypeForLog = peer.GetType().Name;

            // Level 7: Conditional Logging
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("GetMessagesAsync: Attempting to get messages for Peer (Type: {PeerType}, LoggedID: {PeerId}), Message IDs: [{MessageIdsArray}]",
                    peerTypeForLog, peerIdForLog, messageIdsString);
            }

            try
            {
                // Level 2 & 5: Optimized Cache Key Generation and Usage
                // Using `ValueTuple` for pattern matching is clean and performant.
                string cacheKeySuffix = peer switch
                {
                    TL.InputPeerUser p_u => $"u{p_u.user_id}",
                    TL.InputPeerChat p_c => $"c{p_c.chat_id}",
                    TL.InputPeerChannel p_ch => $"ch{p_ch.channel_id}",
                    TL.InputPeerSelf => "self",
                    _ => $"other_{peer.GetType().Name}_{peer.GetHashCode()}" // More descriptive fallback
                };

                // Level 1: For cache keys, sorted IDs ensure consistent keys regardless of input order.
                IOrderedEnumerable<int> sortedMsgIds = msgIds.OrderBy(id => id); // ToList() is an allocation, keep it IOrderedEnumerable<int>
                string cacheKey = $"msgs_peer_{cacheKeySuffix}_ids_{string.Join("_", sortedMsgIds)}";

                // Level 7: Conditional Logging for cache key
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("GetMessagesAsync: Generated cache key: {CacheKey}", cacheKey);
                }

                // Level 5: Check cache first using TryGetValue.
                if (_messageCache.TryGetValue(cacheKey, out TL.Messages_MessagesBase? cachedMessages))
                {
                    // Level 1 & 7: Optimized logging for cache hit. Use MessagesBase.Count if possible.
                    int cachedMessageCount = cachedMessages?.Count ?? 0;

                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("GetMessagesAsync: Cache HIT for key {CacheKey}. Returning {CachedMessageCount} cached messages for Peer (Type: {PeerType}, LoggedID: {PeerId}).",
                            cacheKey, cachedMessageCount, peerTypeForLog, peerIdForLog);
                    }
                    return cachedMessages!; // Null-forgiving operator, as TryGetValue implies non-null if true.
                }
                else
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation("GetMessagesAsync: Cache MISS for key {CacheKey}. Fetching from API for Peer (Type: {PeerType}, LoggedID: {PeerId}), Message IDs: [{MessageIdsArray}]",
                            cacheKey, peerTypeForLog, peerIdForLog, messageIdsString);
                    }
                }

                // Level 6: Optimize `InputMessage` creation. Avoid LINQ `.Select().ToArray()` if `msgIds` is large
                // by pre-allocating the array.
                InputMessage[] inputMessageIDs = new TL.InputMessage[msgIds.Length];
                for (int i = 0; i < msgIds.Length; i++)
                {
                    inputMessageIDs[i] = new TL.InputMessageID { id = msgIds[i] };
                }

                // Level 8: Improved Resilience Pipeline Execution (Context and CancellationToken)
                TL.Messages_MessagesBase messages = await _resiliencePipeline.ExecuteAsync(
                   async (context, token) => await _client!.Messages_GetMessages(inputMessageIDs).ConfigureAwait(false),
                   new Polly.Context(nameof(GetMessagesAsync)),
                   cancellationToken
               ).ConfigureAwait(false);

                if (messages is null) // Use 'is null'
                {
                    _logger.LogError("GetMessagesAsync: _client.Messages_GetMessages returned null after Polly retries. This is unexpected. Peer: {PeerId}, Message IDs: {MessageIdsString}", peerIdForLog, messageIdsString);
                    throw new InvalidOperationException("Telegram API call to Messages_GetMessages unexpectedly returned null after all retries.");
                }

                // Level 1 & 7: Count messages from the API response for logging. Use MessagesBase.Count
                int fetchedMessageCount = messages.Count; // Use the abstract Count property

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("GetMessagesAsync: Successfully fetched {MessageCountFromApi} items (messages/users/chats container) from API for Peer (Type: {PeerType}, LoggedID: {PeerId}). Caching result with key {CacheKey}. Actual messages in response: {ActualMessageCount}",
                        fetchedMessageCount, peerTypeForLog, peerIdForLog, cacheKey, fetchedMessageCount);
                }

                // Level 5: Cache the fetched messages with defined options.
                _ = _messageCache.Set(cacheKey, messages, _cacheOptions);

                return messages;
            }
            // Level 9: Consistent Error Handling and Logging
            catch (OperationCanceledException oce) // Handle cancellation explicitly
            {
                _logger.LogInformation(oce, "GetMessagesAsync: Operation cancelled for Peer (Type: {PeerType}, LoggedID: {PeerId}), Message IDs: [{MessageIdsArray}].",
                    peerTypeForLog, peerIdForLog, messageIdsString);
                throw; // Re-throw to propagate cancellation.
            }
            catch (TL.RpcException rpcEx)
            {
                _logger.LogError(rpcEx, "GetMessagesAsync: Telegram API (RPC) exception occurred for Peer (Type: {PeerType}, LoggedID: {PeerId}), Message IDs: [{MessageIdsArray}]. Error: {ErrorTypeString}, Code: {ErrorCode}",
                    peerTypeForLog, peerIdForLog, messageIdsString, rpcEx.Message, rpcEx.Code);
                throw; // Re-throw to propagate the exception.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetMessagesAsync: Unhandled generic exception occurred for Peer (Type: {PeerType}, LoggedID: {PeerId}), Message IDs: [{MessageIdsArray}]",
                    peerTypeForLog, peerIdForLog, messageIdsString);
                throw;
            }
        }
        private class AdviceSlipResponse
        {
            public Slip? slip { get; set; }
        }
        private class Slip
        {
            public int id { get; set; }
            public string? advice { get; set; }
        }

        // We assume the class has an HttpClient instance. For best practice in production apps,
        // this should be managed via IHttpClientFactory and dependency injection.
        // If you don't have one, you can add a static instance like this:
        private static readonly HttpClient _httpClient = new();

        public async Task<string> GetChannelAdviceAsync(CancellationToken cancellationToken)
        {
            // --- 1. PRIMARY STRATEGY: Use our robust internal directory first ---
            try
            {
                _logger.LogDebug("Getting next unique advice for channel from internal service.");
                string advice = _adviceService.GetNextUniqueAdviceForChannel();

                // If we get a valid string, we are done. Return it immediately.
                if (!string.IsNullOrWhiteSpace(advice))
                {
                    return advice;
                }
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "CRITICAL: The internal AdviceService failed unexpectedly. Falling back to external API.");
            }

            // --- 2. FALLBACK STRATEGY: If the internal service fails, try the external API ---
            _logger.LogInformation("Internal advice service did not return a value. Attempting API fallback.");

            const string apiUrl = "https://api.adviceslip.com/advice";
            try
            {
                HttpResponseMessage response = await _httpClient.GetAsync(apiUrl, cancellationToken);
                _ = response.EnsureSuccessStatusCode();

                AdviceSlipResponse? adviceData = await response.Content.ReadFromJsonAsync<AdviceSlipResponse>(cancellationToken: cancellationToken);
                string? apiAdvice = adviceData?.slip?.advice?.Trim();

                if (!string.IsNullOrWhiteSpace(apiAdvice))
                {
                    _logger.LogInformation("Successfully retrieved advice from fallback API.");
                    return apiAdvice;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The fallback advice API failed. Error: {ErrorMessage}", ex.Message);
            }

            // --- 3. FINAL FALLBACK: If all sources fail ---
            _logger.LogWarning("All advice sources failed. Returning a final, hardcoded failsafe advice.");
            return "Always manage your risk before seeking reward.";
        }


        public class SlipData
        {
            public int id { get; set; }
            public string? advice { get; set; }
        }
        /// <summary>
        /// Enhances a fully constructed message with an AI service.
        /// If enhancement succeeds, it returns the new message and clears existing entities.
        /// If it fails for any reason, it returns the original message and entities as a fallback.
        /// </summary>
        /// <param name="originalMessage">The final, fully constructed message text to be enhanced.</param>
        /// <param name="originalEntities">The original message entities.</param>
        /// <param name="peerIdForLog">The peer ID for logging purposes.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A tuple containing the final message to send and its corresponding entities.</returns>
        private async Task<(string message, MessageEntity[]? entities)> EnhanceMessageWithAiAsync(
            string originalMessage,
            MessageEntity[]? originalEntities,
            long peerIdForLog,
            CancellationToken cancellationToken)
        {
            // Only attempt enhancement if there's text content.
            if (string.IsNullOrWhiteSpace(originalMessage))
            {
                return (originalMessage, originalEntities);
            }

            try
            {
                _logger.LogDebug("Attempting to enhance final message with AI for Peer {PeerId}.", peerIdForLog);
                string? enhancedMessage = await _geminiService.EnhanceMessageAsync(originalMessage, cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(enhancedMessage))
                {
                    _logger.LogInformation("Message successfully enhanced by AI for Peer {PeerId}. Parsing as MarkdownV1...", peerIdForLog);
                    // Always parse Gemini output as MarkdownV1
                    (string? parsedText, MessageEntity[]? parsedEntities) = _markdownParserService.ParseMarkdownToTelegramEntities(enhancedMessage);
                    return (parsedText, parsedEntities.Length > 0 ? parsedEntities : null);
                }
                else
                {
                    // FAILURE or FALLBACK: AI service is disabled, failed, out of quota, or returned empty content.
                    _logger.LogDebug("AI enhancement returned no content or is disabled. Proceeding with original constructed message.");
                    // Return the original constructed message and its original entities.
                    return (originalMessage, originalEntities);
                }
            }
            catch (Exception ex)
            {
                // CATCH-ALL: This ensures any unexpected error in the AI service
                // does not stop the message from being sent.
                _logger.LogWarning(ex, "An exception occurred during AI message enhancement. Proceeding with original constructed message.");
                // Return the original constructed message and its original entities.
                return (originalMessage, originalEntities);
            }
        }


        /// <summary>
        /// Sends a message (text or media) to a specified peer.
        /// Handles various message parameters and uses caching and resilience policies.
        /// It also automatically appends a random piece of advice as a footer.
        /// </summary>
        /// <param name="peer">The target peer (user, chat, or channel).</param>
        /// <param name="message">The text message content. Can be null if media is provided (for caption).</param>
        /// <param name="cancellationToken">Cancellation token for the operation.</param>
        /// <param name="replyToMsgId">Optional: The ID of the message to reply to.</param>
        /// <param name="replyMarkup">Optional: Inline keyboard or reply keyboard markup.</param>
        /// <param name="entities">Optional: Message entities for text formatting (bold, italic, links, etc.).</param>
        /// <param name="noWebpage">Optional: Suppress webpage preview for URLs (only applicable for text messages).</param>
        /// <param name="background">Optional: Send message in background (silent notification).</param>
        /// <param name="clearDraft">Optional: Clear draft message in chat.</param>
        /// <param name="schedule_date">Optional: Schedule message for a future date/time.</param>
        /// <param name="media">Optional: Media to send (photo, document, video, etc.). If provided, `message` acts as caption.</param>
        /// <returns>An <see cref="UpdatesBase"/> object containing the sent message update.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the Telegram client is not initialized or API call returns null.</exception>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="peer"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown if both <paramref name="message"/> and <paramref name="media"/> are null.</exception>
        /// <exception cref="RpcException">Thrown if a Telegram API error occurs.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the operation is cancelled.</exception>
        /// <exception cref="Exception">Thrown for any other unhandled errors.</exception>
        public async Task<UpdatesBase> SendMessageAsync(
       InputPeer peer,
       string? message,
       CancellationToken cancellationToken,
       int? replyToMsgId = null,
       ReplyMarkup? replyMarkup = null,
       IEnumerable<MessageEntity>? entities = null,
       bool noWebpage = false,
       bool background = false,
       bool clearDraft = false,
       DateTime? schedule_date = null,
       InputMedia? media = null)
        {
            // =========================================================================
            // === STAGE 0: IDEMPOTENCY CHECK (NEW)
            // =========================================================================
            string messageCacheKey = GenerateMessageCacheKey(peer, message, media, entities, replyToMsgId, replyMarkup, noWebpage, background, clearDraft, schedule_date);
            if (_memoryCache.TryGetValue(messageCacheKey, out _))
            {
                _logger.LogInformation("CACHE HIT: Message with key {MessageCacheKey} has already been sent recently. Skipping.", messageCacheKey);
                await _notifToAdmin.SendNotificationAsync($"✅ **Duplicate Message Skipped**\nPeer: `{GetPeerIdForLog(peer)}`\nCache Key: `{messageCacheKey}`", CancellationToken.None);
                return default!; // Or throw, or return a special UpdatesBase, as per your design
            }
            // =========================================================================
            // === STAGE 1: VALIDATION & PREPARATION
            // =========================================================================
            if (_client is null)
            {
                throw new InvalidOperationException("Telegram API client is not initialized.");
            }

            if (peer is null)
            {
                throw new ArgumentNullException(nameof(peer), "InputPeer cannot be null.");
            }

            if (string.IsNullOrEmpty(message) && media is null)
            {
                throw new ArgumentException("Either message or media must be provided.");
            }

            long peerIdForLog = GetPeerIdForLog(peer);
            _logger.LogDebug("Initiating SendMessageAsync for Peer {PeerId}. Media: {HasMedia}", peerIdForLog, media != null);

            // Convert entities to an array for consistent handling throughout the process.
            MessageEntity[]? entitiesArray = entities?.ToArray();

            // --- Begin Text Pre-processing ---

            // Custom link replacement
            if (!string.IsNullOrEmpty(message) && message.Contains("https://wa.me/message/W6HXT7VWR3U2C1", StringComparison.OrdinalIgnoreCase))
            {
                message = message.Replace("https://wa.me/message/W6HXT7VWR3U2C1", "@capxi", StringComparison.OrdinalIgnoreCase);
                _logger.LogDebug("Replaced WhatsApp link with @capxi for Peer {PeerId}.", peerIdForLog);
            }

            // Markdown parsing (only if no entities are already provided)
            if (!string.IsNullOrWhiteSpace(message) && (entitiesArray == null || !entitiesArray.Any()))
            {
                try
                {
                    (string? parsedText, MessageEntity[]? parsedEntities) = _markdownParserService.ParseMarkdownToTelegramEntities(message);
                    if (parsedEntities.Length > 0)
                    {
                        _logger.LogInformation("Successfully parsed markdown for Peer {PeerId}, found {Count} entities.", peerIdForLog, parsedEntities.Length);
                        message = parsedText;
                        entitiesArray = parsedEntities;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Markdown parsing failed for Peer {PeerId}. Proceeding with original text.", peerIdForLog);
                }
            }

            // AI Enhancement
            if (!string.IsNullOrWhiteSpace(message))
            {
                try
                {
                    // Use Hangfire background job for AI enhancement
                    string? jobId = await _geminiService.EnhanceMessageAsync(message, cancellationToken);

                    if (!string.IsNullOrWhiteSpace(jobId) && !jobId.StartsWith("Job enqueued"))
                    {
                        // If we got a direct result (not a job ID), use it immediately
                        _logger.LogInformation("Message/caption successfully enhanced by AI for Peer {PeerId}.", peerIdForLog);
                        (string? parsedText, MessageEntity[]? parsedEntities) = _markdownParserService.ParseMarkdownToTelegramEntities(jobId);
                        message = parsedText;
                        entitiesArray = parsedEntities.Length > 0 ? parsedEntities : null;
                    }
                    else if (!string.IsNullOrWhiteSpace(jobId))
                    {
                        // Extract job ID from the response
                        string actualJobId = jobId.Replace("Job enqueued successfully. JobId: ", "");
                        _logger.LogInformation("AI enhancement job enqueued for Peer {PeerId}. JobId: {JobId}", peerIdForLog, actualJobId);

                        // For now, proceed with original message and let the enhancement happen in background
                        // In a more sophisticated implementation, you might want to wait for the result
                        // or implement a callback mechanism
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AI message enhancement failed for Peer {PeerId}. Proceeding with original text.", peerIdForLog);
                }
            }

            // --- End Text Pre-processing ---

            // =========================================================================
            // === STAGE 2: DISPATCH TO SENDING WORKER
            // =========================================================================

            int charLimit = media is null ? TelegramMessageHelper.TextMessageMaxLength : TelegramMessageHelper.CaptionMaxLength;
            List<string> messageParts;

            if (string.IsNullOrEmpty(message) || message.Length <= charLimit)
            {
                messageParts = [message ?? string.Empty];
            }
            else
            {
                _logger.LogInformation("Message length ({Length}) exceeds limit ({Limit}). Splitting into multiple parts.", message.Length, charLimit);

                if (entitiesArray?.Any() == true)
                {
                    _logger.LogWarning("Message with entities exceeds length limit. Entities will be stripped to allow splitting.");
                    entitiesArray = null;
                }
                messageParts = TelegramMessageHelper.ChunkMessage(message, charLimit);
                _logger.LogInformation("Message split into {PartCount} parts.", messageParts.Count);
            }

            UpdatesBase result = await SendInPartsAsync(peer, messageParts, media, replyToMsgId, replyMarkup, entitiesArray,
                noWebpage, background, clearDraft, schedule_date, cancellationToken);

            // =========================================================================
            // === STAGE 3: MARK AS SENT ON SUCCESS (NEW)
            // =========================================================================
            _ = _memoryCache.Set(messageCacheKey, true, TimeSpan.FromHours(1));
            _logger.LogInformation("Successfully dispatched message to Peer {PeerId}. Idempotency key set.", peerIdForLog);
            // =========================================================================
            return result;
        }

        /// <summary>
        /// Internal method that handles the logic of sending one or more message parts.
        /// If multiple parts are provided, it sends them as a threaded reply chain.
        /// </summary>
        private async Task<UpdatesBase> SendInPartsAsync(
            InputPeer peer,
            List<string> messageParts,
            InputMedia? media,
            int? initialReplyToMsgId,
            ReplyMarkup? replyMarkup,
            MessageEntity[]? entities,
            bool noWebpage,
            bool background,
            bool clearDraft,
            DateTime? schedule_date,
            CancellationToken cancellationToken)
        {
            long peerIdForLog = GetPeerIdForLog(peer);
            string lockKey = $"send_peer_{peer.GetType().Name}_{peerIdForLog}";

            using IDisposable sendLock = await AsyncLock.LockAsync(lockKey).ConfigureAwait(false);
            _logger.LogDebug("Acquired send lock for peer {PeerId} to send message in {PartCount} part(s).", peerIdForLog, messageParts.Count);

            UpdatesBase? firstMessageUpdate = null;
            int? replyToMsgId = initialReplyToMsgId;

            for (int i = 0; i < messageParts.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string currentPart = messageParts[i];
                bool isFirstPart = i == 0;
                bool isLastPart = i == messageParts.Count - 1;

                long random_id = WTelegram.Helpers.RandomLong();
                InputReplyTo? inputReplyTo = replyToMsgId.HasValue ? new InputReplyToMessage { reply_to_msg_id = replyToMsgId.Value } : null;

                try
                {
                    UpdatesBase currentUpdate;

                    // The first part is special: it might contain media and uses the initial entities/reply markup.
                    if (isFirstPart)
                    {
                        _logger.LogDebug("Sending part 1/{TotalParts} to peer {PeerId}. Media: {HasMedia}", messageParts.Count, peerIdForLog, media != null);
                        currentUpdate = media is null
                            ? await _resiliencePipeline.ExecuteAsync(
                                async token => await _client!.Messages_SendMessage(peer, currentPart, random_id, reply_to: inputReplyTo, reply_markup: isLastPart ? replyMarkup : null, entities: entities, no_webpage: noWebpage, background: background, clear_draft: clearDraft, schedule_date: schedule_date).ConfigureAwait(false),
                                cancellationToken
                            ).ConfigureAwait(false)
                            : await _resiliencePipeline.ExecuteAsync(
                                async token => await _client!.Messages_SendMedia(peer, media, currentPart, random_id, reply_to: inputReplyTo, reply_markup: isLastPart ? replyMarkup : null, entities: entities, background: background, clear_draft: clearDraft, schedule_date: schedule_date).ConfigureAwait(false),
                                cancellationToken
                            ).ConfigureAwait(false);
                    }
                    // Subsequent parts are always text-only replies.
                    else
                    {
                        _logger.LogDebug("Sending part {PartNum}/{TotalParts} as a reply to peer {PeerId}.", i + 1, messageParts.Count, peerIdForLog);
                        currentUpdate = await _resiliencePipeline.ExecuteAsync(
                            async token => await _client!.Messages_SendMessage(peer, currentPart, random_id, reply_to: inputReplyTo, reply_markup: isLastPart ? replyMarkup : null, background: background).ConfigureAwait(false), // No entities, no webpage for subsequent parts
                            cancellationToken
                        ).ConfigureAwait(false);
                    }

                    // Store the first update to return it at the end.
                    firstMessageUpdate ??= currentUpdate;

                    // Set the reply_to_msg_id for the *next* iteration.
                    replyToMsgId = TelegramMessageHelper.GetSentMessageId(currentUpdate);

                    // Small delay between parts to prevent flooding and ensure messages appear in order.
                    if (!isLastPart)
                    {
                        await Task.Delay(250, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send part {PartNum}/{TotalParts} of a multi-part message to peer {PeerId}.", i + 1, messageParts.Count, peerIdForLog);
                    // If any part fails, we stop and re-throw the exception.
                    throw;
                }
            }

            if (firstMessageUpdate is null)
            {
                _logger.LogError("Multi-part message sending completed, but no update was captured. This is unexpected.");
                throw new InvalidOperationException("Failed to send message and capture a response from Telegram.");
            }

            _logger.LogInformation("Successfully sent message in {PartCount} part(s) to peer {PeerId}.", messageParts.Count, peerIdForLog);
            return firstMessageUpdate;
        }


        /// <summary>
        /// Generates a unique, deterministic, and collision-resistant cache key for a media group.
        /// This is the core of the idempotency logic to prevent duplicate album processing and sending.
        /// Enhanced with content-based hashing, comprehensive media type support, and performance optimizations.
        /// </summary>
        /// <param name="media">The collection of media items in the album.</param>
        /// <param name="initialCaption">The initial caption, if any.</param>
        /// <returns>A unique string key suitable for caching.</returns>
        private static string GenerateAlbumCacheKey(ICollection<TL.InputMedia> media, string? initialCaption)
        {
            // Early exit for empty media collections
            if (media == null || media.Count == 0)
            {
                return "AlbumSent-EMPTY_MEDIA";
            }

            // Use a more efficient StringBuilder for string concatenation
            StringBuilder keyBuilder = new(256); // Pre-allocate reasonable capacity

            // Process media items with comprehensive type support
            List<string> mediaIdentifiers = new(media.Count); // Pre-allocate list capacity

            foreach (InputMedia m in media)
            {
                if (m == null)
                {
                    continue;
                }

                string identifier = m switch
                {
                    // Uploaded Media - Use file name for uniqueness (WTelegram doesn't expose file size)
                    TL.InputMediaUploadedPhoto up => $"UPLOAD_PHOTO:{up.file.Name}",
                    TL.InputMediaUploadedDocument ud => $"UPLOAD_DOC:{ud.file.Name}_{ud.mime_type}",

                    // Existing Telegram Media - Use ID, access hash, and file reference
                    TL.InputMediaPhoto p when p.id is TL.InputPhoto ip =>
                        $"EXISTING_PHOTO:{ip.id}_{ip.access_hash}_{Convert.ToBase64String(ip.file_reference)}",
                    TL.InputMediaDocument d when d.id is TL.InputDocument idoc =>
                        $"EXISTING_DOC:{idoc.id}_{idoc.access_hash}_{Convert.ToBase64String(idoc.file_reference)}",

                    // Contact Media
                    TL.InputMediaContact contact => $"CONTACT:{contact.phone_number}_{contact.first_name}_{contact.last_name}",

                    // Game Media
                    TL.InputMediaGame game => $"GAME:{game.id}",

                    // Poll Media
                    TL.InputMediaPoll poll => $"POLL:{poll.poll.question}_{poll.poll.answers.Length}",


                    // WebPage Media
                    TL.InputMediaWebPage webPage => $"WEBPAGE:{webPage.url}",


                    // Unknown or unsupported types
                    _ => $"UNKNOWN_MEDIA:{m.GetType().Name}_{m.GetHashCode()}"
                };

                mediaIdentifiers.Add(identifier);
            }

            // Sort identifiers for deterministic ordering (regardless of input order)
            mediaIdentifiers.Sort(StringComparer.Ordinal);

            // Build the combined string efficiently
            _ = keyBuilder.AppendJoin("|", mediaIdentifiers);

            // Add caption information with normalization
            string normalizedCaption = NormalizeCaption(initialCaption);
            _ = keyBuilder.Append("|CAPTION:");
            _ = keyBuilder.Append(normalizedCaption);

            // Add media count for additional uniqueness
            _ = keyBuilder.Append("|COUNT:");
            _ = keyBuilder.Append(media.Count);

            // Generate a more robust hash using multiple algorithms for collision resistance
            string combinedString = keyBuilder.ToString();
            string finalHash = GenerateRobustHash(combinedString);

            return $"AlbumSent-{finalHash}";
        }

        /// <summary>
        /// Normalizes caption text for consistent hashing by removing extra whitespace and normalizing line endings.
        /// </summary>
        /// <param name="caption">The caption to normalize.</param>
        /// <returns>Normalized caption string.</returns>
        private static string NormalizeCaption(string? caption)
        {
            if (string.IsNullOrWhiteSpace(caption))
            {
                return "NULL";
            }

            // Normalize whitespace and line endings
            return caption
                .Replace("\r\n", "\n")  // Normalize line endings
                .Replace("\r", "\n")    // Handle old Mac line endings
                .Trim()                 // Remove leading/trailing whitespace
                .Replace("\n", "\\n");  // Escape newlines for consistent representation
        }

        /// <summary>
        /// Generates a robust hash using multiple algorithms to minimize collision probability.
        /// </summary>
        /// <param name="input">The input string to hash.</param>
        /// <returns>A base64-encoded hash string.</returns>
        private static string GenerateRobustHash(string input)
        {
            // Use SHA256 as primary hash
            using SHA256 sha256 = System.Security.Cryptography.SHA256.Create();
            byte[] hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));

            // For additional collision resistance, we can combine with a simpler hash
            // This is especially useful for large media collections
            byte[] combinedHash = new byte[hashBytes.Length + 4];
            Array.Copy(hashBytes, 0, combinedHash, 0, hashBytes.Length);

            // Add a simple checksum as additional entropy
            uint checksum = CalculateSimpleChecksum(input);
            byte[] checksumBytes = BitConverter.GetBytes(checksum);
            Array.Copy(checksumBytes, 0, combinedHash, hashBytes.Length, 4);

            // Return a shorter but still unique hash
            return Convert.ToBase64String(combinedHash)
                .Replace("+", "-")  // URL-safe characters
                .Replace("/", "_")
                .Replace("=", "");  // Remove padding for shorter keys
        }

        /// <summary>
        /// Calculates a simple checksum for additional hash entropy.
        /// </summary>
        /// <param name="input">The input string.</param>
        /// <returns>A 32-bit checksum.</returns>
        private static uint CalculateSimpleChecksum(string input)
        {
            uint checksum = 0;
            foreach (char c in input)
            {
                checksum = (checksum << 5) + checksum + c; // Simple rolling hash
            }
            return checksum;
        }

        /// <summary>
        /// Sends a group of media items as an album. This operation is IDEMPOTENT, meaning it will
        /// not re-process or re-send the same album if called multiple times in a short period.
        /// It uses a full pipeline of AI enhancement, formatting, and resilient dispatch.
        /// </summary>
        public async Task SendMediaGroupAsync(
          TL.InputPeer peer,
          ICollection<TL.InputMedia> media,
          CancellationToken cancellationToken,
          string? albumCaption = null,
          TL.MessageEntity[]? albumEntities = null,
          int? replyToMsgId = null,
          bool background = false,
          DateTime? schedule_date = null,
          bool sendAsBot = false)
        {
            // === STAGE 0: VALIDATION & CONTEXT SETUP =================================
            if (_client is null || peer is null || media is null || !media.Any())
            {
                _logger.LogError("SendMediaGroupAsync: Critical validation failed at entry. Aborting.");
                await _notifToAdmin.SendNotificationAsync("❌ SendMediaGroupAsync validation failed. Peer or media was null.", CancellationToken.None);
                throw new InvalidOperationException("Cannot send media group with invalid arguments.");
            }

            long peerIdForLog = GetPeerIdForLog(peer);
            StringBuilder debugReport = new($"🕵️‍♂️ **Album Send Trace: Peer `{peerIdForLog}`**\n");

            // === NEW: CAPTION EXTRACTION FROM MEDIA =================================
            // If no album caption is provided, we cannot extract from TL.InputMedia objects
            // as they don't contain caption information. The caption should be provided
            // through the albumCaption parameter from the forwarding context.
            string? extractedCaption = albumCaption;
            TL.MessageEntity[]? extractedEntities = albumEntities;

            if (string.IsNullOrWhiteSpace(extractedCaption))
            {
                _ = debugReport.AppendLine("**Caption Status:** ⚠️ No album caption provided.");
                _ = debugReport.AppendLine("**Note:** Captions should be passed via albumCaption parameter from forwarding context.");
            }
            else
            {
                _ = debugReport.AppendLine($"**Caption Status:** ✅ Using provided album caption: `{TruncateString(extractedCaption, 50)}`");
            }
            // =========================================================================

            // === NEW: IDEMPOTENCY CHECK - THE CORE FIX FOR DUPLICATES =================
            string albumCacheKey = GenerateAlbumCacheKey(media, extractedCaption);
            if (_memoryCache.TryGetValue(albumCacheKey, out _))
            {
                _logger.LogInformation("CACHE HIT: Album with key {AlbumCacheKey} has already been sent recently. Skipping.", albumCacheKey);
                // We can optionally notify the admin of the duplicate detection.
                await _notifToAdmin.SendNotificationAsync($"✅ **Duplicate Album Skipped**\nPeer: `{peerIdForLog}`\nCache Key: `{albumCacheKey}`", CancellationToken.None);
                return; // EXIT EARLY - This prevents all duplicate work and sends.
            }
            _ = debugReport.AppendLine($"**Cache Key:** `{albumCacheKey}` (MISS)");
            // =========================================================================

            try
            {
                // === STAGE 1: AI ENHANCEMENT ============
                _ = debugReport.AppendLine("\n---");
                _ = debugReport.AppendLine("**1. AI Caption Enhancement:**");

                // Call the GeminiService for text-only enhancement using the extracted caption
                string? enhancedCaption = null;
                (string? currentCaption, MessageEntity[]? currentEntities) = (extractedCaption, extractedEntities); // Default to extracted

                try
                {
                    enhancedCaption = await _geminiService.EnhanceMessageAsync(extractedCaption, cancellationToken);

                    if (!string.IsNullOrWhiteSpace(enhancedCaption) && !enhancedCaption.StartsWith("Job enqueued"))
                    {
                        // If we got a direct result (not a job ID), use it immediately
                        _ = debugReport.AppendLine($"   - ✅ AI service returned enhanced caption.");
                        (currentCaption, currentEntities) = FormatFinalMessage(enhancedCaption, null, debugReport); // Format the AI response
                    }
                    else if (!string.IsNullOrWhiteSpace(enhancedCaption))
                    {
                        // Extract job ID from the response
                        string actualJobId = enhancedCaption.Replace("Job enqueued successfully. JobId: ", "");
                        _ = debugReport.AppendLine($"   - 🔄 AI enhancement job enqueued. JobId: {actualJobId}");
                        // For now, proceed with original caption and let the enhancement happen in background
                        (currentCaption, currentEntities) = FormatFinalMessage(extractedCaption, extractedEntities, debugReport); // Format the original
                    }
                    else
                    {
                        // AI service returned no enhancement
                        _ = string.IsNullOrWhiteSpace(extractedCaption)
                            ? debugReport.AppendLine($"   - 🚫 AI service returned no enhancement and original caption was empty (possibly due to DropMediaCaptions rule).")
                            : debugReport.AppendLine($"   - 🚫 AI service returned no enhancement. Using original caption.");
                        (currentCaption, currentEntities) = FormatFinalMessage(extractedCaption, extractedEntities, debugReport); // Format the original
                    }
                }
                catch (Exception aiEx)
                {
                    // CRITICAL: If AI enhancement fails, fall back to original caption
                    _ = debugReport.AppendLine($"   - ❌ AI enhancement failed: {aiEx.Message}");
                    _ = debugReport.AppendLine($"   - 🔄 Falling back to original caption without enhancement.");
                    _logger.LogWarning(aiEx, "AI enhancement failed for Peer {PeerId}, falling back to original caption", peerIdForLog);

                    // Use the original extracted caption and entities
                    (currentCaption, currentEntities) = (extractedCaption, extractedEntities);
                }

                // === STAGE 2: DISPATCH ===================================================
                _ = debugReport.AppendLine("\n---");
                _ = debugReport.AppendLine($"📤 **Final State to Send:**");
                _ = debugReport.AppendLine($"**Caption:** `{(string.IsNullOrWhiteSpace(currentCaption) ? "(empty)" : TruncateString(currentCaption, 100))}`");
                _ = debugReport.AppendLine($"**Entities:** `{currentEntities?.Length ?? 0}`");

                string lockKey = $"send_media_group_peer_{peer.GetType().Name}_{peerIdForLog}";
                using (await AsyncLock.LockAsync(lockKey).ConfigureAwait(false))
                {
                    _ = await _resiliencePipeline.ExecuteAsync(async (ctx, ct) =>
                        await _client!.SendAlbumAsync(
                            peer: peer,
                            medias: media,
                            caption: currentCaption,
                            entities: currentEntities,
                            reply_to_msg_id: replyToMsgId ?? 0,
                            schedule_date: schedule_date ?? default
                        ).ConfigureAwait(false),
                        new Polly.Context(nameof(SendMediaGroupAsync)),
                        cancellationToken
                    ).ConfigureAwait(false);
                }

                // === NEW: MARK AS SENT ON SUCCESS =======================================
                // If the code reaches here, the send was successful.
                _ = _memoryCache.Set(albumCacheKey, true, TimeSpan.FromHours(1));
                _ = debugReport.AppendLine($"\n✅ **Dispatch Success & Cached:** Media group sent. Key `{albumCacheKey}` is now cached.");
                _logger.LogInformation("Successfully dispatched media group to Peer {PeerId}. Lock released and idempotency key set.", peerIdForLog);
                // =========================================================================
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A critical error occurred during the SendMediaGroupAsync operation for Peer {PeerId}.", peerIdForLog);
                _ = debugReport.AppendLine($"\n❌ **FATAL ERROR:** {ex.Message}");
                throw; // Re-throw so the caller knows it failed.
            }
            finally
            {
                await _notifToAdmin.SendNotificationAsync(debugReport.ToString(), CancellationToken.None);
            }
        }

        #region Private Pipeline Helper Methods

        private (string Caption, TL.MessageEntity[]? Entities) RecoverCaptionFromMedia(
          string? initialCaption, TL.MessageEntity[]? initialEntities,
          ICollection<TL.InputMedia> media, StringBuilder debugReport)
        {
            _ = debugReport.AppendLine($"**Initial Param Caption:** `{(string.IsNullOrWhiteSpace(initialCaption) ? "(empty)" : "Set")}`");
            if (!string.IsNullOrWhiteSpace(initialCaption))
            {
                _ = debugReport.AppendLine("**1. Caption Recovery:** ✅ Using parameter caption.");
                return (initialCaption, initialEntities);
            }

            // This is no longer a critical error. It's a valid path for AI generation.
            _ = debugReport.AppendLine("**1. Caption Recovery:** ⚠️ No parameter caption provided. Proceeding to AI stage for generation from media content.");
            return (string.Empty, null);
        }



        /// <summary>
        /// Encapsulates the logic for AI enhancement using text-only input.
        /// </summary>
        /// <returns>A tuple of the new caption and entities if successful; otherwise, null.</returns>
        private async Task<(string, TL.MessageEntity[])?> EnhanceCaptionWithAiAsync(
            string currentCaption, ICollection<TL.InputMedia> media,
            StringBuilder debugReport, CancellationToken cancellationToken)
        {
            // The AI stage can now run even with an empty initial caption.
            _ = debugReport.AppendLine($"**3. AI Enhancement:** Initiating with caption length: `{currentCaption?.Length ?? 0}`.");

            try
            {
                // If we have no initial text, there's nothing for the AI to do.
                if (string.IsNullOrWhiteSpace(currentCaption))
                {
                    _ = debugReport.AppendLine("   - AI Skipped: 🚫 No text available for enhancement.");
                    return null;
                }

                _ = debugReport.AppendLine($"   - AI Input Type: `Text-only enhancement`");
                string? enhancedCaption = await _geminiService.EnhanceMessageAsync(currentCaption, cancellationToken);

                if (!string.IsNullOrWhiteSpace(enhancedCaption) && !enhancedCaption.StartsWith("Job enqueued"))
                {
                    // If we got a direct result (not a job ID), use it immediately
                    _ = debugReport.AppendLine($"   - Gemini Response: Received caption of length `{enhancedCaption.Length}`.");
                    return (enhancedCaption, null); // Return raw enhanced caption; formatting happens later.
                }
                else if (!string.IsNullOrWhiteSpace(enhancedCaption))
                {
                    // Extract job ID from the response
                    string actualJobId = enhancedCaption.Replace("Job enqueued successfully. JobId: ", "");
                    _ = debugReport.AppendLine($"   - 🔄 Gemini job enqueued. JobId: {actualJobId}");
                    // For now, return null to indicate no enhancement available immediately
                    return null;
                }
                else
                {
                    _ = debugReport.AppendLine("   - Gemini Response: Received empty or null caption.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                // This will correctly catch an OperationCanceledException if the token is triggered during the await.
                _logger.LogError(ex, "An exception occurred within the AI enhancement stage.");
                _ = debugReport.AppendLine($"   - 💥 AI Stage Exception: `{ex.Message}`.");
                return null; // Signal failure
            }
        }

        // No changes are needed for FormatFinalMessage, but it's included for completeness.
        private (string, TL.MessageEntity[]?) FormatFinalMessage(string caption, TL.MessageEntity[]? entities, StringBuilder debugReport)
        {
            if (string.IsNullOrWhiteSpace(caption))
            {
                _ = debugReport.AppendLine("**4. Formatting:** 🚫 Skipped (caption is empty).");
                return (string.Empty, null);
            }

            try
            {
                // Only parse if no entities exist, as AI or pre-processing might have already created them.
                if (entities is null || entities.Length == 0)
                {
                    (string? parsedCaption, MessageEntity[]? parsedEntities) = _markdownParserService.ParseMarkdownToTelegramEntities(caption);
                    _ = debugReport.AppendLine($"**4. Formatting:** ✅ Markdown parsed ({parsedEntities.Length} entities found).");
                    return (parsedCaption, parsedEntities.Length > 0 ? parsedEntities : null);
                }

                _ = debugReport.AppendLine($"**4. Formatting:** ✅ Using pre-existing entities ({entities?.Length ?? 0}).");
                return (caption, entities);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse final markdown.");
                _ = debugReport.AppendLine($"**4. Formatting:** ⚠️ Markdown parse failed: `{ex.Message}`.");
                return (caption, null); // Return raw caption on failure
            }
        }

        #endregion


        /// <summary>
        /// Forwards messages from one peer to another.
        /// Uses resilience policies.
        /// </summary>
        /// <summary>
        /// Forwards messages from one peer to another.
        /// Uses resilience policies.
        /// </summary>
        /// <param name="toPeer">The destination peer.</param>
        /// <param name="messageIds">An array of message IDs to forward.</param>
        /// <param name="fromPeer">The source peer where messages are located.</param>
        /// <param name="cancellationToken">Cancellation token for the operation.</param>
        /// <param name="dropAuthor">Optional: If true, the original sender's name will not be shown.</param>
        /// <param name="noForwards">Optional: If true, the message will not be marked as a forwarded message.</param>
        /// <returns>An <see cref="UpdatesBase"/> object containing the forwarded messages update, or null if no messages were forwarded.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the Telegram client is not initialized or API call returns null unexpectedly.</exception>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="toPeer"/> or <paramref name="fromPeer"/> is null.</exception>
        /// <exception cref="RpcException">Thrown if a Telegram API error occurs.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the operation is cancelled.</exception>
        /// <exception cref="Exception">Thrown for any other unhandled errors.</exception>
        public async Task<TL.UpdatesBase?> ForwardMessagesAsync(
            TL.InputPeer toPeer,
            int[] messageIds,
            TL.InputPeer fromPeer,
            CancellationToken cancellationToken,
            bool dropAuthor = false,
            bool noForwards = false)
        {
            // Level 1: Early Exit for invalid state/arguments.
            if (_client is null)
            {
                _logger.LogError("ForwardMessagesAsync: Telegram client (_client) is not initialized. Cannot forward messages.");
                throw new InvalidOperationException("Telegram API client is not initialized.");
            }
            if (toPeer is null)
            {
                _logger.LogError("ForwardMessagesAsync: ToPeer cannot be null for forwarding messages.");
                throw new ArgumentNullException(nameof(toPeer), "ToPeer cannot be null for forwarding messages.");
            }
            if (fromPeer is null)
            {
                _logger.LogError("ForwardMessagesAsync: FromPeer cannot be null for forwarding messages.");
                throw new ArgumentNullException(nameof(fromPeer), "FromPeer cannot be null for forwarding messages.");
            }
            if (messageIds is null || messageIds.Length == 0)
            {
                // Level 1: Graceful exit for empty input, as per ITelegramUserApiClient's nullable return
                _logger.LogWarning("ForwardMessagesAsync: Message IDs list is null or empty. Nothing to forward. From Peer (Type: {FromPeerType}, ID: {FromPeerId}) To Peer (Type: {ToPeerType}, ID: {ToPeerId}).",
                    fromPeer.GetType().Name, GetPeerIdForLog(fromPeer), toPeer.GetType().Name, GetPeerIdForLog(toPeer));
                return null;
            }

            // Level 2: Prepare logging variables upfront, conditionally format expensive parts.
            string toPeerType = toPeer.GetType().Name;
            long toPeerId = GetPeerIdForLog(toPeer);
            string fromPeerType = fromPeer.GetType().Name;
            long fromPeerId = GetPeerIdForLog(fromPeer);

            string messageIdsString = _logger.IsEnabled(LogLevel.Debug)
                ? (messageIds.Length > 5
                    ? $"{string.Join(", ", messageIds.Take(5))}... (Total: {messageIds.Length})"
                    : string.Join(", ", messageIds))
                : "[...IDs...]"; // Placeholder if not logging verbosely

            // Level 2: Conditional Logging - Debug level for detailed input parameters
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "ForwardMessagesAsync: Attempting to forward {MessageCount} message(s) from Peer (Type: {FromPeerType}, LoggedID: {FromPeerId}) to Peer (Type: {ToPeerType}, LoggedID: {ToPeerId}). Message IDs (partial): [{MessageIdsArray}]. DropAuthor: {DropAuthor}, NoForwards: {NoForwards}.",
                    messageIds.Length, fromPeerType, fromPeerId, toPeerType, toPeerId,
                    messageIdsString,
                    dropAuthor, noForwards
                );
            }

            // Generate unique random IDs for each forwarded message. Required by Telegram API for idempotency.
            long[] randomIdArray = messageIds.Select(_ => WTelegram.Helpers.RandomLong()).ToArray();

            // Level 3: Use a lock specific to the forwarding operation to manage concurrency for this specific from-to pair.
            string lockKey = $"forward_peer_{fromPeerType}_{fromPeerId}_to_{toPeerType}_{toPeerId}";
            _logger.LogTrace("ForwardMessagesAsync: Attempting to acquire forward lock with key: {LockKey}.", lockKey);
            using IDisposable forwardLock = await AsyncLock.LockAsync(lockKey).ConfigureAwait(false);
            _logger.LogDebug("ForwardMessagesAsync: Acquired forward lock with key: {LockKey}.", lockKey);

            try
            {
                // Level 4: Resilience Pipeline Execution for forwarding messages.
                TL.UpdatesBase? result = await _resiliencePipeline.ExecuteAsync(async (context, token) =>
                    await _client!.Messages_ForwardMessages(
                        to_peer: toPeer,
                        from_peer: fromPeer,
                        id: messageIds,
                        random_id: randomIdArray,
                        drop_author: dropAuthor, // WTelegramClient handles mapping these bools to internal flags
                        noforwards: noForwards   // WTelegramClient handles mapping these bools to internal flags
                    ).ConfigureAwait(false),
                    new Polly.Context(nameof(ForwardMessagesAsync)),
                    cancellationToken
                ).ConfigureAwait(false);

                // Level 1: Post-API call validation
                if (result is null)
                {
                    _logger.LogError("ForwardMessagesAsync: WTelegramClient API call returned null for forwarding. From Peer (Type: {FromPeerType}, ID: {FromPeerId}) To Peer (Type: {ToPeerType}, ID: {ToPeerId}). Message IDs: [{MessageIdsArray}]. This is unexpected.",
                        fromPeerType, fromPeerId, toPeerType, toPeerId, string.Join(", ", messageIds));
                    throw new InvalidOperationException("Telegram API call to Messages_ForwardMessages unexpectedly returned null.");
                }

                // Level 2: Informational logging for success
                _logger.LogInformation(
                    "ForwardMessagesAsync: Successfully forwarded {MessageCount} messages. Response type: {ResponseType}. " +
                    "From Peer (Type: {FromPeerType}, ID: {FromPeerId}) " +
                    "To Peer (Type: {ToPeerType}, ID: {ToPeerId}).",
                    messageIds.Length,
                    result.GetType().Name,
                    fromPeerType, fromPeerId,
                    toPeerType, toPeerId);

                return result;
            }
            // Level 6: Consistent Error Handling and Logging
            catch (OperationCanceledException oce) // Handle explicit cancellation first
            {
                _logger.LogInformation(oce, "ForwardMessagesAsync: Operation cancelled for forwarding messages from Peer (Type: {FromPeerType}, ID: {FromPeerId}) to Peer (Type: {ToPeerType}, ID: {ToPeerId}).",
                    fromPeerType, fromPeerId, toPeerType, toPeerId);
                throw; // Re-throw to propagate cancellation.
            }
            catch (TL.RpcException rpcEx) // Handle Telegram API specific errors
            {
                _logger.LogError(rpcEx,
                    "ForwardMessagesAsync: Telegram API (RPC) exception occurred after Polly retries exhausted (or error was not retryable). Error: {ErrorTypeString}, Code: {ErrorCode}. From Peer (Type: {FromPeerType}, ID: {FromPeerId}) To Peer (Type: {ToPeerType}, ID: {ToPeerId}). Message IDs: [{MessageIdsArray}]",
                    rpcEx.Message, rpcEx.Code, fromPeerType, fromPeerId, toPeerType, toPeerId, string.Join(", ", messageIds));
                throw; // Re-throw to propagate the exception.
            }
            catch (Exception ex) // Catch any other unexpected exceptions
            {
                _logger.LogError(ex,
                    "ForwardMessagesAsync: Unhandled generic exception occurred for forwarding. From Peer (Type: {FromPeerType}, ID: {FromPeerId}) To Peer (Type: {ToPeerType}, ID: {ToPeerId}). Message IDs: [{MessageIdsArray}]",
                    fromPeerType, fromPeerId, toPeerType, toPeerId, string.Join(", ", messageIds));
                throw;
            }
            finally
            {
                // Level 2: Trace logging for lock release
                _logger.LogTrace("ForwardMessagesAsync: Forward lock (if acquired) has been released for key: {LockKey}", lockKey);
            }
        }

        /// <summary>
        /// Retrieves the current logged-in user's information.
        /// </summary>
        public async Task<User?> GetSelfAsync(CancellationToken cancellationToken)
        {
            if (_client == null)
            {
                _logger.LogError("GetSelfAsync: Telegram client (_client) is not initialized.");
                return null;
            }
            try
            {
                _logger.LogDebug("GetSelfAsync: Attempting to retrieve self user info using LoginUserIfNeeded.");
                return await _resiliencePipeline.ExecuteAsync(async (context, token) =>
                    await _client!.LoginUserIfNeeded(), // LoginUserIfNeeded will return the logged-in user.
                    new Context(nameof(GetSelfAsync)),
                    cancellationToken);
            }
            catch (RpcException rpcEx)
            {
                _logger.LogError(rpcEx, "GetSelfAsync failed after Polly retries exhausted (or error was not retryable). Error: {ErrorTypeString}, Code: {ErrorCode}",
                                 rpcEx.Message, rpcEx.Code);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetSelfAsync failed due to an unhandled exception.");
                return null;
            }
        }

        public async Task<InputPeer?> ResolvePeerAsync(long peerId, CancellationToken cancellationToken)
        {
            if (_client == null)
            {
                _logger.LogError("ResolvePeerAsync: Client not initialized for PeerId {PeerId}. Cannot resolve.", peerId);
                return null;
            }
            if (peerId == 0)
            {
                _logger.LogWarning("ResolvePeerAsync: PeerId is 0. Cannot resolve a peer with ID 0. Returning null.");
                return null;
            }

            _logger.LogDebug("ResolvePeerAsync: Attempting to resolve PeerId: {PeerId}", peerId);

            // 1. Check User Cache
            if (_userCacheWithExpiry.TryGetValue(peerId, out (User User, DateTime Expiry) userCacheEntry) &&
                userCacheEntry.Expiry > DateTime.UtcNow && userCacheEntry.User != null)
            {
                _logger.LogInformation("ResolvePeerAsync: Found User {UserId} (AccessHash: {AccessHash}) in LOCAL USER CACHE for PeerId {PeerId}.",
                    peerId, userCacheEntry.User.access_hash, peerId);
                return new InputPeerUser(peerId, userCacheEntry.User.access_hash);
            }

            // 2. Check Chat Cache (for Channel and Chat)
            if (_chatCacheWithExpiry.TryGetValue(peerId, out (ChatBase Chat, DateTime Expiry) chatCacheEntry) &&
                chatCacheEntry.Expiry > DateTime.UtcNow && chatCacheEntry.Chat != null)
            {
                if (chatCacheEntry.Chat is TL.Channel channelFromCache)
                {
                    _logger.LogInformation("ResolvePeerAsync: Found Channel {ChannelId} (AccessHash: {AccessHash}) in LOCAL CHAT CACHE for PeerId {PeerId}.",
                        peerId, channelFromCache.access_hash, peerId);
                    return new InputPeerChannel(peerId, channelFromCache.access_hash);
                }
                else if (chatCacheEntry.Chat is Chat chatFromCache)
                {
                    _logger.LogInformation("ResolvePeerAsync: Found Chat {ChatId} in LOCAL CHAT CACHE for PeerId {PeerId}.",
                        peerId, peerId);
                    return new InputPeerChat(peerId);
                }
            }
            _logger.LogDebug("ResolvePeerAsync: PeerId {PeerId} not found in active local cache. Attempting API calls.", peerId);

            // This section is what you need to integrate with YOUR database or rule configuration.
            // Assuming you have a way to fetch the stored access_hash for a given target peerId.
            // THIS IS PSEUDOCODE - REPLACE WITH YOUR ACTUAL DB/RULE ACCESS LOGIC.
            // Example: For the specific rule 'ForwardOurTesting1ToOurTesting2' and target '-1002696634930',
            // you would query your DB for the stored access_hash for this target.
            long? storedAccessHash = null; // Placeholder for DB lookup result
                                           // Example: var rule = _ruleRepository.GetRuleByName("ForwardOurTesting1ToOurTesting2");
                                           // if (rule != null) {
                                           //     var targetChannelInfo = rule.TargetChannelDetails.FirstOrDefault(td => td.ChannelId == peerId);
                                           //     storedAccessHash = targetChannelInfo?.AccessHash;
                                           // }
                                           // END OF PSEUDOCODE FOR DB LOOKUP

            if (peerId < 0 && storedAccessHash.HasValue && storedAccessHash.Value != 0) // Only for negative IDs (channels/groups) and if we have a non-zero stored access hash
            {
                long channelIdFromPeer = PeerToChannelId(peerId);
                _logger.LogInformation("ResolvePeerAsync: Using stored AccessHash {AccessHash} for ChannelId {ChannelId} from database/rule config.", storedAccessHash.Value, channelIdFromPeer);
                return new InputPeerChannel(channelIdFromPeer, storedAccessHash.Value);
            }


            // 3. Try Contacts_ResolveUsername (often works for public IDs or IDs that are usernames)
            string resolveString = peerId.ToString();
            if (peerId > 0) // Only attempt for positive IDs, as negative IDs are not usernames.
            {
                _logger.LogDebug("ResolvePeerAsync: Attempting API call with Contacts_ResolveUsername for string '{ResolveString}' (PeerId {PeerId}).", resolveString, peerId);
                try
                {
                    Contacts_ResolvedPeer resolvedUsernameResponse = await _resiliencePipeline.ExecuteAsync(async (context, token) =>
                        await _client!.Contacts_ResolveUsername(resolveString),
                        new Context(nameof(ResolvePeerAsync) + "_ResolveUsername"),
                        cancellationToken);

                    if (resolvedUsernameResponse?.users != null)
                    {
                        foreach (KeyValuePair<long, User> uEntry in resolvedUsernameResponse.users)
                        {
                            _userCacheWithExpiry[uEntry.Key] = (uEntry.Value, DateTime.UtcNow.Add(_cacheExpiration));
                        }
                    }
                    if (resolvedUsernameResponse?.chats != null)
                    {
                        foreach (KeyValuePair<long, ChatBase> cEntry in resolvedUsernameResponse.chats)
                        {
                            _chatCacheWithExpiry[cEntry.Key] = (cEntry.Value, DateTime.UtcNow.Add(_cacheExpiration));
                        }
                    }

                    if (resolvedUsernameResponse?.peer != null)
                    {
                        if (resolvedUsernameResponse.peer is PeerUser pu)
                        {
                            // THIS IS THE CORRECTED LINE 655
                            TL.User? userObj = resolvedUsernameResponse.users.GetValueOrDefault(pu.user_id);
                            long accessHash = userObj?.access_hash ?? 0;
                            _logger.LogInformation("ResolvePeerAsync: Resolved via Contacts_ResolveUsername to User {UserId} (AccessHash: {AccessHash}) for string '{ResolveString}'.",
                                pu.user_id, accessHash, resolveString);
                            return new InputPeerUser(pu.user_id, accessHash);
                        }
                        // No need to handle PeerChat/PeerChannel here if we explicitly handle them later or expect them to be resolved by ID.
                        _logger.LogWarning("ResolvePeerAsync: Contacts_ResolveUsername for string '{ResolveString}' returned an unhandled peer type: {PeerType}. PeerId: {PeerId}",
                            resolveString, resolvedUsernameResponse.peer.GetType().Name, peerId);
                    }
                    else
                    {
                        _logger.LogWarning("ResolvePeerAsync: Contacts_ResolveUsername for string '{ResolveString}' (PeerId {PeerId}) returned null or null peer. This might indicate the username is not found or not public.",
                           resolveString, peerId);
                    }
                }
                catch (RpcException rpcEx) when (rpcEx.Code == 400 && (rpcEx.Message.Contains("USERNAME_INVALID", StringComparison.OrdinalIgnoreCase) || rpcEx.Message.Contains("USERNAME_NOT_OCCUPIED", StringComparison.OrdinalIgnoreCase) || rpcEx.Message.Contains("PEER_ID_INVALID", StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning("ResolvePeerAsync: Contacts_ResolveUsername for string '{ResolveString}' (PeerId {PeerId}) failed with RPC Error: {RpcError}. This ID string is likely not a known username or public peer ID that can be resolved this way.",
                        resolveString, peerId, rpcEx.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ResolvePeerAsync: Exception during Contacts_ResolveUsername for string '{ResolveString}' (PeerId {PeerId}).", resolveString, peerId);
                }
            }


            // 4. Try Channels_GetChannels (for channel IDs, especially if negative with -100 prefix)
            // This attempt uses access_hash = 0, relying on the client's membership/public access.
            _logger.LogDebug("ResolvePeerAsync: Attempting API call with Channels_GetChannels for PeerId: {PeerId} (AccessHash: 0) as a fallback.", peerId);
            try
            {
                Messages_Chats channelsResponse = await _resiliencePipeline.ExecuteAsync(async (context, token) =>
                    await _client!.Channels_GetChannels(new[] { new InputChannel(PeerToChannelId(peerId), 0) }),
                    new Context(nameof(ResolvePeerAsync) + "_GetChannels"),
                    cancellationToken);

                if (channelsResponse?.chats != null)
                {
                    foreach (KeyValuePair<long, ChatBase> cEntry in channelsResponse.chats)
                    {
                        _chatCacheWithExpiry[cEntry.Key] = (cEntry.Value, DateTime.UtcNow.Add(_cacheExpiration));
                    }
                }

                if (channelsResponse?.chats != null && channelsResponse.chats.TryGetValue(PeerToChannelId(peerId), out ChatBase? chatFromApi) && chatFromApi is TL.Channel telegramChannel)
                {
                    _logger.LogInformation("ResolvePeerAsync: Successfully resolved Channel {PeerId} (API ID: {ApiChannelId}, AccessHash: {AccessHash}) via Channels_GetChannels (fallback).",
                                       peerId, telegramChannel.id, telegramChannel.access_hash);
                    return new InputPeerChannel(telegramChannel.id, telegramChannel.access_hash);
                }
                _logger.LogWarning("ResolvePeerAsync: Fallback Channels_GetChannels for PeerId {PeerId} did not find it in the response or it wasn't a Channel. Chats in response: {ChatCount}",
                                   peerId, channelsResponse?.chats?.Count ?? 0);
            }
            catch (RpcException rpcEx) when (rpcEx.Message.Contains("CHANNEL_INVALID", StringComparison.OrdinalIgnoreCase) || rpcEx.Message.Contains("PEER_ID_INVALID", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("ResolvePeerAsync: Fallback Channels_GetChannels for PeerId {PeerId} failed with RPC Error: {RpcError}. This ID might not be a channel, is inaccessible, or access_hash problem.", peerId, rpcEx.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ResolvePeerAsync: Exception during fallback Channels_GetChannels for PeerId {PeerId}.", peerId);
            }

            // 5. Try Messages_GetChats (for basic group chat IDs that are negative, without -100 prefix)
            if (peerId < 0 && !peerId.ToString().StartsWith("-100")) // Basic group chat IDs are negative, but not -100xxxxxx
            {
                _logger.LogDebug("ResolvePeerAsync: Attempting Messages_GetChats for negative PeerId (non-channel group): {PeerId}.", peerId);
                try
                {
                    Messages_Chats chatsResponse = await _resiliencePipeline.ExecuteAsync(async (context, token) =>
                        await _client!.Messages_GetChats(new long[] { peerId }), // Pass raw long ID
                        new Context(nameof(ResolvePeerAsync) + "_GetChats"),
                        cancellationToken);

                    if (chatsResponse?.chats != null)
                    {
                        foreach (KeyValuePair<long, ChatBase> cEntry in chatsResponse.chats)
                        {
                            _chatCacheWithExpiry[cEntry.Key] = (cEntry.Value, DateTime.UtcNow.Add(_cacheExpiration));
                        }
                    }

                    if (chatsResponse?.chats != null && chatsResponse.chats.TryGetValue(peerId, out ChatBase? chatFromApi) && chatFromApi is Chat telegramChat)
                    {
                        _logger.LogInformation("ResolvePeerAsync: Successfully resolved Chat {PeerId} via Messages_GetChats (fallback).", peerId);
                        return new InputPeerChat(telegramChat.id);
                    }
                    _logger.LogWarning("ResolvePeerAsync: Fallback Messages_GetChats for PeerId {PeerId} did not find it in the response or it wasn't a Chat. Chats in response: {ChatCount}",
                                       peerId, chatsResponse?.chats?.Count ?? 0);
                }
                catch (RpcException rpcEx) when (rpcEx.Message.Contains("CHAT_INVALID", StringComparison.OrdinalIgnoreCase) || rpcEx.Message.Contains("PEER_ID_INVALID", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("ResolvePeerAsync: Fallback Messages_GetChats for PeerId {PeerId} failed with RPC Error: {RpcError}. This ID might not be a chat, is inaccessible, or an invalid peer.", peerId, rpcEx.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ResolvePeerAsync: Exception during fallback Messages_GetChats for PeerId {PeerId}.", peerId);
                }
            }


            // 6. Try Users_GetUsers (for positive user IDs that Contacts_ResolveUsername failed for, requiring direct lookup)
            if (peerId > 0)
            {
                _logger.LogDebug("ResolvePeerAsync: Attempting Users_GetUsers for positive PeerId (user): {PeerId}.", peerId);
                try
                {
                    UserBase[] usersResponse = await _resiliencePipeline.ExecuteAsync(async (context, token) =>
                        await _client!.Users_GetUsers(new[] { new InputUser(peerId, 0) }), // Assuming access_hash 0 for initial lookup
                        new Context(nameof(ResolvePeerAsync) + "_GetUsers"),
                        cancellationToken);

                    if (usersResponse != null && usersResponse.Any())
                    {
                        foreach (UserBase userBase in usersResponse)
                        {
                            if (userBase is User user)
                            {
                                _userCacheWithExpiry[user.id] = (user, DateTime.UtcNow.Add(_cacheExpiration));
                            }
                        }
                    }

                    User? userFromApi = usersResponse?.OfType<User>().FirstOrDefault(u => u.id == peerId);

                    if (userFromApi != null)
                    {
                        _logger.LogInformation("ResolvePeerAsync: Successfully resolved User {PeerId} (API ID: {ApiUserId}, AccessHash: {AccessHash}) via Users_GetUsers (fallback).",
                                           peerId, userFromApi.id, userFromApi.access_hash);
                        return new InputPeerUser(userFromApi.id, userFromApi.access_hash);
                    }
                    _logger.LogWarning("ResolvePeerAsync: Fallback Users_GetUsers for PeerId {PeerId} did not find the user in the response (might be UserEmpty or not found).", peerId);
                }
                catch (RpcException rpcEx) when (rpcEx.Message.Contains("USER_NOT_FOUND", StringComparison.OrdinalIgnoreCase) || rpcEx.Message.Contains("PEER_ID_INVALID", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("ResolvePeerAsync: Fallback Users_GetUsers for PeerId {PeerId} failed with RPC Error: {RpcError}. User might not exist or be accessible.", peerId, rpcEx.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ResolvePeerAsync: Exception during fallback Users_GetUsers for PeerId {PeerId}.", peerId);
                }
            }

            // 7. Last Resort: Directly construct InputPeerChannel/InputPeerUser if all API attempts failed.
            // This is a "best effort" that might succeed if the client is already a member,
            // or if the API accepts an InputPeer without a specific access_hash in some contexts (e.g., if message is forwarded FROM this peer).
            // This explicitly captures the "Directly constructed InputPeerChannel" logic from your log.
            if (peerId < 0) // Assume it's a channel or basic group (negative ID)
            {
                long channelOrChatId = PeerToChannelId(peerId);
                _logger.LogWarning("ResolvePeerAsync: All other API strategies failed for negative PeerId {PeerId}. Attempting direct InputPeerChannel construction with access_hash 0 as a last resort. This may succeed if the client is already a member or if context allows.", peerId);
                return new InputPeerChannel(channelOrChatId, 0); // Access hash 0 is commonly used when unknown or for public entities.
            }
            else if (peerId > 0) // Assume it's a user (positive ID)
            {
                _logger.LogWarning("ResolvePeerAsync: All other API strategies failed for positive PeerId {PeerId}. Attempting direct InputPeerUser construction with access_hash 0 as a last resort. This may succeed if the client has direct knowledge of the user.", peerId);
                return new InputPeerUser(peerId, 0); // Access hash 0 is commonly used when unknown.
            }

            _logger.LogError("ResolvePeerAsync: FAILED to resolve PeerId {PeerId} using all implemented strategies and direct construction. Returning null.", peerId);
            return null;
        }

        /// <summary>
        /// Disposes the TelegramUserApiClient and its resources.
        /// It's important to call this when the service is shutting down.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            _logger.LogInformation("Disposing TelegramUserApiClient and its resources...");

            // Stop and dispose the cache cleanup timer.
            if (_cacheCleanupTimer != null)
            {
                await _cacheCleanupTimer.DisposeAsync();
                _cacheCleanupTimer = null;
                _logger.LogInformation("Cache cleanup timer disposed.");
            }

            // Dispose the WTelegramClient instance.
            // This is crucial for releasing network connections and saving the session properly.
            if (_client != null)
            {
                _client.OnUpdates -= HandleUpdatesBaseAsync; // Unsubscribe from updates.
                _client.Dispose(); // Dispose the client.
                _client = null; // Nullify the client to indicate it's no longer usable.
                _logger.LogInformation("WTelegram.Client instance disposed.");
            }

            // Dispose the connection semaphore.
            try
            {
                _connectionLock.Dispose();
                _logger.LogInformation("Connection semaphore disposed.");
            }
            catch (ObjectDisposedException)
            {
                // Log if already disposed, which is harmless but indicates a double-dispose attempt or race condition.
                _logger.LogWarning("Connection lock was already disposed.");
            }

            _messageCache.Dispose(); // Dispose the MemoryCache.
            _logger.LogInformation("MemoryCache disposed.");

            _logger.LogInformation("TelegramUserApiClient disposal complete.");
            await Task.CompletedTask;
        }

        #endregion

        #region Helper class for async locking
        /// <summary>
        /// Provides an asynchronous locking mechanism using SemaphoreSlim.
        /// Allows for per-key locks to control concurrency for specific operations/resources.
        /// </summary>
        private class AsyncLock
        {
            private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

            /// <summary>
            /// Acquires a lock for a given key. The returned IDisposable should be used in a `using` statement
            /// to ensure the lock is released.
            /// </summary>
            public static async Task<IDisposable> LockAsync(string key)
            {
                // Get or add a SemaphoreSlim for the specific key.
                SemaphoreSlim semaphore = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
                await semaphore.WaitAsync(); // Wait to acquire the lock.
                // Return a disposable object that releases the semaphore when disposed.
                return new DisposableAction(() => semaphore.Release());
            }

            /// <summary>
            /// Private helper class to encapsulate the lock release action within an IDisposable.
            /// </summary>
            private class DisposableAction : IDisposable
            {
                private readonly Action _action;
                public DisposableAction(Action action)
                {
                    _action = action;
                }

                public void Dispose()
                {
                    _action();
                }
            }
        }
        #endregion

        #region Helper Methods
        /// <summary>
        /// Extracts a relevant ID from an InputPeer object for logging purposes.
        /// </summary>
        private long GetPeerIdForLog(InputPeer? peer)
        {
            if (peer is InputPeerUser ipu)
            {
                return ipu.user_id;
            }

            if (peer is InputPeerChat ipc)
            {
                return ipc.chat_id;
            }

            if (peer is InputPeerChannel ipch)
            {
                return ipch.channel_id;
            }

            if (peer is InputPeerSelf)
            {
                return -1; // Special ID for self.
            }

            return 0; // Default or unknown.
        }

        /// <summary>
        /// Helper to convert a peerId (which can be negative for chats/channels) to a channel_id
        /// which is always positive and used in InputChannel.
        /// For channels (supergroups), the ID is usually stored as -100xxxxxx.
        /// WTelegramClient's InputChannel constructor often expects the absolute value of this ID.
        /// </summary>
        /// <param name="peerId">The original peer ID (e.g., -100xxxxxxxxxx for a channel, positive for user/chat)</param>
        /// <returns>The positive channel ID.</returns>
        private long PeerToChannelId(long peerId)
        {
            // Telegram channel IDs (supergroups) are internally represented as negative numbers starting with -100.
            // When constructing InputChannel or similar objects in WTelegramClient, you often need the positive version.
            // For example, if peerId is -1001234567890, the channel ID for InputChannel would be 1234567890.
            if (peerId < 0 && peerId.ToString().StartsWith("-100"))
            {
                return Math.Abs(peerId);
            }
            // For other negative IDs (e.g., basic groups, which are just negative chat_id), Math.Abs is also correct.
            // For positive IDs (users), Math.Abs changes nothing.
            return Math.Abs(peerId);
        }

        /// <summary>
        /// Checks if a Gemini job has completed and optionally waits for it.
        /// </summary>
        /// <param name="jobId">The job ID to check</param>
        /// <param name="waitForCompletion">Whether to wait for completion</param>
        /// <param name="maxWaitTime">Maximum time to wait</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The job result if completed, null otherwise</returns>
        private async Task<string?> CheckGeminiJobResultAsync(string jobId, bool waitForCompletion = false, TimeSpan? maxWaitTime = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return null;
            }

            TimeSpan waitTime = maxWaitTime ?? TimeSpan.FromSeconds(30);
            DateTime startTime = DateTime.UtcNow;

            while (DateTime.UtcNow - startTime < waitTime)
            {
                try
                {
                    string? result = await _geminiService.GetJobResultAsync(jobId, cancellationToken);

                    if (result == "JOB_RUNNING")
                    {
                        if (!waitForCompletion)
                        {
                            return null;
                        }

                        await Task.Delay(1000, cancellationToken); // Wait 1 second before checking again
                        continue;
                    }

                    if (result == "JOB_NOT_FOUND")
                    {
                        return null;
                    }

                    // Job completed (success or failure)
                    return result;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to check Gemini job result for JobId: {JobId}", jobId);
                    if (!waitForCompletion)
                    {
                        return null;
                    }

                    await Task.Delay(1000, cancellationToken);
                }
            }

            return null; // Timeout
        }
        #endregion

        #region ForwardingRules Cache Integration
        // Add a delegate property to allow cache removal from outside (e.g., injected from ForwardingService)
        public Action<long>? RemoveForwardingRulesCacheForChannel { get; set; }
        #endregion

        /// <summary>
        /// Generates a unique, deterministic, and collision-resistant cache key for a single message.
        /// This is the core of the idempotency logic to prevent duplicate message processing and sending.
        /// </summary>
        /// <param name="peer">The target peer.</param>
        /// <param name="message">The message text.</param>
        /// <param name="media">The media (if any).</param>
        /// <param name="entities">The message entities (if any).</param>
        /// <param name="replyToMsgId">The reply-to message ID (if any).</param>
        /// <param name="replyMarkup">The reply markup (if any).</param>
        /// <param name="noWebpage">No webpage preview flag.</param>
        /// <param name="background">Background flag.</param>
        /// <param name="clearDraft">Clear draft flag.</param>
        /// <param name="schedule_date">Scheduled date (if any).</param>
        /// <returns>A unique string key suitable for caching.</returns>
        private static string GenerateMessageCacheKey(
            InputPeer peer,
            string? message,
            InputMedia? media,
            IEnumerable<MessageEntity>? entities,
            int? replyToMsgId,
            ReplyMarkup? replyMarkup,
            bool noWebpage,
            bool background,
            bool clearDraft,
            DateTime? schedule_date)
        {
            StringBuilder keyBuilder = new(256);
            _ = keyBuilder.Append($"Peer:{peer?.GetType().Name}:{GetPeerIdForCache(peer)}|");
            _ = keyBuilder.Append($"Msg:{NormalizeCaption(message)}|");
            if (media != null)
            {
                _ = keyBuilder.Append($"Media:{media.GetType().Name}:{media.GetHashCode()}|");
            }
            if (entities != null)
            {
                foreach (MessageEntity entity in entities)
                {
                    _ = keyBuilder.Append($"Entity:{entity.GetType().Name}:{entity.GetHashCode()}|");
                }
            }
            _ = keyBuilder.Append($"ReplyTo:{replyToMsgId}|Markup:{replyMarkup?.GetType().Name}:{replyMarkup?.GetHashCode()}|");
            _ = keyBuilder.Append($"NoWebpage:{noWebpage}|Background:{background}|ClearDraft:{clearDraft}|Schedule:{schedule_date?.ToString("O")}");
            // Use robust hash for collision resistance
            string combinedString = keyBuilder.ToString();
            string finalHash = GenerateRobustHash(combinedString);
            return $"MsgSent-{finalHash}";
        }

        // Helper for peer ID extraction for cache key
        private static long GetPeerIdForCache(InputPeer? peer)
        {
            return peer is InputPeerUser ipu
                ? ipu.user_id
                : peer is InputPeerChat ipc ? ipc.chat_id : peer is InputPeerChannel ipch ? ipch.channel_id : peer is InputPeerSelf ? -1 : 0;
        }
    }
    public static class TelegramMessageHelper
    {
        public const int TextMessageMaxLength = 4086;
        public const int CaptionMaxLength = 1014;

        public static List<string> ChunkMessage(string text, int limit)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= limit)
            {
                return [text ?? string.Empty];
            }

            List<string> chunks = [];
            string remainingText = text.Trim();

            while (remainingText.Length > 0)
            {
                // If the remaining text fits within the limit, add it as the last chunk.
                if (remainingText.Length <= limit)
                {
                    chunks.Add(remainingText);
                    break;
                }

                // Determine the best split point *at or before* the limit.
                int splitIndex = FindBestSplitIndex(remainingText, limit);

                // Add the chunk to the list.
                chunks.Add(remainingText[..splitIndex].TrimEnd());

                // Update the remaining text.
                remainingText = remainingText[splitIndex..].TrimStart();
            }

            return chunks;
        }

        /// <summary>
        /// Finds the best index to split the string at or before the given limit.
        /// Prioritizes natural breaks (newline, space) over arbitrary cuts.
        /// </summary>
        private static int FindBestSplitIndex(string text, int limit)
        {
            // First, consider the limit itself as a potential split point.
            int bestIndex = limit;

            // Try to find the last newline before or at the limit.
            int newlineIndex = text.LastIndexOf('\n', limit - 1);
            if (newlineIndex > 0) // If newline is found and not at the very beginning
            {
                bestIndex = newlineIndex;
            }
            else
            {
                // If no newline, try to find the last space before or at the limit.
                int spaceIndex = text.LastIndexOf(' ', limit - 1);
                if (spaceIndex > 0) // If space is found and not at the very beginning
                {
                    bestIndex = spaceIndex;
                }
            }

            // Heuristic: Avoid splitting immediately after an opening bold/italic marker if possible.
            // This is complex and can be very error-prone if not carefully implemented.
            // For simplicity and robustness, we'll stick to newline/space as primary.
            // A more advanced approach would involve parsing entities.

            // If the calculated bestIndex is 0 (meaning the limit hit before any meaningful split),
            // we must split at the limit to avoid an infinite loop.
            if (bestIndex <= 0 && text.Length > 0)
            {
                return Math.Min(limit, text.Length);
            }

            return Math.Min(bestIndex, text.Length); // Ensure we don't go out of bounds
        }

        /// <summary>
        /// Extracts the sent message ID from the various UpdatesBase types returned by WTelegramClient.
        /// </summary>
        public static int? GetSentMessageId(UpdatesBase updatesBase)
        {
            return updatesBase switch
            {
                Updates { updates: var u } => u.OfType<UpdateNewMessage>().FirstOrDefault()?.message.ID ??
                                              u.OfType<UpdateNewChannelMessage>().FirstOrDefault()?.message.ID,
                UpdateShortSentMessage ssm => ssm.id,
                _ => null
            };
        }
    }
}
