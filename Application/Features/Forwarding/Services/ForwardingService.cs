// File: Application\Features\Forwarding\Services\ForwardingService.cs

using Application.Features.Forwarding.Interfaces;
using Domain.Features.Forwarding.Entities; // For ForwardingRule
using Domain.Features.Forwarding.Repositories; // For IForwardingRuleRepository
using Hangfire; // For [AutomaticRetry], IBackgroundJobClient
// Specific usings for caching and shared models
using Microsoft.Extensions.Caching.Memory; // For IMemoryCache, MemoryCacheEntryOptions
using Microsoft.Extensions.Logging;
using System; // For ArgumentNullException, InvalidOperationException, TimeSpan
using System.Collections.Generic; // For List
using System.Linq; // For Where, Any, ToList
using System.Threading;
using System.Threading.Tasks;
using TL; // For Peer, MessageEntity
// MODIFICATION START: Add Polly namespaces for resilience policies
using Polly;
using Polly.Retry;
// MODIFICATION END

namespace Application.Features.Forwarding.Services
{
    public class ForwardingService : IForwardingService
    {

        private readonly IForwardingRuleRepository _ruleRepository;
        private readonly ILogger<ForwardingService> _logger; // Logger type specific to this service
        private readonly IBackgroundJobClient _backgroundJobClient; // Kept for consistency if other enqueueing is needed
        private readonly MessageProcessingService _messageProcessingService; // ADDED: Dependency for MessageProcessingService

        // Caching fields for performance
        private readonly IMemoryCache _memoryCache;
        private readonly TimeSpan _rulesCacheExpiration = TimeSpan.FromMinutes(5); // Cache rules for 5 minutes

        // MODIFICATION START: Declare Polly retry policy for database operations (rule retrieval)
        private readonly AsyncRetryPolicy<IEnumerable<ForwardingRule>> _ruleRetrievalRetryPolicy;
        // MODIFICATION END

