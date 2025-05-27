// File: Application\Features\Forwarding\Interfaces\IForwardingJobActions.cs
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Domain.Features.Forwarding.Entities;
using TL; // استفاده از Domain.ForwardingRule

namespace Application.Features.Forwarding.Interfaces
{
    /// <summary>
    /// Interface for handling message forwarding jobs
    /// </summary>
    public interface IForwardingJobActions
    {
        /// <summary>
        /// Processes a message and relays it to a target channel based on forwarding rules
        /// </summary>
        /// <param name="sourceMessageId">ID of the source message to forward (TL.Message.id is int)</param>
        /// <param name="rawSourcePeerId">Positive ID of the source peer (channel/chat/user) for Telegram API calls</param>
        /// <param name="targetChannelId">The ID of the target channel/chat/user (from database, could be -100xxxx)</param>
        /// <param name="rule">The Domain ForwardingRule object</param>
        /// <param name="cancellationToken">Token for cancellation</param>
        /// <returns>A task representing the asynchronous operation</returns>
        Task ProcessAndRelayMessageAsync(
               int sourceMessageId,
               long rawSourcePeerId,
               long targetChannelId,
               Domain.Features.Forwarding.Entities.ForwardingRule rule,
               string messageContent,
               MessageEntity[]? messageEntities,
               Peer? senderPeerForFilter,          // جدید: FromPeer اصلی پیام (از آبجکت Update)
                 InputMedia? inputMediaToSend, // اگر مدیا دارد، اینجا آماده
               CancellationToken cancellationToken);
    }
}