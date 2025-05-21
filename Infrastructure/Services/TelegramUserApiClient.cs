// File: src/Infrastructure/Services/TelegramUserApiClient.cs
#region Usings
using Application.Common.Interfaces;
using Infrastructure.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TL;        // For core Telegram types
using WTelegram; // For Client, Helpers, and WTC extension methods
using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;
using System.Threading.Tasks;
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
                if (updates is UpdatesBase updatesBase)
                {
                    await HandleUpdatesBaseAsync(updatesBase);
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

        private string? AskCode(string question, string? sourceMethod)
        {
            if (string.IsNullOrWhiteSpace(sourceMethod)) sourceMethod = "console";
            _logger.LogInformation("WTC Request: {Question} (Source: {SourceMethod})", question.Trim(), sourceMethod);
            if (sourceMethod.Equals("console", StringComparison.OrdinalIgnoreCase))
            {
                Console.Write(question); return Console.ReadLine();
            }
            _logger.LogWarning("AskCode src '{SourceMethod}' NI for '{Question}'.", sourceMethod, question.Trim()); return null;
        }
        #endregion

        #region WTelegramClient Update Handler
        private async Task HandleUpdatesBaseAsync(UpdatesBase updatesBase) // From _client.OnUpdates
        {
            _logger.LogTrace("HandleUpdatesBaseAsync: Received UpdatesBase of type {Type}", updatesBase.GetType().Name);
            updatesBase.CollectUsersChats(_userCache, _chatCache);

            List<Update> updatesToDispatch = new List<Update>();

            // 1. Handle container updates like Updates and UpdatesCombined
            if (updatesBase is Updates updatesContainer && updatesContainer.updates != null)
            {
                updatesToDispatch.AddRange(updatesContainer.updates);
            }
            else if (updatesBase is UpdatesCombined updatesCombinedContainer && updatesCombinedContainer.updates != null)
            {
                updatesToDispatch.AddRange(updatesCombinedContainer.updates);
            }
            // 2. Handle standalone "short" updates by converting them to their "fuller" TL.Update equivalents
            else if (updatesBase is UpdateShortMessage usm)
            {
                Peer userPeer;
                if (_userCache.TryGetValue(usm.user_id, out var cachedUser))
                {
                    // Convert User to PeerUser
                    userPeer = new PeerUser { user_id = cachedUser.id };
                }
                else
                {
                    // Fallback: Create a minimal PeerUser. This peer object will lack access_hash.
                    // WTelegramClient's ToInputPeer(_client) might resolve it later if it's in recent dialogs.
                    userPeer = new PeerUser { user_id = usm.user_id };
                    _logger.LogWarning("HandleUpdatesBaseAsync: User {UserId} from UpdateShortMessage not in cache, using minimal PeerUser.", usm.user_id);
                }

                var msg = new Message
                {
                    flags = 0, // Initialize
                    id = usm.id,
                    peer_id = userPeer, // The chat this message belongs to (in PM, it's the other user)
                    from_id = userPeer, // The sender (for UpdateShortMessage, it's the user_id)
                    message = usm.message,
                    date = usm.date,
                    entities = usm.entities,
                    media = null, // UpdateShortMessage schema does not directly provide a full MessageMedia object
                    reply_to = usm.reply_to, // UpdateShortMessage has this
                    fwd_from = usm.fwd_from, // UpdateShortMessage has this
                    via_bot_id = usm.via_bot_id,
                    ttl_period = usm.ttl_period
                };

                // Set message flags based on usm flags
                var uf = usm.flags;
                if (uf.HasFlag(UpdateShortMessage.Flags.out_))
                    msg.flags |= Message.Flags.out_;

                if (uf.HasFlag(UpdateShortMessage.Flags.mentioned))
                    msg.flags |= Message.Flags.mentioned;

                if (uf.HasFlag(UpdateShortMessage.Flags.silent))
                    msg.flags |= Message.Flags.silent;

                if (uf.HasFlag(UpdateShortMessage.Flags.media_unread))
                    msg.flags |= Message.Flags.media_unread;

                updatesToDispatch.Add(new UpdateNewMessage { message = msg, pts = usm.pts, pts_count = usm.pts_count });
            }
            else if (updatesBase is UpdateShortChatMessage uscm)
            {
                Peer chatPeer;
                if (_chatCache.TryGetValue(uscm.chat_id, out var cachedChat))
                {
                    // Convert ChatBase to appropriate Peer type
                    if (cachedChat is Channel channel)
                    {
                        chatPeer = new PeerChannel { channel_id = channel.id };
                    }
                    else
                    {
                        chatPeer = new PeerChat { chat_id = cachedChat.ID };
                    }
                }
                else
                {
                    chatPeer = new PeerChat { chat_id = uscm.chat_id };
                    _logger.LogWarning("HandleUpdatesBaseAsync: Chat {ChatId} from UpdateShortChatMessage not in cache, using minimal PeerChat.", uscm.chat_id);
                }

                Peer? fromPeer = null; // from_id is critical for group messages
                if (_userCache.TryGetValue(uscm.from_id, out var cachedFromUser))
                {
                    // Convert User to PeerUser
                    fromPeer = new PeerUser { user_id = cachedFromUser.id };
                }
                else
                {
                    fromPeer = new PeerUser { user_id = uscm.from_id };
                    _logger.LogWarning("HandleUpdatesBaseAsync: User {UserId} (from_id) from UpdateShortChatMessage not in cache, using minimal PeerUser.", uscm.from_id);
                }

                var msg = new Message
                {
                    flags = 0, // Initialize
                    id = uscm.id,
                    peer_id = chatPeer, // The group/chat where message appeared
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

                updatesToDispatch.Add(new UpdateNewMessage { message = msg, pts = uscm.pts, pts_count = uscm.pts_count });
            }
            else if (updatesBase is UpdateShortSentMessage /* ussm */)
            {
                // As before, typically ignored for message forwarding scenarios.
                _logger.LogTrace("Ignoring UpdateShortSentMessage for OnCustomUpdateReceived dispatch.");
            }

            // Process the collected TL.Update objects
            foreach (var update in updatesToDispatch)
            {
                OnCustomUpdateReceived?.Invoke(update);
            }
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
                    foreach (var user in _userCache)
                    {
                        _userCacheWithExpiry[user.Key] = (user.Value, DateTime.UtcNow.Add(_cacheExpiration));
                    }
                    foreach (var chat in _chatCache)
                    {
                        _chatCacheWithExpiry[chat.Key] = (chat.Value, DateTime.UtcNow.Add(_cacheExpiration));
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
                    // Extract the DC number from the error message
                    if (int.TryParse(e.Message.Split('_').Last(), out int dcNumber))
                    {
                        _logger.LogWarning("User API: Phone number needs to be migrated to DC{DCNumber}. Reconnecting...", dcNumber);
                        
                        // Dispose current client
                        _client.OnUpdates -= HandleUpdatesBaseAsync;
                        _client.Dispose();
                        _client = null;

                        // Create new client with the correct DC
                        //_client = new WTelegram.Client(ConfigProvider, dcNumber);
                        //_client.OnUpdates += async updates =>
                        //{
                        //    if (updates is UpdatesBase updatesBase)
                        //    {
                        //        await HandleUpdatesBaseAsync(updatesBase);
                        //    }
                        //};

                        // Try logging in again with the new DC
                        User loggedInUser = await _client.LoginUserIfNeeded();
                        _logger.LogInformation("User API Logged in on DC{DCNumber}: {User}", dcNumber, loggedInUser?.ToString());
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
                    // If the semaphore is disposed, create a new one
                    _connectionLock = new SemaphoreSlim(1, 1);
                }
            }
        }

        public async Task<Messages_MessagesBase?> GetMessagesAsync(InputPeer peer, params int[] messageIds)
        {
            if (peer == null || !messageIds.Any()) return null;
            try
            {
                // Check cache first
                var cacheKey = $"msg_{peer.GetHashCode()}_{string.Join("_", messageIds)}";
                if (_messageCache.TryGetValue(cacheKey, out Messages_MessagesBase? cachedMessages))
                {
                    return cachedMessages;
                }

                var msgIdObjects = messageIds.Select(id => new InputMessageID { id = id }).ToArray();
                var messages = await _client.Messages_GetMessages(msgIdObjects);
                
                // Cache the result
                if (messages != null)
                {
                    _messageCache.Set(cacheKey, messages, _cacheOptions);
                }
                
                return messages;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetMessagesAsync failed.");
                return null;
            }
        }

        public async Task<UpdatesBase?> SendMessageAsync(InputPeer peer, string message, MessageEntity[]? entities = null, InputMedia? media = null, long? replyToMsgId = null, bool noWebpage = false)
        {
            if (peer == null) return null;
            try
            {
                int replyTo = replyToMsgId.HasValue ? (int)replyToMsgId.Value : 0;
                
                // Use a semaphore to prevent concurrent sends to the same peer
                using var sendLock = await AsyncLock.LockAsync($"send_{peer.GetHashCode()}");
                
                Message sentMessage = await _client.SendMessageAsync(peer, message,
                    entities: entities, media: media, reply_to_msg_id: replyTo);

                if (sentMessage != null)
                {
                    var updates = new Updates
                    {
                        updates = new Update[] { new UpdateNewMessage { message = sentMessage } },
                        users = new Dictionary<long, User>(),
                        chats = new Dictionary<long, ChatBase>(),
                        date = sentMessage.date,
                        seq = 0
                    };

                    // Add self to users
                    if (_client.User != null)
                    {
                        updates.users[_client.User.id] = _client.User;
                    }

                    // Add target peer to appropriate collection
                    if (peer is InputPeerUser ipUser)
                    {
                        if (_userCacheWithExpiry.TryGetValue(ipUser.user_id, out var userData) && userData.Expiry > DateTime.UtcNow)
                        {
                            updates.users[userData.User.id] = userData.User;
                        }
                    }
                    else if (peer is InputPeerChat ipChat)
                    {
                        if (_chatCacheWithExpiry.TryGetValue(ipChat.chat_id, out var chatData) && chatData.Expiry > DateTime.UtcNow)
                        {
                            updates.chats[chatData.Chat.ID] = chatData.Chat;
                        }
                    }
                    else if (peer is InputPeerChannel ipChannel)
                    {
                        if (_chatCacheWithExpiry.TryGetValue(ipChannel.channel_id, out var channelData) && channelData.Expiry > DateTime.UtcNow)
                        {
                            updates.chats[channelData.Chat.ID] = channelData.Chat;
                        }
                    }

                    return updates;
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SendMessageAsync failed.");
                return null;
            }
        }

        public async Task<UpdatesBase?> ForwardMessagesAsync(InputPeer toPeer, int[] messageIds, InputPeer fromPeer, bool dropAuthor = false, bool dropMediaCaptions = false, bool noForwards = false)
        {
            if (toPeer == null || fromPeer == null || !messageIds.Any()) return null;
            try
            {
                var randomIdArray = messageIds.Select(_ => WTelegram.Helpers.RandomLong()).ToArray();
                // As per your 4.3.4 notes, it's client.Messages_ForwardMessages(...) and the last arg is randomIdArray
                // The flags drop_author, etc., are optional named parameters in WTC's C# wrapper
                // for the low-level RPC call.
                // The return is UpdatesBase for Messages_ForwardMessages.
                return await _client.Messages_ForwardMessages(
                    to_peer: toPeer,
                    from_peer: fromPeer,
                    id: messageIds, // message IDs to forward
                    random_id: randomIdArray, // required random IDs
                    // Optional flags for the call:
                    drop_author: dropAuthor,
                    drop_media_captions: dropMediaCaptions,
                    noforwards: noForwards
                );
            }
            catch (Exception ex) { _logger.LogError(ex, "ForwardMessagesAsync failed."); return null; }
        }

        public async Task<User?> GetSelfAsync()
        {
            try { return await _client.LoginUserIfNeeded(); }
            catch (Exception ex) { _logger.LogError(ex, "GetSelfAsync failed."); return null; }
        }

        public async Task<InputPeer?> ResolvePeerAsync(long peerId)
        {
            if (peerId == 0) return null;
            try
            {
                // Use Contacts_ResolveUsername for user IDs
                var resolved = await _client.Contacts_ResolveUsername(peerId.ToString());
                if (resolved != null)
                {
                    // Convert the resolved peer to InputPeer
                    if (resolved.peer is PeerUser peerUser)
                    {
                        return new InputPeerUser(peerUser.user_id, 0); // access_hash will be resolved by WTelegram
                    }
                    else if (resolved.peer is PeerChat peerChat)
                    {
                        return new InputPeerChat(peerChat.chat_id);
                    }
                    else if (resolved.peer is PeerChannel peerChannel)
                    {
                        return new InputPeerChannel(peerChannel.channel_id, 0); // access_hash will be resolved by WTelegram
                    }
                }
                _logger.LogWarning("WTelegram: Could not resolve peer ID {PeerId}.", peerId);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WTelegram: Exception in ResolvePeerAsync for ID {PeerId}.", peerId);
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

        // Helper class for async locking
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
    }
}