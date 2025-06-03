using Domain.Features.Forwarding.Entities;
using Hangfire.Server; // Add this using directive for PerformContext
using System.Collections.Generic;
using System.Threading; // Essential for CancellationToken
using System.Threading.Tasks;
using TL;
// No Hangfire.Server using here in the interface; it's clean

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
        /// <param name="rawSourcePeerId">Original ID of the source peer (channel/chat/user) for Telegram API calls</param>
        /// <param name="targetChannelId">The ID of the target channel/chat/user (from database, could be -100xxxx)</param>
        /// <param name="rule">The Domain ForwardingRule object</param>
        /// <param name="messageContent">Main caption/text for the message or media group. Only applies if mediaGroupItems is null or for the first media item in a group.</param>
        /// <param name="messageEntities">Entities for the main caption.</param>
        /// <param name="senderPeerForFilter">Original sender peer for filtering.</param>
        /// <param name="mediaGroupItems">A list of media items with their individual captions/entities for media groups.</param>
        /// <param name="cancellationToken">Token for cancellation of the job execution (provided by Hangfire).</param>
        /// <param name="performContext">Hangfire's PerformContext for job-specific execution details.</param> // <-- NEW PARAMETER
        /// <returns>A task representing the asynchronous operation</returns>
        Task ProcessAndRelayMessageAsync(
               int sourceMessageId,
               long rawSourcePeerId,
               long targetChannelId,
               Domain.Features.Forwarding.Entities.ForwardingRule rule,
               string messageContent,
               MessageEntity[]? messageEntities,
               Peer? senderPeerForFilter,
               List<InputMediaWithCaption>? mediaGroupItems,
               CancellationToken cancellationToken,
               PerformContext performContext); // <--- ADDED PerformContext
    }
}