        // Constructor - All dependencies injected, cleaned up duplicates
        public ForwardingService(
             IForwardingRuleRepository ruleRepository,
             ILogger<ForwardingService> logger,
             IBackgroundJobClient backgroundJobClient,
             IMemoryCache memoryCache,
             MessageProcessingService messageProcessingService) // ADDED: Inject MessageProcessingService
        {
            _ruleRepository = ruleRepository ?? throw new ArgumentNullException(nameof(ruleRepository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _backgroundJobClient = backgroundJobClient ?? throw new ArgumentNullException(nameof(backgroundJobClient));
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
            _messageProcessingService = messageProcessingService ?? throw new ArgumentNullException(nameof(messageProcessingService)); // Initialize messageProcessingService

            // MODIFICATION START: Initialize the Polly retry policy for rule retrieval using Policy<IEnumerable<ForwardingRule>>
            _ruleRetrievalRetryPolicy = Policy<IEnumerable<ForwardingRule>> // <--- Changed from 'Policy' to 'Policy<IEnumerable<ForwardingRule>>'
                .Handle<Exception>(ex =>
                {
                    // Filter exceptions that are transient for database operations.
                    // Customize these exception types based on your database and ORM.
                    // Examples for PostgreSQL with Npgsql: Npgsql.NpgsqlException
                    // Examples for SQL Server: System.Data.SqlClient.SqlException, System.TimeoutException
                    if (ex is TimeoutException) return true; // Common for transient network/DB issues
                    // if (ex is Npgsql.NpgsqlException pgEx && pgEx.IsTransient) return true; // Check for transient PostgreSQL errors
                    // if (ex is System.Data.SqlClient.SqlException sqlEx && IsSqlTransientError(sqlEx)) return true; // Custom check for SQL Server transient errors

                    _logger.LogWarning(ex, "Polly: Transient error occurred while retrieving forwarding rules from the repository. Retrying...");
                    return true; // Catch-all for other exceptions for now; refine this to be more specific if possible.
                })
                .WaitAndRetryAsync(new[] // Async policy with increasing delays
                {
                    TimeSpan.FromSeconds(1), // First retry after 1 second
                    TimeSpan.FromSeconds(3), // Second retry after 3 seconds
                    TimeSpan.FromSeconds(10) // Third (final) retry after 10 seconds
                }, (exception, timeSpan, retryCount, context) =>
                {
                });
            // MODIFICATION END
        }

        // --- Standard CRUD/Read methods (minimal logging kept if any) ---

        // NOTE: For other CRUD methods (GetRuleAsync, CreateRuleAsync, UpdateRuleAsync, DeleteRuleAsync),
        // you might also consider applying similar Polly policies if they interact with the database
        // and require resilience against transient failures. For this request, we are focusing on
        // ProcessMessageAsync as it's the core forwarding logic.

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
            // MODIFICATION START: Wrap rule retrieval with the Polly retry policy
            return await _ruleRetrievalRetryPolicy.ExecuteAsync(async () =>
            {
                _logger.LogDebug("ForwardingService: Attempting to retrieve rules for source channel {SourceChannelId} from repository.", sourceChannelId);
                var rules = await _ruleRepository.GetBySourceChannelAsync(sourceChannelId, cancellationToken);
                _logger.LogDebug("ForwardingService: Successfully retrieved {RuleCount} rules for source channel {SourceChannelId}.", rules.Count(), sourceChannelId);
                return rules;
            });
            // MODIFICATION END
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
            // Removed: _logger.LogInformation("Deleted forwarding rule: {RuleName}", rule.RuleName);
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
         List<InputMediaWithCaption>? mediaGroupItems,
         CancellationToken cancellationToken = default)
        {
            // Logging stripped down for speed (only error logs kept)

            // Caching Logic: Attempt to retrieve rules from in-memory cache first
            string cacheKey = $"Rules_SourceChannel_{sourceChannelIdForMatching}";
            IEnumerable<ForwardingRule> rules;

            // This part already calls GetRulesBySourceChannelAsync, which now uses Polly
            if (!_memoryCache.TryGetValue(cacheKey, out rules))
            {
                rules = await GetRulesBySourceChannelAsync(sourceChannelIdForMatching, cancellationToken); // This call is now protected by Polly
                _memoryCache.Set(cacheKey, rules, new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(_rulesCacheExpiration)
                    .SetSlidingExpiration(_rulesCacheExpiration));
                _logger.LogInformation("ForwardingService: Rules for source {SourceId} loaded from DB and cached.", sourceChannelIdForMatching);
            }
            else
            {
                _logger.LogInformation("ForwardingService: Rules for source {SourceId} loaded from cache.", sourceChannelIdForMatching);
            }


            var activeRules = rules.Where(r => r.IsEnabled).ToList();

            if (!activeRules.Any())
            {
                _logger.LogInformation("ForwardingService: No active rules found for source {SourceId}. Skipping message {MessageId}.", sourceChannelIdForMatching, originalMessageId);
                return;
            }

            foreach (var rule in activeRules)
            {
                try
                {
                    if (rule.TargetChannelIds == null || !rule.TargetChannelIds.Any())
                    {
                        _logger.LogWarning("ForwardingService: Rule '{RuleName}' for source {SourceId} has no target channels defined. Skipping this rule for message {MessageId}.", rule.RuleName, sourceChannelIdForMatching, originalMessageId);
                        continue;
                    }

                    // This delegation also benefits from Polly because MessageProcessingService.EnqueueAndRelayMessageAsync
                    // is also internally protected by Polly for its enqueue operations.
                    await _messageProcessingService.EnqueueAndRelayMessageAsync(
                        sourceChannelIdForMatching,
                        originalMessageId,
                        rawSourcePeerIdForApi,
                        messageContent,
                        messageEntities,
                        senderPeerForFilter,
                        mediaGroupItems,
                        rule, // Pass the rule directly to the MessageProcessingService
                        cancellationToken
                    );
                    _logger.LogDebug("ForwardingService: Enqueued job for rule '{RuleName}' (Msg {MessageId}, Source {SourceId}).", rule.RuleName, originalMessageId, sourceChannelIdForMatching);
                }
                catch (Exception ex)
                {
                    // CRITICAL LOG: Always keep error logs for job failures
                    // This catch block handles exceptions *during the enqueueing process* within the loop.
                    // The job itself (ProcessMessageAsync) will retry due to its [AutomaticRetry] attribute
                    // if any of these inner operations throw a persistent error.
                    _logger.LogError(ex, "FORWARDING_SERVICE_ERROR: Error processing message {MessageId} from SourceForMatching {SourceForMatching} for rule '{RuleName}'. This will cause the main job to retry.",
                        originalMessageId, sourceChannelIdForMatching, rule.RuleName);
                    // Throwing here ensures Hangfire retries the entire ProcessMessageAsync job,
                    // which will re-attempt all rules for this message.
                    throw;
                }
            }
        }
    }
}