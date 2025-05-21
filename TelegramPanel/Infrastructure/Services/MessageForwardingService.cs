using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using Application.Features.Forwarding.Interfaces;
using System.Text.RegularExpressions;
using Infrastructure.Settings;

namespace TelegramPanel.Infrastructure.Services
{
    public class MessageForwardingService
    {
        private readonly ILogger<MessageForwardingService> _logger;
        private readonly IForwardingJobActions _forwardingJobActions;
        private readonly List<ForwardingRule> _forwardingRules;

        public MessageForwardingService(
            ILogger<MessageForwardingService> logger,
            IForwardingJobActions forwardingJobActions,
            IOptions<List<ForwardingRule>> forwardingRules)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _forwardingJobActions = forwardingJobActions ?? throw new ArgumentNullException(nameof(forwardingJobActions));
            _forwardingRules = forwardingRules?.Value ?? new List<ForwardingRule>();

            // Log all configured rules at startup
            _logger.LogInformation("MessageForwardingService initialized with {RuleCount} rules:", _forwardingRules.Count);
            foreach (var rule in _forwardingRules)
            {
                _logger.LogInformation(
                    "Rule '{RuleName}': Source={SourceChannelId}, Targets={TargetChannelIds}, Enabled={IsEnabled}",
                    rule.RuleName,
                    rule.SourceChannelId,
                    string.Join(",", rule.TargetChannelIds),
                    rule.IsEnabled);
            }
        }

        public async Task HandleMessageAsync(Message message, CancellationToken cancellationToken = default)
        {
            if (message == null)
            {
                _logger.LogWarning("Received null message in HandleMessageAsync");
                return;
            }

            // Get the source channel ID
            var sourceChannelId = message.Chat.Id;
            _logger.LogInformation(
                "Processing message {MessageId} from chat {SourceChannelId} (Type: {MessageType})",
                message.MessageId,
                sourceChannelId,
                message.Type);
            
            // Find applicable forwarding rules for this source channel
            var applicableRules = _forwardingRules
                .Where(r => r.IsEnabled && r.SourceChannelId == sourceChannelId)
                .ToList();

            if (!applicableRules.Any())
            {
                _logger.LogDebug("No forwarding rules found for source channel {SourceChannelId}", sourceChannelId);
                return;
            }

            _logger.LogInformation("Found {RuleCount} applicable rules for channel {SourceChannelId}", 
                applicableRules.Count, sourceChannelId);

            // Process each applicable rule
            foreach (var rule in applicableRules)
            {
                try
                {
                    _logger.LogInformation(
                        "Processing rule '{RuleName}' for message {MessageId}",
                        rule.RuleName,
                        message.MessageId);

                    foreach (var targetChannelId in rule.TargetChannelIds)
                    {
                        _logger.LogInformation(
                            "Forwarding message {MessageId} to target channel {TargetChannelId} using rule '{RuleName}'",
                            message.MessageId,
                            targetChannelId,
                            rule.RuleName);

                        await _forwardingJobActions.ProcessAndRelayMessageAsync(
                            sourceMessageId: message.MessageId,
                            rawSourcePeerId: sourceChannelId,
                            targetChannelId: targetChannelId,
                            ruleName: rule.RuleName,
                            cancellationToken: cancellationToken
                        );

                        _logger.LogInformation(
                            "Successfully forwarded message {MessageId} to channel {TargetChannelId}",
                            message.MessageId,
                            targetChannelId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, 
                        "Error forwarding message {MessageId} from channel {SourceChannelId} to target channel(s) using rule {RuleName}",
                        message.MessageId, sourceChannelId, rule.RuleName);
                }
            }
        }
    }
} 