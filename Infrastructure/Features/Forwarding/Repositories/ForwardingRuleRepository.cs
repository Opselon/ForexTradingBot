// File: Infrastructure/Features/Forwarding/Repositories/ForwardingRuleRepository.cs

#region Usings
// Standard .NET & NuGet
using System;
using System.Collections.Generic;
using System.Linq; // Required for Where extension method
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore; // For EF Core functionalities
using Microsoft.Extensions.Logging;   // For logging

// Project specific
using Domain.Features.Forwarding.Entities;     // For ForwardingRule entity
using Domain.Features.Forwarding.Repositories; // For IForwardingRuleRepository interface
using Infrastructure.Data;                     // For AppDbContext (assuming this is the correct namespace)
#endregion

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

        #region Read Operations

        public async Task<ForwardingRule?> GetByIdAsync(string ruleName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ruleName))
            {
                _logger.LogWarning("ForwardingRuleRepository: GetByIdAsync called with null or empty ruleName.");
                return null;
            }
            _logger.LogTrace("ForwardingRuleRepository: Fetching forwarding rule by RuleName: {RuleName}", ruleName);
            return await _dbContext.ForwardingRules
                .FirstOrDefaultAsync(r => r.RuleName == ruleName, cancellationToken);
        }

        public async Task<IEnumerable<ForwardingRule>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogTrace("ForwardingRuleRepository: Fetching all forwarding rules, AsNoTracking.");
            return await _dbContext.ForwardingRules
                .AsNoTracking()
                .OrderBy(r => r.RuleName)
                .ToListAsync(cancellationToken);
        }

        public async Task<IEnumerable<ForwardingRule>> GetBySourceChannelAsync(long sourceChannelId, CancellationToken cancellationToken = default)
        {
            _logger.LogTrace("ForwardingRuleRepository: Fetching forwarding rules by SourceChannelId: {SourceChannelId}, AsNoTracking.", sourceChannelId);
            return await _dbContext.ForwardingRules
                .Where(r => r.SourceChannelId == sourceChannelId)
                .AsNoTracking()
                .OrderBy(r => r.RuleName)
                .ToListAsync(cancellationToken);
        }

        #endregion

        #region Write Operations (with SaveChangesAsync inside Repository)

        public async Task AddAsync(ForwardingRule rule, CancellationToken cancellationToken = default)
        {
            if (rule == null)
            {
                _logger.LogError("ForwardingRuleRepository: Attempted to add a null ForwardingRule object.");
                throw new ArgumentNullException(nameof(rule));
            }
            if (string.IsNullOrWhiteSpace(rule.RuleName))
            {
                _logger.LogError("ForwardingRuleRepository: Attempted to add a ForwardingRule with null or empty RuleName.");
                throw new ArgumentException("RuleName cannot be null or empty.", nameof(rule.RuleName));
            }
            _logger.LogInformation("ForwardingRuleRepository: Adding forwarding rule with RuleName: {RuleName}, SourceChannelId: {SourceChannelId}",
                                   rule.RuleName, rule.SourceChannelId);
            try
            {
                await _dbContext.ForwardingRules.AddAsync(rule, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("ForwardingRuleRepository: Successfully added forwarding rule: {RuleName}", rule.RuleName);
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogError(dbEx, "ForwardingRuleRepository: Error adding forwarding rule {RuleName} to the database.", rule.RuleName);
                throw;
            }
        }

        public async Task UpdateAsync(ForwardingRule rule, CancellationToken cancellationToken = default)
        {
            if (rule == null)
            {
                _logger.LogError("ForwardingRuleRepository: Attempted to update with a null ForwardingRule object.");
                throw new ArgumentNullException(nameof(rule));
            }
            if (string.IsNullOrWhiteSpace(rule.RuleName))
            {
                _logger.LogError("ForwardingRuleRepository: Attempted to update a ForwardingRule without a valid RuleName (used as identifier).");
                throw new ArgumentException("RuleName cannot be null or empty for update identification.", nameof(rule.RuleName));
            }
            _logger.LogInformation("ForwardingRuleRepository: Updating forwarding rule with RuleName: {RuleName}", rule.RuleName);
            try
            {
                _dbContext.ForwardingRules.Update(rule);
                await _dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("ForwardingRuleRepository: Successfully updated forwarding rule: {RuleName}", rule.RuleName);
            }
            catch (DbUpdateConcurrencyException concEx)
            {
                _logger.LogError(concEx, "ForwardingRuleRepository: Concurrency conflict while updating forwarding rule {RuleName}. The rule may have been modified or deleted by another user.", rule.RuleName);
                throw;
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogError(dbEx, "ForwardingRuleRepository: Error updating forwarding rule {RuleName} in the database.", rule.RuleName);
                throw;
            }
        }

        public async Task DeleteAsync(string ruleName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ruleName))
            {
                _logger.LogWarning("ForwardingRuleRepository: DeleteAsync called with null or empty ruleName.");
                return;
            }
            _logger.LogInformation("ForwardingRuleRepository: Attempting to delete forwarding rule with RuleName: {RuleName}", ruleName);
            var ruleToDelete = await _dbContext.ForwardingRules.FirstOrDefaultAsync(r => r.RuleName == ruleName, cancellationToken);

            if (ruleToDelete == null)
            {
                _logger.LogWarning("ForwardingRuleRepository: Forwarding rule with RuleName {RuleName} not found for deletion.", ruleName);
                return;
            }

            try
            {
                _dbContext.ForwardingRules.Remove(ruleToDelete);
                await _dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("ForwardingRuleRepository: Successfully deleted forwarding rule: {RuleName}", ruleName);
            }
            catch (DbUpdateConcurrencyException concEx)
            {
                _logger.LogError(concEx, "ForwardingRuleRepository: Concurrency conflict while deleting forwarding rule {RuleName}.", ruleName);
                throw;
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogError(dbEx, "ForwardingRuleRepository: Error deleting forwarding rule {RuleName} from the database. It might be in use.", ruleName);
                throw;
            }
        }
    }
}

#endregion