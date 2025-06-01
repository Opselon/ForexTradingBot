// File: Application\Features\Forwarding\Services\MessageProcessingService.cs

#region Usings
using Application.Common.Interfaces;
using Application.Features.Forwarding.Interfaces;
using Domain.Features.Forwarding.Entities;
using Domain.Features.Forwarding.ValueObjects;
using Hangfire;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TL;
using Polly; // For Policy, RetryPolicy, Context class
using Polly.Retry;
#endregion

namespace Application.Features.Forwarding.Services
{
    public class MessageProcessingService
    {
        private readonly ILogger<MessageProcessingService> _logger;
        private readonly ITelegramUserApiClient _userApiClient;
        private readonly IBackgroundJobClient _backgroundJobClient;
        private readonly RetryPolicy<string> _enqueueRetryPolicy;

        public MessageProcessingService(
            ILogger<MessageProcessingService> logger,
            ITelegramUserApiClient userApiClient,
            IBackgroundJobClient backgroundJobClient)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userApiClient = userApiClient ?? throw new ArgumentNullException(nameof(userApiClient));
            _backgroundJobClient = backgroundJobClient ?? throw new ArgumentNullException(nameof(backgroundJobClient));

            _enqueueRetryPolicy = Policy<string>
                .Handle<Exception>(ex =>
                {
                    _logger.LogWarning(ex, "Polly[Enqueue]: Error occurred while enqueueing Hangfire job. Retrying...");
                    return true; // Retries on any exception during enqueue attempt
                })
                .WaitAndRetry(new[]
                {
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(200),
                    TimeSpan.FromMilliseconds(400),
                    TimeSpan.FromMilliseconds(800),
                    TimeSpan.FromSeconds(1)
                },
                (delegateResult, timeSpan, retryAttempt, context) => // Correct signature for onRetry callback in Polly v8+
                {
                    string errorMessage = delegateResult.Exception?.Message ?? "No exception message provided.";

                    _logger.LogWarning(delegateResult.Exception, // Pass the actual exception to the logger for rich details
                        "PollyRetry[Enqueue]: Enqueue operation failed (Context: '{ContextKey}', Attempt: {RetryAttempt}). Retrying in {TimeSpan}. Error: {ErrorMessage}",
                        context.OperationKey ?? "N/A", retryAttempt, timeSpan, errorMessage);
                });
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
                await Task.CompletedTask; // Resolves CS1998 warning for early exit.
                return;
            }

            _logger.LogInformation("MESSAGE_PROCESSING_SERVICE: Processing rule '{RuleName}' for message {OriginalMessageId} from source {SourceId}. Found {TargetCount} target channel(s).",
                rule.RuleName, originalMessageId, sourceChannelIdForMatching, rule.TargetChannelIds.Count);

            foreach (var targetChannelId in rule.TargetChannelIds)
            {
                cancellationToken.ThrowIfCancellationRequested(); // Check for cancellation for each target channel

                string jobDescription = $"Msg:{originalMessageId}|Rule:{rule.RuleName}|Target:{targetChannelId}";
                _logger.LogDebug("MESSAGE_PROCESSING_SERVICE: Attempting to enqueue job for {JobDescription}.", jobDescription);

                PolicyResult<string> enqueueResult = _enqueueRetryPolicy.ExecuteAndCapture(
                    // The lambda now accepts a 'Context' parameter from Polly's ExecuteAndCapture
                    (pollyContext) =>
                    {
                        // Removed the problematic reference to Polly.Context.NoOp.RetryAttempt.
                        // Inside this 'ExecuteAndCapture' delegate, we are performing the actual 'Enqueue' operation,
                        // which is part of *one attempt* within the retry policy.
                        // The actual retry attempt number is logged in the `onRetry` callback of the policy itself.
                        _logger.LogTrace("Executing Hangfire Enqueue for {OperationKey}.", pollyContext.OperationKey);

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
                    },
                    new Context(jobDescription) // Pass the custom context for this execution
                );

                if (enqueueResult.Outcome == OutcomeType.Successful)
                {
                    _logger.LogInformation("MESSAGE_PROCESSING_SERVICE: Successfully enqueued job '{JobId}' for {JobDescription}.", enqueueResult.Result, jobDescription);
                }
                else
                {
                    string failureReason = enqueueResult.FinalException?.Message ?? enqueueResult.Outcome.ToString();
                    _logger.LogCritical(enqueueResult.FinalException,
                        "MESSAGE_PROCESSING_SERVICE_ENQUEUE_FAILED_FINAL: Failed to enqueue job for {JobDescription} after all retries. This job WILL NOT BE PROCESSED. Reason: {FailureReason}",
                        jobDescription, failureReason);
                }
            }

            await Task.CompletedTask;
        }
    }
}