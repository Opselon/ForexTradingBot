using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Features.Forwarding.Entities;
using Domain.Features.Forwarding.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Infrastructure.Data;

namespace Infrastructure.Features.Forwarding.Repositories
{
    public class ForwardingRuleRepository : IForwardingRuleRepository
    {
        private readonly AppDbContext _dbContext;
        private readonly ILogger<ForwardingRuleRepository> _logger;

        public ForwardingRuleRepository(
            AppDbContext dbContext,
            ILogger<ForwardingRuleRepository> logger)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<ForwardingRule?> GetByIdAsync(string ruleName, CancellationToken cancellationToken = default)
        {
            return await _dbContext.ForwardingRules
                .FirstOrDefaultAsync(r => r.RuleName == ruleName, cancellationToken);
        }

        public async Task<IEnumerable<ForwardingRule>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return await _dbContext.ForwardingRules
                .ToListAsync(cancellationToken);
        }

        public async Task<IEnumerable<ForwardingRule>> GetBySourceChannelAsync(long sourceChannelId, CancellationToken cancellationToken = default)
        {
            return await _dbContext.ForwardingRules
                .Where(r => r.SourceChannelId != null && r.SourceChannelId == sourceChannelId)
                .ToListAsync(cancellationToken);
        }

        public async Task AddAsync(ForwardingRule rule, CancellationToken cancellationToken = default)
        {
            await _dbContext.ForwardingRules.AddAsync(rule, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Added forwarding rule: {RuleName}", rule.RuleName);
        }

        public async Task UpdateAsync(ForwardingRule rule, CancellationToken cancellationToken = default)
        {
            _dbContext.ForwardingRules.Update(rule);
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Updated forwarding rule: {RuleName}", rule.RuleName);
        }

        public async Task DeleteAsync(string ruleName, CancellationToken cancellationToken = default)
        {
            var rule = await GetByIdAsync(ruleName, cancellationToken);
            if (rule != null)
            {
                _dbContext.ForwardingRules.Remove(rule);
                await _dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Deleted forwarding rule: {RuleName}", ruleName);
            }
        }
    }
} 