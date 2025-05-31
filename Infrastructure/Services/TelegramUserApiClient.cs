// File: src/Infrastructure/Services/TelegramUserApiClient.cs
#region Usings
using Application.Common.Interfaces;
using Infrastructure.Settings;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql.Replication.PgOutput.Messages;
using Polly;
using Polly.Retry;
using System;
using System.Collections.Concurrent;
using System.Threading;
using TL;        // For core Telegram types
#endregion

namespace Infrastructure.Services
{
    public class TelegramUserApiClient : ITelegramUserApiClient
    {
        #region Private Readonly Fields
        private readonly ILogger<TelegramUserApiClient> _logger;
        private readonly TelegramUserApiSettings _settings;
        private readonly Dictionary<long, User> _userCache = new();
        private readonly Dictionary<long, ChatBase> _chatCache = new();
        private readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(30);
        private readonly ConcurrentDictionary<long, (User User, DateTime Expiry)> _userCacheWithExpiry = new();
        private readonly ConcurrentDictionary<long, (ChatBase Chat, DateTime Expiry)> _chatCacheWithExpiry = new();
        private readonly MemoryCache _messageCache = new MemoryCache(new MemoryCacheOptions());
        private readonly MemoryCacheEntryOptions _cacheOptions = new MemoryCacheEntryOptions()
            .SetSlidingExpiration(TimeSpan.FromMinutes(5))
            .SetAbsoluteExpiration(TimeSpan.FromHours(1));
        private readonly TimeSpan _cacheCleanupInterval = TimeSpan.FromMinutes(10);

        // NEW: Polly ResiliencePipeline and retry delays
        // This pipeline will automatically retry transient errors in API calls.
        private readonly ResiliencePipeline _resiliencePipeline;
        // Define an array of TimeSpan for exponential backoff retry delays.
        // These delays are chosen to be relatively short for "real-time" feel,
        // while still giving the server time to recover.
        private readonly TimeSpan[] _retryDelays = new TimeSpan[]
        {
            TimeSpan.FromMilliseconds(200), // First retry: Wait 200ms
            TimeSpan.FromMilliseconds(500), // Second retry: Wait 500ms
            TimeSpan.FromSeconds(1),        // Third retry: Wait 1 second
            TimeSpan.FromSeconds(2),        // Fourth retry: Wait 2 seconds
            TimeSpan.FromSeconds(4),        // Fifth retry: Wait 4 seconds
            TimeSpan.FromSeconds(8)         // Sixth (final) retry: Wait 8 seconds
        };
        #endregion

        #region Private Fields
        private WTelegram.Client? _client;
        private SemaphoreSlim _connectionLock = new SemaphoreSlim(1, 1);
        private System.Threading.Timer? _cacheCleanupTimer;
        #endregion

        #region Public Properties & Events
        public WTelegram.Client NativeClient => _client!;
        public event Action<Update> OnCustomUpdateReceived = delegate { }; // TL.Update
        #endregion

        #region Constructor
        public TelegramUserApiClient(
             ILogger<TelegramUserApiClient> logger,
             IOptions<TelegramUserApiSettings> settingsOptions)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settingsOptions?.Value ?? throw new ArgumentNullException(nameof(settingsOptions));

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
                if (msLevel != Microsoft.Extensions.Logging.LogLevel.None)
                    _logger.Log(msLevel, "[WTelegram] {Message}", message);
            };

            _client = new WTelegram.Client(ConfigProvider);
            _client.OnUpdates += async updates =>
            {
                _logger.LogCritical("[USER_API_ON_UPDATES_TRIGGERED] Raw updates object of type: {UpdateType} from WTelegram.Client", updates.GetType().FullName);
                if (updates is UpdatesBase updatesBase)
                {
                    await HandleUpdatesBaseAsync(updatesBase);
                }
                else
                {
                    _logger.LogWarning("[USER_API_ON_UPDATES_TRIGGERED] Received 'updates' that is NOT UpdatesBase. Type: {UpdateType}", updates.GetType().FullName);
                }
            };

            _cacheCleanupTimer = new System.Threading.Timer(
                      CacheCleanup,
                      null,
                      (int)_cacheCleanupInterval.TotalMilliseconds,
                      (int)_cacheCleanupInterval.TotalMilliseconds
                  );
            _logger.LogInformation("Started cache cleanup timer with interval {IntervalMinutes} minutes.", _cacheCleanupInterval.TotalMinutes);

