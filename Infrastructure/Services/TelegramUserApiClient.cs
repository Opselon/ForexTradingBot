// File: src/Infrastructure/Services/TelegramUserApiClient.cs
#region Usings
using Application.Common.Interfaces;
using Infrastructure.Settings;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
        public bool IsConnected { get; private set; } = false;
        #region Private Readonly Fields
        private readonly ILogger<TelegramUserApiClient> _logger;
        private readonly TelegramUserApiSettings _settings;
        private readonly ConcurrentDictionary<long, (TL.User User, DateTime Expiry)> _userCacheWithExpiry = new();
        private readonly ConcurrentDictionary<long, (TL.ChatBase Chat, DateTime Expiry)> _chatCacheWithExpiry = new();
        private readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(30);
        private readonly MemoryCache _messageCache = new MemoryCache(new MemoryCacheOptions());
        private readonly MemoryCacheEntryOptions _cacheOptions = new MemoryCacheEntryOptions()
            .SetSlidingExpiration(TimeSpan.FromMinutes(5))
            .SetAbsoluteExpiration(TimeSpan.FromHours(1));
        private readonly TimeSpan _cacheCleanupInterval = TimeSpan.FromMinutes(10);

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
        #endregion

        #region Private Fields
        private WTelegram.Client? _client;
        private readonly SemaphoreSlim _connectionLock = new SemaphoreSlim(1, 1);
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
        public TelegramUserApiClient(
             ILogger<TelegramUserApiClient> logger,
             IOptions<TelegramUserApiSettings> settingsOptions,
             bool useChannelForDispatch = false) // LEVEL 10: New parameter in constructor
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settingsOptions?.Value ?? throw new ArgumentNullException(nameof(settingsOptions));
            _useChannelForDispatch = useChannelForDispatch; // LEVEL 10: Initialize the flag

            // Set up the session file path for WTelegramClient's custom session management.
            _sessionPath = Path.Combine(AppContext.BaseDirectory, _settings.SessionPath ?? "telegram_user.session");

            // Ensure the directory for the session file exists.
            string? sessionDir = Path.GetDirectoryName(_sessionPath);
            if (!string.IsNullOrEmpty(sessionDir) && !Directory.Exists(sessionDir))
            {
                _logger.LogInformation("Creating session directory: {SessionDirectory}", sessionDir);
                _ = Directory.CreateDirectory(sessionDir);
            }

            // Configure WTelegramClient's internal logging to use Microsoft.Extensions.Logging.
            WTelegram.Helpers.Log = (level, message) =>
            {
                var msLevel = level switch
                {
                    0 => Microsoft.Extensions.Logging.LogLevel.Trace,
                    1 => Microsoft.Extensions.Logging.LogLevel.Debug,
                    2 => Microsoft.Extensions.Logging.LogLevel.Information,
                    3 => Microsoft.Extensions.Logging.LogLevel.Warning,
                    4 => Microsoft.Extensions.Logging.LogLevel.Error,
                    _ => Microsoft.Extensions.Logging.LogLevel.None,
                };           
            };

            // Define custom session loader delegate for WTelegramClient.
            byte[]? startSessionLoader()
            {
                _logger.LogTrace("Custom session loader: Attempting to load session from {SessionPath}...", _sessionPath);
                if (!File.Exists(_sessionPath))
                {
                    _logger.LogDebug("Custom session loader: Session file {SessionPath} does not exist. Returning null (empty session).", _sessionPath);
                    return null; // Return null if no session file exists, WTelegramClient will create a new session.
                }
                try
                {
                    byte[] data = File.ReadAllBytes(_sessionPath);
                    _logger.LogInformation("Custom session loader: Successfully loaded {BytesRead} bytes from {SessionPath}.", data.Length, _sessionPath);
                    return data;
                }
                catch (Exception ex)
                {
                    // Log the cryptographic error specifically
                    _logger.LogError(ex, "Custom session loader: Error reading or decrypting session file {SessionPath}. This usually indicates file corruption or an incorrect API hash/ID. Attempting to delete corrupt file to force relogin.", _sessionPath);
                    try
                    {
                        File.Delete(_sessionPath);
                        _logger.LogInformation("Successfully deleted corrupt session file {SessionPath}. A new login will be required.", _sessionPath);
                    }
                    catch (Exception deleteEx)
                    {
                        _logger.LogError(deleteEx, "Failed to delete corrupt session file {SessionPath}. Manual intervention might be required.", _sessionPath);
                    }
                    return null; // Return null to signal WTelegramClient to start a new session (triggers relogin).
                }
            }

            // Define custom session saver delegate for WTelegramClient.
            void saveSessionAction(byte[] bytes)
            {
                _logger.LogTrace("Custom session saver: Attempting to save {BytesLength} bytes to {SessionPath}...", bytes.Length, _sessionPath);
                try
                {
                    // Use a temporary file and rename to ensure atomic write and data integrity.
                    string tempPath = _sessionPath + ".tmp";
                    File.WriteAllBytes(tempPath, bytes);
                    File.Move(tempPath, _sessionPath, overwrite: true); // Overwrite existing session file
                    _logger.LogInformation("Custom session saver: Successfully saved {BytesLength} bytes to {SessionPath}.", bytes.Length, _sessionPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Custom session saver: Error writing to session file {SessionPath}. Data might not be persisted.", _sessionPath);
                }
            }

            _client = new WTelegram.Client(ConfigProvider, startSessionLoader(), saveSessionAction);

            // Subscribe to WTelegramClient's OnUpdates event to process incoming updates.
            _client.OnUpdates += async updates =>
            {
                //      _logger.LogCritical("[USER_API_ON_UPDATES_TRIGGERED] Raw updates object of type: {UpdateType} from WTelegram.Client", updates.GetType().FullName);
                if (updates is TL.UpdatesBase updatesBase) // Use TL.UpdatesBase
                {
                    await HandleUpdatesBaseAsync(updatesBase);
                }
                else
                {
                    //   _logger.LogWarning("[USER_API_ON_UPDATES_TRIGGERED] Received 'updates' that is NOT UpdatesBase. Type: {UpdateType}", updates.GetType().FullName);
                }
            };

            // Start a periodic timer for cache cleanup.
            _cacheCleanupTimer = new System.Threading.Timer(
                      CacheCleanup,
                      null,
                      (int)_cacheCleanupInterval.TotalMilliseconds,
                      (int)_cacheCleanupInterval.TotalMilliseconds
                  );
            _logger.LogInformation("Started cache cleanup timer with interval {IntervalMinutes} minutes.", _cacheCleanupInterval.TotalMinutes);

            // Configure Polly for resilience (retry policy for network and specific RPC errors).
            _resiliencePipeline = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder()
                .Handle<HttpRequestException>()
                // <<< CHANGE: Added to handle transient network I/O errors.
                // This directly addresses the critical IOException seen in the logs.
                .Handle<IOException>()
                .Handle<RpcException>(rpcEx =>
                {
                    // Server-side errors (5xx) are often transient and worth retrying.
                    if (rpcEx.Code is >= 500 and < 600)
                    {
                        _logger.LogWarning(rpcEx, "Polly: Retrying RPC error {RpcCode} ({RpcMessage}) as it's a server-side error.", rpcEx.Code, rpcEx.Message);
                        return true;
                    }
                    // Rate-limiting and flood control errors are explicitly retry-able.
                    if (rpcEx.Message.Contains("TOO_MANY_REQUESTS", StringComparison.OrdinalIgnoreCase) ||
                        rpcEx.Message.Contains("FLOOD_WAIT_", StringComparison.OrdinalIgnoreCase))
                    {
                        // For FLOOD_WAIT, we can be smarter. If the wait time is excessive, we should not retry.
                        if (rpcEx.Message.StartsWith("FLOOD_WAIT_") &&
                            int.TryParse(rpcEx.Message.Substring("FLOOD_WAIT_".Length), out int seconds))
                        {
                            // Failsafe: If Telegram requests a wait time longer than our max delay, abort.
                            if (seconds > _retryDelays[^1].TotalSeconds * 2)
                            {
                                _logger.LogCritical(rpcEx, "Polly: Encountered a FLOOD_WAIT of {Seconds}s, which greatly exceeds max configured retry delay. Aborting retries for this specific error to prevent resource exhaustion.", seconds);
                                return false;
                            }
                            _logger.LogWarning(rpcEx, "Polly: Retrying FLOOD_WAIT of {Seconds}s.", seconds);
                            return true;
                        }
                        _logger.LogWarning(rpcEx, "Polly: Retrying TOO_MANY_REQUESTS or unknown FLOOD_WAIT type.");
                        return true;
                    }
                    // Any other RpcException is considered non-transient (e.g., auth errors, bad requests) and should fail immediately.
                    return false;
                }),
            DelayGenerator = args =>
            {
                var retryAttempt = args.AttemptNumber;
                if (retryAttempt < _retryDelays.Length)
                {
                    var delay = _retryDelays[retryAttempt];
                    // Note: The original log message was slightly confusing. Simplified for clarity.
                    _logger.LogWarning(args.Outcome.Exception, "Polly Retry: Attempt {AttemptNumber} for '{OperationKey}' failed with {ExceptionType}. Delaying for {Delay}ms.",
                        retryAttempt + 1, args.Context.OperationKey ?? "N/A", args.Outcome.Exception?.GetType().Name ?? "N/A", delay.TotalMilliseconds);
                    return ValueTask.FromResult<TimeSpan?>(delay);
                }
                _logger.LogWarning("Polly Retry: Max retries ({MaxRetries}) reached for operation '{OperationKey}'. No further retries will be attempted.",
                    _retryDelays.Length, args.Context.OperationKey ?? "N/A");
                return ValueTask.FromResult<TimeSpan?>(null); // Stop retrying
            },
            MaxRetryAttempts = _retryDelays.Length,
            OnRetry = args => // OnRetry is useful for metrics or state changes, logging is often better in DelayGenerator
            {
                // Logging is already handled well in DelayGenerator, so this could be simplified or used for other side-effects.
                // For now, keeping it as is for consistency with the original code.
                _logger.LogTrace(args.Outcome.Exception,
                    "Polly OnRetry: Triggered for '{OperationKey}' on attempt {AttemptNumber}.",
                    args.Context.OperationKey ?? "N/A", args.AttemptNumber + 1);
                return default;
            },
        })
        .Build();

            // LEVEL 10: Start channel pipeline if enabled
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
                await foreach (var update in _updateChannel.Reader.ReadAllAsync()) // Reader is now correctly accessed
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
                return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
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
                if (_internalWtcChatCache.TryGetValue(id, out var cachedChat))
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
                if (_internalWtcUserCache.TryGetValue(id, out var cachedUser))
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
            var userPeer = ResolvePeer(usm.user_id, false);

            var msg = new TL.Message
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
            var chatPeer = ResolvePeer(uscm.chat_id, true);
            var fromPeer = ResolvePeer(uscm.from_id, false);

            var msg = new TL.Message
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
            return key switch
            {
                "api_id" => _settings.ApiId.ToString(),
                "api_hash" => _settings.ApiHash,
                "phone_number" => _settings.PhoneNumber,
                "verification_code" => AskCode("Telegram asks for verification code: ", _settings.VerificationCodeSource),
                "password" => AskCode("Telegram asks for 2FA password (if enabled): ", _settings.TwoFactorPasswordSource),
                _ => null
            };
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
                                   trimmedQuestion.Length > 50 ? trimmedQuestion.Substring(0, 50) + "..." : trimmedQuestion,
                                   effectiveSourceMethod);

            if (effectiveSourceMethod.Equals("console", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // Check if the application is running in an interactive user environment.
                    if (!Environment.UserInteractive)
                    {
                        _logger.LogWarning("AskCode (console): Application is not running in an interactive user environment. Cannot prompt for \"{QuestionDisplay}\". Returning null.",
                                           trimmedQuestion.Length > 50 ? trimmedQuestion.Substring(0, 50) + "..." : trimmedQuestion);
                        return null;
                    }
                    // Attempt to detect if console input is redirected (e.g., from a file or pipe).
                    try
                    {
                        if (Console.IsInputRedirected)
                        {
                            _logger.LogInformation("AskCode (console): Input is redirected. Reading from redirected input for \"{QuestionDisplay}\".",
                                                   trimmedQuestion.Length > 50 ? trimmedQuestion.Substring(0, 50) + "..." : trimmedQuestion);
                        }
                    }
                    catch (InvalidOperationException ioExConsoleCheck)
                    {
                        // This can happen if no console is attached (e.g., a service or non-interactive process).
                        _logger.LogWarning(ioExConsoleCheck, "AskCode (console): No console available or console operation failed during pre-check for \"{QuestionDisplay}\". Returning null.",
                                           trimmedQuestion.Length > 50 ? trimmedQuestion.Substring(0, 50) + "..." : trimmedQuestion);
                        return null;
                    }

                    Console.Out.Write(trimmedQuestion + " ");
                    string? userInput = Console.ReadLine();

                    if (userInput != null)
                    {
                        _logger.LogInformation("WTC Input Received: User provided input of length {InputLength} for \"{QuestionDisplay}\" from console.",
                                               userInput.Length,
                                               trimmedQuestion.Length > 50 ? trimmedQuestion.Substring(0, 50) + "..." : trimmedQuestion);
                        return userInput;
                    }
                    else
                    {
                        _logger.LogWarning("WTC Input Received: User cancelled input (EOF) or input stream ended for \"{QuestionDisplay}\" from console. Returning null.",
                                           trimmedQuestion.Length > 50 ? trimmedQuestion.Substring(0, 50) + "..." : trimmedQuestion);
                        return null;
                    }
                }
                catch (IOException ioEx)
                {
                    _logger.LogError(ioEx, "WTC Input Error: An IOException occurred while trying to read from console for \"{QuestionDisplay}\". Returning null.",
                                     trimmedQuestion.Length > 50 ? trimmedQuestion.Substring(0, 50) + "..." : trimmedQuestion);
                    return null;
                }
                catch (OperationCanceledException ocEx)
                {
                    _logger.LogWarning(ocEx, "WTC Input Warning: Console read operation was ostensibly cancelled for \"{QuestionDisplay}\". Returning null.",
                                       trimmedQuestion.Length > 50 ? trimmedQuestion.Substring(0, 50) + "..." : trimmedQuestion);
                    return null;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "WTC Input Error: An unexpected error occurred during console input for \"{QuestionDisplay}\". Returning null.",
                                     trimmedQuestion.Length > 50 ? trimmedQuestion.Substring(0, 50) + "..." : trimmedQuestion);
                    return null;
                }
            }
            else
            {
                // Implement other source methods here (e.g., fetching from a secure vault, external API).
                _logger.LogWarning("WTC Input Request: Source method '{SourceMethod}' is not implemented for question \"{QuestionDisplay}\". Returning null.",
                                   effectiveSourceMethod,
                                   trimmedQuestion.Length > 50 ? trimmedQuestion.Substring(0, 50) + "..." : trimmedQuestion);
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
            var now = DateTime.UtcNow;

            // Use ToArray() to avoid modification during enumeration.
            foreach (var entry in _userCacheWithExpiry.ToArray())
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
            foreach (var entry in _chatCacheWithExpiry.ToArray())
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
            foreach (var userEntry in _internalWtcUserCache)
            {
                _ = _userCacheWithExpiry.AddOrUpdate(userEntry.Key,
                                                (userEntry.Value, expiryTime),
                                                (key, existingVal) => (userEntry.Value, expiryTime)); // Always update value and expiry
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("Cached user {UserId} with expiry {ExpiryTime}.", userEntry.Key, expiryTime);
                }
            }
            foreach (var chatEntry in _internalWtcChatCache)
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
            List<TL.Update> updatesToDispatch = new List<TL.Update>(100); // Use TL.Update

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
                    foreach (var update in updatesToDispatch)
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
                    foreach (var update in updatesToDispatch)
                    {
                        // Capture the update variable for the lambda expression to avoid closure over loop variable,
                        // ensuring each Task operates on its specific 'update' object.
                        var currentUpdate = update;

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
            return string.IsNullOrEmpty(str) ? "[null_or_empty]" : str.Length <= maxLength ? str : str.Substring(0, maxLength) + "...";
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
        public async Task ConnectAndLoginAsync(CancellationToken cancellationToken)
        {
            // IsConnected should only be true AFTER successful connection and login.
            // Move this assignment to the success path.

            // Level 3: Use a SemaphoreSlim to prevent multiple concurrent connection attempts.
            // This ensures only one login process runs at a time.
            _logger.LogTrace("ConnectAndLoginAsync: Attempting to acquire connection lock.");
            await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("ConnectAndLoginAsync: Acquired connection lock.");

            try
            {
                // Level 1: Early Exit for invalid state.
                if (_client is null)
                {
                    _logger.LogCritical("ConnectAndLoginAsync: WTelegram.Client instance is unexpectedly null. This indicates a potential issue with initialization or prior disposal. Cannot proceed.");
                    throw new InvalidOperationException("Telegram API client is not initialized. Ensure it's correctly set up in the constructor.");
                }

                _logger.LogInformation("ConnectAndLoginAsync: Attempting to connect and login user API.");

                TL.User? loggedInUser = null;
                try
                {
                    // Level 4: Resilience Pipeline for initial LoginUserIfNeeded.
                    // This pipeline handles transient network issues and retries.
                    // Specific RpcExceptions (2FA, DC Migrate) are handled in the outer catch blocks below,
                    // as they require specific actions beyond simple retry.
                    loggedInUser = await _resiliencePipeline.ExecuteAsync(
                        async (context, token) => await _client.LoginUserIfNeeded().ConfigureAwait(false), // WTelegramClient handles its own CancellationToken integration
                        new Polly.Context(nameof(ConnectAndLoginAsync)),
                        cancellationToken
                    ).ConfigureAwait(false);
                }
                // Level 6: Specific RpcException handling for 2FA.
                catch (TL.RpcException e) when (e.Code == 401 && (e.Message.Contains("SESSION_PASSWORD_NEEDED", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("account_password_input_needed", StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning(e, "ConnectAndLoginAsync: 2FA needed for login. Attempting re-login with 2FA (via ConfigProvider).");
                    // WTelegramClient's LoginUserIfNeeded() will internally call ConfigProvider("password").
                    loggedInUser = await _client!.LoginUserIfNeeded().ConfigureAwait(false);
                    _logger.LogInformation("ConnectAndLoginAsync: User API Logged in with 2FA.");
                }
                // Level 6: Specific RpcException handling for DC Migration.
                catch (TL.RpcException e) when (e.Message.StartsWith("PHONE_MIGRATE_", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(e.Message.Split('_').Last(), out int dcNumber))
                    {
                        _logger.LogWarning(e, "ConnectAndLoginAsync: Phone number needs to be migrated to DC{DCNumber}. WTelegramClient will handle reconnection.", dcNumber);
                        // WTelegramClient's LoginUserIfNeeded() handles DC migration internally.
                        loggedInUser = await _client!.LoginUserIfNeeded().ConfigureAwait(false);
                        _logger.LogInformation("ConnectAndLoginAsync: User API successfully migrated to DC{DCNumber}.", dcNumber);
                    }
                    else
                    {
                        _logger.LogError(e, "ConnectAndLoginAsync: Failed to parse DC number from migration error message: {ErrorMessage}. Re-throwing original exception.", e.Message);
                        throw; // Re-throw the original exception if parsing fails.
                    }
                }

                // Level 1: Post-login validation.
                if (loggedInUser is null)
                {
                    _logger.LogError("ConnectAndLoginAsync: User API Login Failed: LoginUserIfNeeded returned null after all attempts. Check configuration and authentication steps.");
                    throw new InvalidOperationException("WTelegramClient failed to log in user.");
                }

                _logger.LogInformation("ConnectAndLoginAsync: User API Logged in successfully: ID {UserId}, Full Name: {FullName}, Phone: {PhoneNumber}",
                    loggedInUser.id, $"{loggedInUser.first_name} {loggedInUser.last_name}", loggedInUser.phone);

                // Level 5: After successful login, refresh dialogs and populate caches.
                await RefreshDialogsAndCachesAsync(cancellationToken).ConfigureAwait(false);

                // Only set IsConnected to true if everything above succeeded.
                IsConnected = true;
            }
            // Level 6: Consistent Error Handling and Logging for broader exceptions.
            catch (IOException ioEx)
            {
                _logger.LogError(ioEx, "ConnectAndLoginAsync: A low-level network I/O error occurred. Inner Exception: {InnerException}. Check network connectivity and firewall rules.", ioEx.InnerException?.Message);
                IsConnected = false;
                throw;
            }
            catch (OperationCanceledException oce)
            {
                _logger.LogInformation(oce, "ConnectAndLoginAsync: Operation cancelled during connection/login process.");
                IsConnected = false; // Ensure state reflects cancellation
                throw; // Re-throw to propagate the cancellation
            }
            catch (TL.RpcException rpcEx) // Catch any remaining RpcExceptions not handled by specific clauses
            {
                // Log RPC errors as ERROR, indicating a problem but allowing the caller's retry policy to take over.
                _logger.LogError(rpcEx, "ConnectAndLoginAsync: Telegram API (RPC) error during connection/login: {ErrorTypeString}, Code: {ErrorCode}. This might be a transient issue or require investigation.", rpcEx.Message, rpcEx.Code);
                IsConnected = false;
                throw; // Re-throw for caller to handle retries/degradation.
            }
            catch (NullReferenceException nre) // Specifically catch the NullReferenceException observed
            {
                // CHANGE HERE: Log as a WARNING instead of ERROR if you want to reduce log severity for individual occurrences.
                _logger.LogWarning(nre, "ConnectAndLoginAsync: Unexpected NullReferenceException from WTelegram.Client during connection/login process. This may indicate a transient client library issue or network problem.");
                IsConnected = false;
                throw; // Re-throw to ensure the calling policy retries.
            }
            catch (Exception ex) // General catch-all for any other unexpected, unclassified exceptions.
            {
                // Log any other unhandled exceptions as CRITICAL, as they might indicate truly unexpected
                // problems requiring immediate attention.
                _logger.LogCritical(ex, "ConnectAndLoginAsync: Critical, unclassified error occurred during connect/login process. Service may be unrecoverable without manual intervention.");
                IsConnected = false;
                throw; // Re-throw to propagate the failure.
            }
            finally
            {
                // Level 3: Ensure the semaphore is always released.
                try
                {
                    _ = _connectionLock.Release();
                    _logger.LogDebug("ConnectAndLoginAsync: Connection lock released.");
                }
                catch (ObjectDisposedException ode)
                {
                    _logger.LogWarning(ode, "ConnectAndLoginAsync: Connection lock was disposed during release. This can occur during application shutdown.");
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
                foreach (var userEntry in _internalWtcUserCache)
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
                foreach (var chatEntry in _internalWtcChatCache)
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
                var sortedMsgIds = msgIds.OrderBy(id => id); // ToList() is an allocation, keep it IOrderedEnumerable<int>
                var cacheKey = $"msgs_peer_{cacheKeySuffix}_ids_{string.Join("_", sortedMsgIds)}";

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
                var inputMessageIDs = new TL.InputMessage[msgIds.Length];
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
        private static readonly HttpClient _httpClient = new HttpClient();

        /// <summary>
        /// Fetches a random piece of advice from the public Advice Slip API.
        /// This is a helper method used by SendRandomAdviceAsync.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for the operation.</param>
        /// <returns>A string containing a piece of advice, or null if the request fails.</returns>
        private async Task<string?> get_random_advice(CancellationToken cancellationToken)
        {
            const string apiUrl = "https://api.adviceslip.com/advice";
            try
            {
                _logger.LogDebug("GetRandomAdviceAsync: Requesting advice from API: {ApiUrl}", apiUrl);

                // Send the GET request
                HttpResponseMessage response = await _httpClient.GetAsync(apiUrl, cancellationToken);

                // Ensure the request was successful
                response.EnsureSuccessStatusCode();

                // Read and deserialize the JSON response
                var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var adviceData = await JsonSerializer.DeserializeAsync<AdviceSlipResponse>(responseStream, cancellationToken: cancellationToken);

                string? advice = adviceData?.slip?.advice;

                if (string.IsNullOrWhiteSpace(advice))
                {
                    _logger.LogWarning("GetRandomAdviceAsync: API returned a successful response but the advice content was empty.");
                    return null;
                }

                _logger.LogInformation("GetRandomAdviceAsync: Successfully retrieved advice: '{Advice}'", advice);
                return advice;
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError(httpEx, "GetRandomAdviceAsync: An HTTP error occurred while calling the advice API.");
                return null;
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "GetRandomAdviceAsync: Failed to deserialize the JSON response from the advice API.");
                return null;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("GetRandomAdviceAsync: Operation was cancelled.");
                throw; // Re-throw to respect the cancellation
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetRandomAdviceAsync: An unexpected error occurred while fetching advice.");
                return null;
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
            string? message, // Message can be null if media is present (caption)
            CancellationToken cancellationToken,
            int? replyToMsgId = null,
            ReplyMarkup? replyMarkup = null,
            IEnumerable<MessageEntity>? entities = null, // KEEP as IEnumerable<MessageEntity> for input flexibility
            bool noWebpage = false, // Will only be used for text messages
            bool background = false,
            bool clearDraft = false,
            DateTime? schedule_date = null,
            InputMedia? media = null)
        {
            // Level 1: Early Exit for invalid state/arguments.
            if (_client is null)
            {
                _logger.LogError("SendMessageAsync: Telegram client (_client) is not initialized. Cannot send message.");
                throw new InvalidOperationException("Telegram API client is not initialized.");
            }
            if (peer is null)
            {
                _logger.LogError("SendMessageAsync: InputPeer cannot be null.");
                throw new ArgumentNullException(nameof(peer), "InputPeer cannot be null for sending message.");
            }
            if (string.IsNullOrEmpty(message) && media is null)
            {
                _logger.LogError("SendMessageAsync: Both message content and media are null or empty. Nothing to send.");
                throw new ArgumentException("Either message content or media must be provided for sending a message.", nameof(message));
            }

            // Level 2: Prepare logging variables upfront, conditionally format expensive parts.
            long peerIdForLog = GetPeerIdForLog(peer); // Assumed helper method
            string peerTypeForLog = peer.GetType().Name;
            string truncatedMessage = _logger.IsEnabled(LogLevel.Debug) ? TruncateString(message, 100) : "[...message...]"; // Assumed helper method
            bool hasMedia = media != null;

            // Convert entities to array once if needed, and for logging.
            MessageEntity[]? entitiesArray = entities?.ToArray(); // Convert to array early if not null/empty
            string entitiesInfo = _logger.IsEnabled(LogLevel.Debug) && entitiesArray != null && entitiesArray.Any()
                ? $"Count: {entitiesArray.Length}, Types: [{string.Join(", ", entitiesArray.Select(e => e.GetType().Name))}]"
                : (entitiesArray != null && entitiesArray.Any() ? "[...entities...]" : "None");


            // Level 3: Define a lock key specific to the peer to prevent simultaneous sends to the same chat.
            string lockKey = $"send_peer_{peerTypeForLog}_{peerIdForLog}";

            // Level 2: Conditional Logging - Debug level for detailed input parameters
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "SendMessageAsync: Preparing to send message to Peer (Type: {PeerType}, LoggedID: {PeerId}). " +
                    "Message (partial): '{MessageContent}'. Entities: {EntitiesInfo}. Media: {HasMedia}. ReplyToMsgID: {ReplyToMsgId}. " +
                    "NoWebpage: {NoWebpageFlag} (ignored for media). Background: {BackgroundFlag}. ClearDraft: {ClearDraftFlag}. ScheduleDate: {ScheduleDate}.",
                    peerTypeForLog,
                    peerIdForLog,
                    truncatedMessage,
                    entitiesInfo,
                    hasMedia,
                    replyToMsgId.HasValue ? replyToMsgId.Value.ToString() : "N/A",
                    noWebpage, background, clearDraft, schedule_date.HasValue ? schedule_date.Value.ToString("s") : "N/A");
            }

            // Level 4: Custom pre-processing example (if required)
            if (!string.IsNullOrEmpty(message) && message!.Contains("https://wa.me/message/W6HXT7VWR3U2C1", StringComparison.OrdinalIgnoreCase))
            {
                message = message.Replace("https://wa.me/message/W6HXT7VWR3U2C1", "@capxi", StringComparison.OrdinalIgnoreCase);
                _logger.LogDebug("SendMessageAsync: Replaced WhatsApp link with @capxi for Peer {PeerId}.", peerIdForLog);
            }

            // --- NEW: Add advice footer to text messages and media captions ---
            try
            {
                // Call the get_random_advice function, which makes the API call.
                string? randomAdvice = await get_random_advice(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(randomAdvice))
                {
                    // Format the footer with an emoji
                    string footer = $"\n\n💡 {randomAdvice}";

                    // If the original message is null/empty (e.g., for a media caption), the advice becomes the message.
                    // Otherwise, it's appended to the existing message.
                    message = string.IsNullOrEmpty(message) ? $"💡 {randomAdvice}" : $"{message}{footer}";

                    _logger.LogDebug("SendMessageAsync: Appended random advice footer to the message for Peer {PeerId}.", peerIdForLog);

                    // Re-truncate the message for accurate logging
                    truncatedMessage = _logger.IsEnabled(LogLevel.Debug) ? TruncateString(message, 100) : "[...message...]";
                }
                else
                {
                    _logger.LogWarning("SendMessageAsync: Could not retrieve random advice to append as footer. Sending message without it.");
                }
            }
            catch (Exception ex)
            {
                // Log the error but do not re-throw. This ensures that if the advice API is down,
                // the original message can still be sent without the footer.
                _logger.LogWarning(ex, "SendMessageAsync: Failed to get/append random advice due to an exception. Message will be sent without the footer.");
            }
            // --- END OF NEW LOGIC ---

            try
            {
                // Level 3: Acquire a lock. Use 'await using' for proper disposal.
                _logger.LogTrace("SendMessageAsync: Attempting to acquire send lock with key: {LockKey}", lockKey);
                // Assuming AsyncLock.LockAsync is a static method that returns an IDisposable
                using var sendLock = await AsyncLock.LockAsync(lockKey).ConfigureAwait(false);
                _logger.LogDebug("SendMessageAsync: Acquired send lock with key: {LockKey} for Peer (Type: {PeerType}, LoggedID: {PeerId})",
                    lockKey, peerTypeForLog, peerIdForLog);

                long random_id = WTelegram.Helpers.RandomLong();
                InputReplyTo? inputReplyTo = replyToMsgId.HasValue
                    ? new InputReplyToMessage { reply_to_msg_id = replyToMsgId.Value }
                    : null;

                UpdatesBase updatesBase;

                // Level 5: Conditionally call Messages_SendMessage or Messages_SendMedia
                if (media is null)
                {
                    _logger.LogDebug("SendMessageAsync: Calling _client.Messages_SendMessage (text-only) for Peer (Type: {PeerType}, LoggedID: {PeerId}).",
                        peerTypeForLog, peerIdForLog);

                    updatesBase = await _resiliencePipeline.ExecuteAsync(
                        async (context, token) => await _client!.Messages_SendMessage(
                            peer: peer,
                            message: message!, // Message is guaranteed not null here due to early exit check
                            random_id: random_id,
                            reply_to: inputReplyTo,
                            reply_markup: replyMarkup,
                            entities: entitiesArray, // Pass the array here
                            no_webpage: noWebpage,
                            background: background,
                            clear_draft: clearDraft,
                            schedule_date: schedule_date
                        ).ConfigureAwait(false),
                        new Polly.Context(nameof(SendMessageAsync) + "_Text"),
                        cancellationToken
                    ).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogDebug("SendMessageAsync: Calling _client.Messages_SendMedia for Peer (Type: {PeerType}, LoggedID: {PeerId}).",
                        peerTypeForLog, peerIdForLog);

                    updatesBase = await _resiliencePipeline.ExecuteAsync(
                        async (context, token) => await _client!.Messages_SendMedia(
                            peer: peer,
                            media: media,
                            random_id: random_id,
                            message: message, // Message acts as caption here, can be null
                            reply_to: inputReplyTo,
                            reply_markup: replyMarkup,
                            entities: entitiesArray, // Pass the array here (for caption entities)
                            background: background,
                            clear_draft: clearDraft,
                            schedule_date: schedule_date
                        // no_webpage parameter is NOT available for Messages_SendMedia
                        ).ConfigureAwait(false),
                        new Polly.Context(nameof(SendMessageAsync) + "_Media"),
                        cancellationToken
                    ).ConfigureAwait(false);
                }

                // Level 1: Post-API call validation
                if (updatesBase is null)
                {
                    _logger.LogError("SendMessageAsync: WTelegramClient API call returned null after Polly retries. This is unexpected. Peer: {PeerId}, Message (partial): '{MessageContent}'", peerIdForLog, truncatedMessage);
                    throw new InvalidOperationException("Telegram API call to SendMessage/SendMedia unexpectedly returned null after all retries.");
                }

                // Level 2: Informational logging for success
                _logger.LogInformation(
                    "SendMessageAsync: Message sent successfully via API. Response Type: {ResponseType}. " +
                    "Peer (Type: {PeerType}, LoggedID: {PeerId}). Message (partial): '{TruncatedMessage}'",
                    updatesBase.GetType().Name,
                    peerTypeForLog, peerIdForLog,
                    truncatedMessage);

                return updatesBase;
            }
            // Level 6: Consistent Error Handling and Logging
            catch (OperationCanceledException oce) // Handle explicit cancellation first
            {
                _logger.LogInformation(oce, "SendMessageAsync: Operation cancelled for Peer (Type: {PeerType}, LoggedID: {PeerId}), Message (partial): '{MessageContent}'.",
                    peerTypeForLog, peerIdForLog, truncatedMessage);
                throw; // Re-throw to propagate cancellation.
            }
            catch (RpcException rpcEx) // Handle Telegram API specific errors
            {
                _logger.LogError(rpcEx, "SendMessageAsync: Telegram API (RPC) exception occurred after Polly retries exhausted (or error was not retryable) for Peer (Type: {PeerType}, LoggedID: {PeerId}). Error: {ErrorTypeString}, Code: {ErrorCode}. Message (partial): '{MessageContent}'",
                    peerTypeForLog, peerIdForLog, rpcEx.Message, rpcEx.Code, truncatedMessage);
                throw; // Re-throw to propagate the exception.
            }
            catch (Exception ex) // Catch any other unexpected exceptions
            {
                _logger.LogError(ex, "SendMessageAsync: Unhandled generic exception occurred for Peer (Type: {PeerType}, LoggedID: {PeerId}). Message (partial): '{MessageContent}'",
                    peerTypeForLog, peerIdForLog, truncatedMessage);
                throw;
            }
            finally
            {
                // Level 2: Trace logging for lock release
                _logger.LogTrace("SendMessageAsync: Send lock (if acquired) has been released for key: {LockKey}", lockKey);
            }
        }
        /// <summary>
        /// Sends a group of media items as an album to a specified peer.
        /// Uses resilience policies.
        /// </summary>
        public async Task SendMediaGroupAsync( // Returns Task (void), as per interface
           TL.InputPeer peer,
           ICollection<TL.InputMedia> media,
           CancellationToken cancellationToken,
           string? albumCaption = null,
           TL.MessageEntity[]? albumEntities = null, // Directly MessageEntity[] as per interface
           int? replyToMsgId = null,
           bool background = false, // Interface requires this, but SendAlbumAsync does not have it. Will be ignored.
           DateTime? schedule_date = null,
           bool sendAsBot = false) // Interface requires this, but SendAlbumAsync does not have it. Will be ignored.
        {
            // Level 1: Early Exit for invalid state/arguments.
            if (_client is null)
            {
                _logger.LogError("SendMediaGroupAsync: Telegram client (_client) is not initialized. Cannot send media group.");
                throw new InvalidOperationException("Telegram API client is not initialized.");
            }
            if (peer is null)
            {
                _logger.LogError("SendMediaGroupAsync: InputPeer is null. Cannot send media group.");
                throw new ArgumentNullException(nameof(peer), "InputPeer cannot be null for sending media group.");
            }
            if (media is null || !media.Any())
            {
                _logger.LogError("SendMediaGroupAsync: Media list cannot be null or empty. Aborting send.");
                throw new ArgumentException("Media list cannot be null or empty for sending media group.", nameof(media));
            }

            // Level 2: Prepare logging variables upfront, conditionally format expensive parts.
            long peerIdForLog = GetPeerIdForLog(peer);
            string peerTypeForLog = peer.GetType().Name;

            string effectiveAlbumCaptionForLog = _logger.IsEnabled(LogLevel.Debug)
                ? (string.IsNullOrWhiteSpace(albumCaption)
                    ? (media.FirstOrDefault()?.GetType().Name is string firstMediaName
                        ? $"First media type: {firstMediaName}"
                        : "[No Caption]")
                    : TruncateString(albumCaption, 50))
                : "[...caption...]";

            string mediaItemsInfo = _logger.IsEnabled(LogLevel.Debug)
                ? (media.Count > 5 ? $"{media.Count} items (e.g., {string.Join(", ", media.Take(2).Select(m => m.GetType().Name))}...)" : $"Total: {media.Count} items")
                : "[...media items...]";

            string lockKey = $"send_media_group_peer_{peerTypeForLog}_{peerIdForLog}";

            // Level 2: Conditional Logging - Debug level for detailed input parameters
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "SendMediaGroupAsync: Attempting to send media group to Peer (Type: {PeerType}, LoggedID: {PeerId}). " +
                    "Media Items: {MediaItemsInfo}. Album Caption: '{AlbumCaptionPreview}'. ReplyToMsgID: {ReplyToMsgId}. " +
                    "Background: {BackgroundFlag} (ignored for albums). ScheduleDate: {ScheduleDate}. SendAsBot: {SendAsBotFlag} (ignored for albums).",
                    peerTypeForLog, peerIdForLog, mediaItemsInfo, effectiveAlbumCaptionForLog,
                    replyToMsgId.HasValue ? replyToMsgId.Value.ToString() : "N/A",
                    background, schedule_date.HasValue ? schedule_date.Value.ToString("s") : "N/A", sendAsBot);
            }

            try
            {
                // Level 3: Acquire a lock for sending media groups to a specific peer.
                _logger.LogTrace("SendMediaGroupAsync: Attempting to acquire send lock with key: {LockKey}", lockKey);
                using var sendLock = await AsyncLock.LockAsync(lockKey).ConfigureAwait(false);
                _logger.LogDebug("SendMediaGroupAsync: Acquired send lock with key: {LockKey} for Peer (Type: {PeerType}, LoggedID: {PeerId})",
                    lockKey, peerTypeForLog, peerIdForLog);

                int replyToMsgIdInt = replyToMsgId.HasValue ? replyToMsgId.Value : 0;

                // Level 4: Resilience Pipeline Execution for sending media group.
                TL.Message[] sentMessages = await _resiliencePipeline.ExecuteAsync(async (context, token) =>
                    await _client!.SendAlbumAsync(
                        peer: peer,
                        medias: media, // ICollection<InputMedia>
                        caption: albumCaption,
                        reply_to_msg_id: replyToMsgIdInt,
                        entities: albumEntities, // Directly MessageEntity[] as per WTC method signature
                        schedule_date: schedule_date ?? default(DateTime)
                    // videoUrlAsFile is not on the interface, relying on WTelegramClient's default (false)
                    // background is not on SendAlbumAsync
                    ).ConfigureAwait(false),
                    new Polly.Context(nameof(SendMediaGroupAsync)),
                    cancellationToken // Pass cancellation token to Polly pipeline
                ).ConfigureAwait(false);

                // Level 1: Post-API call validation
                if (sentMessages is null || sentMessages.Length == 0)
                {
                    _logger.LogError("SendMediaGroupAsync: WTelegramClient API call returned null or empty array after Polly retries. This is unexpected. Peer: {PeerId}, Media Items: {MediaItemsInfo}", peerIdForLog, mediaItemsInfo);
                    throw new InvalidOperationException("Telegram API call to SendAlbumAsync unexpectedly returned null or empty array after all retries.");
                }

                // Level 2: Informational logging for success
                _logger.LogInformation("SendMediaGroupAsync: Successfully sent media group of {MediaCount} items to Peer (Type: {PeerType}, LoggedID: {PeerId}). First message ID: {FirstMessageId}.",
                    media.Count, peerTypeForLog, peerIdForLog, sentMessages.FirstOrDefault()?.id ?? 0);

                // Note: Interface expects Task (void), so we don't return sentMessages.
                // If you need the messages, change the interface return type.
            }
            // Level 6: Consistent Error Handling and Logging
            catch (OperationCanceledException oce)
            {
                _logger.LogInformation(oce, "SendMediaGroupAsync: Operation cancelled for Peer (Type: {PeerType}, LoggedID: {PeerId}), Media Items: {MediaItemsInfo}.",
                    peerTypeForLog, peerIdForLog, mediaItemsInfo);
                throw;
            }
            catch (TL.RpcException rpcEx)
            {
                _logger.LogError(rpcEx, "SendMediaGroupAsync: Telegram API (RPC) exception occurred after Polly retries exhausted (or error was not retryable) for Peer (Type: {PeerType}, LoggedID: {PeerId}). Error: {ErrorTypeString}, Code: {ErrorCode}.",
                    peerTypeForLog, peerIdForLog, rpcEx.Message, rpcEx.Code);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SendMediaGroupAsync: Unhandled generic exception occurred for Peer (Type: {PeerType}, LoggedID: {PeerId}).",
                    peerTypeForLog, peerIdForLog);
                throw;
            }
            finally
            {
                _logger.LogTrace("SendMediaGroupAsync: Send lock (if acquired) has been released for key: {LockKey}", lockKey);
            }
        }


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
            var randomIdArray = messageIds.Select(_ => WTelegram.Helpers.RandomLong()).ToArray();

            // Level 3: Use a lock specific to the forwarding operation to manage concurrency for this specific from-to pair.
            string lockKey = $"forward_peer_{fromPeerType}_{fromPeerId}_to_{toPeerType}_{toPeerId}";
            _logger.LogTrace("ForwardMessagesAsync: Attempting to acquire forward lock with key: {LockKey}.", lockKey);
            using var forwardLock = await AsyncLock.LockAsync(lockKey).ConfigureAwait(false);
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
                _logger.LogWarning("ResolvePeerAsync: PeerId is 0. Cannot resolve a peer with ID 0. Returning null.", peerId);
                return null;
            }

            _logger.LogDebug("ResolvePeerAsync: Attempting to resolve PeerId: {PeerId}", peerId);

            // 1. Check User Cache
            if (_userCacheWithExpiry.TryGetValue(peerId, out var userCacheEntry) &&
                userCacheEntry.Expiry > DateTime.UtcNow && userCacheEntry.User != null)
            {
                _logger.LogInformation("ResolvePeerAsync: Found User {UserId} (AccessHash: {AccessHash}) in LOCAL USER CACHE for PeerId {PeerId}.",
                    peerId, userCacheEntry.User.access_hash, peerId);
                return new InputPeerUser(peerId, userCacheEntry.User.access_hash);
            }

            // 2. Check Chat Cache (for Channel and Chat)
            if (_chatCacheWithExpiry.TryGetValue(peerId, out var chatCacheEntry) &&
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
                        foreach (var uEntry in resolvedUsernameResponse.users)
                        {
                            _userCacheWithExpiry[uEntry.Key] = (uEntry.Value, DateTime.UtcNow.Add(_cacheExpiration));
                        }
                    }
                    if (resolvedUsernameResponse?.chats != null)
                    {
                        foreach (var cEntry in resolvedUsernameResponse.chats)
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
                    foreach (var cEntry in channelsResponse.chats)
                    {
                        _chatCacheWithExpiry[cEntry.Key] = (cEntry.Value, DateTime.UtcNow.Add(_cacheExpiration));
                    }
                }

                if (channelsResponse?.chats != null && channelsResponse.chats.TryGetValue(PeerToChannelId(peerId), out var chatFromApi) && chatFromApi is TL.Channel telegramChannel)
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
                        foreach (var cEntry in chatsResponse.chats)
                        {
                            _chatCacheWithExpiry[cEntry.Key] = (cEntry.Value, DateTime.UtcNow.Add(_cacheExpiration));
                        }
                    }

                    if (chatsResponse?.chats != null && chatsResponse.chats.TryGetValue(peerId, out var chatFromApi) && chatFromApi is Chat telegramChat)
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
                        foreach (var userBase in usersResponse)
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
                var semaphore = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
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
        #endregion
    }
}