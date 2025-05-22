using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Features.Forwarding.Entities;
using Domain.Features.Forwarding.Repositories;
using Microsoft.Extensions.Logging;

namespace Application.Features.Forwarding.Services
{
    public class ForwardingService : IForwardingService
    {
        private readonly IForwardingRuleRepository _ruleRepository;
        private readonly MessageProcessingService _messageProcessor;
        private readonly ILogger<ForwardingService> _logger;

        public ForwardingService(
            IForwardingRuleRepository ruleRepository,
            MessageProcessingService messageProcessor,
            ILogger<ForwardingService> logger)
        {
            _ruleRepository = ruleRepository ?? throw new ArgumentNullException(nameof(ruleRepository));
            _messageProcessor = messageProcessor ?? throw new ArgumentNullException(nameof(messageProcessor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        public async Task ProcessMessageAsync(long sourceChannelId, long messageId, CancellationToken cancellationToken = default)
        {
            var rules = await _ruleRepository.GetBySourceChannelAsync(sourceChannelId, cancellationToken);
            var activeRules = rules.Where(r => r.IsEnabled).ToList();

            if (!activeRules.Any())
            {
                _logger.LogDebug("No active forwarding rules found for channel {ChannelId}", sourceChannelId);
                return;
            }

            foreach (var rule in activeRules)
            {
                try
                {
                    foreach (var targetChannelId in rule.TargetChannelIds)
                    {
                        await _messageProcessor.ProcessAndRelayMessageAsync(
                            (int)messageId,
                            sourceChannelId,
                            targetChannelId,
                            rule,
                            cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Error processing message {MessageId} from channel {ChannelId} using rule {RuleName}",
                        messageId, sourceChannelId, rule.RuleName);
                }
            }
        }
    }
} 