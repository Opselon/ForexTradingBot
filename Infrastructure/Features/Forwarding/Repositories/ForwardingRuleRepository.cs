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
    /// <summary>
    /// Implements IForwardingRuleRepository providing data access methods for ForwardingRule entities.
    /// This repository includes SaveChangesAsync calls within its write methods, differing from a pure UoW pattern.
    /// Focuses on robust operation, clear logging, and performance for read queries.
    /// </summary>
    public class ForwardingRuleRepository : IForwardingRuleRepository
    {
        private readonly AppDbContext _dbContext; // Changed type from Infrastructure.Data.AppDbContext to just AppDbContext, assuming correct using
        private readonly ILogger<ForwardingRuleRepository> _logger;

        public ForwardingRuleRepository(
            AppDbContext dbContext, // Consistent type
            ILogger<ForwardingRuleRepository> logger)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        #region Read Operations

        /// <inheritdoc />
        public async Task<ForwardingRule?> GetByIdAsync(string ruleName, CancellationToken cancellationToken = default)
        {
            // Robustness: Validate input. RuleName is PK, so should not be null/empty.
            if (string.IsNullOrWhiteSpace(ruleName))
            {
                _logger.LogWarning("ForwardingRuleRepository: GetByIdAsync called with null or empty ruleName.");
                return null;
            }

            _logger.LogTrace("ForwardingRuleRepository: Fetching forwarding rule by RuleName: {RuleName}", ruleName);
            // Performance: Primary key lookup is generally efficient.
            // AsNoTracking might be applicable if fetched for read-only display.
            // However, GetByIdAsync is often used before an update/delete, so tracking might be desired.
            // For this scenario, let's assume tracking might be needed due to DeleteAsync's usage.
            return await _dbContext.ForwardingRules
                .FirstOrDefaultAsync(r => r.RuleName == ruleName, cancellationToken);
        }

        /// <inheritdoc />
        public async Task<IEnumerable<ForwardingRule>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogTrace("ForwardingRuleRepository: Fetching all forwarding rules, AsNoTracking.");
            // Performance: Use AsNoTracking for read-only lists to reduce EF Core overhead.
            return await _dbContext.ForwardingRules
                .AsNoTracking()
                .OrderBy(r => r.RuleName) // Provide a default sensible order
                .ToListAsync(cancellationToken);
        }

        /// <inheritdoc />
        public async Task<IEnumerable<ForwardingRule>> GetBySourceChannelAsync(long sourceChannelId, CancellationToken cancellationToken = default)
        {
            _logger.LogTrace("ForwardingRuleRepository: Fetching forwarding rules by SourceChannelId: {SourceChannelId}, AsNoTracking.", sourceChannelId);
            // Performance: Ensure SourceChannelId is indexed in the database.
            // Use AsNoTracking for read-only lists.
            return await _dbContext.ForwardingRules
                .Where(r => r.SourceChannelId == sourceChannelId) // Removed "r.SourceChannelId != null" as equality check with long implies it's not null (long cannot be null, long? can).
                                                                  // If SourceChannelId is nullable (long?), then "r.SourceChannelId.HasValue && r.SourceChannelId.Value == sourceChannelId" is more explicit.
                                                                  // Assuming SourceChannelId is `long` not `long?` as per `r.SourceChannelId == sourceChannelId`. If it IS `long?`, original check `!= null` was fine.
                                                                  // For the current provided `long sourceChannelId`, the `!= null` is indeed redundant.
                .AsNoTracking()
                .OrderBy(r => r.RuleName) // Consistent ordering
                .ToListAsync(cancellationToken);
        }

        #endregion

        #region Write Operations (with SaveChangesAsync inside Repository)

        /// <inheritdoc />
        public async Task AddAsync(ForwardingRule rule, CancellationToken cancellationToken = default)
        {
            // Robustness: Validate input object.
            if (rule == null)
            {
                _logger.LogError("ForwardingRuleRepository: Attempted to add a null ForwardingRule object.");
                throw new ArgumentNullException(nameof(rule));
            }
            // Robustness: Validate key property (RuleName).
            if (string.IsNullOrWhiteSpace(rule.RuleName))
            {
                _logger.LogError("ForwardingRuleRepository: Attempted to add a ForwardingRule with null or empty RuleName.");
                throw new ArgumentException("RuleName cannot be null or empty.", nameof(rule.RuleName));
            }

            // Optional: Check for existing RuleName to prevent duplicates if RuleName must be unique.
            // This adds an extra DB hit but ensures data integrity if DB constraint isn't sufficient or checked later.
            // if (await _dbContext.ForwardingRules.AnyAsync(r => r.RuleName == rule.RuleName, cancellationToken))
            // {
            //     _logger.LogWarning("ForwardingRuleRepository: Attempted to add a ForwardingRule with a duplicate RuleName: {RuleName}", rule.RuleName);
            //     throw new InvalidOperationException($"A forwarding rule with RuleName '{rule.RuleName}' already exists.");
            // }

            // Assuming CreatedAt/UpdatedAt timestamps are managed by the entity or interceptors if needed.
            // rule.CreatedAt = DateTime.UtcNow;
            // rule.UpdatedAt = DateTime.UtcNow;

            _logger.LogInformation("ForwardingRuleRepository: Adding forwarding rule with RuleName: {RuleName}, SourceChannelId: {SourceChannelId}",
                                   rule.RuleName, rule.SourceChannelId);
            try
            {
                await _dbContext.ForwardingRules.AddAsync(rule, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken); // Immediate save.
                _logger.LogInformation("ForwardingRuleRepository: Successfully added forwarding rule: {RuleName}", rule.RuleName);
            }
            catch (DbUpdateException dbEx) // Handles errors during SaveChanges (e.g., unique constraint violation, DB connection issues)
            {
                _logger.LogError(dbEx, "ForwardingRuleRepository: Error adding forwarding rule {RuleName} to the database.", rule.RuleName);
                // Consider specific exception types (e.g., for unique constraints) for more tailored error messages or retries.
                throw; // Re-throw to allow higher layers to handle it.
            }
        }

        /// <inheritdoc />
        public async Task UpdateAsync(ForwardingRule rule, CancellationToken cancellationToken = default)
        {
            // Robustness: Validate input object and key properties.
            if (rule == null)
            {
                _logger.LogError("ForwardingRuleRepository: Attempted to update with a null ForwardingRule object.");
                throw new ArgumentNullException(nameof(rule));
            }
            if (string.IsNullOrWhiteSpace(rule.RuleName)) // Assuming RuleName is immutable or correctly passed for identification.
            {
                _logger.LogError("ForwardingRuleRepository: Attempted to update a ForwardingRule without a valid RuleName (used as identifier).");
                throw new ArgumentException("RuleName cannot be null or empty for update identification.", nameof(rule.RuleName));
            }

            // Assuming RuleName is the primary key or unique identifier used to fetch the rule for update.
            // If the 'rule' object passed in is already tracked by the DbContext (e.g., fetched via GetByIdAsync),
            // simply modifying its properties and calling SaveChangesAsync is enough.
            // The `_dbContext.ForwardingRules.Update(rule)` marks all properties as modified,
            // which is suitable if 'rule' is a detached entity (e.g., from a DTO).
            // For optimistic concurrency, a RowVersion/Timestamp field on ForwardingRule would be necessary.

            // rule.UpdatedAt = DateTime.UtcNow; // Update timestamp if entity has it.

            _logger.LogInformation("ForwardingRuleRepository: Updating forwarding rule with RuleName: {RuleName}", rule.RuleName);
            try
            {
                // If rule is detached or to ensure all fields are marked for update:
                _dbContext.ForwardingRules.Update(rule);
                // If 'rule' is already tracked and only specific properties changed:
                // var entry = _dbContext.Entry(rule);
                // if (entry.State == EntityState.Detached) _dbContext.ForwardingRules.Attach(rule);
                // entry.State = EntityState.Modified; // Or allow EF to track changes if already attached.

                await _dbContext.SaveChangesAsync(cancellationToken); // Immediate save.
                _logger.LogInformation("ForwardingRuleRepository: Successfully updated forwarding rule: {RuleName}", rule.RuleName);
            }
            catch (DbUpdateConcurrencyException concEx)
            {
                _logger.LogError(concEx, "ForwardingRuleRepository: Concurrency conflict while updating forwarding rule {RuleName}. The rule may have been modified or deleted by another user.", rule.RuleName);
                // Implement concurrency conflict resolution strategy (e.g., client wins, server wins, or notify user).
                throw; // Re-throw for higher layers or specific handling.
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogError(dbEx, "ForwardingRuleRepository: Error updating forwarding rule {RuleName} in the database.", rule.RuleName);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string ruleName, CancellationToken cancellationToken = default)
        {
            // Robustness: Validate input ruleName.
            if (string.IsNullOrWhiteSpace(ruleName))
            {
                _logger.LogWarning("ForwardingRuleRepository: DeleteAsync called with null or empty ruleName.");
                return; // Or throw ArgumentException, depending on desired strictness.
            }

            _logger.LogInformation("ForwardingRuleRepository: Attempting to delete forwarding rule with RuleName: {RuleName}", ruleName);
            // Fetch the rule first to ensure it exists and to allow EF Core to track it for deletion.
            // This uses the repository's own GetByIdAsync, which might or might not use AsNoTracking.
            // For deletion, the entity needs to be tracked.
            var ruleToDelete = await _dbContext.ForwardingRules.FirstOrDefaultAsync(r => r.RuleName == ruleName, cancellationToken); // Ensure tracked

            if (ruleToDelete == null)
            {
                _logger.LogWarning("ForwardingRuleRepository: Forwarding rule with RuleName {RuleName} not found for deletion.", ruleName);
                return; // Rule doesn't exist, nothing to delete.
            }

            try
            {
                _dbContext.ForwardingRules.Remove(ruleToDelete);
                await _dbContext.SaveChangesAsync(cancellationToken); // Immediate save.
                _logger.LogInformation("ForwardingRuleRepository: Successfully deleted forwarding rule: {RuleName}", ruleName);
            }
            catch (DbUpdateConcurrencyException concEx) // Should be rare for delete if identified by PK unless rule changes between fetch and delete.
            {
                _logger.LogError(concEx, "ForwardingRuleRepository: Concurrency conflict while deleting forwarding rule {RuleName}.", ruleName);
                throw;
            }
            catch (DbUpdateException dbEx) // E.g. foreign key constraints preventing delete
            {
                _logger.LogError(dbEx, "ForwardingRuleRepository: Error deleting forwarding rule {RuleName} from the database. It might be in use.", ruleName);
                throw;
            }
        }
        #endregion
    }
}