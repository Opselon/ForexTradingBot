// File: src/Infrastructure/Services/TelegramUserApiClient.cs
#region Usings
using Application.Common.Interfaces;
using Infrastructure.Settings;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql.Replication.PgOutput.Messages;
using System.Collections.Concurrent;
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
        #endregion

        #region Private Fields
        private WTelegram.Client? _client;
        private SemaphoreSlim _connectionLock = new SemaphoreSlim(1, 1);
        #endregion

        #region Public Properties & Events
        public WTelegram.Client NativeClient => _client;
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

            // Create new client instance
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

        #endregion

        #region WTelegramClient Update Handler
        private async Task HandleUpdatesBaseAsync(UpdatesBase updatesBase) // From _client.OnUpdates
        {
            // --- Start of Method Logging ---
            _logger.LogDebug("HandleUpdatesBaseAsync: Received UpdatesBase of type {UpdatesBaseType}. Update content (partial): {UpdatesBaseContent}",
                updatesBase.GetType().Name,
                TruncateString(updatesBase.ToString(), 200)); // Log partial content for context

            // --- User/Chat Collection Logging ---
            int initialUserCacheCount = _userCache.Count; // Assuming .Count is available
            int initialChatCacheCount = _chatCache.Count; // Assuming .Count is available

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

            List<Update> updatesToDispatch = new List<Update>();

            // 1. Handle container updates like Updates and UpdatesCombined
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
            // 2. Handle standalone "short" updates by converting them to their "fuller" TL.Update equivalents
            else if (updatesBase is UpdateShortMessage usm)
            {
                _logger.LogDebug("HandleUpdatesBaseAsync: Processing 'UpdateShortMessage'. MsgID: {MessageId}, UserID: {UserId}, PTS: {Pts}",
                    usm.id, usm.user_id, usm.pts);
                Peer userPeer;
                if (_userCache.TryGetValue(usm.user_id, out var cachedUser))
                {
                    userPeer = new PeerUser { user_id = cachedUser.id };
                    _logger.LogTrace("HandleUpdatesBaseAsync (USM): User {UserId} found in cache.", usm.user_id);
                }
                else
                {
                    userPeer = new PeerUser { user_id = usm.user_id };
                    _logger.LogWarning("HandleUpdatesBaseAsync (USM): User {UserId} from UpdateShortMessage not in cache, using minimal PeerUser. This may affect functionality requiring access_hash.", usm.user_id);
                }

                // Detailed construction logging (optional, can be Trace)
                _logger.LogTrace("HandleUpdatesBaseAsync (USM): Constructing Message for MsgID {MessageId}. FromID: {FromId}, PeerID: {PeerId}, Message: {MessageContent}",
                    usm.id, usm.user_id, usm.user_id, TruncateString(usm.message, 50));

                var msg = new Message
                {
                    // ... (your existing message construction) ...
                    // No changes needed here unless you want to log specific flag settings
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
                    ttl_period = usm.ttl_period
                };
                var uf = usm.flags;
                if (uf.HasFlag(UpdateShortMessage.Flags.out_)) msg.flags |= Message.Flags.out_;
                if (uf.HasFlag(UpdateShortMessage.Flags.mentioned)) msg.flags |= Message.Flags.mentioned;
                if (uf.HasFlag(UpdateShortMessage.Flags.silent)) msg.flags |= Message.Flags.silent;
                if (uf.HasFlag(UpdateShortMessage.Flags.media_unread)) msg.flags |= Message.Flags.media_unread;


                updatesToDispatch.Add(new UpdateNewMessage { message = msg, pts = usm.pts, pts_count = usm.pts_count });
                _logger.LogDebug("HandleUpdatesBaseAsync (USM): Added UpdateNewMessage for MsgID {MessageId} to dispatch list.", usm.id);
            }
            else if (updatesBase is UpdateShortChatMessage uscm)
            {
                _logger.LogDebug("HandleUpdatesBaseAsync: Processing 'UpdateShortChatMessage'. MsgID: {MessageId}, FromID: {FromId}, ChatID: {ChatId}, PTS: {Pts}",
                    uscm.id, uscm.from_id, uscm.chat_id, uscm.pts);
                Peer chatPeer;
                if (_chatCache.TryGetValue(uscm.chat_id, out var cachedChat))
                {
                    if (cachedChat is Channel channel)
                    {
                        chatPeer = new PeerChannel { channel_id = channel.id };
                    }
                    else // Assuming Chat or ChatForbidden
                    {
                        chatPeer = new PeerChat { chat_id = cachedChat.ID };
                    }
                    _logger.LogTrace("HandleUpdatesBaseAsync (USCM): Chat {ChatId} (Type: {ChatType}) found in cache.", uscm.chat_id, cachedChat.GetType().Name);
                }
                else
                {
                    // If chat is not in cache, we only have the chat_id.
                    // For channels, this is problematic as PeerChannel requires channel_id.
                    // WTelegramClient often resolves this internally or through subsequent updates.
                    // Defaulting to PeerChat if unknown is a common fallback but might be inaccurate for channels.
                    chatPeer = new PeerChat { chat_id = uscm.chat_id };
                    _logger.LogWarning("HandleUpdatesBaseAsync (USCM): Chat {ChatId} from UpdateShortChatMessage not in cache, using minimal PeerChat. This may be inaccurate if it's a channel and might affect functionality requiring access_hash or channel specifics.", uscm.chat_id);
                }

                Peer? fromPeer = null; // from_id is critical for group messages
                if (_userCache.TryGetValue(uscm.from_id, out var cachedFromUser))
                {
                    fromPeer = new PeerUser { user_id = cachedFromUser.id };
                    _logger.LogTrace("HandleUpdatesBaseAsync (USCM): Sender User {UserId} found in cache.", uscm.from_id);
                }
                else
                {
                    fromPeer = new PeerUser { user_id = uscm.from_id };
                    _logger.LogWarning("HandleUpdatesBaseAsync (USCM): Sender User {UserId} (from_id) from UpdateShortChatMessage not in cache, using minimal PeerUser. This may affect functionality requiring access_hash.", uscm.from_id);
                }

                // --- CORRECTED PART ---
                // Define targetChatIdForLog by checking the type of chatPeer
                long targetChatIdForLog = 0;
                if (chatPeer is PeerUser pu) targetChatIdForLog = pu.user_id; // Should not happen for chatPeer in USCM, but for safety
                else if (chatPeer is PeerChat pc) targetChatIdForLog = pc.chat_id;
                else if (chatPeer is PeerChannel pch) targetChatIdForLog = pch.channel_id;
                // --- END OF CORRECTED PART ---

                _logger.LogTrace("HandleUpdatesBaseAsync (USCM): Constructing Message for MsgID {MessageId}. FromID: {FromId}, TargetChatID: {TargetChatId}, Message: {MessageContent}",
                   uscm.id, uscm.from_id, targetChatIdForLog, TruncateString(uscm.message, 50));

                var msg = new Message
                {
                    flags = 0, // Initialize
                    id = uscm.id,
                    peer_id = chatPeer, // The group/channel/chat where message appeared
                    from_id = fromPeer, // The user who sent it
                    message = uscm.message,
                    date = uscm.date,
                    entities = uscm.entities,
                    media = null, // UpdateShortChatMessage schema does not directly provide a full MessageMedia object
                    reply_to = uscm.reply_to,
                    fwd_from = uscm.fwd_from,
                    via_bot_id = uscm.via_bot_id,
                    ttl_period = uscm.ttl_period
                };
                // Note: UpdateShortChatMessage flags (uscm.flags) are not directly mapped to Message flags
                // in the same way as UpdateShortMessage.flags are.
                // If uscm.flags has bits like 'out', 'mentioned', etc., you'd need to map them:
                // Example: if (uscm.flags.HasFlag(UpdateShortChatMessage.Flags.out_)) msg.flags |= Message.Flags.out_;
                // Consult WTelegramClient documentation or TL schema for UpdateShortChatMessage.Flags if needed.


                updatesToDispatch.Add(new UpdateNewMessage { message = msg, pts = uscm.pts, pts_count = uscm.pts_count });
                _logger.LogDebug("HandleUpdatesBaseAsync (USCM): Added UpdateNewMessage for MsgID {MessageId} to dispatch list.", uscm.id);
            }

            // --- Dispatching Collected Updates ---
            if (updatesToDispatch.Any())
            {
                _logger.LogInformation("HandleUpdatesBaseAsync: Dispatching {DispatchCount} TL.Update object(s) via OnCustomUpdateReceived.", updatesToDispatch.Count);
                foreach (var update in updatesToDispatch)
                {
                    _logger.LogTrace("HandleUpdatesBaseAsync: Dispatching update of type {UpdateType}. Update content (partial): {UpdateContent}",
                        update.GetType().Name, TruncateString(update.ToString(), 100));
                    try
                    {
                        OnCustomUpdateReceived?.Invoke(update); // Assuming this is synchronous or fire-and-forget. If it's async, consider how to handle it.
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "HandleUpdatesBaseAsync: Exception during OnCustomUpdateReceived invocation for update type {UpdateType}. Update content (partial): {UpdateContent}",
                            update.GetType().Name, TruncateString(update.ToString(), 100));
                        // Decide if one failing subscriber should stop others, or if you should continue.
                    }
                }
                _logger.LogInformation("HandleUpdatesBaseAsync: Finished dispatching {DispatchCount} TL.Update object(s).", updatesToDispatch.Count);
            }
            else
            {
                _logger.LogDebug("HandleUpdatesBaseAsync: No TL.Update objects to dispatch from this UpdatesBase of type {UpdatesBaseType}.", updatesBase.GetType().Name);
            }
            // No explicit 'await Task.CompletedTask;' needed if the method is already async due to other awaits (or if OnCustomUpdateReceived.Invoke is async itself and awaited).
            // If the method signature has 'async Task' but there are no awaits, then `return Task.CompletedTask;` can be used. Here it seems implicitly async.
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
                // Force new connection by disposing existing client if needed
                if (_client != null)
                {
                    _logger.LogInformation("Disposing existing client to force new connection...");
                    _client.OnUpdates -= HandleUpdatesBaseAsync;
                    _client.Dispose();
                    _client = null;
                }

                // Create new client instance
                _client = new WTelegram.Client(ConfigProvider);
                _client.OnUpdates += async updates =>
                {
                    if (updates is UpdatesBase updatesBase)
                    {
                        await HandleUpdatesBaseAsync(updatesBase);
                    }
                };

                _logger.LogInformation("Connecting User API (Session: {SessionPath})...", _settings.SessionPath);
                try
                {
                    User loggedInUser = await _client.LoginUserIfNeeded();
                    _logger.LogInformation("User API Logged in: {User}", loggedInUser?.ToString());
                    
                    // Pre-fetch and cache dialogs
                    var dialogs = await _client.Messages_GetAllDialogs();
                    dialogs.CollectUsersChats(_userCache, _chatCache);

                    // Update expiry caches
                    int usersTransferred = 0;
                    if (_userCache != null && _userCache.Any()) // Check if _userCache is not null and has items
                    {
                        _logger.LogDebug("Refreshing user cache. Found {UserCacheCount} users in simple cache.", _userCache.Count);
                        foreach (var userEntry in _userCache) // Changed 'user' to 'userEntry' for clarity if 'user' is a type name
                        {
                            // Assuming userEntry.Key is the user identifier (e.g., long userId)
                            // Assuming userEntry.Value is the user object
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
                    if (_chatCache != null && _chatCache.Any()) // Check if _chatCache is not null and has items
                    {
                        _logger.LogDebug("Refreshing chat cache. Found {ChatCacheCount} chats in simple cache.", _chatCache.Count);
                        foreach (var chatEntry in _chatCache) // Changed 'chat' to 'chatEntry' for clarity
                        {
                            // Assuming chatEntry.Key is the chat identifier (e.g., long chatId)
                            // Assuming chatEntry.Value is the chat object
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
                    User loggedInUser = await _client.LoginUserIfNeeded();
                    _logger.LogInformation("User API Logged in with 2FA: {User}", loggedInUser?.ToString());
                }
                catch (RpcException e) when (e.Message.StartsWith("PHONE_MIGRATE_"))
                {
                    if (int.TryParse(e.Message.Split('_').Last(), out int dcNumber))
                    {
                        _logger.LogWarning("User API: Phone number needs to be migrated to DC{DCNumber}. Reconnecting...", dcNumber);

                        if (_client != null) // Check if _client exists before trying to use it or dispose
                        {
                            _client.OnUpdates -= HandleUpdatesBaseAsync;
                            _client.Dispose();
                            _client = null;
                        }

                        // Create new client with the correct DC
                        _logger.LogInformation("Creating new WTelegram.Client instance for DC{DCNumber}.", dcNumber);
                        _client = new WTelegram.Client(ConfigProvider);// <<< UNCOMMENTED & CORRECTED: dcToUse parameter

                        _client.OnUpdates += async updates => // Re-attach event handler
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
                        User loggedInUser = await _client.LoginUserIfNeeded(); // Try logging in again with the new client
                        _logger.LogInformation("User API Logged in on DC{DCNumber}: {User}", dcNumber, loggedInUser?.ToString());
                        // You should also re-fetch dialogs here if migration happens
                        var dialogs = await _client.Messages_GetAllDialogs();
                        dialogs.CollectUsersChats(_userCache, _chatCache);
                        // And update expiry caches
                    }
                    else
                    {
                        _logger.LogError("User API: Failed to parse DC number from migration error: {ErrorMessage}", e.Message);
                        throw; // Rethrow if parsing DC fails
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
                    // If the semaphore is disposed, create a new one
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
            string messageIdsString = messageIds.Length > 5
                ? $"{string.Join(", ", messageIds.Take(5))}... (Total: {messageIds.Length})"
                : string.Join(", ", messageIds);

            // For logging InputPeer, its ToString() is often informative enough.
            // If you need just the ID, you have to cast.
            long peerIdForLog = 0;
            string peerTypeForLog = peer?.GetType().Name ?? "Unknown";
            if (peer is InputPeerUser ipu) peerIdForLog = ipu.user_id;
            else if (peer is InputPeerChat ipc) peerIdForLog = ipc.chat_id;
            else if (peer is InputPeerChannel ipch) peerIdForLog = ipch.channel_id;
            else if (peer is InputPeerSelf) peerTypeForLog = "Self"; // No direct ID here, client knows it.
                                                                     // Add other InputPeer types if necessary (InputPeerEmpty, etc.)

            _logger.LogDebug("GetMessagesAsync: Attempting to get messages for Peer (Type: {PeerType}, LoggedID: {PeerId}), Message IDs: [{MessageIdsArray}]",
                peerTypeForLog,
                peerIdForLog, // Using the extracted ID for logging
                messageIdsString);

            if (peer == null || !messageIds.Any())
            {
                _logger.LogWarning("GetMessagesAsync: InputPeer is null or no message IDs provided. Returning null. Peer: {PeerString}, MessageIDs Count: {MessageIdCount}",
                    peer?.ToString() ?? "null", // Using ToString() for the object itself if it's null
                    messageIds?.Length ?? 0);
                return null;
            }

            try
            {
                // Cache Key Generation: Use the numerical ID and type for robustness.
                string cacheKeySuffix = peer is InputPeerUser p_u ? $"u{p_u.user_id}" :
                                        peer is InputPeerChat p_c ? $"c{p_c.chat_id}" :
                                        peer is InputPeerChannel p_ch ? $"ch{p_ch.channel_id}" :
                                        peer is InputPeerSelf ? "self" :
                                        $"other{peer.GetHashCode()}"; // Fallback, less ideal

                var cacheKey = $"msgs_peer{cacheKeySuffix}_ids{string.Join("_", messageIds.Select(id => id.ToString()))}";
                _logger.LogTrace("GetMessagesAsync: Generated cache key: {CacheKey}", cacheKey);

                if (_messageCache.TryGetValue(cacheKey, out Messages_MessagesBase? cachedMessages))
                {
                    int cachedMessageCount = 0;
                    if (cachedMessages is Messages_Messages mm) cachedMessageCount = mm.messages.Length;
                    else if (cachedMessages is Messages_MessagesSlice mms) cachedMessageCount = mms.messages.Length;
                    else if (cachedMessages is Messages_ChannelMessages mcm) cachedMessageCount = mcm.messages.Length;
                    // Add other Messages_MessagesBase derived types if necessary

                    _logger.LogDebug("GetMessagesAsync: Cache HIT for key {CacheKey}. Returning {CachedMessageCount} cached messages for Peer (Type: {PeerType}, LoggedID: {PeerId}).",
                        cacheKey,
                        cachedMessageCount,
                        peerTypeForLog,
                        peerIdForLog);
                    return cachedMessages;
                }
                else
                {
                    _logger.LogInformation("GetMessagesAsync: Cache MISS for key {CacheKey}. Fetching from API for Peer (Type: {PeerType}, LoggedID: {PeerId}), Message IDs: [{MessageIdsArray}]",
                        cacheKey, peerTypeForLog, peerIdForLog, messageIdsString);
                }

                var msgIdObjects = messageIds.Select(id => (InputMessage)new InputMessageID { id = id }).ToArray();
                _logger.LogDebug("GetMessagesAsync: Calling _client.Messages_GetMessages with {InputMessageCount} InputMessageID objects.", msgIdObjects.Length);

                Messages_MessagesBase? messages = await _client.Messages_GetMessages(msgIdObjects);

                if (messages != null)
                {
                    int fetchedMessageCount = 0;
                    if (messages is Messages_Messages mm) fetchedMessageCount = mm.messages.Length;
                    else if (messages is Messages_MessagesSlice mms) fetchedMessageCount = mms.messages.Length;
                    else if (messages is Messages_ChannelMessages mcm) fetchedMessageCount = mcm.messages.Length;
                    // Add other Messages_MessagesBase derived types if necessary

                    _logger.LogInformation("GetMessagesAsync: Successfully fetched {MessageCountFromApi} items (messages/users/chats container) from API for Peer (Type: {PeerType}, LoggedID: {PeerId}). Caching result with key {CacheKey}. Actual messages in response: {ActualMessageCount}",
                        fetchedMessageCount, // This is the count of MessageBase objects in the response
                        peerTypeForLog,
                        peerIdForLog,
                        cacheKey,
                        fetchedMessageCount); // Assuming the 'messages' field count is what we want
                    _messageCache.Set(cacheKey, messages, _cacheOptions);
                }
                else
                {
                    _logger.LogWarning("GetMessagesAsync: _client.Messages_GetMessages returned null for Peer (Type: {PeerType}, LoggedID: {PeerId}), Message IDs: [{MessageIdsArray}]. Not caching.",
                        peerTypeForLog, peerIdForLog, messageIdsString);
                }

                return messages;
            }
            catch (RpcException rpcEx)
            {
                // RpcException.Message contains the ErrorType (e.g., "FLOOD_WAIT_X", "USER_ID_INVALID")
                // RpcException.Code contains the numerical code
                _logger.LogError(rpcEx, "GetMessagesAsync: RpcException occurred while fetching messages for Peer (Type: {PeerType}, LoggedID: {PeerId}), Message IDs: [{MessageIdsArray}]. Error: {ErrorTypeString}, Code: {ErrorCode}",
                    peerTypeForLog,
                    peerIdForLog,
                    messageIdsString,
                    rpcEx.Message, // This is where WTelegramClient puts the error type string
                    rpcEx.Code);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetMessagesAsync: Unhandled generic exception occurred while fetching messages for Peer (Type: {PeerType}, LoggedID: {PeerId}), Message IDs: [{MessageIdsArray}]",
                    peerTypeForLog,
                    peerIdForLog,
                    messageIdsString);
                return null;
            }
        }
        // Assuming _logger is an ILogger instance available in the class
        // Assuming _client is an instance of WTelegram.Client
        // Assuming _userCacheWithExpiry and _chatCacheWithExpiry are Dictionaries or similar
        // Assuming AsyncLock is a class you have for managing named asynchronous locks

        // Assuming _logger, _client, _userCacheWithExpiry, _chatCacheWithExpiry, AsyncLock are defined

        // Assuming _logger is an ILogger instance available in the class
        // Assuming _client is an instance of WTelegram.Client
        // Assuming _userCacheWithExpiry and _chatCacheWithExpiry are Dictionaries or similar
        // Assuming AsyncLock is a class you have for managing named asynchronous locks

        public async Task<UpdatesBase?> SendMessageAsync(
            InputPeer peer,
            string message,
            MessageEntity[]? entities = null,
            InputMedia? media = null,
            long? replyToMsgId = null,
            bool noWebpage = false)
        {
            // --- Define variables for logging at the top of the method scope ---
            long peerIdForLog = 0;
            string peerTypeForLog = peer?.GetType().Name ?? "Unknown";
            if (peer is InputPeerUser ipu) peerIdForLog = ipu.user_id;
            else if (peer is InputPeerChat ipc) peerIdForLog = ipc.chat_id;
            else if (peer is InputPeerChannel ipch) peerIdForLog = ipch.channel_id;
            else if (peer is InputPeerSelf) peerTypeForLog = "Self"; // No direct ID here, client knows it.

            string truncatedMessage = TruncateString(message, 100);
            bool hasMedia = media != null;
            string entitiesString = entities != null && entities.Any()
                ? $"Count: {entities.Length}, Types: [{string.Join(", ", entities.Select(e => e.GetType().Name))}]"
                : "None";
            string lockKey = $"send_peer_{peerTypeForLog}_{peerIdForLog}"; // Define lockKey here to be accessible in finally

            // --- Method Entry and Parameter Logging ---
            _logger.LogDebug(
                "SendMessageAsync: Attempting to send message to Peer (Type: {PeerType}, LoggedID: {PeerId}). " +
                "Message (partial): '{MessageContent}'. Entities: {EntitiesInfo}. Media: {HasMedia}. ReplyToMsgID: {ReplyToMsgId}. " +
                "Desired NoWebpage: {NoWebpageFlag} (Note: Current SendMessageAsync overload may not directly support this flag)",
                peerTypeForLog,
                peerIdForLog,
                truncatedMessage,
                entitiesString,
                hasMedia,
                replyToMsgId.HasValue ? replyToMsgId.Value.ToString() : "N/A",
                noWebpage);

            if (peer == null)
            {
                _logger.LogWarning("SendMessageAsync: InputPeer is null. Cannot send message. Message (partial): '{MessageContent}'", truncatedMessage);
                return null;
            }

            try
            {
                int replyTo = replyToMsgId.HasValue ? (int)replyToMsgId.Value : 0;

                _logger.LogTrace("SendMessageAsync: Attempting to acquire send lock with key: {LockKey}", lockKey);

                using var sendLock = await AsyncLock.LockAsync(lockKey);
                _logger.LogDebug("SendMessageAsync: Acquired send lock with key: {LockKey} for Peer (Type: {PeerType}, LoggedID: {PeerId})",
                    lockKey, peerTypeForLog, peerIdForLog);

                _logger.LogDebug("SendMessageAsync: Calling _client.SendMessageAsync for Peer (Type: {PeerType}, LoggedID: {PeerId}). ReplyTo: {ReplyToIdValue}. Desired NoWebpage: {NoWebpageFlag}",
                    peerTypeForLog, peerIdForLog, replyTo, noWebpage);

                Message sentMessage = await _client.SendMessageAsync(peer, message,
                    entities: entities,
                    media: media,
                    reply_to_msg_id: replyTo);

                if (sentMessage != null)
                {
                    _logger.LogInformation(
                        "SendMessageAsync: Message sent successfully via API. SentMsgID: {SentMessageId}, Date: {SentDate}, PeerID: {PeerIdOfSentMessage}. WebpagePreviewDisabled (behavior depends on entities/helper): {NoWebpageFlag}",
                        sentMessage.id,
                        sentMessage.date,
                        sentMessage.peer_id?.ToString() ?? "N/A",
                        noWebpage);

                    _logger.LogDebug("SendMessageAsync: Constructing Updates object for sent message ID {SentMessageId}.", sentMessage.id);
                    var updates = new Updates // TL.Updates
                    {
                        updates = new Update[] { new UpdateNewMessage { message = sentMessage, pts = 0, pts_count = 0 } },
                        users = new Dictionary<long, User>(),
                        chats = new Dictionary<long, ChatBase>(),
                        date = sentMessage.date,
                        seq = 0
                    };

                    if (_client.User != null)
                    {
                        updates.users[_client.User.id] = _client.User;
                        _logger.LogTrace("SendMessageAsync: Added self (Client User ID: {ClientUserId}) to Updates.users.", _client.User.id);
                    }
                    else
                    {
                        _logger.LogWarning("SendMessageAsync: _client.User is null. Cannot add self to Updates.users.");
                    }

                    _logger.LogTrace("SendMessageAsync: Attempting to add target Peer (Type: {PeerType}, ID: {TargetPeerId}) to Updates users/chats from cache.",
                        peerTypeForLog, peerIdForLog); // Used already defined vars

                    if (peer is InputPeerUser ipUser)
                    {
                        if (_userCacheWithExpiry.TryGetValue(ipUser.user_id, out var userDataTuple) && userDataTuple.User != null && userDataTuple.Expiry > DateTime.UtcNow)
                        {
                            updates.users[userDataTuple.User.id] = userDataTuple.User;
                            _logger.LogDebug("SendMessageAsync: Cache HIT for target User {UserId}. Added to Updates.users.", userDataTuple.User.id);
                        }
                        else
                        {
                            _logger.LogWarning("SendMessageAsync: Cache MISS or expired for target User {UserId}. User not added to Updates.users from cache.", ipUser.user_id);
                        }
                    }
                    else if (peer is InputPeerChat ipChat)
                    {
                        if (_chatCacheWithExpiry.TryGetValue(ipChat.chat_id, out var chatDataTuple) && chatDataTuple.Chat != null && chatDataTuple.Expiry > DateTime.UtcNow)
                        {
                            updates.chats[chatDataTuple.Chat.ID] = chatDataTuple.Chat;
                            _logger.LogDebug("SendMessageAsync: Cache HIT for target Chat {ChatId}. Added to Updates.chats.", chatDataTuple.Chat.ID);
                        }
                        else
                        {
                            _logger.LogWarning("SendMessageAsync: Cache MISS or expired for target Chat {ChatId}. Chat not added to Updates.chats from cache.", ipChat.chat_id);
                        }
                    }
                    else if (peer is InputPeerChannel ipChannel)
                    {
                        if (_chatCacheWithExpiry.TryGetValue(ipChannel.channel_id, out var channelDataTuple) && channelDataTuple.Chat is Channel channel && channelDataTuple.Expiry > DateTime.UtcNow)
                        {
                            updates.chats[channel.id] = channel;
                            _logger.LogDebug("SendMessageAsync: Cache HIT for target Channel {ChannelId}. Added to Updates.chats.", channel.id);
                        }
                        else
                        {
                            _logger.LogWarning("SendMessageAsync: Cache MISS or expired for target Channel {ChannelId}. Channel not added to Updates.chats from cache.", ipChannel.channel_id);
                        }
                    }
                    else if (peer is InputPeerSelf)
                    {
                        _logger.LogTrace("SendMessageAsync: Target peer is InputPeerSelf. Self user already added if _client.User is not null.");
                    }

                    _logger.LogInformation("SendMessageAsync: Successfully processed sent message and constructed Updates object for Peer (Type: {PeerType}, LoggedID: {PeerId}). Returning Updates.",
                        peerTypeForLog, peerIdForLog); // Used already defined vars
                    return updates;
                }
                else
                {
                    _logger.LogWarning("SendMessageAsync: _client.SendMessageAsync returned null for Peer (Type: {PeerType}, LoggedID: {PeerId}). Message (partial): '{MessageContent}'",
                        peerTypeForLog, peerIdForLog, truncatedMessage); // Used already defined vars
                    return null;
                }
            }
            catch (RpcException rpcEx)
            {
                _logger.LogError(rpcEx, "SendMessageAsync: RpcException for Peer (Type: {PeerType}, LoggedID: {PeerId}). Error: {ErrorTypeString}, Code: {ErrorCode}. Message (partial): '{MessageContent}'",
                    peerTypeForLog, // Pass defined variable
                    peerIdForLog,   // Pass defined variable
                    rpcEx.Message,
                    rpcEx.Code,
                    truncatedMessage); // Pass defined variable
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SendMessageAsync: Unhandled generic exception for Peer (Type: {PeerType}, LoggedID: {PeerId}). Message (partial): '{MessageContent}'",
                    peerTypeForLog, // Pass defined variable
                    peerIdForLog,   // Pass defined variable
                    truncatedMessage); // Pass defined variable
                return null;
            }
            finally
            {
                _logger.LogTrace("SendMessageAsync: Send lock (if acquired) has been released for key: {LockKey}", lockKey); // lockKey is now in scope
            }
        }




        // Assuming _logger is an ILogger instance available in the class
        // Assuming _client is an instance of WTelegram.Client

        // Assuming _logger is an ILogger instance available in the class
        // Assuming _client is an instance of WTelegram.Client

        // Assuming _logger is an ILogger instance available in the class
        // Assuming _client is an instance of WTelegram.Client

        public async Task<UpdatesBase?> ForwardMessagesAsync(
            InputPeer toPeer,
            int[] messageIds,
            InputPeer fromPeer,
            bool dropAuthor = false,
            bool dropMediaCaptions = false,
            bool noForwards = false)
        {
            // ... (logging setup for peer types, IDs, messageIdsString remains the same) ...
            string toPeerType = toPeer?.GetType().Name ?? "Unknown";
            long toPeerId = GetPeerIdForLog(toPeer);
            string fromPeerType = fromPeer?.GetType().Name ?? "Unknown";
            long fromPeerId = GetPeerIdForLog(fromPeer);
            string messageIdsString = messageIds != null && messageIds.Any()
                ? (messageIds.Length > 3 ? $"{string.Join(", ", messageIds.Take(3))}... (Total: {messageIds.Length})" : string.Join(", ", messageIds))
                : "None";

            _logger.LogInformation(
                "ForwardMessagesAsync: Attempting to forward messages. " +
                "From Peer (Type: {FromPeerType}, ID: {FromPeerId}) " +
                "To Peer (Type: {ToPeerType}, ID: {ToPeerId}). " +
                "Message IDs: [{MessageIdsArray}]. " +
                "DropAuthor: {DropAuthorFlag}, DropMediaCaptions: {DropMediaCaptionsFlag}, NoForwards: {NoForwardsFlag}",
                fromPeerType, fromPeerId,
                toPeerType, toPeerId,
                messageIdsString,
                dropAuthor, dropMediaCaptions, noForwards);

            if (toPeer == null || fromPeer == null || messageIds == null || !messageIds.Any())
            {
                _logger.LogWarning(
                    "ForwardMessagesAsync: Invalid parameters. " +
                    "ToPeer is null: {IsToPeerNull}, FromPeer is null: {IsFromPeerNull}, MessageIDs is null or empty: {AreMessageIdsInvalid}. Aborting forward.",
                    toPeer == null,
                    fromPeer == null,
                    messageIds == null || !messageIds.Any());
                return null;
            }

            try
            {
                // --- CORRECTED Random ID Generation (reverted to original correct form) ---
                var randomIdArray = messageIds.Select(_ => WTelegram.Helpers.RandomLong()).ToArray();
                _logger.LogDebug("ForwardMessagesAsync: Generated {RandomIdCount} random IDs using WTelegram.Helpers.RandomLong() for forwarding.", randomIdArray.Length);

                _logger.LogDebug(
                    "ForwardMessagesAsync: Calling _client.Messages_ForwardMessages. " +
                    "From Peer (Type: {FromPeerType}, ID: {FromPeerId}) " +
                    "To Peer (Type: {ToPeerType}, ID: {ToPeerId}).",
                    fromPeerType, fromPeerId,
                    toPeerType, toPeerId);

                UpdatesBase? result = await _client.Messages_ForwardMessages(
                    to_peer: toPeer,
                    from_peer: fromPeer,
                    id: messageIds,
                    random_id: randomIdArray, // Use the generated array
                    drop_author: dropAuthor,
                    drop_media_captions: dropMediaCaptions,
                    noforwards: noForwards
                );

                if (result != null)
                {
                    int updateCount = 0;
                    if (result is Updates u) updateCount = u.updates.Length;
                    else if (result is UpdatesCombined uc) updateCount = uc.updates.Length;

                    _logger.LogInformation(
                        "ForwardMessagesAsync: Successfully forwarded messages. Response type: {ResponseType}, Contained updates (approx): {UpdateCount}. " +
                        "From Peer (Type: {FromPeerType}, ID: {FromPeerId}) " +
                        "To Peer (Type: {ToPeerType}, ID: {ToPeerId}).",
                        result.GetType().Name,
                        updateCount,
                        fromPeerType, fromPeerId,
                        toPeerType, toPeerId);
                }
                else
                {
                    _logger.LogWarning(
                        "ForwardMessagesAsync: _client.Messages_ForwardMessages returned null. " +
                        "From Peer (Type: {FromPeerType}, ID: {FromPeerId}) " +
                        "To Peer (Type: {ToPeerType}, ID: {ToPeerId}). Message IDs: [{MessageIdsArray}]",
                        fromPeerType, fromPeerId,
                        toPeerType, toPeerId,
                        messageIdsString);
                }
                return result;
            }
            catch (RpcException rpcEx)
            {
                _logger.LogError(rpcEx,
                    "ForwardMessagesAsync: RpcException. Error: {ErrorTypeString}, Code: {ErrorCode}. " +
                    "From Peer (Type: {FromPeerType}, ID: {FromPeerId}) " +
                    "To Peer (Type: {ToPeerType}, ID: {ToPeerId}). Message IDs: [{MessageIdsArray}]",
                    rpcEx.Message,
                    rpcEx.Code,
                    fromPeerType, fromPeerId,
                    toPeerType, toPeerId,
                    messageIdsString);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ForwardMessagesAsync: Unhandled generic exception. " +
                    "From Peer (Type: {FromPeerType}, ID: {FromPeerId}) " +
                    "To Peer (Type: {ToPeerType}, ID: {ToPeerId}). Message IDs: [{MessageIdsArray}]",
                    fromPeerType, fromPeerId,
                    toPeerType, toPeerId,
                    messageIdsString);
                return null;
            }
        }



        // Helper function to get a numerical ID from InputPeer for logging
        private long GetPeerIdForLog(InputPeer? peer)
        {
            if (peer is InputPeerUser ipu) return ipu.user_id;
            if (peer is InputPeerChat ipc) return ipc.chat_id;
            if (peer is InputPeerChannel ipch) return ipch.channel_id;
            // InputPeerSelf doesn't have an explicit ID field in the same way,
            // it implies the current user. For logging, 0 or a specific marker can be used.
            if (peer is InputPeerSelf) return -1; // Or some other indicator for "self"
            return 0; // Default for unknown or null
        }
        public async Task<User?> GetSelfAsync()
        {
            try { return await _client.LoginUserIfNeeded(); }
            catch (Exception ex) { _logger.LogError(ex, "GetSelfAsync failed."); return null; }
        }

        public async Task<InputPeer?> ResolvePeerAsync(long peerId)
        {
            if (peerId == 0)
            {
                _logger.LogWarning("ResolvePeerAsync: Peer ID is 0, cannot resolve. Returning null.");
                return null;
            }

            try
            {
                if (peerId.ToString().StartsWith("-100"))
                {
                    long channelId = Math.Abs(peerId) - 1000000000000;
                    _logger.LogDebug("ResolvePeerAsync: Handling canonical channel ID {PeerId} (shortened to: {ChannelId})", peerId, channelId);

                    if (_chatCacheWithExpiry.TryGetValue(channelId, out var cachedData))
                    {
                        if (cachedData.Chat is Channel channelFromCache && cachedData.Expiry > DateTime.UtcNow)
                        {
                            _logger.LogDebug("ResolvePeerAsync: Found channel {ChannelId} (API ID: {ApiChannelId}) in cache. AccessHash: {AccessHash}",
                                             channelId, channelFromCache.id, channelFromCache.access_hash);
                            return new InputPeerChannel(channelFromCache.id, channelFromCache.access_hash);
                        }
                        else if (cachedData.Expiry <= DateTime.UtcNow)
                        {
                            _logger.LogDebug("ResolvePeerAsync: Found channel {ChannelId} in cache, but it has expired.", channelId);
                        }
                    }

                    _logger.LogDebug("ResolvePeerAsync: Channel {ChannelId} not found in cache or expired. Fetching from API.", channelId);
                    try
                    {
                        var channelsResponse = await _client.Channels_GetChannels(new[] { new InputChannel(channelId, 0) });

                        // --- Declaration and Assignment Correction for chatFromApi ---
                        // Declare chatFromApi here, initializing to null.
                        // This ensures it's in scope and definitely assigned before use in the 'else' path.
                        ChatBase? chatFromApi = null;

                        if (channelsResponse?.chats != null &&
                            channelsResponse.chats.TryGetValue(channelId, out chatFromApi) && // chatFromApi is assigned by 'out'.
                            chatFromApi is Channel telegramChannel)
                        {
                            // Success path: channel found and is of type Channel
                            _chatCacheWithExpiry[channelId] = (telegramChannel, DateTime.UtcNow.Add(_cacheExpiration));
                            _logger.LogDebug("ResolvePeerAsync: Retrieved channel {ChannelIdFromInput} (API ID: {ApiChannelId}) from API. AccessHash: {AccessHash}. Cache updated.",
                                             channelId, telegramChannel.id, telegramChannel.access_hash);
                            return new InputPeerChannel(telegramChannel.id, telegramChannel.access_hash);
                        }

                        // Failure path: If we didn't return above, something went wrong.
                        // chatFromApi is guaranteed to be assigned at this point:
                        // - null if channelsResponse or .chats was null (retains initial null).
                        // - null if TryGetValue returned false (out param set to default).
                        // - non-null if TryGetValue returned true but it wasn't a Channel.

                        // Now log the specific reason for failure using chatFromApi safely.
                        if (channelsResponse == null)
                        {
                            _logger.LogWarning("ResolvePeerAsync: Call to Channels_GetChannels returned null for input channel ID {InputChannelId}.", channelId);
                        }
                        else if (channelsResponse.chats == null)
                        {
                            _logger.LogWarning("ResolvePeerAsync: Channels_GetChannels response for input channel ID {InputChannelId} had a null chats collection.", channelId);
                        }
                        else if (chatFromApi == null) // True if TryGetValue returned false (key not found) OR if preceding conditions were false.
                        {
                            _logger.LogWarning("ResolvePeerAsync: Input channel ID {InputChannelId} not found in Channels_GetChannels response chats dictionary (TryGetValue returned false or was not reached).", channelId);
                        }
                        else // chatFromApi is not null here, so TryGetValue succeeded but 'chatFromApi is Channel' was false.
                        {
                            _logger.LogWarning("ResolvePeerAsync: Chat object for input ID {InputChannelId} (API ID: {ApiChatId}) from API was found but is not a Channel type. Actual type: {ActualType}.",
                                               channelId, chatFromApi.ID, chatFromApi.GetType().FullName);
                        }

                        _logger.LogWarning("ResolvePeerAsync: Could not resolve channel ID {PeerId} (short ID: {ChannelId}) via Channels_GetChannels due to previous warnings.", peerId, channelId);
                        return null; // Explicitly return null for this resolution path
                    }
                    catch (RpcException rpcEx) // Catch RpcException from Channels_GetChannels
                    {
                        _logger.LogError(rpcEx, "ResolvePeerAsync: RpcException during Channels_GetChannels for channelId {ChannelId} (original peerId {PeerId}). Error: {ErrorType}, Code: {ErrorCode}",
                                         channelId, peerId, rpcEx.Message, rpcEx.Code);
                        return null;
                    }
                    catch (Exception ex) // Catch other exceptions from Channels_GetChannels
                    {
                        _logger.LogWarning(ex, "ResolvePeerAsync: Generic exception during Channels_GetChannels for channelId {ChannelId} (original peerId {PeerId}).", channelId, peerId);
                        return null;
                    }
                }
                else // Not a "-100" prefixed ID
                {
                    _logger.LogDebug("ResolvePeerAsync: Peer ID {PeerId} is not a canonical channel ID. Attempting to resolve via Contacts_ResolveUsername.", peerId);
                    var resolved = await _client.Contacts_ResolveUsername(peerId.ToString());

                    if (resolved?.peer != null)
                    {
                        if (resolved.peer is PeerUser peerUser)
                        {
                            _logger.LogDebug("ResolvePeerAsync: Resolved peer ID {PeerId} to User {UserId} via Contacts_ResolveUsername.", peerId, peerUser.user_id);
                            return new InputPeerUser(peerUser.user_id, 0);
                        }
                        else if (resolved.peer is PeerChat peerChat)
                        {
                            _logger.LogDebug("ResolvePeerAsync: Resolved peer ID {PeerId} to Chat {ChatId} via Contacts_ResolveUsername.", peerId, peerChat.chat_id);
                            return new InputPeerChat(peerChat.chat_id);
                        }
                        else if (resolved.peer is PeerChannel peerChannel)
                        {
                            _logger.LogDebug("ResolvePeerAsync: Resolved peer ID {PeerId} to Channel {ChannelId} via Contacts_ResolveUsername.", peerId, peerChannel.channel_id);
                            return new InputPeerChannel(peerChannel.channel_id, 0);
                        }
                        else
                        {
                            _logger.LogWarning("ResolvePeerAsync: Contacts_ResolveUsername for {PeerId} returned an unknown peer type: {PeerType}", peerId, resolved.peer.GetType().Name);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("ResolvePeerAsync: Could not resolve peer ID {PeerId} via Contacts_ResolveUsername (result or peer was null).", peerId);
                    }
                    return null; // Return null if Contacts_ResolveUsername didn't yield a usable peer
                }
            }
            catch (RpcException rpcEx) // Catch RpcException from peerId.ToString() or other top-level operations.
            {
                _logger.LogError(rpcEx, "ResolvePeerAsync: RpcException at top-level for peer ID {PeerId}. Error: {ErrorType}, Code: {ErrorCode}", peerId, rpcEx.Message, rpcEx.Code);
                return null;
            }
            catch (Exception ex) // Catch other exceptions at top-level
            {
                _logger.LogError(ex, "ResolvePeerAsync: General exception at top-level for peer ID {PeerId}.", peerId);
                return null;
            }
        }
        public async ValueTask DisposeAsync()
        {
            _logger.LogInformation("Disposing TelegramUserApiClient...");
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