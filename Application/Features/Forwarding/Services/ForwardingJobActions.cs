// File: Application\Features\Forwarding\Services\ForwardingJobActions.cs

using Application.Common.Interfaces;
using Application.Features.Forwarding.Interfaces;

// Use the DOMAIN entities and value objects for rules directly
using Domain.Features.Forwarding.Entities; // This is your DomainRule
using Domain.Features.Forwarding.ValueObjects;
using Hangfire;
using Microsoft.Extensions.Logging;
using Polly.Retry;
using Polly;
using System.Text;
using System.Text.RegularExpressions;
using TL;

namespace Application.Features.Forwarding.Services
{
    public class ForwardingJobActions : IForwardingJobActions
    {
        private readonly ILogger<ForwardingJobActions> _logger;
        private readonly ITelegramUserApiClient _userApiClient;
        // private readonly List<ForwardingRule> _allRules; // REMOVE: No longer using settings-based rules list here
        private const int MaxRetries = 3; // This is for Hangfire's AutomaticRetry.

        // MODIFICATION START: Declare Polly policies for individual API calls
        private readonly AsyncRetryPolicy<TL.InputPeer?> _resolvePeerRetryPolicy;
        private readonly AsyncRetryPolicy<TL.UpdatesBase?> _sendMessageRetryPolicy; // For SendMessage and ForwardMessages (which both return UpdatesBase?)
        private readonly AsyncRetryPolicy _sendMediaGroupRetryPolicy; // For SendMediaGroup (which returns Task/void)
        // MODIFICATION END

        public ForwardingJobActions(
            ILogger<ForwardingJobActions> logger,
            ITelegramUserApiClient userApiClient)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userApiClient = userApiClient ?? throw new ArgumentNullException(nameof(userApiClient));
            _logger.LogInformation("ForwardingJobActions initialized.");

            // MODIFICATION START: Initialize Polly policies in the constructor
            // Policy for resolving peers (returns InputPeer?)
            _resolvePeerRetryPolicy = Policy<TL.InputPeer?>
                .Handle<RpcException>(ex => !ex.Message.Contains("FLOOD_WAIT_")) // Exclude FLOOD_WAIT, let Hangfire filter handle it
                .Or<HttpRequestException>() // General network errors
                .Or<IOException>()          // I/O errors
                .WaitAndRetryAsync(new[]
                {
                    TimeSpan.FromMilliseconds(100), // 1st retry after 100ms
                    TimeSpan.FromMilliseconds(250), // 2nd retry after 250ms
                    TimeSpan.FromMilliseconds(500), // 3rd retry after 500ms
                    TimeSpan.FromSeconds(1)         // 4th retry after 1s
                }, (exception, timeSpan, retryCount, context) =>
                {
    
                });

            // Policy for sending single messages or forwarding (returns UpdatesBase?)
            _sendMessageRetryPolicy = Policy<TL.UpdatesBase?>
                .Handle<RpcException>(ex => !ex.Message.Contains("FLOOD_WAIT_")) // Exclude FLOOD_WAIT
                .Or<HttpRequestException>()
                .Or<IOException>()
                .WaitAndRetryAsync(new[]
                {
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(250),
                    TimeSpan.FromMilliseconds(500),
                    TimeSpan.FromSeconds(1)
                }, (exception, timeSpan, retryCount, context) =>
                {
          
                });

