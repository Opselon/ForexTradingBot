#region Usings
using Application.Common.Interfaces;
using Application.Features.Forwarding.Interfaces;
using Domain.Features.Forwarding.Entities;
using Domain.Features.Forwarding.ValueObjects;
using Hangfire;
using Hangfire.Server; // Crucial: Add this for PerformContext
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TL;
#endregion

namespace Application.Features.Forwarding.Services
{
    public class ForwardingJobActions : IForwardingJobActions
    {
        private readonly ILogger<ForwardingJobActions> _logger;
        private readonly ITelegramUserApiClient _userApiClient;
        private const int HangfireMaxJobRetries = 20;

        // Polly policies for individual Telegram API calls *within* a single Hangfire job attempt.
        private readonly AsyncRetryPolicy<TL.InputPeer?> _resolvePeerRetryPolicy;
        private readonly AsyncRetryPolicy<TL.UpdatesBase?> _sendMessageRetryPolicy;
        private readonly AsyncRetryPolicy _sendMediaGroupRetryPolicy; // For methods returning Task (void)

        public ForwardingJobActions(
                   ILogger<ForwardingJobActions> logger,
                   ITelegramUserApiClient userApiClient)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userApiClient = userApiClient ?? throw new ArgumentNullException(nameof(userApiClient));
            _logger.LogInformation("ForwardingJobActions initialized. Configured with Hangfire for job-level retries and Polly for API call-level transient error handling.");

            var shortDelayAttempts = new[]
            {
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2) // Max 5 retries for a single API call attempt within a Hangfire job
            };

            // Policy for ResolvePeerAsync (returns InputPeer?)
            _resolvePeerRetryPolicy = Policy<TL.InputPeer?>
                .HandleResult(result =>
                {
                    if (result == null)
                    {
                        _logger.LogWarning("Polly (ResolvePeer): Result was null. Retrying to resolve peer.");
                        return true; // Retry if the result is null
                    }
                    return false; // Don't retry for non-null results
                })
                .Or<RpcException>(ex => // Use .Or<ExceptionType> after .HandleResult
                {
                    bool isFloodWait = ex.Message.Contains("FLOOD_WAIT_", StringComparison.OrdinalIgnoreCase);
                    if (isFloodWait)
                    {
                        _logger.LogWarning(ex, "Polly intercepted FLOOD_WAIT error ({RpcError}). This will cause the exception to bubble up and trigger a Hangfire job retry.", ex.Message);
                    }
                    else
                    {
                        bool isInvalidOrNotFound = ex.Message.Contains("USER_NOT_FOUND", StringComparison.OrdinalIgnoreCase) ||
                                                   ex.Message.Contains("CHANNEL_INVALID", StringComparison.OrdinalIgnoreCase) ||
                                                   ex.Message.Contains("CHAT_INVALID", StringComparison.OrdinalIgnoreCase) ||
                                                   ex.Message.Contains("PEER_ID_INVALID", StringComparison.OrdinalIgnoreCase);

                        if (isInvalidOrNotFound)
                        {
                            _logger.LogWarning(ex, "Polly caught RpcException ({RpcError}) indicating invalid/not found peer. Retrying API call.", ex.Message);
                        }
                        else
                        {
                            _logger.LogWarning(ex, "Polly caught RpcException ({RpcError}). Retrying API call for transient error.", ex.Message);
                        }
                    }
                    return !isFloodWait;
                })
                .Or<HttpRequestException>(ex => { _logger.LogWarning(ex, "Polly caught HttpRequestException ({ErrorMessage}). Retrying for transient network issue.", ex.Message); return true; })
                .Or<IOException>(ex => { _logger.LogWarning(ex, "Polly caught IOException ({ErrorMessage}). Retrying for transient I/O issue.", ex.Message); return true; })
                .WaitAndRetryAsync(shortDelayAttempts, (delegateResult, timeSpan, retryAttempt, context) =>
                {
                    string outcomeStatus;
                    string errorMessage;

                    if (delegateResult.Exception != null)
                    {
                        outcomeStatus = "Failed (Exception)";
                        errorMessage = delegateResult.Exception.Message;
                    }
                    else if (delegateResult.Result == null)
                    {
                        outcomeStatus = "Failed (Null Result)";
                        errorMessage = "Result was null.";
                    }
                    else
                    {
                        outcomeStatus = "Unexpectedly Retrying"; // Should not happen if only failure cases are handled
                        errorMessage = "Unexpected result or state.";
                    }

                    _logger.LogWarning(delegateResult.Exception, // Pass the actual exception object if it exists
                        "Polly (ResolvePeer): Attempt {RetryAttempt} for '{ContextKey}' {OutcomeStatus}. Retrying in {TimeSpan}. Error: {ErrorMessage}",
                        retryAttempt, context.OperationKey ?? "N/A", outcomeStatus, timeSpan, errorMessage);
                });

            // Policy for SendMessageAsync and ForwardMessagesAsync (returns UpdatesBase?)
            _sendMessageRetryPolicy = Policy<TL.UpdatesBase?>
                .HandleResult(result =>
                {
                    if (result == null)
                    {
                        _logger.LogWarning("Polly (SendMessage/Forward): Result was null. Retrying send operation.");
                        return true; // Retry if the result is null
                    }
                    return false; // Don't retry for non-null results
                })
                .Or<RpcException>(ex => // Use .Or<ExceptionType> after .HandleResult
                {
                    bool isFloodWait = ex.Message.Contains("FLOOD_WAIT_", StringComparison.OrdinalIgnoreCase);
                    if (isFloodWait)
                    {
                        _logger.LogWarning(ex, "Polly intercepted FLOOD_WAIT error ({RpcError}). This will cause the exception to bubble up and trigger a Hangfire job retry.", ex.Message);
                    }
                    else
                    {
                        _logger.LogWarning(ex, "Polly caught RpcException ({RpcError}). Retrying API call for transient error.", ex.Message);
                    }
                    return !isFloodWait;
                })
                .Or<HttpRequestException>(ex => { _logger.LogWarning(ex, "Polly caught HttpRequestException ({ErrorMessage}). Retrying for transient network issue.", ex.Message); return true; })
                .Or<IOException>(ex => { _logger.LogWarning(ex, "Polly caught IOException ({ErrorMessage}). Retrying for transient I/O issue.", ex.Message); return true; })
                .WaitAndRetryAsync(shortDelayAttempts, (delegateResult, timeSpan, retryAttempt, context) =>
                {
                    string outcomeStatus;
                    string errorMessage;

                    if (delegateResult.Exception != null)
                    {
                        outcomeStatus = "Failed (Exception)";
                        errorMessage = delegateResult.Exception.Message;
                    }
                    else if (delegateResult.Result == null)
                    {
                        outcomeStatus = "Failed (Null Result)";
                        errorMessage = "Result was null.";
                    }
                    else
                    {
                        outcomeStatus = "Unexpectedly Retrying"; // Should not happen if only failure cases are handled
                        errorMessage = "Unexpected result or state.";
                    }

                    _logger.LogWarning(delegateResult.Exception,
                        "Polly (SendMessage/Forward): Attempt {RetryAttempt} for '{ContextKey}' {OutcomeStatus}. Retrying in {TimeSpan}. Error: {ErrorMessage}",
                        retryAttempt, context.OperationKey ?? "N/A", outcomeStatus, timeSpan, errorMessage);
                });

