// File: Application\Features\Forwarding\Services\ForwardingService.cs
using Application.Features.Forwarding.Interfaces;
using Domain.Features.Forwarding.Entities; // برای ForwardingRule
using Domain.Features.Forwarding.Repositories; // برای IForwardingRuleRepository
using Microsoft.Extensions.Logging;
using TL;
using System.Collections.Generic; // For List, Any, ToList
using System.Linq; // For Where
using System.Threading;
using System.Threading.Tasks;
using Hangfire; // For BackgroundJob.Enqueue

namespace Application.Features.Forwarding.Services
{
    public class ForwardingService : IForwardingService
    {
        // حذف فیلد _forwardingService که باعث وابستگی حلقه‌ای می‌شد
        // private readonly IForwardingService _forwardingService;

        private readonly IForwardingRuleRepository _ruleRepository;
        private readonly ILogger<ForwardingService> _logger;
        private readonly IBackgroundJobClient _backgroundJobClient; // Add this if you need to enqueue jobs directly here

        // Constructor اصلاح شده: IForwardingService تزریق نمی‌شود
        public ForwardingService(
            IForwardingRuleRepository ruleRepository,
            ILogger<ForwardingService> logger,
            IBackgroundJobClient backgroundJobClient) // Add IBackgroundJobClient if not already present
        {
            _ruleRepository = ruleRepository ?? throw new ArgumentNullException(nameof(ruleRepository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _backgroundJobClient = backgroundJobClient ?? throw new ArgumentNullException(nameof(backgroundJobClient));
        }

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
            _logger.LogInformation("Created forwarding rule: {RuleName}", rule.RuleName);
        }

        public async Task UpdateRuleAsync(ForwardingRule rule, CancellationToken cancellationToken = default)
        {
            var existingRule = await _ruleRepository.GetByIdAsync(rule.RuleName, cancellationToken);
            if (existingRule == null)
            {
                throw new InvalidOperationException($"Rule with name '{rule.RuleName}' not found.");
            }

            await _ruleRepository.UpdateAsync(rule, cancellationToken);
            _logger.LogInformation("Updated forwarding rule: {RuleName}", rule.RuleName);
        }

        public async Task DeleteRuleAsync(string ruleName, CancellationToken cancellationToken = default)
        {
            var existingRule = await _ruleRepository.GetByIdAsync(ruleName, cancellationToken);
            if (existingRule == null)
            {
                throw new InvalidOperationException($"Rule with name '{ruleName}' not found.");
            }

            await _ruleRepository.DeleteAsync(ruleName, cancellationToken);
            _logger.LogInformation("Deleted forwarding rule: {RuleName}", ruleName);
        }



        public async Task ProcessMessageAsync(
            long sourceChannelIdForMatching,
            long originalMessageId,
            long rawSourcePeerIdForApi,
            string messageContent,
            TL.MessageEntity[]? messageEntities,
            Peer? senderPeerForFilter,
            List<InputMediaWithCaption>? mediaGroupItems, // CHANGED: Now a list
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation(">>>> FORWARDING_SERVICE: ProcessMessageAsync called. SourceForMatching: {SourceForMatching}, OriginalMsgId: {OriginalMsgId}, RawSourceForApi: {RawSourceForApi}. Content Preview: '{ContentPreview}'. Has Media Group: {HasMediaGroup}. Sender Peer: {SenderPeer}",
                sourceChannelIdForMatching, originalMessageId, rawSourcePeerIdForApi, TruncateString(messageContent, 50), mediaGroupItems != null && mediaGroupItems.Any(), senderPeerForFilter?.ToString() ?? "N/A");

            var rules = await _ruleRepository.GetBySourceChannelAsync(sourceChannelIdForMatching, cancellationToken);
            _logger.LogInformation(">>>> FORWARDING_SERVICE: Found {RuleCount} rules for SourceChannelId {SourceChannelId} from DB.", rules.Count(), sourceChannelIdForMatching);

            var activeRules = rules.Where(r => r.IsEnabled).ToList();
            _logger.LogInformation(">>>> FORWARDING_SERVICE: Found {ActiveRuleCount} ACTIVE rules for channel {SourceChannelIdForMatching}.", activeRules.Count, sourceChannelIdForMatching);

            if (!activeRules.Any())
            {
                _logger.LogWarning(">>>> FORWARDING_SERVICE: No active forwarding rules found for channel {ChannelId} (Matching ID from DB).", sourceChannelIdForMatching);
                return;
            }

            foreach (var rule in activeRules)
            {
                _logger.LogInformation(">>>> FORWARDING_SERVICE: Processing with ACTIVE rule: '{RuleName}' for MsgID: {OriginalMsgId}. Targets: {TargetCount}",
                    rule.RuleName, originalMessageId, rule.TargetChannelIds?.Count ?? 0);
                try
                {
                    if (rule.TargetChannelIds == null || !rule.TargetChannelIds.Any())
                    {
                        _logger.LogWarning(">>>> FORWARDING_SERVICE: Rule '{RuleName}' has no target channels defined. Skipping message {OriginalMsgId}.", rule.RuleName, originalMessageId);
                        continue;
                    }

                    foreach (var targetChannelIdFromDb in rule.TargetChannelIds)
                    {
                        _logger.LogInformation(">>>> FORWARDING_SERVICE: Enqueuing Hangfire job for Rule '{RuleName}', MsgID {OriginalMsgId}, RawSourcePeer {RawSourcePeerIdForApi}, Target DB ID {TargetChannelIdFromDb}. Content Preview: '{ContentPreview}'. Has Media Group: {HasMediaGroup}. Sender Peer: {SenderPeer}",
                    rule.RuleName, originalMessageId, rawSourcePeerIdForApi, targetChannelIdFromDb, TruncateString(messageContent, 50), mediaGroupItems != null && mediaGroupItems.Any(), senderPeerForFilter?.ToString() ?? "N/A");

                        _backgroundJobClient.Enqueue<IForwardingJobActions>(processor =>
                            processor.ProcessAndRelayMessageAsync(
                        (int)originalMessageId,
                        rawSourcePeerIdForApi,
                        targetChannelIdFromDb,
                        rule,
                        messageContent,
                        messageEntities,
                        senderPeerForFilter,
                        mediaGroupItems, // CHANGED: Now a list
                        CancellationToken.None
                    ));
                        _logger.LogInformation(">>>> FORWARDING_SERVICE: Successfully enqueued Hangfire job for Rule '{RuleName}', Target DB ID {TargetChannelIdFromDb}.", rule.RuleName, targetChannelIdFromDb);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, ">>>> FORWARDING_SERVICE: Error processing message {MessageId} from SourceForMatching {SourceForMatching} for rule '{RuleName}'.",
                        originalMessageId, sourceChannelIdForMatching, rule.RuleName);
                }
            }
            _logger.LogInformation(">>>> FORWARDING_SERVICE: Finished processing message {OriginalMsgId} for source {SourceForMatching}. All applicable jobs enqueued.", originalMessageId, sourceChannelIdForMatching);
        }


        // Helper function to truncate strings for logging
        private string TruncateString(string? str, int maxLength)
        {
            if (string.IsNullOrEmpty(str)) return "[null_or_empty]";
            return str.Length <= maxLength ? str : str.Substring(0, maxLength) + "...";
        }
    }
}