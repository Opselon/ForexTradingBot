using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using Application.Features.Forwarding.Interfaces;
using System.Text.RegularExpressions;
using Domain.Features.Forwarding.Entities;
using Hangfire;
using System.Text.Json;
using Application.Common.Interfaces;

namespace TelegramPanel.Infrastructure.Services
{
    public class MessageForwardingService
    {
        private readonly ILogger<MessageForwardingService> _logger;
        private readonly IForwardingJobActions _forwardingJobActions;
        private readonly List<ForwardingRule> _forwardingRules;
        private readonly INotificationJobScheduler _jobScheduler;

        public MessageForwardingService(
            ILogger<MessageForwardingService> logger,
            IForwardingJobActions forwardingJobActions,
            IOptions<List<ForwardingRule>> forwardingRules,
            INotificationJobScheduler jobScheduler)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _forwardingJobActions = forwardingJobActions ?? throw new ArgumentNullException(nameof(forwardingJobActions));
            _forwardingRules = forwardingRules?.Value ?? new List<ForwardingRule>();
            _jobScheduler = jobScheduler ?? throw new ArgumentNullException(nameof(jobScheduler));

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
                    if (!ShouldProcessMessage(message, rule))
                    {
                        _logger.LogDebug(
                            "Message {MessageId} does not match filter criteria for rule '{RuleName}'",
                            message.MessageId,
                            rule.RuleName);
                        continue;
                    }

                    _logger.LogInformation(
                        "Processing rule '{RuleName}' for message {MessageId}",
                        rule.RuleName,
                        message.MessageId);

                    foreach (var targetChannelId in rule.TargetChannelIds)
                    {
                        _logger.LogInformation(
                            "Scheduling forwarding job for message {MessageId} to target channel {TargetChannelId} using rule '{RuleName}'",
                            message.MessageId,
                            targetChannelId,
                            rule.RuleName);

                        // Schedule the forwarding job with Hangfire
                        var jobId = _jobScheduler.Enqueue<IForwardingJobActions>(job =>
                            job.ProcessAndRelayMessageAsync(
                                message.MessageId,
                                sourceChannelId,
                                targetChannelId,
                                rule.RuleName,
                                CancellationToken.None
                            ));

                        _logger.LogInformation(
                            "Successfully scheduled forwarding job {JobId} for message {MessageId} to channel {TargetChannelId}",
                            jobId,
                            message.MessageId,
                            targetChannelId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, 
                        "Error scheduling forwarding job for message {MessageId} from channel {SourceChannelId} using rule {RuleName}",
                        message.MessageId, sourceChannelId, rule.RuleName);
                }
            }
        }

        private bool ShouldProcessMessage(Message message, ForwardingRule rule)
        {
            if (rule.FilterOptions == null)
            {
                return true;
            }

            var filterOptions = rule.FilterOptions;

            // Check message type
            if (filterOptions.AllowedMessageTypes != null && filterOptions.AllowedMessageTypes.Any())
            {
                var messageType = message.Type.ToString();
                if (!filterOptions.AllowedMessageTypes.Contains(messageType))
                {
                    _logger.LogDebug("Message type {MessageType} not in allowed types for rule {RuleName}",
                        messageType, rule.RuleName);
                    return false;
                }
            }

            // Check message text content
            if (!string.IsNullOrEmpty(filterOptions.ContainsText))
            {
                var messageText = message.Text ?? message.Caption ?? string.Empty;
                if (filterOptions.ContainsTextIsRegex)
                {
                    try
                    {
                        var regex = new Regex(filterOptions.ContainsText, 
                            (RegexOptions)filterOptions.ContainsTextRegexOptions);
                        if (!regex.IsMatch(messageText))
                        {
                            _logger.LogDebug("Message text does not match regex pattern for rule {RuleName}",
                                rule.RuleName);
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing regex pattern for rule {RuleName}", rule.RuleName);
                        return false;
                    }
                }
                else if (!messageText.Contains(filterOptions.ContainsText, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Message text does not contain required text for rule {RuleName}",
                        rule.RuleName);
                    return false;
                }
            }

            // Check message length
            var textLength = (message.Text ?? message.Caption ?? string.Empty).Length;
            if (filterOptions.MinMessageLength.HasValue && textLength < filterOptions.MinMessageLength.Value)
            {
                _logger.LogDebug("Message length {Length} is less than minimum {MinLength} for rule {RuleName}",
                    textLength, filterOptions.MinMessageLength.Value, rule.RuleName);
                return false;
            }
            if (filterOptions.MaxMessageLength.HasValue && textLength > filterOptions.MaxMessageLength.Value)
            {
                _logger.LogDebug("Message length {Length} is greater than maximum {MaxLength} for rule {RuleName}",
                    textLength, filterOptions.MaxMessageLength.Value, rule.RuleName);
                return false;
            }

            // Check sender restrictions
            if (message.From != null)
            {
                if (filterOptions.AllowedSenderUserIds != null && filterOptions.AllowedSenderUserIds.Any() &&
                    !filterOptions.AllowedSenderUserIds.Contains(message.From.Id))
                {
                    _logger.LogDebug("Sender {SenderId} not in allowed senders for rule {RuleName}",
                        message.From.Id, rule.RuleName);
                    return false;
                }

                if (filterOptions.BlockedSenderUserIds != null && filterOptions.BlockedSenderUserIds.Any() &&
                    filterOptions.BlockedSenderUserIds.Contains(message.From.Id))
                {
                    _logger.LogDebug("Sender {SenderId} is blocked for rule {RuleName}",
                        message.From.Id, rule.RuleName);
                    return false;
                }
            }

            return true;
        }
    }
} 