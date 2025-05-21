using System.Threading;
using System.Threading.Tasks;

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
        /// <param name="sourceMessageId">ID of the source message to forward</param>
        /// <param name="rawSourcePeerId">Raw ID of the source peer (channel/chat)</param>
        /// <param name="targetChannelId">ID of the target channel to forward to</param>
        /// <param name="ruleName">Name of the forwarding rule to apply</param>
        /// <param name="cancellationToken">Token for cancellation</param>
        /// <returns>A task representing the asynchronous operation</returns>
        Task ProcessAndRelayMessageAsync(
            int sourceMessageId,
            long rawSourcePeerId,
            long targetChannelId,
            string ruleName,
            CancellationToken cancellationToken);
    }
} 