            // Policy for SendMediaGroupAsync (returns Task/void)
            _sendMediaGroupRetryPolicy = Policy
                .Handle<RpcException>(ex =>
                {
                    bool isFloodWait = ex.Message.Contains("FLOOD_WAIT_", StringComparison.OrdinalIgnoreCase);
                    if (isFloodWait)
                    {
                        _logger.LogWarning(ex, "Polly intercepted FLOOD_WAIT error ({RpcError}). This will cause the exception to bubble up and trigger a Hangfire job retry.", ex.Message);
                    }
                    else
                    {
                        _logger.LogWarning(ex, "Polly caught RpcException ({RpcError}). Retrying API call for transient error.", ex.Message);
                    }
                    return !isFloodWait;
                })
                .Or<HttpRequestException>(ex => { _logger.LogWarning(ex, "Polly caught HttpRequestException ({ErrorMessage}). Retrying for transient network issue.", ex.Message); return true; })
                .Or<IOException>(ex => { _logger.LogWarning(ex, "Polly caught IOException ({ErrorMessage}). Retrying for transient I/O issue.", ex.Message); return true; })
                .WaitAndRetryAsync(shortDelayAttempts, (exception, timeSpan, retryAttempt, context) =>
                {
                    _logger.LogWarning(exception,
                        "Polly (SendMediaGroup): Attempt {RetryAttempt} for '{ContextKey}' failed. Retrying in {TimeSpan}. Error: {ErrorMessage}",
                        retryAttempt, context.OperationKey ?? "N/A", timeSpan, exception.Message);
                });
        }


        /// <summary>
        /// Processes and relays a message according to a forwarding rule.
        /// This is the entry point for Hangfire jobs, and thus carries the [AutomaticRetry] attribute.
        /// </summary>
        /// <param name="cancellationToken">CancellationToken for the job execution, provided by Hangfire.</param>
        [AutomaticRetry(Attempts = HangfireMaxJobRetries, DelaysInSeconds = new int[] {
            0, 1, 3, 5, 10, 20, 30, 60, 120, 180, 300, 420, 540, 660, 780, 900, 1020, 1140, 1260, 1380
        })]
        public async Task ProcessAndRelayMessageAsync( // Matches IForwardingJobActions signature
            int sourceMessageId,
            long rawSourcePeerId,
            long targetChannelId, // This is the numerical ID, e.g., 2696634930
            Domain.Features.Forwarding.Entities.ForwardingRule rule,
            string messageContent,
            TL.MessageEntity[]? messageEntities,
            Peer? senderPeerForFilter,
            List<InputMediaWithCaption>? mediaGroupItems,
            CancellationToken cancellationToken, // <-- Matches interface, Hangfire injects this
            PerformContext performContext) // <-- Inject PerformContext here
        {
            // Fix: Get job ID directly from injected PerformContext
            string jobId = performContext.BackgroundJob.Id;


            _logger.LogInformation("Job:{JobId}: Starting ProcessAndRelay for MsgID {SourceMsgId} (from RawSourcePeer {RawSourcePeerId}) to Target {TargetChannelId} via DB Rule '{RuleName}'. Initial Content Preview: '{MessageContentPreview}'. Has Media Group: {HasMediaGroup}. Sender Peer: {SenderPeerType}. CancellationToken Hash: {CTHashCode}",
                jobId, sourceMessageId, rawSourcePeerId, targetChannelId, rule.RuleName, TruncateString(messageContent, 50), mediaGroupItems != null && mediaGroupItems.Any(), senderPeerForFilter?.GetType().Name ?? "N/A", cancellationToken.GetHashCode());

            if (rule == null)
            {
                _logger.LogError("Job:{JobId}: RULE_NULL_ERROR: Received a null rule for processing message {SourceMsgId}. This is a critical configuration error; stopping job.", jobId, sourceMessageId);
                return; // Critical: Stop processing if rule is null.
            }
            if (!rule.IsEnabled)
            {
                _logger.LogInformation("Job:{JobId}: Skipping disabled rule '{RuleName}' for MsgID {SourceMsgId}.", jobId, rule.RuleName, sourceMessageId);
                return; // Skip if rule is disabled.
            }

            cancellationToken.ThrowIfCancellationRequested(); // Check for external cancellation early

            // Flag to track if the original message had any content before edits/media processing.
            bool originalMessageHadTextContent = !string.IsNullOrEmpty(messageContent) || (messageEntities != null && messageEntities.Any());
            bool originalMessageHadMediaContent = mediaGroupItems != null && mediaGroupItems.Any();

            if (!ShouldProcessMessageBasedOnFilters(messageContent, senderPeerForFilter, rule.FilterOptions, sourceMessageId, rule.RuleName, jobId, cancellationToken))
            {
                _logger.LogInformation("Job:{JobId}: Message {SourceMsgId} filtered out by rule '{RuleName}'. No further action taken.", jobId, sourceMessageId, rule.RuleName);
                return; // Message filtered out by rule.
            }


            try
            {
                // --- 1. Resolve Source Peer ---
                _logger.LogDebug("Job:{JobId}: Resolving FromPeer (RawSourcePeerId: {RawSourcePeerId}) for Rule: '{RuleName}'.", jobId, rawSourcePeerId, rule.RuleName);
                var fromPeer = await _resolvePeerRetryPolicy.ExecuteAsync(
                    async (pollyContext, pollyCancellationToken) =>
                        await _userApiClient.ResolvePeerAsync(rawSourcePeerId, pollyCancellationToken),
                    new Context($"ResolveFromPeer_{rawSourcePeerId}_Rule_{rule.RuleName}"),
                    cancellationToken
                );

                if (fromPeer == null)
                {
                    _logger.LogError("Job:{JobId}: Failed to resolve source peer (ID: {RawSourcePeerId}) for rule '{RuleName}' after all Polly retries. Cannot proceed. This failure will trigger Hangfire's next retry.",
                        jobId, rawSourcePeerId, rule.RuleName);
                    throw new InvalidOperationException($"Could not resolve source peer for rule '{rule.RuleName}' (ID: {rawSourcePeerId}).");
                }

                // --- 2. Resolve Target Peer ---
                _logger.LogDebug("Job:{JobId}: Resolving TargetPeer (ID: {TargetChannelId}) for Rule: '{RuleName}'.", jobId, targetChannelId, rule.RuleName);

                InputPeer? toPeer = await _resolvePeerRetryPolicy.ExecuteAsync(
                    async (pollyContext, pollyCancellationToken) =>
                        await _userApiClient.ResolvePeerAsync(targetChannelId, pollyCancellationToken),
                    new Context($"ResolveToPeer_{targetChannelId}_Rule_{rule.RuleName}"),
                    cancellationToken
                );

                if (toPeer == null)
                {
                    _logger.LogError("Job:{JobId}: Failed to resolve target peer (ID: {TargetChannelId}) for rule '{RuleName}' after all Polly retries. Cannot proceed. This failure will trigger Hangfire's next retry.",
                        jobId, targetChannelId, rule.RuleName);
                    throw new InvalidOperationException($"Could not resolve target peer for rule '{rule.RuleName}' (ID: {targetChannelId}).");
                }

                cancellationToken.ThrowIfCancellationRequested(); // Check again before major operation

                // --- 3. Determine Send Type and Execute ---
                // The decision to use custom send or simple forward logic.
                // Custom send is needed for any text/entity manipulation or if media groups are present.
                // NoForwards also implies custom send to use the Messages_SendMessage/SendMedia flags.
                bool needsCustomSend = (rule.EditOptions != null &&
                                        (
                                         !string.IsNullOrEmpty(rule.EditOptions.PrependText) ||
                                         !string.IsNullOrEmpty(rule.EditOptions.AppendText) ||
                                         (rule.EditOptions.TextReplacements != null && rule.EditOptions.TextReplacements.Any()) ||
                                         rule.EditOptions.RemoveLinks ||
                                         rule.EditOptions.StripFormatting ||
                                         !string.IsNullOrEmpty(rule.EditOptions.CustomFooter) ||
                                         rule.EditOptions.DropMediaCaptions || // DropMediaCaptions means we modify content, so custom send
                                         rule.EditOptions.NoForwards // NoForwards flag needs to be set in SendMessage/SendMedia
                                        )) ||
                                        (originalMessageHadMediaContent); // If original message had media, it will be handled by custom send path (even if mediaGroupItems is null, it should trigger full API to copy media from original message)


                if (needsCustomSend)
                {
                    _logger.LogDebug("Job:{JobId}: Custom send determined for MsgID {SourceMsgId} based on rule '{RuleName}' edits or media presence. Initiating custom send.",
                        jobId, sourceMessageId, rule.RuleName);
                    await ProcessCustomSendAsync(toPeer, rule, messageContent, messageEntities, mediaGroupItems, originalMessageHadMediaContent, originalMessageHadTextContent, cancellationToken, jobId);
                }
                else
                {
                    _logger.LogDebug("Job:{JobId}: Simple forward determined for MsgID {SourceMsgId} for rule '{RuleName}'. Initiating direct forward.",
                        jobId, sourceMessageId, rule.RuleName);
                    await ProcessSimpleForwardAsync(fromPeer, toPeer, sourceMessageId, rule, cancellationToken, jobId);
                }

                _logger.LogInformation("Job:{JobId}: Message {SourceMsgId} successfully processed and relayed to Target {TargetPeerIdForLog} for rule '{RuleName}'.",
                    jobId, sourceMessageId, GetInputPeerIdValueForLogging(toPeer), rule.RuleName);
            }
            // Explicitly catch Telegram FLOOD_WAIT RPC exceptions
            catch (RpcException rpcEx) when (rpcEx.Message.Contains("FLOOD_WAIT_", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(rpcEx, "Job:{JobId}: Received Telegram FLOOD_WAIT error while processing message {SourceMsgId} for rule '{RuleName}'. Re-throwing to allow Hangfire to retry with longer delay.",
                    jobId, sourceMessageId, rule.RuleName);
                throw; // Re-throw to engage Hangfire's [AutomaticRetry] with its configured long backoff
            }
            // Catch OperationCanceledException thrown by CancellationToken.ThrowIfCancellationRequested()
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Job:{JobId}: Job execution for message {SourceMsgId} for rule '{RuleName}' was explicitly cancelled by CancellationToken.",
                    jobId, sourceMessageId, rule.RuleName);
                // No need to re-throw. Hangfire detects this exception type and sets job status to 'Canceled'.
            }
            // Catch any other general exceptions and re-throw them for Hangfire to handle as failures.
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job:{JobId}: An unexpected error occurred while processing message {SourceMsgId} (RawSourcePeer: {RawSourcePeerId}) for rule '{RuleName}'. Re-throwing to allow Hangfire to track failure and potentially retry.",
                    jobId, sourceMessageId, rawSourcePeerId, rule.RuleName);
                throw; // Re-throw to allow Hangfire to track the job's failure and apply further retries if configured
            }
        }

        private bool ShouldProcessMessageBasedOnFilters(string messageContent, Peer? senderPeer, MessageFilterOptions filterOptions, int messageId, string ruleName, string currentJobId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (filterOptions == null)
            {
                _logger.LogTrace("Job:{JobId}: ShouldProcessMessageBasedOnFilters: No FilterOptions defined for Rule '{RuleName}'. Message {MessageId} will be processed.",
                    currentJobId, ruleName, messageId);
                return true;
            }

            // --- ContainsText Filter ---
            if (!string.IsNullOrEmpty(filterOptions.ContainsText))
            {
                bool matches;
                if (filterOptions.ContainsTextIsRegex)
                {
                    try
                    {
                        matches = Regex.IsMatch(messageContent, filterOptions.ContainsText, filterOptions.ContainsTextRegexOptions);
                        _logger.LogTrace("Job:{JobId}: Filter: Regex pattern '{RegexPattern}' matched: {IsMatch} for MsgID {MessageId} in Rule '{RuleName}'.",
                            currentJobId, filterOptions.ContainsText, matches, messageId, ruleName);
                    }
                    catch (ArgumentException ex)
                    {
                        _logger.LogError(ex, "Job:{JobId}: Invalid regex pattern '{RegexPattern}' in rule '{RuleName}'. Treating as non-match.",
                            currentJobId, filterOptions.ContainsText, ruleName);
                        matches = false;
                    }
                }
                else
                {
                    matches = messageContent.Contains(filterOptions.ContainsText, StringComparison.OrdinalIgnoreCase);
                    _logger.LogTrace("Job:{JobId}: Filter: Text '{ContainsText}' matched: {IsMatch} (case-insensitive) for MsgID {MessageId} in Rule '{RuleName}'.",
                        currentJobId, filterOptions.ContainsText, matches, messageId, ruleName);
                }

                if (!matches)
                {
                    _logger.LogDebug("Job:{JobId}: Message {MessageId} for Rule '{RuleName}' filtered out: does NOT contain required text/pattern '{ContainsText}'.",
                        currentJobId, messageId, ruleName, filterOptions.ContainsText);
                    return false;
                }
            }

            // --- Sender ID Filters ---
            if ((filterOptions.AllowedSenderUserIds != null && filterOptions.AllowedSenderUserIds.Any()) ||
                (filterOptions.BlockedSenderUserIds != null && filterOptions.BlockedSenderUserIds.Any()))
            {
                if (!(senderPeer is PeerUser userSender))
                {
                    // If allowed sender IDs are specified, and sender is not a user, it's filtered out.
                    if (filterOptions.AllowedSenderUserIds != null && filterOptions.AllowedSenderUserIds.Any())
                    {
                        _logger.LogDebug("Job:{JobId}: Message {MessageId} from non-user sender ({SenderType}) filtered out by rule '{RuleName}' (allowed user IDs explicitly defined).",
                           currentJobId, messageId, senderPeer?.GetType().Name ?? "Null", ruleName);
                        return false;
                    }
                    // If no allowed users and only blocked are specified, non-user senders pass by default
                }
                else // Sender is a PeerUser
                {
                    // Check against allowed sender list
                    if (filterOptions.AllowedSenderUserIds != null && filterOptions.AllowedSenderUserIds.Any() && !filterOptions.AllowedSenderUserIds.Contains(userSender.user_id))
                    {
                        _logger.LogDebug("Job:{JobId}: Message {MessageId} from sender {SenderId} is NOT in allowed sender list for Rule '{RuleName}'. Skipping.",
                            currentJobId, messageId, userSender.user_id, ruleName);
                        return false;
                    }

                    // Check against blocked sender list
                    if (filterOptions.BlockedSenderUserIds != null && filterOptions.BlockedSenderUserIds.Any() && filterOptions.BlockedSenderUserIds.Contains(userSender.user_id))
                    {
                        _logger.LogDebug("Job:{JobId}: Message {MessageId} from sender {SenderId} IS in blocked sender list for Rule '{RuleName}'. Skipping.",
                            currentJobId, messageId, userSender.user_id, ruleName);
                        return false;
                    }
                    _logger.LogTrace("Job:{JobId}: Filter: Sender {SenderId} for MsgID {MessageId} passed sender filters for Rule '{RuleName}'.",
                        currentJobId, userSender.user_id, messageId, ruleName);
                }
            }
            _logger.LogTrace("Job:{JobId}: Message {MessageId} passed ALL filters for rule '{RuleName}'.", currentJobId, messageId, ruleName);
            return true;
        }

        private async Task ProcessCustomSendAsync(
            InputPeer toPeer,
            Domain.Features.Forwarding.Entities.ForwardingRule rule,
            string initialMessageContentFromOrchestrator,
            TL.MessageEntity[]? initialEntitiesFromOrchestrator,
            List<InputMediaWithCaption>? mediaGroupItems,
            bool originalMessageHadMediaContent,
            bool originalMessageHadTextContent,
            CancellationToken cancellationToken,
            string jobId)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation("Job:{JobId}: ProcessCustomSendAsync: Entering custom send logic for Rule: '{RuleName}'. Target: {TargetPeer}. MediaGroup: {HasMediaGroup}. Initial Content Preview: '{OrchestratorContentPreview}'.",
                jobId, rule.RuleName, GetInputPeerIdValueForLogging(toPeer), mediaGroupItems != null && mediaGroupItems.Any(), TruncateString(initialMessageContentFromOrchestrator, 50));

            string finalCaption;
            TL.MessageEntity[]? finalEntities;

            // Apply content modifications based on rule.EditOptions
            if (rule.EditOptions != null)
            {
                // Decide if captions should be dropped before applying other edits
                // DropMediaCaptions should only affect actual media captions, not standalone text messages.
                bool shouldDropCaptionThisRun = rule.EditOptions.DropMediaCaptions && originalMessageHadMediaContent;

                if (shouldDropCaptionThisRun)
                {
                    finalCaption = string.Empty;
                    finalEntities = null;
                    _logger.LogInformation("Job:{JobId}: ProcessCustomSendAsync: Rule '{RuleName}' has DropMediaCaptions enabled for a media message. Caption and entities cleared.", jobId, rule.RuleName);
                }
                else
                {
                    // If not dropping caption, apply all other text/entity edits
                    (finalCaption, finalEntities) = ApplyEditOptions(initialMessageContentFromOrchestrator, initialEntitiesFromOrchestrator, rule.EditOptions, null, jobId);
                    _logger.LogDebug("Job:{JobId}: ProcessCustomSendAsync: Applied EditOptions. Final Caption Length: {Length}, Entities Count: {Entities}.", jobId, finalCaption.Length, finalEntities?.Length ?? 0);
                }
            }
            else
            {
                // No edit options, use original content as is
                finalCaption = initialMessageContentFromOrchestrator ?? string.Empty;
                finalEntities = initialEntitiesFromOrchestrator?.ToArray();
                _logger.LogDebug("Job:{JobId}: ProcessCustomSendAsync: No EditOptions configured. Using original content as final.", jobId);
            }


            // Log the final state of caption and entities after all edits
            _logger.LogInformation("Job:{JobId}: ProcessCustomSendAsync: After ALL caption processing for Rule '{RuleName}': Final Caption (Length {Length}, IsEmpty: {IsEmpty}): '{CaptionPreview}'. Final Entities Count: {EntitiesCount}.",
                jobId, rule.RuleName, finalCaption.Length, string.IsNullOrEmpty(finalCaption), TruncateString(finalCaption, 100), finalEntities?.Length ?? 0);

            // Determine if there's *any* content to send after all edits.
            bool hasFinalTextContent = !string.IsNullOrEmpty(finalCaption) || (finalEntities != null && finalEntities.Any());
            bool hasFinalMediaContent = mediaGroupItems != null && mediaGroupItems.Any();

            if (hasFinalMediaContent)
            {
                _logger.LogDebug("Job:{JobId}: ProcessCustomSendAsync: Handling as media group with {Count} item(s).", jobId, mediaGroupItems.Count);

                // Ensure mediaToSendForApi is correctly populated for the API call
                ICollection<TL.InputMedia> mediaToSendForApi = mediaGroupItems
                                                        .Where(item => item.Media != null)
                                                        .Select(item => item.Media!)
                                                        .ToList();

                if (mediaToSendForApi.Count == 0)
                {
                    _logger.LogWarning("Job:{JobId}: ProcessCustomSendAsync: No valid media items after filtering media group buffer. Skipping send for rule '{RuleName}'.", jobId, rule.RuleName);
                }
                else if (mediaToSendForApi.Count == 1)
                {
                    _logger.LogDebug("Job:{JobId}: ProcessCustomSendAsync: Media group resolved to a single item. Sending as a single media message.", jobId);
                    await _sendMessageRetryPolicy.ExecuteAsync(
                        async (pollyContext, pollyCancellationToken) =>
                            await _userApiClient.SendMessageAsync(
                                toPeer,
                                finalCaption,
                                cancellationToken: pollyCancellationToken,
                                entities: finalEntities,
                                media: mediaToSendForApi.First(),
                                noWebpage: rule.EditOptions?.RemoveLinks ?? false,
                                background: false,
                                schedule_date: null,
                                sendAsBot: false
                            ),
                        new Context($"SendSingleMediaFromGroup_{GetInputPeerIdValueForLogging(toPeer)}_Rule_{rule.RuleName}"),
                        cancellationToken
                    );
                    _logger.LogInformation("Job:{JobId}: Single media message (originally part of group) sent to Target {TargetChannelId} via Rule '{RuleName}'.",
                        jobId, GetInputPeerIdValueForLogging(toPeer), rule.RuleName);
                }
                else // mediaToSendForApi.Count > 1, send as album
                {
                    _logger.LogDebug("Job:{JobId}: ProcessCustomSendAsync: Sending {Count} media items as an album.", jobId, mediaToSendForApi.Count);

                    await _sendMediaGroupRetryPolicy.ExecuteAsync(
                        async (pollyContext, pollyCancellationToken) =>
                            await _userApiClient.SendMediaGroupAsync(
                                peer: toPeer,
                                media: mediaToSendForApi,
                                cancellationToken: pollyCancellationToken,
                                albumCaption: finalCaption,
                                albumEntities: finalEntities,
                                replyToMsgId: null,
                                background: false,
                                schedule_date: null,
                                sendAsBot: false
                            ),
                        new Context(nameof(ITelegramUserApiClient.SendMediaGroupAsync)),
                        cancellationToken
                    );
                    _logger.LogInformation("Job:{JobId}: Media group (Album) successfully sent to Target {TargetChannelId} via Rule '{RuleName}'.",
                        jobId, GetInputPeerIdValueForLogging(toPeer), rule.RuleName);
                }
            }
            else if (hasFinalTextContent) // Only send text if there's actual text/entities
            {
                _logger.LogInformation("Job:{JobId}: ProcessCustomSendAsync: Sending as single text message. Final Caption Length: {CaptionLength}. NoWebpagePreview: {NoWebpagePreview}. Rule: '{RuleName}'. Target: {TargetPeer}",
                    jobId, finalCaption.Length, rule.EditOptions?.RemoveLinks ?? false, rule.RuleName, GetInputPeerIdValueForLogging(toPeer));

                await _sendMessageRetryPolicy.ExecuteAsync(
                    async (pollyContext, pollyCancellationToken) =>
                        await _userApiClient.SendMessageAsync(
                            toPeer,
                            finalCaption,
                            cancellationToken: pollyCancellationToken,
                            entities: finalEntities,
                            media: null,
                            noWebpage: rule.EditOptions?.RemoveLinks ?? false,
                            background: false,
                            schedule_date: null,
                            sendAsBot: false
                        ),
                    new Context($"SendTextMessage_{GetInputPeerIdValueForLogging(toPeer)}_Rule_{rule.RuleName}"),
                    cancellationToken
                );
                _logger.LogInformation("Job:{JobId}: Single text message sent to Target {TargetChannelId} via Rule '{RuleName}'.",
                    jobId, GetInputPeerIdValueForLogging(toPeer), rule.RuleName);
            }
            else if (originalMessageHadTextContent || originalMessageHadMediaContent) // Message became empty after edits, but original had content.
            {
                string defaultSkippedMessage = "[Original message filtered or content stripped by rule]";
                _logger.LogWarning("Job:{JobId}: ProcessCustomSendAsync: Original message had content, but it became empty after edits for rule '{RuleName}'. Sending default placeholder: '{PlaceholderMessage}'",
                    jobId, rule.RuleName, defaultSkippedMessage);

                await _sendMessageRetryPolicy.ExecuteAsync(
                    async (pollyContext, pollyCancellationToken) =>
                        await _userApiClient.SendMessageAsync(
                            toPeer,
                            defaultSkippedMessage,
                            cancellationToken: pollyCancellationToken,
                            entities: null,
                            media: null,
                            noWebpage: true, // Always disable webpage for placeholders
                            background: false,
                            schedule_date: null,
                            sendAsBot: false
                        ),
                    new Context($"SendPlaceholderMessage_{GetInputPeerIdValueForLogging(toPeer)}_Rule_{rule.RuleName}"),
                    cancellationToken
                );
                _logger.LogInformation("Job:{JobId}: Placeholder message sent to Target {TargetChannelId} for Rule '{RuleName}'.",
                    jobId, GetInputPeerIdValueForLogging(toPeer), rule.RuleName);
            }
            else // No original content, no final content, nothing to send.
            {
                _logger.LogDebug("Job:{JobId}: ProcessCustomSendAsync: Original message had no content, and no media to send. Skipping message transmission for rule '{RuleName}'.", jobId, rule.RuleName);
            }

            _logger.LogInformation("Job:{JobId}: Custom send for Rule '{RuleName}' completed for MsgID preview: '{MessageContentPreview}'", jobId, rule.RuleName, TruncateString(initialMessageContentFromOrchestrator, 50));
        }

        private async Task ProcessSimpleForwardAsync(
                 InputPeer fromPeer,
                 InputPeer toPeer,
                 int sourceMessageId,
                 ForwardingRule rule,
                 CancellationToken cancellationToken,
                 string jobId)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool dropAuthor = rule.EditOptions?.RemoveSourceForwardHeader ?? false;
            bool noForwardsMetadata = rule.EditOptions?.NoForwards ?? false;


            _logger.LogInformation("Job:{JobId}: Attempting to forward message {SourceMsgId} from Source {FromPeer} to Target {ToPeer} via Rule '{RuleName}'. Drop Author Header: {DropAuthor}, Remove Forward Metadata: {NoForwardsMetadata}.",
                jobId, sourceMessageId, GetInputPeerIdValueForLogging(fromPeer), GetInputPeerIdValueForLogging(toPeer), rule.RuleName, dropAuthor, noForwardsMetadata);

            await _sendMessageRetryPolicy.ExecuteAsync(
                async (pollyContext, pollyCancellationToken) =>
                    await _userApiClient.ForwardMessagesAsync(
                        toPeer,
                        new[] { sourceMessageId },
                        fromPeer,
                        dropAuthor: dropAuthor,
                        noForwards: noForwardsMetadata, // Pass the noForwards flag
                        cancellationToken: pollyCancellationToken
                    ),
                new Context($"ForwardMessage_{sourceMessageId}_to_{GetInputPeerIdValueForLogging(toPeer)}_Rule_{rule.RuleName}"),
                cancellationToken
            );

            _logger.LogInformation("Job:{JobId}: Message {SourceMsgId} successfully forwarded to Target {TargetChannelId} via Rule '{RuleName}'.",
                jobId, sourceMessageId, GetInputPeerIdValueForLogging(toPeer), rule.RuleName);
        }

        private (string text, TL.MessageEntity[]? entities) ApplyEditOptions(
            string initialText,
            TL.MessageEntity[]? initialEntities,
            Domain.Features.Forwarding.ValueObjects.MessageEditOptions options,
            TL.MessageMedia? originalMediaIgnored, // Not used here, but kept if you expand to media content edits
            string jobId)
        {
            _logger.LogDebug("Job:{JobId}: ApplyEditOptions: Initial text length: {InitialTextLength}. Options: StripFormat={StripFormatting}, RemoveLinks={RemoveLinks}, DropMediaCaptions={DropMediaCaptions}, NoForwards={NoForwards}",
                jobId, initialText?.Length ?? 0, options.StripFormatting, options.RemoveLinks, options.DropMediaCaptions, options.NoForwards);

            var newTextBuilder = new StringBuilder(initialText ?? string.Empty);
            List<TL.MessageEntity>? currentEntities = initialEntities?.ToList();

            // 1. Strip Formatting (HTML/Markdown)
            if (options.StripFormatting)
            {
                // This regex is very basic for HTML. For robust Markdown, you'd need a more complex parser.
                // Assuming simple HTML-like tags for now.
                newTextBuilder = new StringBuilder(Regex.Replace(initialText ?? string.Empty, "<.*?>", string.Empty, RegexOptions.Singleline));
                currentEntities = null; // Formatting stripped, so entities are no longer valid.
                _logger.LogTrace("Job:{JobId}: ApplyEditOptions: Stripping formatting; entities cleared. Text after HTML strip: '{StrippedTextPreview}'", jobId, TruncateString(newTextBuilder.ToString(), 50));
            }

            // 2. Remove Custom Emoji Entities
            // This needs to be done carefully to adjust offsets of other entities correctly.
            if (currentEntities != null && currentEntities.Any(e => e is TL.MessageEntityCustomEmoji))
            {
                _logger.LogDebug("Job:{JobId}: ApplyEditOptions: Processing {Count} custom emoji entities for removal.", jobId, currentEntities.Count(e => e is TL.MessageEntityCustomEmoji));

                var tempStringBuilder = new StringBuilder();
                var updatedEntities = new List<TL.MessageEntity>();
                int originalOffset = 0;
                int currentNewOffset = 0; // Tracks offset in the new (modified) string

                var sortedEntities = currentEntities.OrderBy(e => e.Offset).ToList();

                foreach (var entity in sortedEntities)
                {
                    if (entity == null) continue;

                    // Append text between previous entity/start and current entity
                    if (entity.Offset > originalOffset)
                    {
                        var textSegment = newTextBuilder.ToString().Substring(originalOffset, entity.Offset - originalOffset);
                        tempStringBuilder.Append(textSegment);
                        currentNewOffset += textSegment.Length;
                    }

                    if (entity is TL.MessageEntityCustomEmoji customEmoji)
                    {
                        // Skip appending the custom emoji text/placeholder, effectively removing it.
                        _logger.LogTrace("Job:{JobId}: ApplyEditOptions: Removing custom emoji entity ({DocumentId}). Original text segment was '{OriginalSegment}'.",
                            jobId, customEmoji.document_id, TruncateString(newTextBuilder.ToString().Substring(entity.Offset, Math.Min(entity.Length, newTextBuilder.Length - entity.Offset)), 20));
                        // currentNewOffset does NOT increase for removed emoji.
                    }
                    else
                    {
                        // For other entities, append their text and adjust their offset.
                        int actualLength = Math.Min(entity.Length, newTextBuilder.Length - entity.Offset);
                        if (actualLength < 0) actualLength = 0; // Defensive check

                        var textSegment = newTextBuilder.ToString().Substring(entity.Offset, actualLength);
                        tempStringBuilder.Append(textSegment);

                        // Clone and add with adjusted offset
                        updatedEntities.Add(CloneEntityWithOffset(entity, currentNewOffset - entity.Offset, jobId));
                        currentNewOffset += textSegment.Length;
                    }
                    originalOffset = entity.Offset + entity.Length; // Move original offset past the processed entity
                }

                // Append any remaining text after the last entity
                if (originalOffset < newTextBuilder.Length)
                {
                    tempStringBuilder.Append(newTextBuilder.ToString().Substring(originalOffset));
                }

                newTextBuilder = tempStringBuilder;
                currentEntities = updatedEntities; // Update entities list
                _logger.LogDebug("Job:{JobId}: ApplyEditOptions: Finished processing custom emoji entities. New text length: {NewTextLength}, new entities count: {NewEntitiesCount}", jobId, newTextBuilder.Length, currentEntities.Count);
            }


            // 3. Text Replacements
            if (options.TextReplacements != null && options.TextReplacements.Any())
            {
                string textAfterAllReplacements = newTextBuilder.ToString();
                bool textChangedByReplace = false;

                foreach (var rep in options.TextReplacements)
                {
                    if (string.IsNullOrEmpty(rep.Find))
                    {
                        _logger.LogTrace("Job:{JobId}: ApplyEditOptions: Skipping empty 'Find' pattern in text replacement rule.", jobId);
                        continue;
                    }

                    if (rep.IsRegex)
                    {
                        string beforeRegexReplace = textAfterAllReplacements;
                        textAfterAllReplacements = Regex.Replace(beforeRegexReplace, rep.Find, rep.ReplaceWith ?? string.Empty, rep.RegexOptions);
                        if (beforeRegexReplace != textAfterAllReplacements) textChangedByReplace = true;
                    }
                    else
                    {
                        string beforePlainReplace = textAfterAllReplacements;
                        textAfterAllReplacements = textAfterAllReplacements.Replace(rep.Find, rep.ReplaceWith ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                        if (beforePlainReplace != textAfterAllReplacements) textChangedByReplace = true;
                    }
                }

                if (textChangedByReplace)
                {
                    newTextBuilder = new StringBuilder(textAfterAllReplacements);
                    _logger.LogDebug("Job:{JobId}: ApplyEditOptions: Text replacements performed. Final text length after replacements: {Length}.", jobId, newTextBuilder.Length);

                    // Re-map entities if text changed
                    if (currentEntities != null && currentEntities.Any())
                    {
                        var remappedEntities = new List<TL.MessageEntity>();
                        foreach (var entity in currentEntities)
                        {
                            string entityText = initialText.Substring(entity.Offset, entity.Length); // Get original text of entity
                            int newOffset = newTextBuilder.ToString().IndexOf(entityText, StringComparison.Ordinal); // Find it in new text

                            if (newOffset != -1)
                            {
                                remappedEntities.Add(CloneEntityWithOffset(entity, newOffset - entity.Offset, jobId));
                            }
                            else
                            {
                                _logger.LogWarning("Job:{JobId}: ApplyEditOptions: Entity text '{EntityTextPreview}' for entity type {EntityType} not found after text replacements. Entity will be dropped.",
                                    jobId, TruncateString(entityText, 20), entity.GetType().Name);
                            }
                        }
                        currentEntities = remappedEntities;
                        _logger.LogInformation("Job:{JobId}: ApplyEditOptions: Remapped {OriginalCount} entities to {RemappedCount} after text replacements.",
                            jobId, initialEntities?.Length ?? 0, remappedEntities.Count);
                    }
                }
            }


            // 4. Prepend Text
            if (!string.IsNullOrEmpty(options.PrependText))
            {
                _logger.LogDebug("Job:{JobId}: ApplyEditOptions: Prepending text: '{PrependTextPreview}'", jobId, TruncateString(options.PrependText, 50));
                newTextBuilder.Insert(0, options.PrependText);
                if (currentEntities != null)
                {
                    int offsetShift = options.PrependText.Length;
                    _logger.LogTrace("Job:{JobId}: ApplyEditOptions: Adjusting {EntitiesCount} entities by offset {OffsetShift} due to prepend.", jobId, currentEntities.Count, offsetShift);
                    var adjustedEntities = new List<TL.MessageEntity>();
                    foreach (var e in currentEntities)
                    {
                        if (e != null) adjustedEntities.Add(CloneEntityWithOffset(e, offsetShift, jobId));
                    }
                    currentEntities = adjustedEntities;
                }
            }

            // 5. Append Text / Custom Footer
            string textToAppend = "";
            if (!string.IsNullOrEmpty(options.AppendText))
            {
                textToAppend += options.AppendText;
                _logger.LogDebug("Job:{JobId}: ApplyEditOptions: Appending text: '{AppendTextPreview}'", jobId, TruncateString(options.AppendText, 50));
            }
            if (!string.IsNullOrEmpty(options.CustomFooter))
            {
                // Ensure footer starts on a new line if content exists and doesn't end with a newline
                if (newTextBuilder.Length > 0 && !newTextBuilder.ToString().EndsWith("\n") && !string.IsNullOrEmpty(options.CustomFooter) && !options.CustomFooter.StartsWith("\n"))
                {
                    textToAppend = "\n" + textToAppend; // Add newline before any appended text
                }
                textToAppend += options.CustomFooter;
                _logger.LogDebug("Job:{JobId}: ApplyEditOptions: Appending custom footer: '{CustomFooterPreview}'", jobId, TruncateString(options.CustomFooter, 50));
            }
            if (!string.IsNullOrEmpty(textToAppend))
            {
                newTextBuilder.Append(textToAppend);
            }

            // 6. Remove Links (after all text manipulation, as offsets would be messy otherwise)
            if (options.RemoveLinks && currentEntities != null)
            {
                int initialCount = currentEntities.Count;
                currentEntities.RemoveAll(e => e is TL.MessageEntityUrl || e is TL.MessageEntityTextUrl);
                _logger.LogDebug("Job:{JobId}: ApplyEditOptions: Link entities removed. Count before: {InitialCount}, Count after: {CurrentCount}", jobId, initialCount, currentEntities.Count);
            }

            string finalText = newTextBuilder.ToString();
            _logger.LogDebug("Job:{JobId}: ApplyEditOptions: Final text length: {FinalTextLength}. Final entities count: {EntitiesCount}.", jobId, finalText.Length, currentEntities?.Count ?? 0);

            // Final sanity check for entities: remove any that are now out of bounds.
            if (currentEntities != null)
            {
                currentEntities = currentEntities
                    .Where(e => e != null && e.Offset >= 0 && e.Length > 0 && e.Offset + e.Length <= finalText.Length)
                    .ToList();
                _logger.LogTrace("Job:{JobId}: ApplyEditOptions: After final sanity check, {ValidEntitiesCount} entities remain valid.", jobId, currentEntities.Count);
            }

            return (finalText, currentEntities?.ToArray());
        }

        private string TruncateString(string? str, int maxLength)
        {
            if (string.IsNullOrEmpty(str)) return "[null_or_empty]";
            return str.Length <= maxLength ? str : str.Substring(0, maxLength) + "...";
        }

        private string GetInputPeerIdValueForLogging(InputPeer peer)
        {
            return peer switch
            {
                InputPeerUser user => $"User({user.user_id})",
                InputPeerChannel channel => $"Channel({channel.channel_id})",
                InputPeerChat chat => $"Chat({chat.chat_id})",
                InputPeerSelf _ => "Self",
                _ => $"OtherPeerType:{peer?.GetType().Name ?? "Null"}"
            };
        }

        private TL.MessageEntity CloneEntityWithOffset(TL.MessageEntity oldEntity, int offsetDelta, string jobId)
        {
            int newOffset = oldEntity.Offset + offsetDelta;
            int newLength = oldEntity.Length;

            // Defensive check for negative offset after delta.
            if (newOffset < 0)
            {
                _logger.LogWarning("Job:{JobId}: CloneEntityWithOffset: Calculated negative new offset {NewOffset} for entity {EntityType} (original {OriginalOffset}, delta {OffsetDelta}). Keeping original offset. THIS INDICATES A BUG in text/entity manipulation logic (e.g., deleting text before entity without re-evaluating offset).",
                    jobId, newOffset, oldEntity.GetType().Name, oldEntity.Offset, offsetDelta);
                newOffset = oldEntity.Offset; // Attempt to salvage by keeping original offset.
            }

            return oldEntity switch
            {
                TL.MessageEntityBold _ => new TL.MessageEntityBold { Offset = newOffset, Length = newLength },
                TL.MessageEntityItalic _ => new TL.MessageEntityItalic { Offset = newOffset, Length = newLength },
                TL.MessageEntityUnderline _ => new TL.MessageEntityUnderline { Offset = newOffset, Length = newLength },
                TL.MessageEntityStrike _ => new TL.MessageEntityStrike { Offset = newOffset, Length = newLength },
                TL.MessageEntitySpoiler _ => new TL.MessageEntitySpoiler { Offset = newOffset, Length = newLength },
                TL.MessageEntityCode _ => new TL.MessageEntityCode { Offset = newOffset, Length = newLength },
                TL.MessageEntityPre pre => new TL.MessageEntityPre { Offset = newOffset, Length = newLength, language = pre.language },
                TL.MessageEntityBlockquote blockquote => new TL.MessageEntityBlockquote { Offset = newOffset, Length = newLength, flags = blockquote.flags },
                TL.MessageEntityEmail _ => new TL.MessageEntityEmail { Offset = newOffset, Length = newLength },
                TL.MessageEntityCashtag _ => new TL.MessageEntityCashtag { Offset = newOffset, Length = newLength },
                TL.MessageEntityMention _ => new TL.MessageEntityMention { Offset = newOffset, Length = newLength },
                TL.MessageEntityPhone _ => new TL.MessageEntityPhone { Offset = newOffset, Length = newLength },
                TL.MessageEntityBotCommand _ => new TL.MessageEntityBotCommand { Offset = newOffset, Length = newLength },
                TL.MessageEntityBankCard _ => new TL.MessageEntityBankCard { Offset = newOffset, Length = newLength },
                TL.MessageEntityHashtag _ => new TL.MessageEntityHashtag { Offset = newOffset, Length = newLength },
                TL.MessageEntityUrl url => new TL.MessageEntityUrl { Offset = newOffset, Length = newLength },
                TL.MessageEntityTextUrl textUrl => new TL.MessageEntityTextUrl { Offset = newOffset, Length = newLength, url = textUrl.url },
                TL.MessageEntityMentionName mentionName => new TL.MessageEntityMentionName { Offset = newOffset, Length = newLength, user_id = mentionName.user_id },
                TL.MessageEntityCustomEmoji customEmoji => new TL.MessageEntityCustomEmoji { Offset = newOffset, Length = newLength, document_id = customEmoji.document_id },

                _ => DefaultCaseHandler(oldEntity, offsetDelta, jobId)
            };
        }

        private TL.MessageEntity DefaultCaseHandler(TL.MessageEntity oldEntity, int offsetDelta, string jobId)
        {
            _logger.LogWarning("Job:{JobId}: CloneEntityWithOffset (DefaultCaseHandler): Unhandled entity type '{EntityType}'. Attempting generic clone. Offset adjustment {OffsetDelta}. IMPORTANT: Any type-specific properties beyond Offset and Length WILL BE LOST. Please consider adding this entity type to the switch case in CloneEntityWithOffset for full fidelity.",
                jobId, oldEntity.GetType().Name, offsetDelta);
            try
            {
                // Create a new instance of the exact runtime type of the entity
                var newGenericEntity = (TL.MessageEntity)Activator.CreateInstance(oldEntity.GetType())!;
                newGenericEntity.Offset = oldEntity.Offset + offsetDelta;
                newGenericEntity.Length = oldEntity.Length;
                return newGenericEntity;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job:{JobId}: CloneEntityWithOffset (DefaultCaseHandler): Failed to create generic instance for entity type '{EntityType}'. This entity might be malformed or invalid.",
                    jobId, oldEntity.GetType().Name);
            }
            _logger.LogError("Job:{JobId}: CloneEntityWithOffset (DefaultCaseHandler): FINAL FALLBACK - returning original entity type '{EntityType}' with potentially incorrect offset/lost properties. Data integrity for this specific entity compromised.",
                jobId, oldEntity.GetType().Name);
            return oldEntity;
        }
    }

    // Hangfire Job Context - Corrected usage.
    // Hangfire injects PerformContext into job methods.
    // Remove the static `BackgroundJobContext.Current` and rely on injection.
    // The previous error `CS1061: 'JobData' does not contain a definition for 'StateHistory'` was indeed
    // because I was trying to access `StateHistory` from `JobData` (which is in `Hangfire.Common`),
    // whereas `PerformContext` gives you direct access to the `BackgroundJob` object from `Hangfire.Storage.Monitoring`,
    // which *does* have state history.
    // However, the simplest fix for `jobId` is `performContext.BackgroundJob.Id`.
    // If you need more than just the ID, you would access `performContext.BackgroundJob.StateHistory`
    // assuming `performContext.BackgroundJob` is `Hangfire.Storage.Monitoring.BackgroundJob` type or similar.
    // For logging the job ID, `performContext.BackgroundJob.Id` is sufficient.
}