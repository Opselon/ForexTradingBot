// File: Application\Features\Forwarding\Services\MessageProcessingService.cs

using Application.Common.Interfaces;
using Application.Features.Forwarding.Interfaces;
using Domain.Features.Forwarding.Entities;
using Domain.Features.Forwarding.ValueObjects;
using Hangfire;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TL;
// MODIFICATION START: Add Polly namespace for resilience policies
using Polly;
using Polly.Retry;
// MODIFICATION END

namespace Application.Features.Forwarding.Services
{
    public class MessageProcessingService
    {
        private readonly ILogger<MessageProcessingService> _logger;
        private readonly ITelegramUserApiClient _userApiClient;
        private readonly IBackgroundJobClient _backgroundJobClient;
        // MODIFICATION START: Declare Polly retry policy as generic RetryPolicy<string>
        private readonly RetryPolicy<string> _enqueueRetryPolicy;
        // MODIFICATION END

        public MessageProcessingService(
            ILogger<MessageProcessingService> logger,
            ITelegramUserApiClient userApiClient,
            IBackgroundJobClient backgroundJobClient)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userApiClient = userApiClient ?? throw new ArgumentNullException(nameof(userApiClient));
            _backgroundJobClient = backgroundJobClient ?? throw new ArgumentNullException(nameof(backgroundJobClient));

            // MODIFICATION START: Initialize the Polly retry policy as generic Policy<string>
            _enqueueRetryPolicy = Policy<string> // Specify the return type is string
                .Handle<Exception>(ex =>
                {
                    // Filter exceptions that are transient for enqueueing.
                    _logger.LogWarning(ex, "Polly: Transient error occurred while enqueueing Hangfire job. Retrying...");
                    return true;
                })
                .WaitAndRetry(new[]
                {
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(200),
                    TimeSpan.FromMilliseconds(400),
                    TimeSpan.FromMilliseconds(800),
                    TimeSpan.FromSeconds(1)
                }, (exception, timeSpan, retryCount, context) =>
                {
            
                });
            // MODIFICATION END
        }

        /// <summary>
        /// Enqueues a message for background processing and relaying to each target channel
        /// specified in the forwarding rule. Each target channel receives its own Hangfire job
        /// with built-in retry mechanisms, ensuring robust delivery.
        /// </summary>
        /// <remarks>
        /// This method's primary responsibility is to prepare the necessary data and schedule
        /// the jobs with Hangfire. The actual message processing, including applying edit options
        /// and handling media groups, is delegated to `IForwardingJobActions.ProcessAndRelayMessageAsync`,
        /// which is decorated with `[AutomaticRetry]` for high reliability.
        /// The enqueueing itself is now protected by a Polly retry policy.
        /// </remarks>

        public async Task EnqueueAndRelayMessageAsync(
           long sourceChannelIdForMatching,
           long originalMessageId,
           long rawSourcePeerIdForApi,
           string messageContent,
           MessageEntity[]? messageEntities,
           Peer? senderPeerForFilter,
           List<InputMediaWithCaption>? mediaGroupItems, // Contains media items, with individual captions/entities for processing
           Domain.Features.Forwarding.Entities.ForwardingRule rule,
           CancellationToken cancellationToken)
        {
            if (rule.TargetChannelIds == null || !rule.TargetChannelIds.Any())
            {
                _logger.LogWarning("MESSAGE_PROCESSING_SERVICE: Rule '{RuleName}' has no target channels defined for message {OriginalMessageId}. Skipping job enqueueing.",
                    rule.RuleName, originalMessageId);
                return;
            }

            _logger.LogInformation("MESSAGE_PROCESSING_SERVICE: Processing rule '{RuleName}' for message {OriginalMessageId} from source {SourceId}. Found {TargetCount} target channel(s).",
                rule.RuleName, originalMessageId, sourceChannelIdForMatching, rule.TargetChannelIds.Count);

            foreach (var targetChannelId in rule.TargetChannelIds)
            {
                string jobDescription = $"Msg:{originalMessageId}|Rule:{rule.RuleName}|Target:{targetChannelId}";
                _logger.LogDebug("MESSAGE_PROCESSING_SERVICE: Attempting to enqueue job for {JobDescription}.", jobDescription);

                // MODIFICATION START: Wrap the enqueue operation with the Polly retry policy
                PolicyResult<string> enqueueResult = _enqueueRetryPolicy.ExecuteAndCapture(() => // Now correctly generic PolicyResult<string>
                {
                    return _backgroundJobClient.Enqueue<IForwardingJobActions>(processor =>
                        processor.ProcessAndRelayMessageAsync(
                            (int)originalMessageId,
                            rawSourcePeerIdForApi,
                            targetChannelId,
                            rule,
                            messageContent,
                            messageEntities,
                            senderPeerForFilter,
                            mediaGroupItems,
                            CancellationToken.None
                        ));
                });

                if (enqueueResult.Outcome == OutcomeType.Successful)
                {
                    _logger.LogInformation("MESSAGE_PROCESSING_SERVICE: Successfully enqueued job '{JobId}' for {JobDescription}.", enqueueResult.Result, jobDescription);
                }
                else
                {
                    _logger.LogCritical(enqueueResult.FinalException, "MESSAGE_PROCESSING_SERVICE_ENQUEUE_FAILED_FINAL: Failed to enqueue job for {JobDescription} after all retries. This job WILL NOT BE PROCESSED.", jobDescription);
                }
                // MODIFICATION END
            }
        }
    }
}