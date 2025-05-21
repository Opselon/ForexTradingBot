// File: src/Application/Common/Interfaces/ITelegramUserApiClient.cs
using System;
using System.Threading;
using System.Threading.Tasks;
using TL; // This is WTelegramClient's namespace for Telegram types

namespace Application.Common.Interfaces
{
    public interface ITelegramUserApiClient : IAsyncDisposable // Good practice to make it IAsyncDisposable
    {
        /// <summary>
        /// Exposes the native WTelegram.Client object if advanced, direct access is needed.
        /// Use with caution as it bypasses this abstraction.
        /// </summary>
        WTelegram.Client NativeClient { get; }

        /// <summary>
        /// Event raised when a relevant new or edited message update is received.
        /// The TL.Update type is from WTelegramClient.
        /// This event should provide the specific TL.Update object (e.g., UpdateNewMessage, UpdateNewChannelMessage).
        /// </summary>
        event Action<Update> OnCustomUpdateReceived;

        /// <summary>
        /// Connects to Telegram and logs in the user account if not already connected/logged in.
        /// Handles session loading/saving.
        /// </summary>
        Task ConnectAndLoginAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves specific messages by their IDs from a given peer.
        /// </summary>
        /// <param name="peer">The InputPeer of the chat/channel where messages are located.</param>
        /// <param name="messageIds">An array of message IDs to retrieve.</param>
        /// <returns>A Messages_MessagesBase object containing the fetched messages, or null on error.</returns>
        Task<Messages_MessagesBase?> GetMessagesAsync(InputPeer peer, params int[] messageIds);

        /// <summary>
        /// Sends a message (text or media) to a peer.
        /// </summary>
        /// <param name="peer">The target InputPeer.</param>
        /// <param name="message">The text message or caption for media.</param>
        /// <param name="entities">Optional message entities for formatting.</param>
        /// <param name="media">Optional InputMedia to send (for photos, videos, documents). If sending existing media, this would be like InputMediaPhoto, InputMediaDocument.</param>
        /// <param name="replyToMsgId">Optional ID of the message to reply to.</param>
        /// <param name="noWebpage">If true, disables link previews for this message.</param>
        /// <returns>An UpdatesBase object representing the result of the send operation, or null on error.</returns>
        Task<UpdatesBase?> SendMessageAsync(InputPeer peer, string message, MessageEntity[]? entities = null, InputMedia? media = null, long? replyToMsgId = null, bool noWebpage = false);

        /// <summary>
        /// Forwards messages from one peer to another.
        /// </summary>
        /// <param name="toPeer">The target InputPeer to forward messages to.</param>
        /// <param name="messageIds">An array of message IDs to forward from the fromPeer.</param>
        /// <param name="fromPeer">The source InputPeer from which messages are forwarded.</param>
        /// <param name="dropAuthor">If true, sender's name will be hidden (message appears as sent by you). WTelegram equivalent is `drop_author`.</param>
        /// <param name="dropMediaCaptions">If true, media captions will be removed. WTelegram equivalent is `drop_media_captions`.</param>
        /// <param name="noForwards">If true, the forwarded message will not be linkable to the original message (removes "forwarded from" header). WTelegram equivalent is `noforwards`.</param>
        /// <returns>An UpdatesBase object representing the result of the forward operation, or null on error.</returns>
        Task<UpdatesBase?> ForwardMessagesAsync(InputPeer toPeer, int[] messageIds, InputPeer fromPeer, bool dropAuthor = false, bool dropMediaCaptions = false, bool noForwards = false);

        /// <summary>
        /// Gets the currently logged-in user's information.
        /// </summary>
        /// <returns>A TL.User object representing the current user, or null on error.</returns>
        Task<User?> GetSelfAsync();

        /// <summary>
        /// Resolves a peer ID (user, chat, or channel) into an InputPeer object.
        /// This is crucial for converting IDs from configuration or other sources into usable WTelegramClient types.
        /// The implementation should handle access_hash if necessary or rely on WTelegramClient's internal resolution.
        /// </summary>
        /// <param name="peerId">The numeric ID of the peer.
        /// For users: positive ID.
        /// For chats: negative ID.
        /// For channels: usually the "short" ID or the "marked" ID (-100... format).</param>
        /// <returns>An InputPeer object, or null if resolution fails.</returns>
        Task<InputPeer?> ResolvePeerAsync(long peerId);

        // Specific resolvers might be useful if you often know the type
        // Task<InputUser?> ResolveInputUserAsync(long userId);
        // Task<InputChannel?> ResolveInputChannelAsync(long channelId); // channelId here is the "short" positive ID
        // Task<ChatBase?> GetChatAsync(InputPeer peer); // This was in your code, might be useful
    }
}