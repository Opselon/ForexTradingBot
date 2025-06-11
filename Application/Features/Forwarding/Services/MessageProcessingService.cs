#region Usings
using Application.Common.Interfaces;
using Application.Features.Forwarding.Interfaces;
using Hangfire;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using TL;
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

            _logger.LogInformation("MessageProcessingService initialized. Configured with Polly for Hangfire job enqueueing resilience.");

            // Polly policy to handle transient errors when enqueuing jobs into Hangfire.
            _enqueueRetryPolicy = Policy<string>
                .Handle<Exception>(ex =>
                {
                    _logger.LogWarning(ex, "Polly[Enqueue]: Error occurred while attempting to enqueue Hangfire job. Retrying...");
                    return true; // Always retry on any exception during enqueue
                })
                .WaitAndRetry(new[]
                {
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(200),
                    TimeSpan.FromMilliseconds(400),
                    TimeSpan.FromMilliseconds(800),
                    TimeSpan.FromSeconds(1)
                },
                (delegateResult, timeSpan, retryAttempt, context) =>
                {
                    string errorMessage = delegateResult.Exception?.Message ?? "No exception message provided.";

                    _logger.LogWarning(delegateResult.Exception,
                        "PollyRetry[Enqueue]: Enqueue operation for {OperationKey} failed (Attempt: {RetryAttempt}). Retrying in {TimeSpan}. Error: {ErrorMessage}",
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
        /// The enqueueing itself is now protected by a Polly retry policy, ensuring the enqueue operation is anti-hang.
        /// The overall real-time characteristic (low-latency delivery) is dependent on Hangfire worker availability.
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
                cancellationToken.ThrowIfCancellationRequested();

                string jobDescription = $"Msg:{originalMessageId}|Rule:{rule.RuleName}|Target:{targetChannelId}";
                _logger.LogDebug("MESSAGE_PROCESSING_SERVICE: Attempting to enqueue job for {JobDescription}.", jobDescription);

                PolicyResult<string> enqueueResult = _enqueueRetryPolicy.ExecuteAndCapture(
                    (pollyContext) =>
                    {
                        // Enqueue the job. Hangfire will inject `PerformContext` and `CancellationToken` (for the job's lifecycle)
                        // into the `ProcessAndRelayMessageAsync` method when it runs.
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
                                CancellationToken.None, // Pass CancellationToken.None for the job's own token (it will get its own from Hangfire)
                                null! // Pass null for PerformContext here; Hangfire will inject the actual context when the job executes.
                                      // Using null! with nullable reference types enabled means "I know this is null but it will be handled".
                                      // This assumes Hangfire's DI will provide the non-null PerformContext.
                            ));
                    },
                    new Context(jobDescription)
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