            // Policy for sending media groups (returns Task/void)
            _sendMediaGroupRetryPolicy = Policy
                .Handle<RpcException>(ex => !ex.Message.Contains("FLOOD_WAIT_")) // Exclude FLOOD_WAIT
                .Or<HttpRequestException>()
                .Or<IOException>()
                .WaitAndRetryAsync(new[]
                {
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(250),
                    TimeSpan.FromMilliseconds(500),
                    TimeSpan.FromSeconds(1)
                }, (exception, timeSpan, retryCount, context) =>
                {
                    _logger.LogWarning(exception, "Polly (SendMediaGroup): Attempt {RetryCount} failed after {TimeSpan}. Error: {ErrorMessage}",
                        retryCount, timeSpan, exception.Message);
                });
            // MODIFICATION END
        }
        [AutomaticRetry(Attempts = MaxRetries, DelaysInSeconds = new int[] {
            0,   // Immediate retry
            1,   // 1 second delay
            3,   // 3 seconds delay
            5,   // 5 seconds delay
            10,  // 10 seconds delay
            20,  // 20 seconds delay
            30,  // 30 seconds delay
            60,  // 1 minute delay
            120, // 2 minutes delay
            180, // 3 minutes delay
            300, // 5 minutes delay
            420, // 7 minutes delay
            540, // 9 minutes delay
            660, // 11 minutes delay
            780, // 13 minutes delay
            900, // 15 minutes delay
            1020, // 17 minutes delay
            1140, // 19 minutes delay
            1260, // 21 minutes delay
            1380  // 23 minutes delay (Last attempt)
        })]
        public async Task ProcessAndRelayMessageAsync(
            int sourceMessageId,
            long rawSourcePeerId,
            long targetChannelId,
            Domain.Features.Forwarding.Entities.ForwardingRule rule,
            string messageContent,
            TL.MessageEntity[]? messageEntities,
            Peer? senderPeerForFilter,
            List<InputMediaWithCaption>? mediaGroupItems,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("Job: Starting ProcessAndRelay for MsgID {SourceMsgId} from RawSourcePeer {RawSourcePeerId} to Target {TargetChannelId} via DB Rule '{RuleName}'. Initial Content Preview: '{MessageContentPreview}'. Has Media Group: {HasMediaGroup}. Sender Peer: {SenderPeer}",
                sourceMessageId, rawSourcePeerId, targetChannelId, rule.RuleName, TruncateString(messageContent, 50), mediaGroupItems != null && mediaGroupItems.Any(), senderPeerForFilter?.ToString() ?? "N/A");

            if (rule == null)
            {
                throw new ArgumentNullException(nameof(rule), "Rule object cannot be null.");
            }
            if (!rule.IsEnabled)
            {
                _logger.LogInformation("Job: Skipping disabled rule '{RuleName}' for MsgID {SourceMsgId}.", rule.RuleName, sourceMessageId);
                return;
            }

            if (!ShouldProcessMessageBasedOnFilters(messageContent, senderPeerForFilter, rule.FilterOptions, sourceMessageId, rule.RuleName))
            {
                return;
            }

            try
            {
                _logger.LogDebug("Job: Resolving FromPeer using positive ID: {RawSourcePeerId} for Rule: {RuleName}", rawSourcePeerId, rule.RuleName);
                // MODIFICATION: Apply Polly policy to ResolvePeerAsync. Removed CancellationToken due to interface mismatch.
                var fromPeer = await _resolvePeerRetryPolicy.ExecuteAsync(async () => // Added 'async'
                    await _userApiClient.ResolvePeerAsync(rawSourcePeerId) // <-- CancellationToken removed here to match interface
                );
                // MODIFICATION END

                long targetIdForResolve;
                string targetTypeHint = "unknown";
                if (targetChannelId < 0 && targetChannelId.ToString().StartsWith("-100"))
                {
                    targetIdForResolve = Math.Abs(targetChannelId) - 1000000000000L;
                    targetTypeHint = "channel_short_positive";
                    _logger.LogDebug("Job: TargetChannelId {TargetChannelId} is -100xxx format. Using short positive ID {TargetIdForResolve} for resolving. Rule: {RuleName}",
                        targetChannelId, targetIdForResolve, rule.RuleName);
                }
                else if (targetChannelId < 0)
                {
                    targetIdForResolve = Math.Abs(targetChannelId);
                    targetTypeHint = "chat_positive";
                    _logger.LogDebug("Job: TargetChannelId {TargetChannelId} is negative (not -100xxx). Using positive ID {TargetIdForResolve} for resolving. Rule: {RuleName}",
                       targetChannelId, targetIdForResolve, rule.RuleName);
                }
                else
                {
                    targetIdForResolve = targetChannelId;
                    targetTypeHint = "positive_direct";
                    _logger.LogDebug("Job: TargetChannelId {TargetChannelId} is positive. Using ID {TargetIdForResolve} directly for resolving. Rule: {RuleName}",
                        targetChannelId, targetIdForResolve, rule.RuleName);
                }

                // MODIFICATION: Apply Polly policy to ResolvePeerAsync. Removed CancellationToken due to interface mismatch.
                var toPeer = await _resolvePeerRetryPolicy.ExecuteAsync(async () => // Added 'async'
                    await _userApiClient.ResolvePeerAsync(targetIdForResolve) // <-- CancellationToken removed here to match interface
                );
                // MODIFICATION END

                if (fromPeer == null || toPeer == null)
                {
                    throw new InvalidOperationException($"Could not resolve source (ID: {rawSourcePeerId}) or target (OrigID: {targetChannelId} / ResolveID: {targetIdForResolve}) peer for rule '{rule.RuleName}'.");
                }

                // Determine if custom send is needed based on edit options OR if it's a media group
                bool needsCustomSend = (rule.EditOptions != null &&
                        (
                         !string.IsNullOrEmpty(rule.EditOptions.PrependText) ||
                         !string.IsNullOrEmpty(rule.EditOptions.AppendText) ||
                         (rule.EditOptions.TextReplacements != null && rule.EditOptions.TextReplacements.Any()) ||
                         rule.EditOptions.RemoveLinks ||
                         rule.EditOptions.StripFormatting ||
                         !string.IsNullOrEmpty(rule.EditOptions.CustomFooter) ||
                         rule.EditOptions.DropMediaCaptions ||
                         rule.EditOptions.NoForwards
                        )) ||
                        (mediaGroupItems != null && mediaGroupItems.Any());

                if (needsCustomSend)
                {
                    await ProcessCustomSendAsync(toPeer, rule, messageContent, messageEntities, mediaGroupItems, cancellationToken);
                }
                else
                {
                    await ProcessSimpleForwardAsync(fromPeer, toPeer, sourceMessageId, rule, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job: Error processing message {SourceMsgId} from RawSourcePeer {RawSourcePeerId} to TargetChannelId {TargetChannelId} for rule {RuleName}",
                    sourceMessageId, rawSourcePeerId, targetChannelId, rule.RuleName);
                throw;
            }
        }
        private bool ShouldProcessMessageBasedOnFilters(string messageContent, Peer? senderPeer, MessageFilterOptions filterOptions, int messageId, string ruleName)
        {
            if (filterOptions == null)
            {
                _logger.LogTrace("ShouldProcessMessageBasedOnFilters: No FilterOptions defined for Rule '{RuleName}'. Message {MessageId} will be processed.", ruleName, messageId);
                return true;
            }

            if (!string.IsNullOrEmpty(filterOptions.ContainsText))
            {
                bool matches;
                if (filterOptions.ContainsTextIsRegex)
                {
                    matches = Regex.IsMatch(messageContent, filterOptions.ContainsText, filterOptions.ContainsTextRegexOptions);
                }
                else
                {
                    matches = messageContent.Contains(filterOptions.ContainsText, StringComparison.OrdinalIgnoreCase);
                }

                if (!matches)
                {
                    _logger.LogDebug("ShouldProcessMessageBasedOnFilters: Message {MessageId} for Rule '{RuleName}' does NOT contain required text '{ContainsText}'. Skipping.",
                        messageId, ruleName, filterOptions.ContainsText);
                    return false;
                }
                _logger.LogTrace("ShouldProcessMessageBasedOnFilters: Message {MessageId} for Rule '{RuleName}' contains required text '{ContainsText}'. Proceeding with filter check.",
                        messageId, ruleName, filterOptions.ContainsText);
            }

            if (filterOptions.AllowedSenderUserIds != null && filterOptions.AllowedSenderUserIds.Any())
            {
                if (senderPeer is PeerUser userSender)
                {
                    if (!filterOptions.AllowedSenderUserIds.Contains(userSender.user_id))
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }

            }

            if (filterOptions.BlockedSenderUserIds != null && filterOptions.BlockedSenderUserIds.Any())
            {
                if (senderPeer is PeerUser userSender)
                {
                    if (filterOptions.BlockedSenderUserIds.Contains(userSender.user_id))
                    {
                        return false;
                    }
                    _logger.LogTrace("ShouldProcessMessageBasedOnFilters: Message {MessageId} for Rule '{RuleName}' is NOT from a blocked sender {SenderId}. Proceeding with filter check.",
                        messageId, ruleName, userSender.user_id);
                }
            }
            return true;
        }


        [AutomaticRetry(Attempts = MaxRetries, DelaysInSeconds = new int[] {
            0,   // Immediate retry
            1,   // 1 second delay
            3,   // 3 seconds delay
            5,   // 5 seconds delay
            10,  // 10 seconds delay
            20,  // 20 seconds delay
            30,  // 30 seconds delay
            60,  // 1 minute delay
            120, // 2 minutes delay
            180, // 3 minutes delay
            300, // 5 minutes delay
            420, // 7 minutes delay
            540, // 9 minutes delay
            660, // 11 minutes delay
            780, // 13 minutes delay
            900, // 15 minutes delay
            1020, // 17 minutes delay
            1140, // 19 minutes delay
            1260, // 21 minutes delay
            1380  // 23 minutes delay (Last attempt)
        })]
        private async Task ProcessCustomSendAsync(
            InputPeer toPeer,
            Domain.Features.Forwarding.Entities.ForwardingRule rule,
            string initialMessageContentFromOrchestrator,
            TL.MessageEntity[]? initialEntitiesFromOrchestrator,
            List<InputMediaWithCaption>? mediaGroupItems,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("ProcessCustomSendAsync: >>> Starting for Rule: '{RuleName}'. Has MediaGroup: {HasMediaGroup}. Initial Content Preview: '{OrchestratorContentPreview}'.",
            rule.RuleName, mediaGroupItems != null && mediaGroupItems.Any(), TruncateString(initialMessageContentFromOrchestrator, 50));

            string finalCaption = initialMessageContentFromOrchestrator ?? string.Empty;
            TL.MessageEntity[]? finalEntities = initialEntitiesFromOrchestrator?.ToArray();

            string albumCaption = null;
            TL.MessageEntity[]? albumEntities = null;

            if (rule.EditOptions?.DropMediaCaptions == true)
            {
                finalCaption = string.Empty;
                finalEntities = null;
                _logger.LogInformation("ProcessCustomSendAsync: Rule '{RuleName}' has DropMediaCaptions enabled. Clearing main caption and entities.", rule.RuleName);
            }
            else if (rule.EditOptions != null)
            {
                // NOTE: No logic change in ApplyEditOptions here, only the call.
                (finalCaption, finalEntities) = ApplyEditOptions(finalCaption, finalEntities, rule.EditOptions, null);
                _logger.LogDebug("ProcessCustomSendAsync: Applied EditOptions. Final Caption Length: {FinalCaptionLength}, Entities Count: {EntitiesCount}.", finalCaption.Length, finalEntities?.Length ?? 0);
            }
            else
            {
                _logger.LogDebug("ProcessCustomSendAsync: No EditOptions defined for Rule: '{RuleName}'. Using original content.", rule.RuleName);
            }

            _logger.LogInformation("ProcessCustomSendAsync: After ALL caption processing for Rule '{RuleName}': Final Caption (Length {Length}, IsEmpty: {IsEmpty}): '{CaptionPreview}'. Final Entities Count: {EntitiesCount}.",
                rule.RuleName, finalCaption.Length, string.IsNullOrEmpty(finalCaption), TruncateString(finalCaption, 100), finalEntities?.Length ?? 0);

            if (mediaGroupItems != null && mediaGroupItems.Any())
            {
                _logger.LogDebug("ProcessCustomSendAsync: Identified as media group with {Count} items.", mediaGroupItems.Count);
                var mediaToSend = new List<InputSingleMedia>();

                for (int i = 0; i < mediaGroupItems.Count; i++)
                {
                    var item = mediaGroupItems[i];
                    if (item.Media != null)
                    {
                        var singleMedia = new InputSingleMedia();
                        singleMedia.media = item.Media;

                        if (i == 0)
                        {
                            albumCaption = finalCaption;
                            albumEntities = finalEntities;
                            _logger.LogTrace("ProcessCustomSendAsync: First media item. Assigning processed caption/entities to album level. Album Caption set to: '{AlbumCaptionPreview}', Entities Count: {EntitiesCount}",
                                TruncateString(albumCaption, 50), albumEntities?.Length ?? 0);
                        }
                        mediaToSend.Add(singleMedia);
                    }
                    else
                    {
                        _logger.LogWarning("ProcessCustomSendAsync: Media item {Index} in group is null after CreateInputMedia. Skipping.", i);
                    }
                }

                if (mediaToSend.Count > 1)
                {
                    _logger.LogDebug("ProcessCustomSendAsync: Sending {Count} media items as an album.", mediaToSend.Count);

                    _logger.LogInformation("ProcessCustomSendAsync: Calling SendMediaGroupAsync. Album Caption (Length {Length}, IsEmpty: {IsEmpty}): '{AlbumCaptionPreview}'. Album Entities Count: {AlbumEntitiesCount}.",
                        albumCaption.Length, string.IsNullOrEmpty(albumCaption), TruncateString(albumCaption, 100), albumEntities?.Length ?? 0);

                    // MODIFICATION: Apply Polly policy to SendMediaGroupAsync. Removed CancellationToken due to interface mismatch.
                    await _sendMediaGroupRetryPolicy.ExecuteAsync(async () => // Added 'async'
                        await _userApiClient.SendMediaGroupAsync( // Added 'await'
                            toPeer,
                            mediaToSend.ToArray(),
                            albumCaption: albumCaption,
                            albumEntities: albumEntities,
                            replyToMsgId: null,
                            background: false,
                            schedule_date: null,
                            sendAsBot: false,
                            parsedMentions: null
                        // CancellationToken removed here to match interface
                        )
                    );
                    _logger.LogInformation("ProcessCustomSendAsync: Media group (Album) sent to Target {TargetChannelId} via Rule '{RuleName}'.",
                        GetInputPeerIdValueForLogging(toPeer), rule.RuleName);
                }
                else if (mediaToSend.Count == 1)
                {
                    _logger.LogDebug("ProcessCustomSendAsync: Only one valid media item ({MediaType}) from group. Sending as single media message.", mediaToSend[0].media?.GetType().Name);

                    _logger.LogInformation("ProcessCustomSendAsync: Calling SendMessageAsync (single media). Caption (Length {Length}, IsEmpty: {IsEmpty}): '{CaptionPreview}'. Entities Count: {EntitiesCount}.",
                        finalCaption.Length, string.IsNullOrEmpty(finalCaption), TruncateString(finalCaption, 100), finalEntities?.Length ?? 0);

                    // MODIFICATION: Apply Polly policy to SendMessageAsync. Removed CancellationToken due to interface mismatch.
                    await _sendMessageRetryPolicy.ExecuteAsync(async () => // Added 'async'
                         await _userApiClient.SendMessageAsync( // Added 'await'
                            toPeer,
                            finalCaption,
                            replyToMsgId: null,
                            entities: finalEntities,
                            media: mediaToSend[0].media,
                            noWebpage: rule.EditOptions?.RemoveLinks ?? false,
                            background: false,
                            clearDraft: false,
                            schedule_date: null,
                            sendAsBot: false,
                            parsedMentions: null
                        // CancellationToken removed here to match interface
                        )
                    );
                    _logger.LogInformation("ProcessCustomSendAsync: Single media message (from group) sent to Target {TargetChannelId} via Rule '{RuleName}'.",
                        GetInputPeerIdValueForLogging(toPeer), rule.RuleName);
                }
                else
                {
                    _logger.LogWarning("ProcessCustomSendAsync: No valid media items to send from the media group buffer after filtering. Skipping send for rule '{RuleName}'.", rule.RuleName);
                }
            }
            else
            {
                _logger.LogDebug("ProcessCustomSendAsync: Not a media group, processing as single text/media message.");

                if (string.IsNullOrEmpty(finalCaption) && mediaGroupItems == null)
                {
                    _logger.LogWarning("ProcessCustomSendAsync: After edits, no text and no media for single message. Rule: '{RuleName}'. Skipping SendMessageAsync.", rule.RuleName);
                    return;
                }

                _logger.LogInformation("ProcessCustomSendAsync: Sending as single text message. Final Caption Length: {CaptionLength}, NoWebpagePreview: {NoWebpagePreview}. Rule: '{RuleName}'",
                    finalCaption.Length, rule.EditOptions?.RemoveLinks ?? false, rule.RuleName);

                _logger.LogInformation("ProcessCustomSendAsync: Calling SendMessageAsync (text-only). Caption (Length {Length}, IsEmpty: {IsEmpty}): '{CaptionPreview}'. Entities Count: {EntitiesCount}.",
                    finalCaption.Length, string.IsNullOrEmpty(finalCaption), TruncateString(finalCaption, 100), finalEntities?.Length ?? 0);

                // MODIFICATION: Apply Polly policy to SendMessageAsync. Removed CancellationToken due to interface mismatch.
                await _sendMessageRetryPolicy.ExecuteAsync(async () => // Added 'async'
                    await _userApiClient.SendMessageAsync( // Added 'await'
                        toPeer,
                        finalCaption,
                        replyToMsgId: null,
                        entities: finalEntities,
                        media: null,
                        noWebpage: rule.EditOptions?.RemoveLinks ?? false,
                        background: false,
                        clearDraft: false,
                        schedule_date: null,
                        sendAsBot: false,
                        parsedMentions: null
                    // CancellationToken removed here to match interface
                    )
                );
                _logger.LogInformation("ProcessCustomSendAsync: Single text message sent to Target {TargetChannelId} via Rule '{RuleName}'.",
                        GetInputPeerIdValueForLogging(toPeer), rule.RuleName);
            }
        }

        // File: Application\Features\Forwarding\Services\ForwardingJobActions.cs

        // ... (existing Usings and other class members) ...
        [AutomaticRetry(Attempts = MaxRetries, DelaysInSeconds = new int[] {
            0,   // Immediate retry
            1,   // 1 second delay
            3,   // 3 seconds delay
            5,   // 5 seconds delay
            10,  // 10 seconds delay
            20,  // 20 seconds delay
            30,  // 30 seconds delay
            60,  // 1 minute delay
            120, // 2 minutes delay
            180, // 3 minutes delay
            300, // 5 minutes delay
            420, // 7 minutes delay
            540, // 9 minutes delay
            660, // 11 minutes delay
            780, // 13 minutes delay
            900, // 15 minutes delay
            1020, // 17 minutes delay
            1140, // 19 minutes delay
            1260, // 21 minutes delay
            1380  // 23 minutes delay (Last attempt)
        })]
        private (string text, TL.MessageEntity[]? entities) ApplyEditOptions(
            string initialText,
            TL.MessageEntity[]? initialEntities,
            Domain.Features.Forwarding.ValueObjects.MessageEditOptions options,
            TL.MessageMedia? originalMediaIgnored)
        {
            _logger.LogDebug("ApplyEditOptions: Initial text length: {InitialTextLength}. Options: StripFormat={StripFormatting}, RemoveLinks={RemoveLinks}, DropMediaCaptions={DropMediaCaptions}",
                initialText.Length, options.StripFormatting, options.RemoveLinks, options.DropMediaCaptions);

            var newTextBuilder = new StringBuilder(initialText);
            List<TL.MessageEntity>? currentEntities = initialEntities?.ToList();

            if (options.StripFormatting)
            {
                newTextBuilder = new StringBuilder(Regex.Replace(initialText, "<.*?>", string.Empty)); // Simple HTML tag stripping
                currentEntities = null; // Stripping formatting means removing all entities
                _logger.LogTrace("ApplyEditOptions: Stripping formatting, entities cleared. Text after HTML strip: '{StrippedTextPreview}'", TruncateString(newTextBuilder.ToString(), 50));
            }

            // --- Handle Premium Emojis (Custom Emojis) ---
            if (currentEntities != null && currentEntities.Any(e => e is TL.MessageEntityCustomEmoji))
            {
                _logger.LogDebug("ApplyEditOptions: Processing {Count} custom emoji entities.", currentEntities.Count(e => e is TL.MessageEntityCustomEmoji));

                var tempStringBuilder = new StringBuilder();
                var updatedEntities = new List<TL.MessageEntity>();
                int originalOffset = 0; // Tracks position in original string
                int currentNewOffset = 0; // Tracks position in new string (after removals/changes)

                // Sort entities by offset to process them in order
                var sortedEntities = currentEntities.OrderBy(e => e.Offset).ToList();

                foreach (var entity in sortedEntities)
                {
                    if (entity == null) continue;

                    // Append text segment *before* the current entity from original text
                    if (entity.Offset > originalOffset)
                    {
                        var textSegment = newTextBuilder.ToString().Substring(originalOffset, entity.Offset - originalOffset);
                        tempStringBuilder.Append(textSegment);
                        currentNewOffset += textSegment.Length;
                    }

                    if (entity is TL.MessageEntityCustomEmoji customEmoji)
                    {
                        // Action: Remove the custom emoji entity and its corresponding text placeholder.
                        _logger.LogTrace("ApplyEditOptions: Removing custom emoji entity and its text placeholder for document_id {DocumentId}. Original text was '{OriginalSegment}'.",
                            customEmoji.document_id, newTextBuilder.ToString().Substring(entity.Offset, entity.Length));
                        // The `tempStringBuilder` does NOT append the text for this entity.
                        // `currentNewOffset` does NOT advance by `entity.Length` for this entity.
                    }
                    else
                    {
                        // For other entities, append their text and re-add the entity with an adjusted offset.
                        var textSegment = newTextBuilder.ToString().Substring(entity.Offset, entity.Length);
                        tempStringBuilder.Append(textSegment);
                        updatedEntities.Add(CloneEntityWithOffset(entity, currentNewOffset - entity.Offset));
                        currentNewOffset += textSegment.Length;
                    }
                    // Advance originalOffset to past the processed entity
                    originalOffset = entity.Offset + entity.Length;
                }

                // Append any remaining text after the last entity from original text
                if (originalOffset < newTextBuilder.Length)
                {
                    tempStringBuilder.Append(newTextBuilder.ToString().Substring(originalOffset));
                }

                newTextBuilder = tempStringBuilder; // Update the main text builder with text after emoji removal
                currentEntities = updatedEntities; // Update the main entities list without custom emojis
                _logger.LogDebug("ApplyEditOptions: Finished processing custom emoji entities. New text length: {NewTextLength}, new entities count: {NewEntitiesCount}", newTextBuilder.Length, currentEntities.Count);
            }

            if (options.TextReplacements != null && options.TextReplacements.Any())
            {
                string tempText = newTextBuilder.ToString();
                bool textChangedByReplace = false;

                // Store original entities and their positions for remapping
                List<(TL.MessageEntity entity, int originalOffset, int originalLength)> entityOriginalInfo = currentEntities?
                    .Select(e => (e, e.Offset, e.Length))
                    .ToList() ?? new List<(TL.MessageEntity, int, int)>();

                // MODIFICATION START: Updated tuple definition to include originalLength
                var textTransformations = new List<(int originalStartIndex, int originalEndIndex, int originalLength, int newLength)>();
                // MODIFICATION END

                foreach (var rep in options.TextReplacements)
                {
                    if (string.IsNullOrEmpty(rep.Find))
                    {
                        _logger.LogTrace("ApplyEditOptions: Skipping empty Find in text replacement.");
                        continue;
                    }
                    string oldTextBeforeReplaceIteration = tempText;

                    // Use a temporary list for new changes in this iteration to avoid modifying textTransformations during iteration
                    // MODIFICATION START: Updated tuple definition for currentIterationChanges
                    var currentIterationChanges = new List<(int originalStartIndex, int originalEndIndex, int originalLength, int newLength)>();
                    // MODIFICATION END

                    if (rep.IsRegex)
                    {
                        _logger.LogTrace("ApplyEditOptions: Regex Replace. Find: '{Find}', ReplaceWith: '{ReplaceWith}', Options: {RegexOptions}", rep.Find, rep.ReplaceWith, rep.RegexOptions);

                        tempText = Regex.Replace(oldTextBeforeReplaceIteration, rep.Find, m => {
                            int matchStart = m.Index;
                            int matchLength = m.Length;
                            string replacement = rep.ReplaceWith ?? string.Empty;
                            int replacementLength = replacement.Length;

                            // Adjust matchStart based on prior transformations for accurate original indices
                            int adjustedMatchStart = matchStart;
                            foreach (var existingChange in textTransformations)
                            {
                                if (adjustedMatchStart >= existingChange.originalStartIndex)
                                {
                                    adjustedMatchStart -= (existingChange.originalLength - existingChange.newLength);
                                }
                            }

                            // MODIFICATION START: Added matchLength as originalLength to the tuple
                            currentIterationChanges.Add((adjustedMatchStart, adjustedMatchStart + matchLength, matchLength, replacementLength));
                            // MODIFICATION END
                            return replacement;
                        }, rep.RegexOptions);
                    }
                    else // Plain string replacement
                    {
                        _logger.LogTrace("ApplyEditOptions: String Replace (OrdinalIgnoreCase). Find: '{Find}', ReplaceWith: '{ReplaceWith}'", rep.Find, rep.ReplaceWith);

                        int lastIndex = 0;
                        int findLen = rep.Find.Length;
                        string replacement = rep.ReplaceWith ?? string.Empty;
                        int replaceLen = replacement.Length;

                        string currentTextToSearch = oldTextBeforeReplaceIteration;
                        while (true)
                        {
                            int index = currentTextToSearch.IndexOf(rep.Find, lastIndex, StringComparison.OrdinalIgnoreCase);
                            if (index == -1) break;

                            // Calculate original index before any prior transformations
                            int originalIndex = index;
                            foreach (var existingChange in textTransformations)
                            {
                                if (originalIndex >= existingChange.originalStartIndex)
                                {
                                    originalIndex -= (existingChange.originalLength - existingChange.newLength);
                                }
                            }

                            // MODIFICATION START: Added findLen as originalLength to the tuple
                            currentIterationChanges.Add((originalIndex, originalIndex + findLen, findLen, replaceLen));
                            // MODIFICATION END

                            currentTextToSearch = currentTextToSearch.Remove(index, findLen).Insert(index, replacement);
                            lastIndex = index + replaceLen;
                        }
                        tempText = currentTextToSearch; // Update tempText with the result of this iteration's replacements
                    }

                    if (oldTextBeforeReplaceIteration != tempText)
                    {
                        textChangedByReplace = true;
                        textTransformations.AddRange(currentIterationChanges); // Add to the main list of changes
                    }
                }

                if (textChangedByReplace)
                {
                    newTextBuilder = new StringBuilder(tempText);

                    // Remap entities based on collected text changes
                    if (currentEntities != null && currentEntities.Any())
                    {
                        var remappedEntities = new List<TL.MessageEntity>();
                        foreach (var (entity, originalOffset, originalLength) in entityOriginalInfo)
                        {
                            int newOffset = originalOffset;
                            int newLength = originalLength;
                            bool entityDropped = false;

                            foreach (var change in textTransformations.OrderBy(c => c.originalStartIndex))
                            {
                                // If the entity is entirely after the change, its offset is shifted
                                if (newOffset >= change.originalEndIndex)
                                {
                                    newOffset += (change.newLength - change.originalLength);
                                }
                                // If the entity is entirely before the change, its offset is unaffected by this change
                                else if (newOffset + newLength <= change.originalStartIndex)
                                {
                                    // No change needed for this entity by this change
                                }
                                // If the entity overlaps with the change, it's problematic.
                                // For simplicity, if it significantly overlaps or is contained within a replacement, drop it.
                                else if (!(newOffset + newLength <= change.originalStartIndex || newOffset >= change.originalEndIndex)) // If there's any overlap
                                {
                                    _logger.LogWarning("ApplyEditOptions: Entity type {EntityType} (Orig Offset: {OriginalOffset}, Length: {OriginalLength}) overlaps with text replacement (Original Start: {ChangeOriginalStartIndex}, End: {ChangeOriginalEndIndex}). Dropping entity.",
                                        entity.GetType().Name, originalOffset, originalLength, change.originalStartIndex, change.originalEndIndex);
                                    entityDropped = true;
                                    break; // No need to check other changes for this entity
                                }
                            }

                            if (!entityDropped)
                            {
                                if (newOffset >= 0 && newOffset + newLength <= newTextBuilder.Length)
                                {
                                    remappedEntities.Add(CloneEntityWithOffset(entity, newOffset - originalOffset));
                                }
                                else
                                {
                                    _logger.LogWarning("ApplyEditOptions: Remapped entity type {EntityType} resulted in invalid new offset ({NewOffset}) or length ({NewLength}) within final text. Dropping entity.",
                                        entity.GetType().Name, newOffset, newLength);
                                }
                            }
                        }
                        currentEntities = remappedEntities;
                        _logger.LogInformation("ApplyEditOptions: Text replacements occurred. Attempted remapping {OriginalCount} entities to {RemappedCount}. Accuracy not guaranteed for complex changes.",
                            entityOriginalInfo.Count, remappedEntities.Count);
                    }
                }
            }

            string finalText = newTextBuilder.ToString();

            // Prepend text logic
            if (!string.IsNullOrEmpty(options.PrependText))
            {
                _logger.LogDebug("ApplyEditOptions: Prepending text: '{PrependText}'", options.PrependText);
                finalText = options.PrependText + finalText;
                if (currentEntities != null && options.PrependText != null)
                {
                    int offset = options.PrependText.Length;
                    _logger.LogTrace("ApplyEditOptions: Adjusting {EntitiesCount} entities by offset {Offset} due to prepend.", currentEntities.Count, offset);
                    var adjustedEntities = new List<TL.MessageEntity>();
                    foreach (var e in currentEntities)
                    {
                        if (e != null)
                        {
                            adjustedEntities.Add(CloneEntityWithOffset(e, offset));
                        }
                        else
                        {
                            _logger.LogWarning("ApplyEditOptions: Encountered a null MessageEntity while adjusting offsets.");
                        }
                    }
                    currentEntities = adjustedEntities;
                }
            }

            // Append text and Custom Footer logic
            string textToAppend = "";
            if (!string.IsNullOrEmpty(options.AppendText))
            {
                textToAppend += options.AppendText;
                _logger.LogDebug("ApplyEditOptions: Appending text: '{AppendText}'", options.AppendText);
            }
            if (!string.IsNullOrEmpty(options.CustomFooter))
            {
                if (!string.IsNullOrEmpty(textToAppend) && !textToAppend.EndsWith("\n") && !options.CustomFooter.StartsWith("\n"))
                {
                    textToAppend += "\n"; // Ensure new line if content is directly before footer
                }
                textToAppend += options.CustomFooter;
                _logger.LogDebug("ApplyEditOptions: Appending custom footer: '{CustomFooter}'", options.CustomFooter);
            }

            if (!string.IsNullOrEmpty(textToAppend))
            {
                finalText += textToAppend;
            }

            // Remove links logic
            if (options.RemoveLinks && currentEntities != null)
            {
                int initialCount = currentEntities.Count;
                currentEntities.RemoveAll(e => e is TL.MessageEntityUrl || e is TL.MessageEntityTextUrl);
                _logger.LogDebug("ApplyEditOptions: Link entities removed. Count before: {InitialCount}, Count after: {CurrentCount}", initialCount, currentEntities.Count);
            }

            _logger.LogDebug("ApplyEditOptions: Final text length: {FinalTextLength}. Entities count: {EntitiesCount}", finalText.Length, currentEntities?.Count ?? 0);
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
                _ => peer?.ToString() ?? "NullPeer"
            };
        }



