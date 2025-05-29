// File: Application\Features\Forwarding\Services\ForwardingJobActions.cs

using Application.Common.Interfaces;
using Application.Features.Forwarding.Interfaces;

// Use the DOMAIN entities and value objects for rules directly
using Domain.Features.Forwarding.Entities; // This is your DomainRule
using Domain.Features.Forwarding.ValueObjects;
using Hangfire;
using Microsoft.Extensions.Logging;
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
        private const int MaxRetries = 3;
        // const int RetryDelaySeconds = 5; // Hangfire's AutomaticRetry handles delay strategies

        public ForwardingJobActions(
            ILogger<ForwardingJobActions> logger,
            ITelegramUserApiClient userApiClient)
        // IOptions<List<ForwardingRule>> rulesOptions) // REMOVE: No longer injecting settings rules
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userApiClient = userApiClient ?? throw new ArgumentNullException(nameof(userApiClient));
            // _allRules = rulesOptions?.Value ?? new List<ForwardingRule>(); // REMOVE
            // if (!_allRules.Any()) // REMOVE
            // {
            //     _logger.LogWarning("ForwardingJobActions initialized with zero forwarding rules from settings.");
            // }
            _logger.LogInformation("ForwardingJobActions initialized.");
        }

        [AutomaticRetry(Attempts = MaxRetries)]
        public async Task ProcessAndRelayMessageAsync(
            int sourceMessageId,
            long rawSourcePeerId,
            long targetChannelId,
            Domain.Features.Forwarding.Entities.ForwardingRule rule,
            string messageContent,
            TL.MessageEntity[]? messageEntities,
            Peer? senderPeerForFilter,
            List<InputMediaWithCaption>? mediaGroupItems, // Changed as per previous steps
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("Job: Starting ProcessAndRelay for MsgID {SourceMsgId} from RawSourcePeer {RawSourcePeerId} to Target {TargetChannelId} via DB Rule '{RuleName}'. Initial Content Preview: '{MessageContentPreview}'. Has Media Group: {HasMediaGroup}. Sender Peer: {SenderPeer}",
                sourceMessageId, rawSourcePeerId, targetChannelId, rule.RuleName, TruncateString(messageContent, 50), mediaGroupItems != null && mediaGroupItems.Any(), senderPeerForFilter?.ToString() ?? "N/A");

            if (rule == null)
            {
            //    _logger.LogError("Job: Rule object is null for MsgID {SourceMsgId}. Aborting.", sourceMessageId);
                throw new ArgumentNullException(nameof(rule), "Rule object cannot be null.");
            }
            if (!rule.IsEnabled)
            {
              //  _logger.LogInformation("Job: Rule '{RuleName}' is disabled. Skipping message {SourceMsgId}.", rule.RuleName, sourceMessageId);
                return;
            }

            // --- BEGIN FILTERING LOGIC ---
            if (!ShouldProcessMessageBasedOnFilters(messageContent, senderPeerForFilter, rule.FilterOptions, sourceMessageId, rule.RuleName))
            {
              //  _logger.LogInformation("Job: Message {SourceMsgId} skipped due to filter options of Rule '{RuleName}'.", sourceMessageId, rule.RuleName);
                return;
            }
            // --- END FILTERING LOGIC ---

            try
            {
                _logger.LogDebug("Job: Resolving FromPeer using positive ID: {RawSourcePeerId} for Rule: {RuleName}", rawSourcePeerId, rule.RuleName);
                var fromPeer = await _userApiClient.ResolvePeerAsync(rawSourcePeerId);

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

              //  _logger.LogDebug("Job: Resolving ToPeer with prepared ID: {TargetIdForResolve} (Original TargetId: {TargetChannelId}, TypeHint: {TargetTypeHint}) for Rule: {RuleName}",
          //          targetIdForResolve, targetChannelId, targetTypeHint, rule.RuleName);
                var toPeer = await _userApiClient.ResolvePeerAsync(targetIdForResolve);


                if (fromPeer == null || toPeer == null)
                {
               //     _logger.LogError("Job: Could not resolve FromPeer (RawId: {RawSourcePeerId}, Resolved: {FromPeerStatus}) or ToPeer (OriginalTargetId: {TargetChannelId}, IDUsedForResolve: {TargetIdForResolve}, Resolved: {ToPeerStatus}) for MsgID {SourceMsgId}. Rule: {RuleName}",
                 //       rawSourcePeerId, fromPeer != null, targetChannelId, targetIdForResolve, toPeer != null, sourceMessageId, rule.RuleName);
                    throw new InvalidOperationException($"Could not resolve source (ID: {rawSourcePeerId}) or target (OrigID: {targetChannelId} / ResolveID: {targetIdForResolve}) peer for rule '{rule.RuleName}'.");
                }
               // _logger.LogInformation("Job: Peers resolved. FromPeer: {FromPeerString}, ToPeer: {ToPeerString} for Rule: {RuleName}", GetInputPeerTypeAndIdForLogging(fromPeer), GetInputPeerTypeAndIdForLogging(toPeer), rule.RuleName);

                // Determine if custom send is needed based on edit options OR if it's a media group
                bool needsCustomSend = (rule.EditOptions != null &&
                        (
                         !string.IsNullOrEmpty(rule.EditOptions.PrependText) ||
                         !string.IsNullOrEmpty(rule.EditOptions.AppendText) ||
                         (rule.EditOptions.TextReplacements != null && rule.EditOptions.TextReplacements.Any()) ||
                         rule.EditOptions.RemoveLinks ||
                         rule.EditOptions.StripFormatting ||
                         !string.IsNullOrEmpty(rule.EditOptions.CustomFooter) ||
                         rule.EditOptions.DropMediaCaptions || // This is crucial for media caption removal
                         rule.EditOptions.NoForwards // NoForwards implies custom send if ForwardMessages cannot handle it
                        )) ||
                        (mediaGroupItems != null && mediaGroupItems.Any()); // If media group exists, always custom send

             //   _logger.LogDebug("Job: NeedsCustomSend: {NeedsCustomSend} for Rule: {RuleName}. EditOptions is null: {IsEditOptionsNull}. Has MediaGroup: {HasMediaGroup}.", needsCustomSend, rule.RuleName, rule.EditOptions == null, mediaGroupItems != null && mediaGroupItems.Any());

                if (needsCustomSend)
                {
                //    _logger.LogInformation("Job: Processing custom send for MsgID {SourceMsgId} using DB Rule '{RuleName}'", sourceMessageId, rule.RuleName);
                    await ProcessCustomSendAsync(toPeer, rule, messageContent, messageEntities, mediaGroupItems, cancellationToken);
                }
                else
                {
                  //  _logger.LogInformation("Job: Processing simple forward for MsgID {SourceMsgId} using DB Rule '{RuleName}'", sourceMessageId, rule.RuleName);
                    await ProcessSimpleForwardAsync(fromPeer, toPeer, sourceMessageId, rule, cancellationToken);
                }
             //   _logger.LogInformation("Job: Successfully processed message {SourceMsgId} for rule {RuleName}", sourceMessageId, rule.RuleName);
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
                return true; // No filters, so process
            }

            // Example: Filter by ContainsText
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

            // Example: Filter by AllowedSenderUserIds
            if (filterOptions.AllowedSenderUserIds != null && filterOptions.AllowedSenderUserIds.Any())
            {
                if (senderPeer is PeerUser userSender)
                {
                    if (!filterOptions.AllowedSenderUserIds.Contains(userSender.user_id))
                    {
                    //    _logger.LogDebug("ShouldProcessMessageBasedOnFilters: Message {MessageId} for Rule '{RuleName}' is from unallowed sender {SenderId}. Skipping.",
                    //        messageId, ruleName, userSender.user_id);
                        return false;
                    }
                 //   _logger.LogTrace("ShouldProcessMessageBasedOnFilters: Message {MessageId} for Rule '{RuleName}' is from allowed sender {SenderId}. Proceeding with filter check.",
                   //    messageId, ruleName, userSender.user_id);
                }
                else
                {
                    // If allowed senders are specified but message is not from a user (e.g., from a channel/chat itself),
                    // you might want to skip or treat as allowed. Current logic skips if not from a user.
                  //  _logger.LogDebug("ShouldProcessMessageBasedOnFilters: Message {MessageId} for Rule '{RuleName}' has AllowedSenderUserIds but sender is not a PeerUser ({SenderType}). Skipping.",
                   //     messageId, ruleName, senderPeer?.GetType().Name ?? "Null");
                    return false;
                }

            }

            // Example: Filter by BlockedSenderUserIds
            if (filterOptions.BlockedSenderUserIds != null && filterOptions.BlockedSenderUserIds.Any())
            {
                if (senderPeer is PeerUser userSender)
                {
                    if (filterOptions.BlockedSenderUserIds.Contains(userSender.user_id))
                    {
                    //    _logger.LogDebug("ShouldProcessMessageBasedOnFilters: Message {MessageId} for Rule '{RuleName}' is from BLOCKED sender {SenderId}. Skipping.",
                  //          messageId, ruleName, userSender.user_id);
                        return false;
                    }
                    _logger.LogTrace("ShouldProcessMessageBasedOnFilters: Message {MessageId} for Rule '{RuleName}' is NOT from a blocked sender {SenderId}. Proceeding with filter check.",
                        messageId, ruleName, userSender.user_id);
                }
            }
          //  _logger.LogTrace("ShouldProcessMessageBasedOnFilters: Message {MessageId} passed all active filters for Rule '{RuleName}'.", messageId, ruleName);
            return true;
        }
      

        // UPDATED: ProcessCustomSendAsync to handle List<InputMediaWithCaption> for albums
        // UPDATED: ProcessCustomSendAsync to handle List<InputMediaWithCaption> for albums
        private async Task ProcessCustomSendAsync(
               InputPeer toPeer,
               Domain.Features.Forwarding.Entities.ForwardingRule rule,
               string initialMessageContentFromOrchestrator,
               TL.MessageEntity[]? initialEntitiesFromOrchestrator,
               List<InputMediaWithCaption>? mediaGroupItems, // CHANGED: List of media items
               CancellationToken cancellationToken)
        {
            _logger.LogInformation("ProcessCustomSendAsync: >>> Starting for Rule: '{RuleName}'. Has MediaGroup: {HasMediaGroup}. Initial Content Preview: '{OrchestratorContentPreview}'.",
                rule.RuleName, mediaGroupItems != null && mediaGroupItems.Any(), TruncateString(initialMessageContentFromOrchestrator, 50));

            string finalCaption = initialMessageContentFromOrchestrator;
            TL.MessageEntity[]? finalEntities = initialEntitiesFromOrchestrator?.ToArray();

            // Apply edits to the main caption IF it's not a media group or if it's the main caption of the group
            // For a media group, the "main" caption to be edited is typically the one from the first item
            // or the item explicitly designated by the orchestrator.
            // For simplicity here, we assume the initialMessageContentFromOrchestrator is the relevant caption.
            // If DropMediaCaptions is true AND we have media (either single or group), clear the initial content.
            if (rule.EditOptions?.DropMediaCaptions == true && (mediaGroupItems != null && mediaGroupItems.Any()))
            {
                finalCaption = string.Empty;
                finalEntities = null;
           //     _logger.LogInformation("ProcessCustomSendAsync: DropMediaCaptions is TRUE and media group is present. Clearing main caption.");
            }
            else if (rule.EditOptions?.DropMediaCaptions == true && mediaGroupItems == null)
            {
                // This block handles the case where mediaGroupItems is null but logic still wants to clear caption.
                // This path is less likely if orchestrator always puts media into mediaGroupItems (even for single media).
                finalCaption = string.Empty;
                finalEntities = null;
           //     _logger.LogInformation("ProcessCustomSendAsync: DropMediaCaptions is TRUE and single media implied. Clearing caption.");
            }

            // Apply text transformations (prepend, append, replacements) to the main caption
            if (rule.EditOptions != null)
            {
            //    _logger.LogInformation("ProcessCustomSendAsync: Applying text edit options for Rule: '{RuleName}'. Original Text Preview: '{InitialTextPreview}'.",
              //      rule.RuleName, TruncateString(finalCaption, 50));
                (finalCaption, finalEntities) = ApplyEditOptions(finalCaption, finalEntities, rule.EditOptions, null);
           //     _logger.LogInformation("ProcessCustomSendAsync: After ApplyEditOptions. Final Text Preview: '{FinalTextPreview}'.",
            //        TruncateString(finalCaption, 50));
            }
            else
            {
                _logger.LogDebug("ProcessCustomSendAsync: No EditOptions defined for Rule: '{RuleName}'. Using original content.", rule.RuleName);
            }

            // Determine if sending as a media group or single message
            if (mediaGroupItems != null && mediaGroupItems.Any())
            {
                _logger.LogInformation("ProcessCustomSendAsync: Sending as Media Group ({Count} items).", mediaGroupItems.Count);
                var mediaToSend = new List<InputSingleMedia>(); // WTelegramClient expects InputSingleMedia[] for albums

                for (int i = 0; i < mediaGroupItems.Count; i++)
                {
                    var item = mediaGroupItems[i];
                    if (item.Media != null)
                    {
                        // FIXED: Correct usage of InputSingleMedia with its properties
                        var singleMedia = new InputSingleMedia { media = item.Media };

                        // Assign the final (edited) caption/entities to the first media item in the group.
                        // Subsequent items will have their original captions (if any) or empty if DropMediaCaptions is true.
                        if (i == 0) // Apply edited caption/entities to the first media item
                        {
                            singleMedia.message = finalCaption; // Correct property name: Message
                            singleMedia.entities = finalEntities; // Correct property name: Entities
                        }
                        else // For subsequent media items, clear caption if DropMediaCaptions is true
                        {
                            if (rule.EditOptions?.DropMediaCaptions == true)
                            {
                                singleMedia.message = string.Empty; // Correct property name: Message
                                singleMedia.entities = null; // Correct property name: Entities
                            }
                            else
                            {
                                // If DropMediaCaptions is false, keep original captions for subsequent items
                                // as provided by orchestrator (item.Caption, item.Entities)
                                singleMedia.message = item.Caption; // Correct property name: Message
                                singleMedia.entities = item.Entities; // Correct property name: Entities
                            }
                        }
                        mediaToSend.Add(singleMedia);
                    }
                    else
                    {
                        _logger.LogWarning("ProcessCustomSendAsync: Media item {Index} in group is null. Skipping.", i);
                    }
                }

                if (mediaToSend.Any())
                {
                    // FIXED: Removed cancellationToken: cancellationToken as SendMediaGroupAsync overload does not have it.
                    await _userApiClient.SendMediaGroupAsync(toPeer, mediaToSend.ToArray()); // Removed cancellationToken
                    _logger.LogInformation("ProcessCustomSendAsync: Media group sent to Target {TargetChannelId} via Rule '{RuleName}'.",
                        GetInputPeerIdValueForLogging(toPeer), rule.RuleName);
                }
                else
                {
                 //   _logger.LogWarning("ProcessCustomSendAsync: No valid media items to send in the media group. Skipping send.", rule.RuleName);
                }
            }
            else // It's a single text-only message (or single media if orchestrator didn't put it in mediaGroupItems)
            {
                // This path is for text-only messages. If single media was passed to the Job
                // not as a List<InputMediaWithCaption> but as a single InputMedia (from older signature),
                // it would NOT be handled correctly here, as `mediaGroupItems` is null.
                // The current orchestrator design implies single media should be a List with one item.

                if (string.IsNullOrEmpty(finalCaption))
                {
                    _logger.LogWarning("ProcessCustomSendAsync: After edits, no text and no media group for MsgID. Rule: '{RuleName}'. Skipping SendMessageAsync.", rule.RuleName);
                    return;
                }

                _logger.LogInformation("ProcessCustomSendAsync: Sending as single text message. Final Caption Length: {CaptionLength}, NoWebpagePreview: {NoWebpagePreview}. Rule: '{RuleName}'",
                    finalCaption.Length, rule.EditOptions?.RemoveLinks ?? false, rule.RuleName);

                await _userApiClient.SendMessageAsync(
                    toPeer,
                    finalCaption, // The modified text/caption
                    entities: finalEntities, // The modified entities
                    media: null, // If mediaGroupItems is null/empty, we're sending text-only
                    noWebpage: rule.EditOptions?.RemoveLinks ?? false
                );
             //   _logger.LogInformation("ProcessCustomSendAsync: Single text message sent to Target {TargetChannelId} via Rule '{RuleName}'.",
              //      GetInputPeerIdValueForLogging(toPeer), rule.RuleName);
            }
        }


        private string TruncateString(string? str, int maxLength)
        {
            if (string.IsNullOrEmpty(str)) return "[null_or_empty]";
            return str.Length <= maxLength ? str : str.Substring(0, maxLength) + "...";
        }


        private string GetInputPeerTypeAndIdForLogging(InputPeer peer)
        {
            return peer switch
            {
                InputPeerUser user => $"User({user.user_id})",
                InputPeerChannel channel => $"Channel({channel.channel_id})",
                InputPeerChat chat => $"Chat({chat.chat_id})",
                InputPeerSelf _ => $"Self({_userApiClient.NativeClient?.UserId ?? 0})",
                _ => peer?.ToString() ?? "NullPeer"
            };
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


        // ... (CloneEntityWithOffset and DefaultCaseHandler) ...


        private async Task ProcessSimpleForwardAsync(
            InputPeer fromPeer,
            InputPeer toPeer,
            int sourceMessageId,
            ForwardingRule rule, // This is Domain.Features.Forwarding.Entities.ForwardingRule
            CancellationToken cancellationToken)
        {
            // Simple forward cannot drop captions for media groups; WTelegramClient ForwardMessagesAsync doesn't support it directly.
            // If dropMediaCaptions was true and it was a media message, it should have gone through ProcessCustomSendAsync.
            // If it's a simple forward of a media group, WTelegramClient will forward the entire group as is.
            bool dropAuthor = rule.EditOptions?.DropAuthor ?? rule.EditOptions?.RemoveSourceForwardHeader ?? false;
            bool noForwards = rule.EditOptions?.NoForwards ?? false;
            // Removed dropMediaCaptionsSetting check as it's not applicable for simple forward here.

          //  _logger.LogDebug("ProcessSimpleForwardAsync: Parameters for Rule '{RuleName}': DropAuthor={DropAuthor}, NoForwards={NoForwards}",
            //    rule.RuleName, dropAuthor, noForwards);

            await _userApiClient.ForwardMessagesAsync(
                toPeer: toPeer,
                messageIds: new[] { sourceMessageId },
                fromPeer: fromPeer,
                dropAuthor: dropAuthor,
                noForwards: noForwards
            // Removed dropMediaCaptions: false from here to resolve ambiguity
            );

            _logger.LogInformation("Job: Message {SourceMsgId} forwarded to Target {TargetChannelId} via Rule '{RuleName}'.",
                sourceMessageId, toPeer.ID, rule.RuleName);
        }

        // ApplyEditOptions and CloneEntityWithOffset should be the versions
        // that work with Domain.Features.Forwarding.ValueObjects.MessageEditOptions
        // as provided in my "full Hangfire please" response.
        // I'll re-paste a verified ApplyEditOptions for clarity.
        private (string text, TL.MessageEntity[]? entities) ApplyEditOptions(
           string initialText,
           TL.MessageEntity[]? initialEntities,
           Domain.Features.Forwarding.ValueObjects.MessageEditOptions options,
           MessageMedia? originalMediaIgnored) // Changed parameter name to originalMediaIgnored and will not use it
        {
            _logger.LogDebug("ApplyEditOptions: Initial text length: {InitialTextLength}. Options: StripFormat={StripFormatting}, RemoveLinks={RemoveLinks}, DropMediaCaptions={DropMediaCaptions}",
                initialText.Length, options.StripFormatting, options.RemoveLinks, options.DropMediaCaptions);

            var newTextBuilder = new StringBuilder(initialText);
            List<TL.MessageEntity>? currentEntities = initialEntities?.ToList();

            if (options.StripFormatting)
            {
                currentEntities = null;
                _logger.LogTrace("ApplyEditOptions: Stripping formatting, entities cleared.");
            }

            if (options.TextReplacements != null && options.TextReplacements.Any())
            {
                _logger.LogDebug("ApplyEditOptions: Applying {ReplacementsCount} text replacements.", options.TextReplacements.Count);
                string tempText = newTextBuilder.ToString();
                bool textChangedByReplace = false;
                foreach (var rep in options.TextReplacements)
                {
                    if (string.IsNullOrEmpty(rep.Find))
                    {
                        _logger.LogTrace("ApplyEditOptions: Skipping empty Find in text replacement.");
                        continue;
                    }
                    string oldTextBeforeReplace = tempText;
                    if (rep.IsRegex)
                    {
                        _logger.LogTrace("ApplyEditOptions: Regex Replace. Find: '{Find}', ReplaceWith: '{ReplaceWith}', Options: {RegexOptions}", rep.Find, rep.ReplaceWith, rep.RegexOptions);
                        tempText = Regex.Replace(tempText, rep.Find, rep.ReplaceWith ?? "", rep.RegexOptions);
                    }
                    else
                    {
                        _logger.LogTrace("ApplyEditOptions: String Replace (OrdinalIgnoreCase). Find: '{Find}', ReplaceWith: '{ReplaceWith}'", rep.Find, rep.ReplaceWith);
                        tempText = tempText.Replace(rep.Find, rep.ReplaceWith ?? "", StringComparison.OrdinalIgnoreCase);
                    }
                    if (oldTextBeforeReplace != tempText)
                    {
                        textChangedByReplace = true;
                        _logger.LogTrace("ApplyEditOptions: Text changed by replacement. Find: '{Find}'", rep.Find);
                    }
                }
                if (textChangedByReplace)
                {
                    newTextBuilder = new StringBuilder(tempText);
                    if (currentEntities != null && currentEntities.Any())
                    {
                        _logger.LogWarning("ApplyEditOptions: Text replacements occurred, clearing message entities as entity adjustment is complex.");
                        currentEntities = null;
                    }
                }
            }

            string finalText = newTextBuilder.ToString();

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
                    textToAppend += "\n";
                }
                textToAppend += options.CustomFooter;
                _logger.LogDebug("ApplyEditOptions: Appending custom footer: '{CustomFooter}'", options.CustomFooter);
            }

            if (!string.IsNullOrEmpty(textToAppend))
            {
                finalText += textToAppend;
            }

            if (options.RemoveLinks && currentEntities != null)
            {
                int initialCount = currentEntities.Count;
                currentEntities.RemoveAll(e => e is MessageEntityUrl || e is MessageEntityTextUrl);
                _logger.LogDebug("ApplyEditOptions: Link entities removed. Count before: {InitialCount}, Count after: {CurrentCount}", initialCount, currentEntities.Count);
            }

            // Removed original logic for DropMediaCaptions that used originalMedia
            // This is now handled outside this method in ProcessCustomSendAsync

            _logger.LogDebug("ApplyEditOptions: Final text length: {FinalTextLength}. Entities count: {EntitiesCount}", finalText.Length, currentEntities?.Count ?? 0);
            return (finalText, currentEntities?.ToArray());
        }
        private MessageEntity DefaultCaseHandler(MessageEntity oldEntity, int offsetDelta)
        {
            _logger.LogWarning("CloneEntityWithOffset (DefaultCaseHandler): Unhandled or generic entity type {EntityType}. Attempting generic clone. Offset adjustment WILL BE INCORRECT if text was prepended and this entity type is not specifically handled.", oldEntity.GetType().Name);
            try
            {
                if (Activator.CreateInstance(oldEntity.GetType()) is MessageEntity newGenericEntity)
                {
                    newGenericEntity.Offset = oldEntity.Offset + offsetDelta;
                    newGenericEntity.Length = oldEntity.Length;
                    // This does not copy other properties specific to the unknown entity type.
                    return newGenericEntity;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CloneEntityWithOffset (DefaultCaseHandler): Failed to create or clone instance for entity type {EntityType}.", oldEntity.GetType().Name);
            }
            _logger.LogError("CloneEntityWithOffset (DefaultCaseHandler): FALLBACK - returning original entity type {EntityType} with potentially incorrect offset. THIS IS A BUG if text was prepended.", oldEntity.GetType().Name);
            // Modifying the original entity's offset is dangerous if it's a reference shared elsewhere.
            // However, if it's a new list from ToArray(), it's a shallow copy of references.
            // A true deep clone is safer but more complex.
            // For now, let's create a new instance if possible, otherwise modify (which is risky).
            // The Activator approach above is better than directly modifying oldEntity.Offset here.
            // If Activator fails, returning oldEntity without modification is safer than modifying its offset.
            return oldEntity;
        }


        




// CloneEntityWithOffset logic remains the same as it deals with TL types.
private MessageEntity CloneEntityWithOffset(MessageEntity oldEntity, int offsetDelta)
        {
            int newOffset = oldEntity.Offset + offsetDelta;
            int newLength = oldEntity.Length;

            return oldEntity switch
            {
                MessageEntityBold _ => new MessageEntityBold { Offset = newOffset, Length = newLength },
                MessageEntityItalic _ => new MessageEntityItalic { Offset = newOffset, Length = newLength },
                MessageEntityUnderline _ => new MessageEntityUnderline { Offset = newOffset, Length = newLength },
                MessageEntityStrike _ => new MessageEntityStrike { Offset = newOffset, Length = newLength },
                MessageEntitySpoiler _ => new MessageEntitySpoiler { Offset = newOffset, Length = newLength },
                MessageEntityCode _ => new MessageEntityCode { Offset = newOffset, Length = newLength },
                MessageEntityPre pre => new MessageEntityPre { Offset = newOffset, Length = newLength, language = pre.language },
                MessageEntityUrl url => new MessageEntityUrl { Offset = newOffset, Length = newLength },
                MessageEntityTextUrl textUrl => new MessageEntityTextUrl { Offset = newOffset, Length = newLength, url = textUrl.url },
                MessageEntityMentionName mentionName => new MessageEntityMentionName { Offset = newOffset, Length = newLength, user_id = mentionName.user_id },
                MessageEntityCustomEmoji customEmoji => new MessageEntityCustomEmoji { Offset = newOffset, Length = newLength, document_id = customEmoji.document_id },
                MessageEntityBlockquote blockquote => new MessageEntityBlockquote { Offset = newOffset, Length = newLength, flags = blockquote.flags },
                MessageEntityHashtag hashtag => new MessageEntityHashtag { Offset = newOffset, Length = newLength },
                _ => DefaultCaseHandler(oldEntity, offsetDelta)
            };
        }
    }
}