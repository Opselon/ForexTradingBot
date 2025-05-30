// File: src/Application/Common/Interfaces/ITelegramUserApiClient.cs
using System;
using System.Collections.Generic; // Added for ICollection<MessageEntity> in SendMessageAsync
using System.Threading;
using System.Threading.Tasks;
using TL; // This is WTelegramClient's namespace for Telegram types

namespace Application.Common.Interfaces
{
    public interface ITelegramUserApiClient : IAsyncDisposable
    {
        WTelegram.Client NativeClient { get; }
        event Action<TL.Update> OnCustomUpdateReceived;
        Task ConnectAndLoginAsync(CancellationToken cancellationToken = default);
        Task<TL.Messages_MessagesBase> GetMessagesAsync(TL.InputPeer peer, params int[] msgIds);

        Task<TL.UpdatesBase> SendMessageAsync(TL.InputPeer peer, string message, long? replyToMsgId = null,
                                              TL.ReplyMarkup? replyMarkup = null, IEnumerable<TL.MessageEntity>? entities = null,
                                              bool noWebpage = false, bool background = false, bool clearDraft = false,
                                              DateTime? schedule_date = null,
                                              bool sendAsBot = false,
                                              TL.InputMedia? media = null, int[]? parsedMentions = null);

        /// <summary>
        /// Sends a group of media items (an album) to a peer.
        /// </summary>
        /// <param name="peer">The target InputPeer.</param>
        /// <param name="media">An array of InputSingleMedia objects representing the media items for the album.
        /// The caption for the entire album will be taken from the 'message' field of the first InputSingleMedia item.</param>
        /// <param name="albumCaption">The caption for the entire album.</param>
        /// <param name="albumEntities">Optional message entities for the album's caption.</param>
        /// <param name="replyToMsgId">Optional ID of the message to reply to.</param>
        /// <param name="background">If true, sends the message in the background.</param>
        /// <param name="schedule_date">Optional date to schedule the message for sending.</param>
        /// <param name="sendAsBot">Indicates if the message should be sent anonymously as a channel bot (if applicable and supported by WTelegramClient's underlying methods).</param>
        /// <param name="parsedMentions">Optional list of user IDs that were explicitly mentioned for client-side parsing (might not be directly used by the low-level API call).</param>
        Task SendMediaGroupAsync(TL.InputPeer peer, TL.InputSingleMedia[] media, string albumCaption = null, TL.MessageEntity[]? albumEntities = null, long? replyToMsgId = null,
                            bool background = false, DateTime? schedule_date = null,
                            bool sendAsBot = false, int[]? parsedMentions = null);


        Task<TL.UpdatesBase?> ForwardMessagesAsync(TL.InputPeer toPeer, int[] messageIds, TL.InputPeer fromPeer,
                                                           bool dropAuthor = false, bool noForwards = false,
                                                           int? topMsgId = null, DateTime? scheduleDate = null,
                                                           bool sendAsBot = false);


        Task<TL.User?> GetSelfAsync();

        Task<TL.InputPeer?> ResolvePeerAsync(long peerId);
    }
}
