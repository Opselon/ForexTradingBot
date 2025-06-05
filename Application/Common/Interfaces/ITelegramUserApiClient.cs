// Application/Common/Interfaces/ITelegramUserApiClient.cs
using System;
using System.Collections.Generic; // Used for ICollection<T>
using System.Threading;
using System.Threading.Tasks;
using TL; // WTelegramClient's namespace for Telegram types

namespace Application.Common.Interfaces
{
    public interface ITelegramUserApiClient : IAsyncDisposable
    {
        WTelegram.Client NativeClient { get; }
        event Action<TL.Update> OnCustomUpdateReceived;

        Task ConnectAndLoginAsync(CancellationToken cancellationToken);

        Task<TL.Messages_MessagesBase> GetMessagesAsync(TL.InputPeer peer, int[] msgIds, CancellationToken cancellationToken);

        Task<TL.UpdatesBase> SendMessageAsync(
          TL.InputPeer peer,
          string message,
          CancellationToken cancellationToken, // CancellationToken is required and comes first among optionals
          int? replyToMsgId = null,
          TL.ReplyMarkup? replyMarkup = null,
          IEnumerable<TL.MessageEntity>? entities = null,
          bool noWebpage = false,
          bool background = false,
          bool clearDraft = false,
          DateTime? schedule_date = null,
          TL.InputMedia? media = null); // TL.InputMedia instead of InputSingleMedia for single media messages


        /// <summary>
        /// Sends a group of media items (an album) to a peer.
        /// </summary>
        // THIS IS THE CRITICAL METHOD FOR THIS ERROR. ITS SIGNATURE MUST BE IDENTICAL.
        Task SendMediaGroupAsync(
            TL.InputPeer peer,
            ICollection<TL.InputMedia> media, // This MUST be ICollection<TL.InputMedia>
            CancellationToken cancellationToken, // This MUST be the 3rd parameter and required
            string? albumCaption = null,
            TL.MessageEntity[]? albumEntities = null,
            int? replyToMsgId = null,
            bool background = false,
            DateTime? schedule_date = null,
            bool sendAsBot = false);


        Task<TL.UpdatesBase?> ForwardMessagesAsync(
            TL.InputPeer toPeer,
            int[] messageIds,
            TL.InputPeer fromPeer,
            CancellationToken cancellationToken,
            bool dropAuthor = false,
            bool noForwards = false);


        Task<TL.User?> GetSelfAsync(CancellationToken cancellationToken);

        Task<TL.InputPeer?> ResolvePeerAsync(long peerId, CancellationToken cancellationToken);
    }
}