            // NEW: Initialize the Polly ResiliencePipeline
            _resiliencePipeline = new ResiliencePipelineBuilder()
                            .AddRetry(new RetryStrategyOptions
                            {
                                // Define which exceptions should trigger a retry.
                                ShouldHandle = new PredicateBuilder()
                                    // 1. Handle common network connectivity issues.
                                    .Handle<HttpRequestException>()
                                    // 2. Handle specific WTelegram.Client RPC exceptions that indicate a temporary issue.
                                    .Handle<RpcException>(rpcEx =>
                                    {
                                        // a) Server-side errors (often 5xx range indicate temporary server problems)
                                        if (rpcEx.Code >= 500 && rpcEx.Code < 600)
                                        {
                                            // Logging here is simplified as `args.Context.OperationKey` is not directly
                                            // available in PredicateBuilder's inner lambda.
                                            _logger.LogWarning("Polly: Retrying RPC error {RpcCode} ({RpcMessage}) as it's a server-side error.",
                                                rpcEx.Code, rpcEx.Message);
                                            return true;
                                        }

                                        // b) Rate limiting or flood wait errors: Telegram explicitly tells you to wait.
                                        if (rpcEx.Message.Contains("TOO_MANY_REQUESTS", StringComparison.OrdinalIgnoreCase) ||
                                            rpcEx.Message.Contains("FLOOD_WAIT_", StringComparison.OrdinalIgnoreCase))
                                        {
                                            // Extract the wait duration if present, and decide if it's too long for a retry.
                                            if (rpcEx.Message.StartsWith("FLOOD_WAIT_") &&
                                                int.TryParse(rpcEx.Message.Substring("FLOOD_WAIT_".Length), out int seconds))
                                            {
                                                if (seconds > _retryDelays[_retryDelays.Length - 1].TotalSeconds * 2)
                                                {
                                                    _logger.LogWarning("Polly: Encountered a FLOOD_WAIT of {Seconds}s, which greatly exceeds max configured retry delay. Aborting retries for this specific error.",
                                                        seconds);
                                                    return false; // Do not retry this specific, excessively long flood wait.
                                                }
                                                _logger.LogWarning("Polly: Retrying FLOOD_WAIT of {Seconds}s.", seconds);
                                                return true; // Retry short to medium flood waits.
                                            }
                                            _logger.LogWarning("Polly: Retrying TOO_MANY_REQUESTS or unknown FLOOD_WAIT type.");
                                            return true; // General TOO_MANY_REQUESTS or unparseable FLOOD_WAIT
                                        }
                                        // c) Other specific transient errors can be added here if identified during testing.
                                        return false; // By default, don't retry other RpcExceptions (e.g., PERMISSION_DENIED, USER_NOT_FOUND, invalid peer)
                                    }),
                                // Configure delays between retries. This ensures we don't bombard the server.
                                DelayGenerator = args =>
                                {
                                    var retryAttempt = args.AttemptNumber; // 0-indexed attempt number (0, 1, 2, ...)
                                                                           // Ensure we don't go out of bounds of our _retryDelays array.
                                    if (retryAttempt < _retryDelays.Length)
                                    {
                                        var delay = _retryDelays[retryAttempt];
                                        // Correctly access OperationKey from `args.Context`.
                                        _logger.LogWarning("Polly Retry: Attempt {AttemptNumber} for operation '{OperationKey}'. Delaying for {Delay}ms due to {ExceptionType}.",
                                            retryAttempt + 1, args.Context.OperationKey, delay.TotalMilliseconds, args.Outcome.Exception?.GetType().Name ?? "N/A");

                                        // FIX for CS0121 ambiguity for the non-null case:
                                        // Use ValueTask.FromResult<T>(value) for an already completed ValueTask from a value.
                                        return ValueTask.FromResult<TimeSpan?>(delay);
                                    }
                                    // Correctly access OperationKey from `args.Context`.
                                    _logger.LogWarning("Polly Retry: Attempt {AttemptNumber} for operation '{OperationKey}'. Max retries ({MaxRetries}) reached. No further retries will be attempted.",
                                        retryAttempt + 1, args.Context.OperationKey, _retryDelays.Length);

                                    // FIX for CS0121 ambiguity for the null case:
                                    // Use ValueTask.FromResult<T>(null) to unambiguously create a completed ValueTask with a null result.
                                    return ValueTask.FromResult<TimeSpan?>(null); // Signal to Polly that no more retries should be attempted.
                                },
                                MaxRetryAttempts = _retryDelays.Length, // This sets the maximum number of *retries* (after the initial attempt).
                                                                        // If _retryDelays has 6 entries, this means 6 retries + 1 initial attempt = 7 total attempts.
                                OnRetry = args =>
                                {
                                    // Correctly access OperationKey from `args.Context`.
                                    _logger.LogWarning(args.Outcome.Exception,
                                        "Polly OnRetry: Operation '{OperationKey}' failed on attempt {AttemptNumber} with exception {ExceptionType}. Next retry will be after delay.",
                                        args.Context.OperationKey, args.AttemptNumber + 1, args.Outcome.Exception?.GetType().Name ?? "Unknown");
                                    return default; // Return default(ValueTask) for synchronous callbacks.
                                },
                            })
                            .Build();
        }
        #endregion

        #region Configuration Provider
        private string? ConfigProvider(string key)
        {
            return key switch
            {
                "api_id" => _settings.ApiId.ToString(),
                "api_hash" => _settings.ApiHash,
                "phone_number" => _settings.PhoneNumber,
                "session_pathname" => Path.Combine(AppContext.BaseDirectory, _settings.SessionPath ?? "telegram_user.session"),
                "verification_code" => AskCode("Telegram asks for verification code: ", _settings.VerificationCodeSource),
                "password" => AskCode("Telegram asks for 2FA password (if enabled): ", _settings.TwoFactorPasswordSource),
                _ => null
            };
        }

        /// <summary>
        /// Asks the user for input based on the provided question and source method.
        /// Currently, only "console" source is supported.
        /// Handles potential issues with console availability and logs interactions.
        /// </summary>
        /// <param name="question">The question to ask the user. 
        /// IMPORTANT: If this question might contain sensitive details itself, consider omitting it from logs or sanitizing.</param>
        /// <param name="sourceMethod">The method/source from which the code is being requested (e.g., "console", "api_callback").
        /// Defaults to "console" if null or whitespace.</param>
        /// <returns>The user's input as a string, or null if input could not be obtained, was cancelled, or source is not implemented.</returns>
        /// <remarks>
        /// Security Considerations:
        /// - Logging `question`: Be cautious if `question` itself could contain sensitive information passed from the caller.
        /// - Input validation: The returned code is not validated here; the caller is responsible for validating the format/content.
        ///
        /// Robustness:
        /// - Checks for console availability before attempting to read from it.
        /// - Handles potential exceptions during console read operations.
        ///
        /// Extensibility:
        /// - The `sourceMethod` parameter allows for future expansion to support other input sources,
        ///   though only "console" is implemented.
        /// </remarks>
        private string? AskCode(string question, string? sourceMethod)
        {
            #region Parameter Validation and Defaulting
            // Robustness: Ensure question is not null to prevent issues with Trim().
            // If question can legitimately be null/empty, this check needs adjustment based on business logic.
            if (string.IsNullOrWhiteSpace(question))
            {
                _logger.LogWarning("AskCode called with an empty or null question. SourceMethod: {SourceMethod}", sourceMethod ?? "Unknown");
                // Decide on behavior: return null, throw ArgumentException, etc.
                // Returning null seems consistent with other failure paths.
                return null;
            }

            string effectiveSourceMethod = string.IsNullOrWhiteSpace(sourceMethod) ? "console" : sourceMethod.Trim();
            string trimmedQuestion = question.Trim(); // Trim once for consistent use.
            #endregion

            #region Logging Initial Request
            // Security Note: `trimmedQuestion` is logged. If it can contain sensitive data,
            // this logging should be re-evaluated (e.g., log a generic message or a sanitized version).
            // For instance, if question is "Enter your password for account X:", logging is risky.
            // If question is "Enter 2FA code:", logging `trimmedQuestion` is acceptable.
            _logger.LogInformation("WTC Input Request: \"{QuestionDisplay}\" (Source: {SourceMethod})",
                                   trimmedQuestion.Length > 50 ? trimmedQuestion.Substring(0, 50) + "..." : trimmedQuestion, // Log a truncated question for brevity
                                   effectiveSourceMethod);
            #endregion

            // Currently, only "console" input is supported.
            if (effectiveSourceMethod.Equals("console", StringComparison.OrdinalIgnoreCase))
            {
                #region Console Input Handling
                try
                {
                    // Robustness: Check if a console is actually available for input.
                    // `Console.IsInputRedirected` can be true if input comes from a file/pipe.
                    // `Console.KeyAvailable` (with a try-catch for InvalidOperationException)
                    // can indicate if an interactive console is present.
                    // A simpler check is if WindowHeight > 0, but not foolproof for all environments.
                    // For services, `Environment.UserInteractive` is a good indicator.
                    if (!Environment.UserInteractive) // A common check for non-interactive environments like services.
                    {
                        _logger.LogWarning("AskCode (console): Application is not running in an interactive user environment. Cannot prompt for \"{QuestionDisplay}\".",
                                           trimmedQuestion.Length > 50 ? trimmedQuestion.Substring(0, 50) + "..." : trimmedQuestion);
                        return null;
                    }
                    // Additionally, check for actual console input capability.
                    // This might throw InvalidOperationException if no console is attached.
                    try
                    {
                        // A quick check to see if we can even attempt to read a key.
                        // This doesn't consume the key. If this throws, ReadLine will likely also fail or block.
                        if (Console.IsInputRedirected)
                        {
                            _logger.LogInformation("AskCode (console): Input is redirected. Reading from redirected input for \"{QuestionDisplay}\".",
                                                   trimmedQuestion.Length > 50 ? trimmedQuestion.Substring(0, 50) + "..." : trimmedQuestion);
                        }
                        // else: Standard interactive console expected.
                    }
                    catch (InvalidOperationException ioExConsoleCheck)
                    {
                        _logger.LogWarning(ioExConsoleCheck, "AskCode (console): No console available or console operation failed during pre-check for \"{QuestionDisplay}\".",
                                           trimmedQuestion.Length > 50 ? trimmedQuestion.Substring(0, 50) + "..." : trimmedQuestion);
                        return null;
                    }


                    // Display the question to the console.
                    // Using Console.Out to be explicit, though Console.Write often defaults to this.
                    Console.Out.Write(trimmedQuestion + " "); // Add a space for better user experience before they type.

                    // Read the user's input.
                    // Performance: Console.ReadLine() is blocking. This is expected for interactive input.
                    string? userInput = Console.ReadLine();

                    // Security & Logging: Do NOT log the `userInput` if it's sensitive (e.g., password, token).
                    // Here, we assume the "code" might be like a 2FA code, which is less sensitive *after* use,
                    // but still better not to log values. We'll log receipt and length if needed.
                    if (userInput != null)
                    {
                        _logger.LogInformation("WTC Input Received: User provided input of length {InputLength} for \"{QuestionDisplay}\" from console.",
                                               userInput.Length,
                                               trimmedQuestion.Length > 50 ? trimmedQuestion.Substring(0, 50) + "..." : trimmedQuestion);
                        // Consider if empty input should be treated as null or an empty string.
                        // `Console.ReadLine()` returns an empty string if user just presses Enter.
                        // If an empty string is a valid "code", return it. Otherwise, convert to null or handle.
                        // For this generic `AskCode`, returning the direct input (even if empty) seems reasonable.
                        return userInput;
                    }
                    else
                    {
                        // This case (userInput is null) typically occurs if Ctrl+Z (EOF) is pressed on Windows,
                        // or Ctrl+D on Linux/macOS, without typing anything, or if input stream ends.
                        _logger.LogWarning("WTC Input Received: User cancelled input (EOF) or input stream ended for \"{QuestionDisplay}\" from console.",
                                           trimmedQuestion.Length > 50 ? trimmedQuestion.Substring(0, 50) + "..." : trimmedQuestion);
                        return null; // Indicate no input or cancellation.
                    }
                }
                catch (IOException ioEx) // Handles errors like "Input/output error" if console is detached during read.
                {
                    _logger.LogError(ioEx, "WTC Input Error: An IOException occurred while trying to read from console for \"{QuestionDisplay}\".",
                                     trimmedQuestion.Length > 50 ? trimmedQuestion.Substring(0, 50) + "..." : trimmedQuestion);
                    return null;
                }
                catch (OperationCanceledException ocEx) // Though Console.ReadLine itself doesn't directly accept CancellationToken.
                {
                    // This might be relevant if the console host environment raises it for some reason, though rare for direct Console.ReadLine.
                    _logger.LogWarning(ocEx, "WTC Input Warning: Console read operation was ostensibly cancelled for \"{QuestionDisplay}\".",
                                       trimmedQuestion.Length > 50 ? trimmedQuestion.Substring(0, 50) + "..." : trimmedQuestion);
                    return null;
                }
                catch (Exception ex) // Catch-all for any other unexpected issues.
                {
                    _logger.LogError(ex, "WTC Input Error: An unexpected error occurred during console input for \"{QuestionDisplay}\".",
                                     trimmedQuestion.Length > 50 ? trimmedQuestion.Substring(0, 50) + "..." : trimmedQuestion);
                    // Do not rethrow from here unless AskCode is critical and failure should halt the caller significantly.
                    // Typically for input prompts, returning null to indicate failure is preferred.
                    return null;
                }
                #endregion
            }
            else // Source method is not "console"
            {
                #region Non-Console Source Handling (Placeholder)
                // This part handles source methods other than "console".
                // Currently, it's a placeholder indicating non-implementation.
                _logger.LogWarning("WTC Input Request: Source method '{SourceMethod}' is not implemented for question \"{QuestionDisplay}\". Returning null.",
                                   effectiveSourceMethod,
                                   trimmedQuestion.Length > 50 ? trimmedQuestion.Substring(0, 50) + "..." : trimmedQuestion);
                // Future: Could involve callback mechanisms, message queues, or other IPC for different `sourceMethod` types.
                // Example: if (sourceMethod == "gui_prompt") { /* code to show GUI dialog */ }
                return null;
                #endregion
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

            // Clean _userCacheWithExpiry
            // Use ToArray() to avoid "collection was modified" error if entries are removed during iteration
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
            _logger.LogInformation("Cache cleanup: Removed {UsersRemovedCount} expired users from _userCacheWithExpiry. Current count: {CurrentUserCacheCount}", usersRemoved, _userCacheWithExpiry.Count);

            // Clean _chatCacheWithExpiry
            int chatsRemoved = 0;
            // Use ToArray() for safe iteration
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
            _logger.LogInformation("Cache cleanup: Removed {ChatsRemovedCount} expired chats from _chatCacheWithExpiry. Current count: {CurrentChatCacheCount}", chatsRemoved, _chatCacheWithExpiry.Count);
        }

        #endregion

        #region WTelegramClient Update Handler
        private Task HandleUpdatesBaseAsync(UpdatesBase updatesBase)
        {
            // --- Start of Method Logging ---
            _logger.LogDebug("HandleUpdatesBaseAsync: Received UpdatesBase of type {UpdatesBaseType}. Update content (partial): {UpdatesBaseContent}",
                updatesBase.GetType().Name,
                TruncateString(updatesBase.ToString(), 200));

            // --- User/Chat Collection Logic ---
            int initialUserCacheCount = _userCache.Count;
            int initialChatCacheCount = _chatCache.Count;

            updatesBase.CollectUsersChats(_userCache, _chatCache);

            int usersAddedToCache = _userCache.Count - initialUserCacheCount;
            int chatsAddedToCache = _chatCache.Count - initialChatCacheCount;

            if (usersAddedToCache > 0 || chatsAddedToCache > 0)
            {
                _logger.LogDebug("HandleUpdatesBaseAsync: Collected users/chats. Users added: {UsersAddedCount}, Chats added: {ChatsAddedCount}. Total users in cache: {TotalUserCache}, Total chats in cache: {TotalChatCache}",
                    usersAddedToCache, chatsAddedToCache, _userCache.Count, _chatCache.Count);
            }
            else
            {
                _logger.LogTrace("HandleUpdatesBaseAsync: No new users or chats collected from this UpdatesBase.");
            }

            // Update the expiry caches with recently collected users/chats (from approved Item 5)
            foreach (var userEntry in _userCache)
            {
                _userCacheWithExpiry[userEntry.Key] = (userEntry.Value, DateTime.UtcNow.Add(_cacheExpiration));
            }
            foreach (var chatEntry in _chatCache)
            {
                _chatCacheWithExpiry[chatEntry.Key] = (chatEntry.Value, DateTime.UtcNow.Add(_cacheExpiration));
            }

            List<Update> updatesToDispatch = new List<Update>();

            // --- Handle Different Update Types ---
            if (updatesBase is Updates updatesContainer && updatesContainer.updates != null)
            {
                _logger.LogDebug("HandleUpdatesBaseAsync: Processing 'Updates' container with {UpdateCount} inner updates.", updatesContainer.updates.Length);
                updatesToDispatch.AddRange(updatesContainer.updates);
            }
            else if (updatesBase is UpdatesCombined updatesCombinedContainer && updatesCombinedContainer.updates != null)
            {
                _logger.LogDebug("HandleUpdatesBaseAsync: Processing 'UpdatesCombined' container with {UpdateCount} inner updates.", updatesCombinedContainer.updates.Length);
                updatesToDispatch.AddRange(updatesCombinedContainer.updates);
            }
            // --- REWRITTEN: Message reconstruction for UpdateShortMessage ---
            else if (updatesBase is UpdateShortMessage usm) // usm declared here, correctly scoped
            {
                _logger.LogDebug("HandleUpdatesBaseAsync: Processing 'UpdateShortMessage'. MsgID: {MessageId}, UserID: {UserId}, PTS: {Pts}",
                    usm.id, usm.user_id, usm.pts);
                Peer userPeer; // userPeer declared here, correctly scoped
                if (_userCache.TryGetValue(usm.user_id, out var cachedUser))
                {
                    userPeer = new PeerUser { user_id = cachedUser.id };
                    _logger.LogTrace("HandleUpdatesBaseAsync (USM): User {UserId} found in cache.", usm.user_id);
                }
                else
                {
                    userPeer = new PeerUser { user_id = usm.user_id };
                    _logger.LogWarning("HandleUpdatesBaseAsync (USM): User {UserId} from UpdateShortMessage not in cache, using minimal PeerUser.", usm.user_id);
                }
                _logger.LogTrace("HandleUpdatesBaseAsync (USM): Constructing Message for MsgID {MessageId}. FromID: {FromId}, PeerID: {PeerId}, Message: {MessageContent}",
                    usm.id, usm.user_id, usm.user_id, TruncateString(usm.message, 50));

                // Construct Message using fields that are DEFINITELY available on your TL.Message
                // Properties like 'media', 'grouped_id', 'random_id' that caused errors are now handled defensively.
                var msg = new Message
                {
                    flags = 0,
                    id = usm.id,
                    peer_id = userPeer,
                    from_id = userPeer,
                    message = usm.message,
                    date = usm.date,
                    entities = usm.entities,
                    media = null,       // 'media' is not directly on UpdateShortMessage; set to null.
                    reply_to = usm.reply_to, // Check if reply_to is truly available, if not, it will be null or needs default here. Assuming it is.
                    fwd_from = usm.fwd_from, // Check if fwd_from is truly available, if not, it will be null or needs default here. Assuming it is.
                    via_bot_id = usm.via_bot_id, // If still error, set to 0. Assuming it is present.
                    ttl_period = usm.ttl_period, // If still error, set to 0. Assuming it is present.
                    // grouped_id field from UpdateShortMessage is typically only available in a different, full Update.
                    // For safety, assume default 0 or check for its existence in your TL library's UpdateShortMessage type.
                    // Here setting to 0 assuming TL.Message.grouped_id is a non-nullable long.
                    grouped_id = 0,
                    // 'random_id' property does not belong here as it's typically for *sending* messages via RPC.
                };

                var uf = usm.flags; // uf declared here, correctly scoped
                if (uf.HasFlag(UpdateShortMessage.Flags.out_)) msg.flags |= Message.Flags.out_;
                if (uf.HasFlag(UpdateShortMessage.Flags.mentioned)) msg.flags |= Message.Flags.mentioned;
                if (uf.HasFlag(UpdateShortMessage.Flags.silent)) msg.flags |= Message.Flags.silent;
                if (uf.HasFlag(UpdateShortMessage.Flags.media_unread)) msg.flags |= Message.Flags.media_unread;
                // 'noforwards' flag is typically not available on UpdateShortMessage.Flags
                // if (uf.HasFlag(UpdateShortMessage.Flags.noforwards)) msg.flags |= Message.Flags.noforwards;

                updatesToDispatch.Add(new UpdateNewMessage { message = msg, pts = usm.pts, pts_count = usm.pts_count });
                _logger.LogDebug("HandleUpdatesBaseAsync (USM): Added UpdateNewMessage for MsgID {MessageId} to dispatch list.", usm.id);
            }
            // --- REWRITTEN: Message reconstruction for UpdateShortChatMessage ---
            else if (updatesBase is UpdateShortChatMessage uscm) // uscm declared here, correctly scoped
            {
                _logger.LogDebug("HandleUpdatesBaseAsync: Processing 'UpdateShortChatMessage'. MsgID: {MessageId}, FromID: {FromId}, ChatID: {ChatId}, PTS: {Pts}",
                    uscm.id, uscm.from_id, uscm.chat_id, uscm.pts);
                Peer chatPeer; // chatPeer declared here, correctly scoped
                if (_chatCache.TryGetValue(uscm.chat_id, out var cachedChat))
                {
                    if (cachedChat is Channel channel) { chatPeer = new PeerChannel { channel_id = channel.id }; }
                    else { chatPeer = new PeerChat { chat_id = cachedChat.ID }; }
                    _logger.LogTrace("HandleUpdatesBaseAsync (USCM): Chat {ChatId} (Type: {ChatType}) found in cache.", uscm.chat_id, cachedChat.GetType().Name);
                }
                else
                {
                    chatPeer = new PeerChat { chat_id = uscm.chat_id };
                    _logger.LogWarning("HandleUpdatesBaseAsync (USCM): Chat {ChatId} from UpdateShortChatMessage not in cache, using minimal PeerChat.", uscm.chat_id);
                }
                Peer? fromPeer = null; // fromPeer declared here, correctly scoped
                if (_userCache.TryGetValue(uscm.from_id, out var cachedFromUser))
                {
                    fromPeer = new PeerUser { user_id = cachedFromUser.id };
                    _logger.LogTrace("HandleUpdatesBaseAsync (USCM): Sender User {UserId} found in cache.", uscm.from_id);
                }
                else
                {
                    fromPeer = new PeerUser { user_id = uscm.from_id };
                    _logger.LogWarning("HandleUpdatesBaseAsync (USCM): Sender User {UserId} from UpdateShortChatMessage not in cache, using minimal PeerUser.", uscm.from_id);
                }
                long targetChatIdForLog = 0;
                if (chatPeer is PeerUser pu) targetChatIdForLog = pu.user_id;
                else if (chatPeer is PeerChat pc) targetChatIdForLog = pc.chat_id;
                else if (chatPeer is PeerChannel pch) targetChatIdForLog = pch.channel_id;
                _logger.LogTrace("HandleUpdatesBaseAsync (USCM): Constructing Message for MsgID {MessageId}. FromID: {FromId}, TargetChatID: {TargetChatId}, Message: {MessageContent}",
                   uscm.id, uscm.from_id, targetChatIdForLog, TruncateString(uscm.message, 50));

                // Construct Message using fields that are DEFINITELY available on your TL.Message
                // Properties like 'media', 'grouped_id', 'random_id' that caused errors are now handled defensively.
                var msg = new Message
                {
                    flags = 0,
                    id = uscm.id,
                    peer_id = chatPeer,
                    from_id = fromPeer,
                    message = uscm.message,
                    date = uscm.date,
                    entities = uscm.entities,
                    media = null,       // 'media' is not directly on UpdateShortChatMessage; set to null.
                    reply_to = uscm.reply_to, // Check if reply_to is truly available, if not, it will be null or needs default here. Assuming it is.
                    fwd_from = uscm.fwd_from, // Check if fwd_from is truly available, if not, it will be null or needs default here. Assuming it is.
                    via_bot_id = uscm.via_bot_id, // If still error, set to 0. Assuming it is present.
                    ttl_period = uscm.ttl_period, // If still error, set to 0. Assuming it is present.
                    // grouped_id field from UpdateShortChatMessage is typically only available in a different, full Update.
                    // For safety, assume default 0 or check for its existence in your TL library's UpdateShortChatMessage type.
                    // Here setting to 0 assuming TL.Message.grouped_id is a non-nullable long.
                    grouped_id = 0,
                    // 'random_id' property does not belong here.
                };

                var uf = uscm.flags; // uf declared here, correctly scoped
                if (uf.HasFlag(UpdateShortChatMessage.Flags.out_)) msg.flags |= Message.Flags.out_;
                if (uf.HasFlag(UpdateShortChatMessage.Flags.mentioned)) msg.flags |= Message.Flags.mentioned;
                if (uf.HasFlag(UpdateShortChatMessage.Flags.silent)) msg.flags |= Message.Flags.silent;
                if (uf.HasFlag(UpdateShortChatMessage.Flags.media_unread)) msg.flags |= Message.Flags.media_unread;
                // 'noforwards' flag is typically not available on UpdateShortChatMessage.Flags
                // if (uf.HasFlag(UpdateShortChatMessage.Flags.noforwards)) msg.flags |= Message.Flags.noforwards;

                updatesToDispatch.Add(new UpdateNewMessage { message = msg, pts = uscm.pts, pts_count = uscm.pts_count });
                _logger.LogDebug("HandleUpdatesBaseAsync (USCM): Added UpdateNewMessage for MsgID {MessageId} to dispatch list.", uscm.id);
            }

            // --- Dispatch Updates ---
            if (updatesToDispatch.Any())
            {
                _logger.LogInformation("HandleUpdatesBaseAsync: Dispatching {DispatchCount} TL.Update object(s) via OnCustomUpdateReceived.", updatesToDispatch.Count);
                foreach (var update in updatesToDispatch)
                {
                    _logger.LogTrace("HandleUpdatesBaseAsync: Dispatching update of type {UpdateType}. Update content (partial): {UpdateContent}",
                        update.GetType().Name, TruncateString(update.ToString(), 100));
                    try
                    {
                        OnCustomUpdateReceived?.Invoke(update); // This remains a synchronous call
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "HandleUpdatesBaseAsync: Exception during OnCustomUpdateReceived invocation for update type {UpdateType}. Update content (partial): {UpdateContent}",
                            update.GetType().Name, TruncateString(update.ToString(), 100));
                    }
                }
                _logger.LogInformation("HandleUpdatesBaseAsync: Finished dispatching {DispatchCount} TL.Update object(s).", updatesToDispatch.Count);
            }
            else
            {
                _logger.LogDebug("HandleUpdatesBaseAsync: No TL.Update objects to dispatch from this UpdatesBase of type {UpdatesBaseType}.", updatesBase.GetType().Name);
            }

            return Task.CompletedTask;
        }


        // Helper function to truncate strings for logging to avoid overly long log messages
        private string TruncateString(string? str, int maxLength)
        {
            if (string.IsNullOrEmpty(str)) return "[null_or_empty]";
            return str.Length <= maxLength ? str : str.Substring(0, maxLength) + "...";
        }
        #endregion

        #region ITelegramUserApiClient Implementation
        public async Task ConnectAndLoginAsync(CancellationToken cancellationToken = default)
        {
            await _connectionLock.WaitAsync(cancellationToken);
            try
            {
                if (_client != null)
                {
                    _logger.LogInformation("Disposing existing client to force new connection...");
                    _client.OnUpdates -= HandleUpdatesBaseAsync;
                    _client.Dispose();
                    _client = null;
                }

                _client = new WTelegram.Client(ConfigProvider);
                _client.OnUpdates += async updates =>
                {
                    _logger.LogCritical("[USER_API_ON_UPDATES_TRIGGERED] Raw updates object of type: {UpdateType} from WTelegram.Client", updates.GetType().FullName);
                    if (updates is UpdatesBase updatesBase)
                    {
                        await HandleUpdatesBaseAsync(updatesBase);
                    }
                    else
                    {
                        _logger.LogWarning("[USER_API_ON_UPDATES_TRIGGERED] Received 'updates' that is NOT UpdatesBase. Type: {UpdateType}", updates.GetType().FullName);
                    }
                };

                _logger.LogInformation("Connecting User API (Session: {SessionPath})...", _settings.SessionPath);
                try
                {
                    // Fix for CS1739: Remove named parameters `context:` and `cancellationToken:`.
                    User loggedInUser = await _resiliencePipeline.ExecuteAsync(async (context, token) =>
                        await _client!.LoginUserIfNeeded(), // _client is guaranteed non-null here as it's created above.
                        new Context(nameof(ConnectAndLoginAsync)), // Passed as positional argument.
                        cancellationToken); // Passed as positional argument.

                    _logger.LogInformation("User API Logged in: {User}", loggedInUser?.ToString());

                    // Fix for CS1739: Remove named parameters `context:` and `cancellationToken:`.
                    var dialogs = await _resiliencePipeline.ExecuteAsync(async (context, token) =>
                        await _client!.Messages_GetAllDialogs(), // _client is guaranteed non-null.
                        new Context(nameof(_client.Messages_GetAllDialogs)), // Operation key for logging.
                        cancellationToken); // Pass cancellation token from method parameter.

                    dialogs.CollectUsersChats(_userCache, _chatCache);

                    // ... (existing cache update logic)
                    int usersTransferred = 0;
                    if (_userCache != null && _userCache.Any())
                    {
                        _logger.LogDebug("Refreshing user cache. Found {UserCacheCount} users in simple cache.", _userCache.Count);
                        foreach (var userEntry in _userCache)
                        {
                            _userCacheWithExpiry[userEntry.Key] = (userEntry.Value, DateTime.UtcNow.Add(_cacheExpiration));
                            usersTransferred++;
                            _logger.LogTrace("Transferred user {UserId} to expiry cache. New expiry: {ExpiryTime}",
                                userEntry.Key, _userCacheWithExpiry[userEntry.Key].Item2);
                        }
                        _logger.LogInformation("Successfully transferred {UsersTransferredCount} users to expiry cache.", usersTransferred);
                    }
                    else
                    {
                        _logger.LogInformation("User cache (_userCache) is null or empty. No users to transfer.");
                    }

                    int chatsTransferred = 0;
                    if (_chatCache != null && _chatCache.Any())
                    {
                        _logger.LogDebug("Refreshing chat cache. Found {ChatCacheCount} chats in simple cache.", _chatCache.Count);
                        foreach (var chatEntry in _chatCache)
                        {
                            _chatCacheWithExpiry[chatEntry.Key] = (chatEntry.Value, DateTime.UtcNow.Add(_cacheExpiration));
                            chatsTransferred++;
                            _logger.LogTrace("Transferred chat {ChatId} to expiry cache. New expiry: {ExpiryTime}",
                                chatEntry.Key, _chatCacheWithExpiry[chatEntry.Key].Item2);
                        }
                        _logger.LogInformation("Successfully transferred {ChatsTransferredCount} chats to expiry cache.", chatsTransferred);
                    }
                    else
                    {
                        _logger.LogInformation("Chat cache (_chatCache) is null or empty. No chats to transfer.");
                    }
                }
                catch (RpcException e) when (e.Code == 401 && (e.Message.Contains("SESSION_PASSWORD_NEEDED") || e.Message.Contains("account_password_input_needed")))
                {
                    _logger.LogWarning("User API: 2FA needed. Password requested via ConfigProvider.");
                    User loggedInUser = await _client!.LoginUserIfNeeded();
                    _logger.LogInformation("User API Logged in with 2FA: {User}", loggedInUser?.ToString());
                }
                catch (RpcException e) when (e.Message.StartsWith("PHONE_MIGRATE_"))
                {
                    if (int.TryParse(e.Message.Split('_').Last(), out int dcNumber))
                    {
                        _logger.LogWarning("User API: Phone number needs to be migrated to DC{DCNumber}. Reconnecting...", dcNumber);

                        if (_client != null)
                        {
                            _client.OnUpdates -= HandleUpdatesBaseAsync;
                            _client.Dispose();
                            _client = null;
                        }

                        _logger.LogInformation("Creating new WTelegram.Client instance for DC{DCNumber}.", dcNumber);
                        _client = new WTelegram.Client(ConfigProvider);

                        _client.OnUpdates += async updates =>
                        {
                            _logger.LogCritical("[USER_API_ON_UPDATES_TRIGGERED_DC_MIGRATE] Raw updates object of type: {UpdateType} from WTelegram.Client (after DC Migrate)", updates.GetType().FullName);
                            if (updates is UpdatesBase updatesBase)
                            {
                                await HandleUpdatesBaseAsync(updatesBase);
                            }
                            else
                            {
                                _logger.LogWarning("[USER_API_ON_UPDATES_TRIGGERED_DC_MIGRATE] Received 'updates' that is NOT UpdatesBase. Type: {UpdateType}", updates.GetType().FullName);
                            }
                        };

                        _logger.LogInformation("Re-attempting login after DC migration to DC{DCNumber}...", dcNumber);
                        User loggedInUser = await _client.LoginUserIfNeeded();
                        _logger.LogInformation("User API Logged in on DC{DCNumber}: {User}", dcNumber, loggedInUser?.ToString());

                        // Fix for CS1739: Remove named parameters `context:` and `cancellationToken:`.
                        var dialogs = await _resiliencePipeline.ExecuteAsync(async (context, token) =>
                            await _client!.Messages_GetAllDialogs(),
                            new Context(nameof(_client.Messages_GetAllDialogs) + "_PostMigrate"),
                            cancellationToken);
                        dialogs.CollectUsersChats(_userCache, _chatCache);

                        int usersTransferredAfterMigrate = 0;
                        if (_userCache != null && _userCache.Any())
                        {
                            _logger.LogDebug("Refreshing user cache post-migration. Found {UserCacheCount} users in simple cache.", _userCache.Count);
                            foreach (var userEntry in _userCache)
                            {
                                _userCacheWithExpiry[userEntry.Key] = (userEntry.Value, DateTime.UtcNow.Add(_cacheExpiration));
                                usersTransferredAfterMigrate++;
                            }
                            _logger.LogInformation("Successfully transferred {UsersTransferredCount} users to expiry cache post-migration.", usersTransferredAfterMigrate);
                        }

                        int chatsTransferredAfterMigrate = 0;
                        if (_chatCache != null && _chatCache.Any())
                        {
                            _logger.LogDebug("Refreshing chat cache post-migration. Found {ChatCacheCount} chats in simple cache.", _chatCache.Count);
                            foreach (var chatEntry in _chatCache)
                            {
                                _chatCacheWithExpiry[chatEntry.Key] = (chatEntry.Value, DateTime.UtcNow.Add(_cacheExpiration));
                                chatsTransferredAfterMigrate++;
                            }
                            _logger.LogInformation("Successfully transferred {ChatsTransferredCount} chats to expiry cache post-migration.", chatsTransferredAfterMigrate);
                        }
                    }
                    else
                    {
                        _logger.LogError("User API: Failed to parse DC number from migration error: {ErrorMessage}", e.Message);
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "User API: Failed to connect/login.");
                    throw;
                }
            }
            finally
            {
                try
                {
                    _connectionLock.Release();
                }
                catch (ObjectDisposedException)
                {
                    _connectionLock = new SemaphoreSlim(1, 1);
                }
            }
        }



        // Assuming _logger is an ILogger instance available in the class
        // Assuming _client is an instance of WTelegram.Client
        // Assuming _messageCache is an instance of IMemoryCache or a similar caching mechanism
        // Assuming _cacheOptions is an instance of MemoryCacheEntryOptions or similar

        public async Task<Messages_MessagesBase?> GetMessagesAsync(InputPeer peer, params int[] messageIds)
        {
            if (_client == null)
            {
                _logger.LogError("GetMessagesAsync: Telegram client (_client) is not initialized. Cannot get messages.");
                throw new InvalidOperationException("Telegram API client is not initialized.");
            }
            if (peer == null) throw new ArgumentNullException(nameof(peer), "InputPeer cannot be null for getting messages.");
            if (messageIds == null || !messageIds.Any())
                throw new ArgumentException("Message IDs list cannot be null or empty for getting messages.", nameof(messageIds));


            string messageIdsString = messageIds.Length > 5
                ? $"{string.Join(", ", messageIds.Take(5))}... (Total: {messageIds.Length})"
                : string.Join(", ", messageIds);

            long peerIdForLog = GetPeerIdForLog(peer);
            string peerTypeForLog = peer.GetType().Name;

            _logger.LogDebug("GetMessagesAsync: Attempting to get messages for Peer (Type: {PeerType}, LoggedID: {PeerId}), Message IDs: [{MessageIdsArray}]",
                peerTypeForLog,
                peerIdForLog,
                messageIdsString);

            try
            {
                string cacheKeySuffix = peer is InputPeerUser p_u ? $"u{p_u.user_id}" :
                                        peer is InputPeerChat p_c ? $"c{p_c.chat_id}" :
                                        peer is InputPeerChannel p_ch ? $"ch{p_ch.channel_id}" :
                                        peer is InputPeerSelf ? "self" :
                                        $"other{peer.GetHashCode()}";

                var cacheKey = $"msgs_peer{cacheKeySuffix}_ids{string.Join("_", messageIds.OrderBy(id => id).Select(id => id.ToString()))}";
                _logger.LogTrace("GetMessagesAsync: Generated cache key: {CacheKey}", cacheKey);

                if (_messageCache.TryGetValue(cacheKey, out Messages_MessagesBase? cachedMessages))
                {
                    int cachedMessageCount = (cachedMessages as Messages_Messages)?.messages?.Length ??
                                             (cachedMessages as Messages_MessagesSlice)?.messages?.Length ??
                                             (cachedMessages as Messages_ChannelMessages)?.messages?.Length ?? 0;

                    _logger.LogDebug("GetMessagesAsync: Cache HIT for key {CacheKey}. Returning {CachedMessageCount} cached messages for Peer (Type: {PeerType}, LoggedID: {PeerId}).",
                        cacheKey, cachedMessageCount, peerTypeForLog, peerIdForLog);
                    return cachedMessages;
                }
                else
                {
                    _logger.LogInformation("GetMessagesAsync: Cache MISS for key {CacheKey}. Fetching from API for Peer (Type: {PeerType}, LoggedID: {PeerId}), Message IDs: [{MessageIdsArray}]",
                        cacheKey, peerTypeForLog, peerIdForLog, messageIdsString);
                }

                var msgIdObjects = messageIds.Select(id => (InputMessage)new InputMessageID { id = id }).ToArray();
                _logger.LogDebug("GetMessagesAsync: Calling _client.Messages_GetMessages with {InputMessageCount} InputMessageID objects.", msgIdObjects.Length);

                // Fix for CS1739: Remove named parameters `context:` and `cancellationToken:`.
                Messages_MessagesBase? messages = await _resiliencePipeline.ExecuteAsync(async (context, token) =>
                    await _client!.Messages_GetMessages(msgIdObjects), // Using '!' as _client is checked for null at method start.
                    new Context(nameof(GetMessagesAsync)), // Unique operation key for logging.
                    CancellationToken.None); // Assuming no direct cancellation needed from external for this; otherwise use `cancellationToken`.

                if (messages == null)
                {
                    string errorMessage = $"GetMessagesAsync: _client.Messages_GetMessages returned null for Peer (Type: {peerTypeForLog}, LoggedID: {peerIdForLog}), Message IDs: [{messageIdsString}].";
                    _logger.LogError(errorMessage);
                    throw new InvalidOperationException(errorMessage + " Telegram API call unexpectedly returned null.");
                }

                int fetchedMessageCount = (messages as Messages_Messages)?.messages?.Length ??
                                          (messages as Messages_MessagesSlice)?.messages?.Length ??
                                          (messages as Messages_ChannelMessages)?.messages?.Length ?? 0;

                _logger.LogInformation("GetMessagesAsync: Successfully fetched {MessageCountFromApi} items (messages/users/chats container) from API for Peer (Type: {PeerType}, LoggedID: {PeerId}). Caching result with key {CacheKey}. Actual messages in response: {ActualMessageCount}",
                    fetchedMessageCount, peerTypeForLog, peerIdForLog, cacheKey, fetchedMessageCount);
                _messageCache.Set(cacheKey, messages, _cacheOptions);

                return messages;
            }
            catch (RpcException rpcEx)
            {
                _logger.LogError(rpcEx, "GetMessagesAsync: RpcException occurred after Polly retries exhausted (or error was not retryable) for Peer (Type: {PeerType}, LoggedID: {PeerId}), Message IDs: [{MessageIdsArray}]. Error: {ErrorTypeString}, Code: {ErrorCode}",
                    peerTypeForLog, peerIdForLog, messageIdsString, rpcEx.Message, rpcEx.Code);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetMessagesAsync: Unhandled generic exception occurred after Polly retries exhausted (or error was not retryable) for Peer (Type: {PeerType}, LoggedID: {PeerId}), Message IDs: [{MessageIdsArray}]",
                    peerTypeForLog, peerIdForLog, messageIdsString);
                throw;
            }
        }



        public async Task<UpdatesBase?> SendMessageAsync(
                    InputPeer peer,
                    string message,
                    long? replyToMsgId = null,
                    ReplyMarkup? replyMarkup = null,
                    IEnumerable<MessageEntity>? entities = null,
                    bool noWebpage = false,
                    bool background = false,
                    bool clearDraft = false,
                    DateTime? schedule_date = null,
                    bool sendAsBot = false,
                    InputMedia? media = null,
                    int[]? parsedMentions = null)
        {
            if (_client == null)
            {
                _logger.LogError("SendMessageAsync: Telegram client (_client) is not initialized. Cannot send message.");
                throw new InvalidOperationException("Telegram API client is not initialized.");
            }

            long peerIdForLog = GetPeerIdForLog(peer);
            string peerTypeForLog = peer?.GetType().Name ?? "Unknown";
            string truncatedMessage = TruncateString(message, 100);
            bool hasMedia = media != null;
            string entitiesString = entities != null && entities.Any()
                ? $"Count: {entities.Count()}, Types: [{string.Join(", ", entities.Select(e => e.GetType().Name))}]"
                : "None";
            string lockKey = $"send_peer_{peerTypeForLog}_{peerIdForLog}";

            _logger.LogDebug(
                "SendMessageAsync: Attempting to send message to Peer (Type: {PeerType}, LoggedID: {PeerId}). " +
                "Message (partial): '{MessageContent}'. Entities: {EntitiesInfo}. Media: {HasMedia}. ReplyToMsgID: {ReplyToMsgId}. " +
                "NoWebpage: {NoWebpageFlag}. Background: {BackgroundFlag}. ClearDraft: {ClearDraftFlag}. ScheduleDate: {ScheduleDate}. SendAsBot: {SendAsBotFlag}.",
                peerTypeForLog,
                peerIdForLog,
                truncatedMessage,
                entitiesString,
                hasMedia,
                replyToMsgId.HasValue ? replyToMsgId.Value.ToString() : "N/A",
                noWebpage, background, clearDraft, schedule_date.HasValue ? schedule_date.Value.ToString() : "N/A", sendAsBot);

            if (peer == null)
            {
                _logger.LogWarning("SendMessageAsync: InputPeer is null. Cannot send message. Message (partial): '{MessageContent}'", truncatedMessage);
                throw new ArgumentNullException(nameof(peer), "InputPeer cannot be null for sending message.");
            }

            try
            {
                if (message != null && message.Contains("https://wa.me/message/W6HXT7VWR3U2C1"))
                {
                    message = message.Replace("https://wa.me/message/W6HXT7VWR3U2C1", "@capxi");
                }

                _logger.LogTrace("SendMessageAsync: Attempting to acquire send lock with key: {LockKey}", lockKey);
                using var sendLock = await AsyncLock.LockAsync(lockKey);
                _logger.LogDebug("SendMessageAsync: Acquired send lock with key: {LockKey} for Peer (Type: {PeerType}, LoggedID: {PeerId})",
                    lockKey, peerTypeForLog, peerIdForLog);

                long random_id = WTelegram.Helpers.RandomLong();
                InputReplyTo? inputReplyTo = replyToMsgId.HasValue ? new InputReplyToMessage { reply_to_msg_id = (int)replyToMsgId.Value } : null;

                UpdatesBase updatesBase;

                if (media == null)
                {
                    _logger.LogDebug("SendMessageAsync: Calling _client.Messages_SendMessageAsync (text-only) for Peer (Type: {PeerType}, LoggedID: {PeerId}).",
                        peerTypeForLog, peerIdForLog);
                    // Fix for CS1739: Remove named parameters `context:` and `cancellationToken:`.
                    updatesBase = await _resiliencePipeline.ExecuteAsync(async (context, token) =>
                        await _client!.Messages_SendMessage(
                            peer: peer, message: message, random_id: random_id, reply_to: inputReplyTo,
                            reply_markup: replyMarkup, no_webpage: noWebpage, background: background,
                            clear_draft: clearDraft, schedule_date: schedule_date),
                        new Context(nameof(SendMessageAsync) + "_Text"),
                        CancellationToken.None); // Or `cancellationToken` from method param
                }
                else
                {
                    _logger.LogDebug("SendMessageAsync: Calling _client.Messages_SendMediaAsync for Peer (Type: {PeerType}, LoggedID: {PeerId}).",
                        peerTypeForLog, peerIdForLog);
                    // Fix for CS1739: Remove named parameters `context:` and `cancellationToken:`.
                    updatesBase = await _resiliencePipeline.ExecuteAsync(async (context, token) =>
                        await _client!.Messages_SendMedia(
                            peer: peer, media: media, random_id: random_id, message: message,
                            reply_to: inputReplyTo, reply_markup: replyMarkup, background: background,
                            clear_draft: clearDraft, schedule_date: schedule_date),
                        new Context(nameof(SendMessageAsync) + "_Media"),
                        CancellationToken.None); // Or `cancellationToken` from method param
                }

                if (updatesBase == null)
                {
                    string errorMessage = $"SendMessageAsync: WTelegramClient API call returned null for Peer (Type: {peerTypeForLog}, LoggedID: {peerIdForLog}). Message (partial): '{truncatedMessage}'.";
                    _logger.LogError(errorMessage);
                    throw new InvalidOperationException(errorMessage + " Telegram API call unexpectedly returned null.");
                }

                _logger.LogInformation(
                    "SendMessageAsync: Message sent successfully via API. Response Type: {ResponseType}. " +
                    "Peer (Type: {PeerType}, LoggedID: {PeerId}).",
                    updatesBase.GetType().Name,
                    peerTypeForLog, peerIdForLog);
                return updatesBase;
            }
            catch (RpcException rpcEx)
            {
                _logger.LogError(rpcEx, "SendMessageAsync: Telegram API (RPC) exception occurred after Polly retries exhausted (or error was not retryable) for Peer (Type: {PeerType}, LoggedID: {PeerId}). Error: {ErrorTypeString}, Code: {ErrorCode}. Message (partial): '{MessageContent}'",
                    peerTypeForLog, peerIdForLog, rpcEx.Message, rpcEx.Code, truncatedMessage);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SendMessageAsync: Unhandled generic exception occurred after Polly retries exhausted (or error was not retryable) for Peer (Type: {PeerType}, LoggedID: {PeerId}). Message (partial): '{MessageContent}'",
                    peerTypeForLog, peerIdForLog, truncatedMessage);
                throw;
            }
            finally
            {
                _logger.LogTrace("SendMessageAsync: Send lock (if acquired) has been released for key: {LockKey}", lockKey);
            }
        }


        // EDITED METHOD: SendMediaGroupAsync - Changed call to _client.SendMediaAlbum and removed manual random_id assignment
        public async Task SendMediaGroupAsync(TL.InputPeer peer, TL.InputSingleMedia[] media, string albumCaption = null, TL.MessageEntity[]? albumEntities = null, long? replyToMsgId = null,
                                                             bool background = false, DateTime? schedule_date = null,
                                                             bool sendAsBot = false, int[]? parsedMentions = null)
        {
            if (_client == null)
            {
                _logger.LogError("SendMediaGroupAsync: Telegram client (_client) is not initialized. Cannot send media group.");
                throw new InvalidOperationException("Telegram API client is not initialized.");
            }
            if (peer == null)
            {
                _logger.LogWarning("SendMediaGroupAsync: InputPeer is null. Cannot send media group. Media Items Info: {MediaItemsInfo}", media?.Length.ToString() ?? "null");
                throw new ArgumentNullException(nameof(peer), "InputPeer cannot be null for sending media group.");
            }
            if (media == null || !media.Any())
            {
                _logger.LogWarning("SendMediaGroupAsync: Media list is null or empty. Aborting send. Peer: {PeerType}, ID: {PeerId}", peer?.GetType().Name, GetPeerIdForLog(peer));
                throw new ArgumentException("Media list cannot be null or empty for sending media group.", nameof(media));
            }

            string effectiveAlbumCaptionForLog = string.IsNullOrWhiteSpace(albumCaption)
                ? (media.FirstOrDefault()?.message is string firstMediaMessage && !string.IsNullOrEmpty(firstMediaMessage)
                    ? TruncateString(firstMediaMessage, 50)
                    : "")
                : TruncateString(albumCaption, 50);


            long peerIdForLog = GetPeerIdForLog(peer);
            string peerTypeForLog = peer?.GetType().Name ?? "Unknown";
            string mediaItemsInfo = media.Length > 5 ? $"{media.Length} items (e.g., {string.Join(", ", media.Take(2).Select(m => m.media?.GetType().Name))}...)" : $"Total: {media.Length} items";
            string lockKey = $"send_media_group_peer_{peerTypeForLog}_{peerIdForLog}";

            _logger.LogDebug(
                "SendMediaGroupAsync: Attempting to send media group to Peer (Type: {PeerType}, LoggedID: {PeerId}). " +
                "Media Items: {MediaItemsInfo}. Album Caption: '{AlbumCaptionPreview}'. ReplyToMsgID: {ReplyToMsgId}. Background: {BackgroundFlag}. ScheduleDate: {ScheduleDate}. SendAsBot: {SendAsBotFlag}.",
                peerTypeForLog, peerIdForLog, mediaItemsInfo, effectiveAlbumCaptionForLog,
                replyToMsgId.HasValue ? replyToMsgId.Value.ToString() : "N/A",
                background, schedule_date.HasValue ? schedule_date.Value.ToString() : "N/A", sendAsBot);

            try
            {
                _logger.LogTrace("SendMediaGroupAsync: Attempting to acquire send lock with key: {LockKey}", lockKey);
                using var sendLock = await AsyncLock.LockAsync(lockKey);
                _logger.LogDebug("SendMediaGroupAsync: Acquired send lock with key: {LockKey} for Peer (Type: {PeerType}, LoggedID: {PeerId})",
                    lockKey, peerTypeForLog, peerIdForLog);

                int replyToMsgIdInt = replyToMsgId.HasValue ? (int)replyToMsgId.Value : 0;
                TL.InputPeer? sendAsPeerForApi = null;

                foreach (var m in media)
                {
                    if (m.message != null && m.message.Contains("https://wa.me/message/W6HXT7VWR3U2C1"))
                    {
                        m.message = m.message.Replace("https://wa.me/message/W6HXT7VWR3U2C1", "@capxi");
                    }
                }

                ICollection<TL.InputMedia> inputMedias = media.Select(ism => ism.media).ToList();

                // Fix for CS1739: Remove named parameters `context:` and `cancellationToken:`.
                await _resiliencePipeline.ExecuteAsync(async (context, token) =>
                    await _client!.SendAlbumAsync(
                        peer: peer,
                        medias: inputMedias,
                        caption: albumCaption,
                        reply_to_msg_id: replyToMsgIdInt,
                        entities: albumEntities,
                        schedule_date: schedule_date ?? default(DateTime)
                    ),
                    new Context(nameof(SendMediaGroupAsync)),
                    CancellationToken.None); // Or `cancellationToken` from method param

                _logger.LogInformation("SendMediaGroupAsync: Successfully sent media group to Peer (Type: {PeerType}, LoggedID: {PeerId}).",
                    peerTypeForLog, peerIdForLog);
            }
            catch (TL.RpcException rpcEx)
            {
                _logger.LogError(rpcEx, "SendMediaGroupAsync: Telegram API (RPC) exception occurred after Polly retries exhausted (or error was not retryable) for Peer (Type: {PeerType}, LoggedID: {PeerId}). Error: {ErrorTypeString}, Code: {ErrorCode}.",
                    peerTypeForLog, peerIdForLog, rpcEx.Message, rpcEx.Code);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SendMediaGroupAsync: Unhandled generic exception occurred after Polly retries exhausted (or error was not retryable) for Peer (Type: {PeerType}, LoggedID: {PeerId}).",
                    peerTypeForLog, peerIdForLog);
                throw;
            }
            finally
            {
                _logger.LogTrace("SendMediaGroupAsync: Send lock (if acquired) has been released for key: {LockKey}", lockKey);
            }
        }


        // Assuming _logger is an ILogger instance available in the class
        // Assuming _client is an instance of WTelegram.Client

        public async Task<UpdatesBase?> ForwardMessagesAsync(
          InputPeer toPeer,
          int[] messageIds,
          InputPeer fromPeer,
          bool dropAuthor = false,
          bool noForwards = false,
          int? topMsgId = null,
          DateTime? scheduleDate = null,
          bool sendAsBot = false)
        {
            if (_client == null)
            {
                _logger.LogError("ForwardMessagesAsync: Telegram client (_client) is not initialized. Cannot forward messages.");
                throw new InvalidOperationException("Telegram API client is not initialized.");
            }
            if (toPeer == null) throw new ArgumentNullException(nameof(toPeer), "ToPeer cannot be null for forwarding messages.");
            if (fromPeer == null) throw new ArgumentNullException(nameof(fromPeer), "FromPeer cannot be null for forwarding messages.");
            if (messageIds == null || !messageIds.Any())
                throw new ArgumentException("Message IDs list cannot be null or empty for forwarding messages.", nameof(messageIds));


            string toPeerType = toPeer.GetType().Name;
            long toPeerId = GetPeerIdForLog(toPeer);
            string fromPeerType = fromPeer.GetType().Name;
            long fromPeerId = GetPeerIdForLog(fromPeer);

            _logger.LogDebug(
                "ForwardMessagesAsync: Attempting to forward {MessageCount} message(s) from Peer (Type: {FromPeerType}, LoggedID: {FromPeerId}) to Peer (Type: {ToPeerType}, LoggedID: {ToPeerId}). Message IDs (partial): [{MessageIdsArray}]. DropAuthor: {DropAuthor}, NoForwards: {NoForwards}.",
                messageIds.Length, fromPeerType, fromPeerId, toPeerType, toPeerId,
                messageIds.Length > 5 ? $"{string.Join(", ", messageIds.Take(5))}..." : string.Join(", ", messageIds),
                dropAuthor, noForwards
            );

            var randomIdArray = messageIds.Select(_ => WTelegram.Helpers.RandomLong()).ToArray();

            string lockKey = $"forward_peer_{fromPeerType}_{fromPeerId}_to_{toPeerType}_{toPeerId}";
            using var forwardLock = await AsyncLock.LockAsync(lockKey);

            try
            {
                InputPeer? sendAsPeer = null;

                // Fix for CS1739: Remove named parameters `context:` and `cancellationToken:`.
                UpdatesBase? result = await _resiliencePipeline.ExecuteAsync(async (context, token) =>
                    await _client!.Messages_ForwardMessages(
                        to_peer: toPeer,
                        from_peer: fromPeer,
                        id: messageIds,
                        random_id: randomIdArray,
                        drop_author: dropAuthor,
                        noforwards: noForwards,
                        top_msg_id: topMsgId,
                        schedule_date: scheduleDate
                    ),
                    new Context(nameof(ForwardMessagesAsync)),
                    CancellationToken.None); // Or `cancellationToken` from method param

                if (result == null)
                {
                    string errorMessage = $"ForwardMessagesAsync: WTelegramClient API call returned null for forwarding. From Peer (Type: {fromPeerType}, ID: {fromPeerId}) To Peer (Type: {toPeerType}, ID: {toPeerId}). Message IDs: [{string.Join(", ", messageIds)}].";
                    _logger.LogError(errorMessage);
                    throw new InvalidOperationException(errorMessage + " Telegram API call unexpectedly returned null.");
                }

                _logger.LogInformation(
                    "ForwardMessagesAsync: Successfully forwarded messages. Response type: {ResponseType}. " +
                    "From Peer (Type: {FromPeerType}, ID: {FromPeerId}) " +
                    "To Peer (Type: {ToPeerType}, ID: {ToPeerId}).",
                    result.GetType().Name,
                    fromPeerType, fromPeerId,
                    toPeerType, toPeerId);

                return result;
            }
            catch (RpcException rpcEx)
            {
                _logger.LogError(rpcEx,
                    "ForwardMessagesAsync: Telegram API (RPC) exception occurred after Polly retries exhausted (or error was not retryable). Error: {ErrorTypeString}, Code: {ErrorCode}. From Peer (Type: {FromPeerType}, ID: {FromPeerId}) To Peer (Type: {ToPeerType}, ID: {ToPeerId}). Message IDs: [{MessageIdsArray}]",
                    rpcEx.Message, rpcEx.Code, fromPeerType, fromPeerId, toPeerType, toPeerId, string.Join(", ", messageIds));
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ForwardMessagesAsync: Unhandled generic exception occurred after Polly retries exhausted (or error was not retryable). From Peer (Type: {FromPeerType}, ID: {FromPeerId}) To Peer (Type: {ToPeerType}, ID: {ToPeerId}). Message IDs: [{MessageIdsArray}]",
                    fromPeerType, fromPeerId, toPeerType, toPeerId, string.Join(", ", messageIds));
                throw;
            }
            finally
            {
                _logger.LogTrace("ForwardMessagesAsync: Forward lock (if acquired) has been released for key: {LockKey}", lockKey);
            }
        }

        // Helper function to get a numerical ID from InputPeer for logging
        private long GetPeerIdForLog(InputPeer? peer)
        {
            if (peer is InputPeerUser ipu) return ipu.user_id;
            if (peer is InputPeerChat ipc) return ipc.chat_id;
            if (peer is InputPeerChannel ipch) return ipch.channel_id;
            if (peer is InputPeerSelf) return -1; // Or some other indicator for "self"
            return 0; // Default for unknown or null
        }


        public async Task<User?> GetSelfAsync()
        {
            if (_client == null)
            {
                _logger.LogError("GetSelfAsync: Telegram client (_client) is not initialized.");
                return null;
            }
            try
            {
                // Fix for CS1739: Remove named parameters `context:` and `cancellationToken:`.
                return await _resiliencePipeline.ExecuteAsync(async (context, token) =>
                    await _client!.LoginUserIfNeeded(),
                    new Context(nameof(GetSelfAsync)),
                    CancellationToken.None); // Or `cancellationToken` from method param
            }
            catch (RpcException rpcEx)
            {
                _logger.LogError(rpcEx, "GetSelfAsync failed after Polly retries exhausted (or error was not retryable). Error: {ErrorTypeString}, Code: {ErrorCode}",
                                 rpcEx.Message, rpcEx.Code);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetSelfAsync failed after Polly retries exhausted (or error was not retryable).");
                return null;
            }
        }


        public async Task<InputPeer?> ResolvePeerAsync(long positivePeerId)
        {
            if (_client == null)
            {
                _logger.LogError("ResolvePeerAsync: Client not initialized for PositivePeerId {PositivePeerId}.", positivePeerId);
                return null;
            }
            if (positivePeerId == 0)
            {
                _logger.LogWarning("ResolvePeerAsync: PositivePeerId is 0. Cannot resolve.");
                return null;
            }

            _logger.LogDebug("ResolvePeerAsync: Attempting to resolve PositivePeerId: {PositivePeerId}", positivePeerId);

            // 1. ابتدا کش سفارشی خودتان را با شناسه مثبت بررسی کنید
            if (_userCacheWithExpiry.TryGetValue(positivePeerId, out var userCacheEntry) &&
                userCacheEntry.Expiry > DateTime.UtcNow && userCacheEntry.User != null)
            {
                _logger.LogInformation("ResolvePeerAsync: Found User {UserId} (AH: {AccessHash}) in LOCAL USER CACHE for PositivePeerId {PositivePeerId}.",
                    positivePeerId, userCacheEntry.User.access_hash, positivePeerId);
                return new InputPeerUser(positivePeerId, userCacheEntry.User.access_hash);
            }

            if (_chatCacheWithExpiry.TryGetValue(positivePeerId, out var chatCacheEntry) &&
                chatCacheEntry.Expiry > DateTime.UtcNow && chatCacheEntry.Chat != null)
            {
                if (chatCacheEntry.Chat is Channel channelFromCache)
                {
                    _logger.LogInformation("ResolvePeerAsync: Found Channel {ChannelId} (AH: {AccessHash}) in LOCAL CHAT CACHE for PositivePeerId {PositivePeerId}.",
                        positivePeerId, channelFromCache.access_hash, positivePeerId);
                    return new InputPeerChannel(positivePeerId, channelFromCache.access_hash);
                }
                else if (chatCacheEntry.Chat is Chat chatFromCache)
                {
                    _logger.LogInformation("ResolvePeerAsync: Found Chat {ChatId} in LOCAL CHAT CACHE for PositivePeerId {PositivePeerId}.",
                        positivePeerId, positivePeerId);
                    return new InputPeerChat(positivePeerId);
                }
            }
            _logger.LogDebug("ResolvePeerAsync: PositivePeerId {PositivePeerId} not found in active local cache. Attempting API calls.", positivePeerId);


            string resolveString = positivePeerId.ToString();
            _logger.LogDebug("ResolvePeerAsync: Attempting API call with Contacts_ResolveUsername for string '{ResolveString}' (PositivePeerId {PositivePeerId}).", resolveString, positivePeerId);
            try
            {
                // Fix for CS1739: Remove named parameters `context:` and `cancellationToken:`.
                Contacts_ResolvedPeer resolvedUsernameResponse = await _resiliencePipeline.ExecuteAsync(async (context, token) =>
                    await _client!.Contacts_ResolveUsername(resolveString),
                    new Context(nameof(ResolvePeerAsync) + "_ResolveUsername"),
                    CancellationToken.None); // Or `cancellationToken` from method param

                if (resolvedUsernameResponse?.users != null)
                {
                    foreach (var uEntry in resolvedUsernameResponse.users)
                        _userCacheWithExpiry[uEntry.Key] = (uEntry.Value, DateTime.UtcNow.Add(_cacheExpiration));
                }
                if (resolvedUsernameResponse?.chats != null)
                {
                    foreach (var cEntry in resolvedUsernameResponse.chats)
                        _chatCacheWithExpiry[cEntry.Key] = (cEntry.Value, DateTime.UtcNow.Add(_cacheExpiration));
                }

                if (resolvedUsernameResponse?.peer != null)
                {
                    if (resolvedUsernameResponse.peer is PeerUser pu)
                    {
                        User? userObj = resolvedUsernameResponse.users.GetValueOrDefault(pu.user_id);
                        long accessHash = userObj?.access_hash ?? 0;
                        _logger.LogInformation("ResolvePeerAsync: Resolved via Contacts_ResolveUsername to User {UserId} (AH: {AccessHash}) for string '{ResolveString}'.",
                            pu.user_id, accessHash, resolveString);
                        return new InputPeerUser(pu.user_id, accessHash);
                    }
                    else if (resolvedUsernameResponse.peer is PeerChat pc)
                    {
                        _logger.LogInformation("ResolvePeerAsync: Resolved via Contacts_ResolveUsername to Chat {ChatId} for string '{ResolveString}'.",
                            pc.chat_id, resolveString);
                        return new InputPeerChat(pc.chat_id);
                    }
                    else if (resolvedUsernameResponse.peer is PeerChannel pchan)
                    {
                        Channel? channelObj = resolvedUsernameResponse.chats.GetValueOrDefault(pchan.channel_id) as Channel;
                        long accessHash = channelObj?.access_hash ?? 0;
                        _logger.LogInformation("ResolvePeerAsync: Resolved via Contacts_ResolveUsername to Channel {ChannelId} (AH: {AccessHash}) for string '{ResolveString}'.",
                            pchan.channel_id, accessHash, resolveString);
                        return new InputPeerChannel(pchan.channel_id, accessHash);
                    }
                    _logger.LogWarning("ResolvePeerAsync: Contacts_ResolveUsername for string '{ResolveString}' returned an unhandled peer type: {PeerType}. PositivePeerId: {PositivePeerId}",
                        resolveString, resolvedUsernameResponse.peer.GetType().Name, positivePeerId);
                }
                else
                {
                    _logger.LogWarning("ResolvePeerAsync: Contacts_ResolveUsername for string '{ResolveString}' (PositivePeerId {PositivePeerId}) returned null or null peer.",
                       resolveString, positivePeerId);
                }
            }
            catch (RpcException rpcEx) when (rpcEx.Code == 400 && (rpcEx.Message.Contains("USERNAME_INVALID") || rpcEx.Message.Contains("USERNAME_NOT_OCCUPIED") || rpcEx.Message.Contains("PEER_ID_INVALID")))
            {
                _logger.LogWarning("ResolvePeerAsync: Contacts_ResolveUsername for string '{ResolveString}' (PositivePeerId {PositivePeerId}) failed with RPC Error: {RpcError}. This ID string is likely not a known username or public peer ID that can be resolved this way.",
                    resolveString, positivePeerId, rpcEx.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ResolvePeerAsync: Exception during Contacts_ResolveUsername for string '{ResolveString}' (PositivePeerId {PositivePeerId}).", resolveString, positivePeerId);
            }

            _logger.LogDebug("ResolvePeerAsync: Attempting API call with Channels_GetChannels for PositivePeerId: {PositivePeerId} as a fallback (if it's a channel and previous methods failed).", positivePeerId);
            try
            {
                // Fix for CS1739: Remove named parameters `context:` and `cancellationToken:`.
                Messages_Chats channelsResponse = await _resiliencePipeline.ExecuteAsync(async (context, token) =>
                    await _client!.Channels_GetChannels(new[] { new InputChannel(positivePeerId, 0) }),
                    new Context(nameof(ResolvePeerAsync) + "_GetChannels"),
                    CancellationToken.None); // Or `cancellationToken` from method param

                if (channelsResponse?.chats != null)
                {
                    foreach (var cEntry in channelsResponse.chats)
                        _chatCacheWithExpiry[cEntry.Key] = (cEntry.Value, DateTime.UtcNow.Add(_cacheExpiration));
                }

                if (channelsResponse?.chats != null && channelsResponse.chats.TryGetValue(positivePeerId, out var chatFromApi) && chatFromApi is Channel telegramChannel)
                {
                    _logger.LogInformation("ResolvePeerAsync: Successfully resolved Channel {PositivePeerId} (API ID: {ApiChannelId}, AH: {AccessHash}) via Channels_GetChannels (fallback).",
                                       positivePeerId, telegramChannel.id, telegramChannel.access_hash);
                    return new InputPeerChannel(telegramChannel.id, telegramChannel.access_hash);
                }
                _logger.LogWarning("ResolvePeerAsync: Fallback Channels_GetChannels for PositivePeerId {PositivePeerId} did not find it in the response or it wasn't a Channel. Chats in response: {ChatCount}",
                                   positivePeerId, channelsResponse?.chats?.Count ?? 0);
            }
            catch (RpcException rpcEx) when (rpcEx.Message.Contains("CHANNEL_INVALID") || rpcEx.Message.Contains("PEER_ID_INVALID"))
            {
                _logger.LogWarning("ResolvePeerAsync: Fallback Channels_GetChannels for PositivePeerId {PositivePeerId} failed with RPC Error: {RpcError}. This ID might not be a channel, is inaccessible, or access_hash problem.", positivePeerId, rpcEx.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ResolvePeerAsync: Exception during fallback Channels_GetChannels for PositivePeerId {PositivePeerId}.", positivePeerId);
            }

            _logger.LogError("ResolvePeerAsync: FAILED to resolve PositivePeerId {PositivePeerId} using all implemented strategies.", positivePeerId);
            return null;
        }

        public async ValueTask DisposeAsync()
        {
            _logger.LogInformation("Disposing TelegramUserApiClient...");
            if (_cacheCleanupTimer != null)
            {
                await _cacheCleanupTimer.DisposeAsync();
                _cacheCleanupTimer = null;
                _logger.LogInformation("Cache cleanup timer disposed.");
            }

            if (_client != null)
            {
                _client.OnUpdates -= HandleUpdatesBaseAsync;
                _client.Dispose();
                _client = null;
            }
            try
            {
                _connectionLock.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Ignore if already disposed
            }
            await Task.CompletedTask;
        }

        #endregion

        #region Helper class for async locking

        private class AsyncLock
        {
            private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

            public static async Task<IDisposable> LockAsync(string key)
            {
                var semaphore = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
                await semaphore.WaitAsync();
                return new DisposableAction(() => semaphore.Release());
            }

            private class DisposableAction : IDisposable
            {
                private readonly Action _action;
                public DisposableAction(Action action) => _action = action;
                public void Dispose() => _action();
            }
        }
        #endregion
    }
}