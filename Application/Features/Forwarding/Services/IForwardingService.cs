using System.Threading;
using System.Threading.Tasks;
using Domain.Features.Forwarding.Entities;

namespace Application.Features.Forwarding.Services
{
    public interface IForwardingService
    {
        Task<ForwardingRule?> GetRuleAsync(string ruleName, CancellationToken cancellationToken = default);
        Task<IEnumerable<ForwardingRule>> GetAllRulesAsync(CancellationToken cancellationToken = default);
        Task<IEnumerable<ForwardingRule>> GetRulesBySourceChannelAsync(long sourceChannelId, CancellationToken cancellationToken = default);
        Task CreateRuleAsync(ForwardingRule rule, CancellationToken cancellationToken = default);
        Task UpdateRuleAsync(ForwardingRule rule, CancellationToken cancellationToken = default);
        Task DeleteRuleAsync(string ruleName, CancellationToken cancellationToken = default);
        Task ProcessMessageAsync(long sourceChannelId, long messageId, CancellationToken cancellationToken = default);
    }
} 