        [AutomaticRetry(Attempts = MaxRetries)]
        private async Task ProcessSimpleForwardAsync(
                 InputPeer fromPeer,
                 InputPeer toPeer,
                 int sourceMessageId,
                 ForwardingRule rule,
                 CancellationToken cancellationToken)
        {
            bool dropAuthor = rule.EditOptions?.RemoveSourceForwardHeader ?? false;
            bool noForwards = rule.EditOptions?.NoForwards ?? false;

            await _userApiClient.ForwardMessagesAsync(
                toPeer,
                new[] { sourceMessageId },
                fromPeer,
                dropAuthor: dropAuthor,
                noForwards: noForwards);

            //_logger.LogInformation(
            //    "Message {SourceMsgId} forwarded to Target {TargetChannelId} via Rule '{RuleName}'.",
            //    sourceMessageId, GetInputPeerIdValueForLogging(toPeer), rule.RuleName); // Using global helper, assume it's accessible or make local.
        }

   

        private TL.MessageEntity DefaultCaseHandler(TL.MessageEntity oldEntity, int offsetDelta)
        {
            _logger.LogWarning("CloneEntityWithOffset (DefaultCaseHandler): Unhandled or generic entity type {EntityType}. Attempting generic clone. Offset adjustment WILL BE INCORRECT if text was prepended and this entity type is not specifically handled.", oldEntity.GetType().Name);
            try
            {
                if (Activator.CreateInstance(oldEntity.GetType()) is TL.MessageEntity newGenericEntity)
                {
                    newGenericEntity.Offset = oldEntity.Offset + offsetDelta;
                    newGenericEntity.Length = oldEntity.Length;
                    return newGenericEntity;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CloneEntityWithOffset (DefaultCaseHandler): Failed to create or clone instance for entity type {EntityType}.", oldEntity.GetType().Name);
            }
            _logger.LogError("CloneEntityWithOffset (DefaultCaseHandler): FALLBACK - returning original entity type {EntityType} with potentially incorrect offset. THIS IS A BUG if text was prepended.", oldEntity.GetType().Name);
            return oldEntity;
        }




        private TL.MessageEntity CloneEntityWithOffset(TL.MessageEntity oldEntity, int offsetDelta)
        {
            int newOffset = oldEntity.Offset + offsetDelta;
            int newLength = oldEntity.Length;

            return oldEntity switch
            {
                TL.MessageEntityBold _ => new TL.MessageEntityBold { Offset = newOffset, Length = newLength },
                TL.MessageEntityItalic _ => new TL.MessageEntityItalic { Offset = newOffset, Length = newLength },
                TL.MessageEntityUnderline _ => new TL.MessageEntityUnderline { Offset = newOffset, Length = newLength },
                TL.MessageEntityStrike _ => new TL.MessageEntityStrike { Offset = newOffset, Length = newLength },
                TL.MessageEntitySpoiler _ => new TL.MessageEntitySpoiler { Offset = newOffset, Length = newLength },
                TL.MessageEntityCode _ => new TL.MessageEntityCode { Offset = newOffset, Length = newLength },
                TL.MessageEntityPre pre => new TL.MessageEntityPre { Offset = newOffset, Length = newLength, language = pre.language },
                TL.MessageEntityUrl url => new TL.MessageEntityUrl { Offset = newOffset, Length = newLength },
                TL.MessageEntityTextUrl textUrl => new TL.MessageEntityTextUrl { Offset = newOffset, Length = newLength, url = textUrl.url },
                TL.MessageEntityMentionName mentionName => new TL.MessageEntityMentionName { Offset = newOffset, Length = newLength, user_id = mentionName.user_id },
                TL.MessageEntityCustomEmoji customEmoji => new TL.MessageEntityCustomEmoji { Offset = newOffset, Length = newLength, document_id = customEmoji.document_id },
                TL.MessageEntityBlockquote blockquote => new TL.MessageEntityBlockquote { Offset = newOffset, Length = newLength, flags = blockquote.flags },
                TL.MessageEntityHashtag hashtag => new TL.MessageEntityHashtag { Offset = newOffset, Length = newLength },
                _ => DefaultCaseHandler(oldEntity, offsetDelta)
            };

        }
    }
}