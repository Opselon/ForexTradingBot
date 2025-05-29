// File: Application\Features\Forwarding\Services\ForwardingService.cs
using Application.Features.Forwarding.Interfaces;
using Domain.Features.Forwarding.Entities; // For ForwardingRule
using Domain.Features.Forwarding.Repositories; // For IForwardingRuleRepository
using Microsoft.Extensions.Logging;
using TL; // For Peer, MessageEntity
using System.Collections.Generic; // For List
using System.Linq; // For Where, Any, ToList
using System.Threading;
using System.Threading.Tasks;
using Hangfire; // For [AutomaticRetry], IBackgroundJobClient
using System; // For ArgumentNullException, InvalidOperationException, TimeSpan

// Specific usings for caching and shared models
using Microsoft.Extensions.Caching.Memory; // For IMemoryCache, MemoryCacheEntryOptions

namespace Application.Features.Forwarding.Services
{
    public class ForwardingService : IForwardingService
    {
        private readonly IForwardingRuleRepository _ruleRepository;
        private readonly ILogger<ForwardingService> _logger; // Logger type specific to this service
        private readonly IBackgroundJobClient _backgroundJobClient;

        // Caching fields for performance
        private readonly IMemoryCache _memoryCache; // This was missing in the user's provided constructor/fields
        private readonly TimeSpan _rulesCacheExpiration = TimeSpan.FromMinutes(5); // Cache rules for 5 minutes

        // Constructor - All dependencies injected, cleaned up duplicates
        public ForwardingService(
            IForwardingRuleRepository ruleRepository,
            ILogger<ForwardingService> logger,
            IBackgroundJobClient backgroundJobClient,
            IMemoryCache memoryCache) // IMemoryCache must be injected for caching
        {
            _ruleRepository = ruleRepository ?? throw new ArgumentNullException(nameof(ruleRepository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _backgroundJobClient = backgroundJobClient ?? throw new ArgumentNullException(nameof(backgroundJobClient));
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache)); // Initialize memoryCache
        }

        // --- Standard CRUD/Read methods (minimal logging kept if any) ---

        public async Task<ForwardingRule?> GetRuleAsync(string ruleName, CancellationToken cancellationToken = default)
        {
            return await _ruleRepository.GetByIdAsync(ruleName, cancellationToken);
        }

        public async Task<IEnumerable<ForwardingRule>> GetAllRulesAsync(CancellationToken cancellationToken = default)
        {
            return await _ruleRepository.GetAllAsync(cancellationToken);
        }

        public async Task<IEnumerable<ForwardingRule>> GetRulesBySourceChannelAsync(long sourceChannelId, CancellationToken cancellationToken = default)
        {
            return await _ruleRepository.GetBySourceChannelAsync(sourceChannelId, cancellationToken);
        }

        public async Task CreateRuleAsync(ForwardingRule rule, CancellationToken cancellationToken = default)
        {
            var existingRule = await _ruleRepository.GetByIdAsync(rule.RuleName, cancellationToken);
            if (existingRule != null)
            {
                throw new InvalidOperationException($"A rule with name '{rule.RuleName}' already exists.");
            }
            await _ruleRepository.AddAsync(rule, cancellationToken);
            // Removed: _logger.LogInformation("Created forwarding rule: {RuleName}", rule.RuleName);
        }

        public async Task UpdateRuleAsync(ForwardingRule rule, CancellationToken cancellationToken = default)
        {
            var existingRule = await _ruleRepository.GetByIdAsync(rule.RuleName, cancellationToken);
            if (existingRule == null)
            {
                throw new InvalidOperationException($"Rule with name '{rule.RuleName}' not found.");
            }
            await _ruleRepository.UpdateAsync(rule, cancellationToken);
            // Removed: _logger.LogInformation("Updated forwarding rule: {RuleName}", rule.RuleName);
        }

        public async Task DeleteRuleAsync(string ruleName, CancellationToken cancellationToken = default)
        {
            var existingRule = await _ruleRepository.GetByIdAsync(ruleName, cancellationToken);
            if (existingRule == null)
            {
                throw new InvalidOperationException($"Rule with name '{ruleName}' not found.");
            }
            await _ruleRepository.DeleteAsync(ruleName, cancellationToken);
            // Removed: _logger.LogInformation("Deleted forwarding rule: {RuleName}", ruleName);
        }

        // --- Core Message Processing Method ---

        [AutomaticRetry(Attempts = 10)] // Configure Hangfire to retry job 10 times if it fails
        public async Task ProcessMessageAsync(
         long sourceChannelIdForMatching,
         long originalMessageId,
         long rawSourcePeerIdForApi,
         string messageContent,
         MessageEntity[]? messageEntities,
         Peer? senderPeerForFilter,
         List<InputMediaWithCaption>? mediaGroupItems, // Type uses Application.Common.Models.InputMediaWithCaption
         CancellationToken cancellationToken = default)
        {
            // Logging stripped down for speed (only error logs kept)

            // Caching Logic: Attempt to retrieve rules from in-memory cache first
            string cacheKey = $"Rules_SourceChannel_{sourceChannelIdForMatching}";
            IEnumerable<ForwardingRule> rules;

            if (!_memoryCache.TryGetValue(cacheKey, out rules))
            {
                rules = await _ruleRepository.GetBySourceChannelAsync(sourceChannelIdForMatching, cancellationToken);
                _memoryCache.Set(cacheKey, rules, new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(_rulesCacheExpiration)
                    .SetSlidingExpiration(_rulesCacheExpiration));
            }

            var activeRules = rules.Where(r => r.IsEnabled).ToList();

            if (!activeRules.Any())
            {
                // Warning kept for cases where no active rule exists
                // _logger.LogWarning("No active forwarding rules found for channel {ChannelId} (Matching ID from DB).", sourceChannelIdForMatching);
                return;
            }

            foreach (var rule in activeRules)
            {
                try
                {
                    if (rule.TargetChannelIds == null || !rule.TargetChannelIds.Any())
                    {
                        // Warning kept for misconfigured rules
                        // _logger.LogWarning("Rule '{RuleName}' has no target channels defined. Skipping message {OriginalMsgId}.", rule.RuleName, originalMessageId);
                        continue;
                    }

                    foreach (var targetChannelIdFromDb in rule.TargetChannelIds)
                    {
                        // Enqueue actual relay job to IForwardingJobActions
                        _backgroundJobClient.Enqueue<IForwardingJobActions>(processor =>
                            processor.ProcessAndRelayMessageAsync(
                                (int)originalMessageId,
                                rawSourcePeerIdForApi,
                                targetChannelIdFromDb,
                                rule,
                                messageContent,
                                messageEntities,
                                senderPeerForFilter,
                                mediaGroupItems,
                                CancellationToken.None // Enqueue uses CancellationToken.None as Hangfire manages it
                            ));
                    }
                }
                catch (Exception ex)
                {
                    // CRITICAL LOG: Always keep error logs for job failures
                    _logger.LogError(ex, "FORWARDING_SERVICE_ERROR: Error processing message {MessageId} from SourceForMatching {SourceForMatching} for rule '{RuleName}'.",
                        originalMessageId, sourceChannelIdForMatching, rule.RuleName);
                    // Throwing makes Hangfire retry
                    throw;
                }
            }
        
        // Removed: _logger.LogInformation(">>>> FORWARDING_SERVICE: Finished processing message {OriginalMsgId} for source {SourceForMatching}. All applicable jobs enqueued.", originalMessageId, sourceChannelIdForMatching);
    }
    }
}