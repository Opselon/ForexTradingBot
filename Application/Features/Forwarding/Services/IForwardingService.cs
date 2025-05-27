using Domain.Features.Forwarding.Entities;
using TL;

public interface IForwardingService
{
    Task<ForwardingRule?> GetRuleAsync(string ruleName, CancellationToken cancellationToken = default);
    Task<IEnumerable<ForwardingRule>> GetAllRulesAsync(CancellationToken cancellationToken = default);
    Task<IEnumerable<ForwardingRule>> GetRulesBySourceChannelAsync(long sourceChannelId, CancellationToken cancellationToken = default);
    Task CreateRuleAsync(ForwardingRule rule, CancellationToken cancellationToken = default);
    Task UpdateRuleAsync(ForwardingRule rule, CancellationToken cancellationToken = default);
    Task DeleteRuleAsync(string ruleName, CancellationToken cancellationToken = default);

    // THIS IS THE SIGNATURE THAT ForwardingService.cs NOW IMPLEMENTS
    Task ProcessMessageAsync(
               long sourceChannelIdForMatching,
               long originalMessageId,
               long rawSourcePeerIdForApi,
               string messageContent,
               TL.MessageEntity[]? messageEntities,
               Peer? senderPeerForFilter,
               InputMedia? inputMediaToSend, // NEW
               CancellationToken cancellationToken);

}