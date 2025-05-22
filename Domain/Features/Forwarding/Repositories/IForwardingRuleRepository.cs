using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Domain.Features.Forwarding.Entities;

namespace Domain.Features.Forwarding.Repositories
{
    public interface IForwardingRuleRepository
    {
        Task<ForwardingRule?> GetByIdAsync(string ruleName, CancellationToken cancellationToken = default);
        Task<IEnumerable<ForwardingRule>> GetAllAsync(CancellationToken cancellationToken = default);
        Task<IEnumerable<ForwardingRule>> GetBySourceChannelAsync(long sourceChannelId, CancellationToken cancellationToken = default);
        Task AddAsync(ForwardingRule rule, CancellationToken cancellationToken = default);
        Task UpdateAsync(ForwardingRule rule, CancellationToken cancellationToken = default);
        Task DeleteAsync(string ruleName, CancellationToken cancellationToken = default);
    }
} 