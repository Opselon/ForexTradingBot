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
            InputMedia? inputMediaToSend, // NEW: اطلاعات مدیا از Orchestrator
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("Job: Starting ProcessAndRelay for MsgID {SourceMsgId} from RawSourcePeer {RawSourcePeerId} to Target {TargetChannelId} via DB Rule '{RuleName}'. Initial Content Preview: '{MessageContentPreview}'. Has Input Media: {HasInputMedia}. Sender Peer: {SenderPeer}",
                sourceMessageId, rawSourcePeerId, targetChannelId, rule.RuleName, TruncateString(messageContent, 50), inputMediaToSend != null, senderPeerForFilter?.ToString() ?? "N/A");

            if (rule == null)
            {
                _logger.LogError("Job: Rule object is null for MsgID {SourceMsgId}. Aborting.", sourceMessageId);
                throw new ArgumentNullException(nameof(rule), "Rule object cannot be null.");
            }
            if (!rule.IsEnabled)
            {
                _logger.LogInformation("Job: Rule '{RuleName}' is disabled. Skipping message {SourceMsgId}.", rule.RuleName, sourceMessageId);
                return;
            }

            // --- BEGIN FILTERING LOGIC ---
            if (!ShouldProcessMessageBasedOnFilters(messageContent, senderPeerForFilter, rule.FilterOptions, sourceMessageId, rule.RuleName))
            {
                _logger.LogInformation("Job: Message {SourceMsgId} skipped due to filter options of Rule '{RuleName}'.", sourceMessageId, rule.RuleName);
                return;
            }
            // --- END FILTERING LOGIC ---

            try
            {
                // ... (بقیه کدهای موجود برای Resolve Peer و needsCustomSend) ...
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

                _logger.LogDebug("Job: Resolving ToPeer with prepared ID: {TargetIdForResolve} (Original TargetId: {TargetChannelId}, TypeHint: {TargetTypeHint}) for Rule: {RuleName}",
                    targetIdForResolve, targetChannelId, targetTypeHint, rule.RuleName);
                var toPeer = await _userApiClient.ResolvePeerAsync(targetIdForResolve);


                if (fromPeer == null || toPeer == null)
                {
                    _logger.LogError("Job: Could not resolve FromPeer (RawId: {RawSourcePeerId}, Resolved: {FromPeerStatus}) or ToPeer (OriginalTargetId: {TargetChannelId}, IDUsedForResolve: {TargetIdForResolve}, Resolved: {ToPeerStatus}) for MsgID {SourceMsgId}. Rule: {RuleName}",
                        rawSourcePeerId, fromPeer != null, targetChannelId, targetIdForResolve, toPeer != null, sourceMessageId, rule.RuleName);
                    throw new InvalidOperationException($"Could not resolve source (ID: {rawSourcePeerId}) or target (OrigID: {targetChannelId} / ResolveID: {targetIdForResolve}) peer for rule '{rule.RuleName}'.");
                }
                _logger.LogInformation("Job: Peers resolved. FromPeer: {FromPeerString}, ToPeer: {ToPeerString} for Rule: {RuleName}", GetInputPeerTypeAndIdForLogging(fromPeer), GetInputPeerTypeAndIdForLogging(toPeer), rule.RuleName);

                bool needsCustomSend = rule.EditOptions != null &&
                        (
                         !string.IsNullOrEmpty(rule.EditOptions.PrependText) ||
                         !string.IsNullOrEmpty(rule.EditOptions.AppendText) ||
                         (rule.EditOptions.TextReplacements != null && rule.EditOptions.TextReplacements.Any()) ||
                         rule.EditOptions.RemoveLinks ||
                         rule.EditOptions.StripFormatting ||
                         !string.IsNullOrEmpty(rule.EditOptions.CustomFooter) ||
                         rule.EditOptions.DropMediaCaptions || // این شرط مهم است اگر کپشن مدیا را میخواهید حذف کنید
                         inputMediaToSend != null // اگر مدیایی برای ارسال هست، پس نیاز به کاستوم سِند داریم
                        );
                _logger.LogDebug("Job: NeedsCustomSend: {NeedsCustomSend} for Rule: {RuleName}. EditOptions is null: {IsEditOptionsNull}. InputMediaToSend is null: {IsInputMediaNull}", needsCustomSend, rule.RuleName, rule.EditOptions == null, inputMediaToSend == null);

                if (needsCustomSend && rule.EditOptions != null || inputMediaToSend != null) // اگر EditOptions داریم یا مدیایی برای ارسال هست، Custom Send می‌کنیم
                {
                    _logger.LogInformation("Job: Processing custom send for MsgID {SourceMsgId} using DB Rule '{RuleName}'", sourceMessageId, rule.RuleName);
                    await ProcessCustomSendAsync(fromPeer, toPeer, sourceMessageId, rule, messageContent, messageEntities, inputMediaToSend, cancellationToken);
                }
                else
                {
                    _logger.LogInformation("Job: Processing simple forward for MsgID {SourceMsgId} using DB Rule '{RuleName}'", sourceMessageId, rule.RuleName);
                    await ProcessSimpleForwardAsync(fromPeer, toPeer, sourceMessageId, rule, cancellationToken);
                }
                _logger.LogInformation("Job: Successfully processed message {SourceMsgId} for rule {RuleName}", sourceMessageId, rule.RuleName);
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
                        _logger.LogDebug("ShouldProcessMessageBasedOnFilters: Message {MessageId} for Rule '{RuleName}' is from unallowed sender {SenderId}. Skipping.",
                            messageId, ruleName, userSender.user_id);
                        return false;
                    }
                    _logger.LogTrace("ShouldProcessMessageBasedOnFilters: Message {MessageId} for Rule '{RuleName}' is from allowed sender {SenderId}. Proceeding with filter check.",
                       messageId, ruleName, userSender.user_id);
                }
                else
                {
                    // If allowed senders are specified but message is not from a user (e.g., from a channel/chat itself),
                    // you might want to skip or treat as allowed. Current logic skips if not from a user.
                    _logger.LogDebug("ShouldProcessMessageBasedOnFilters: Message {MessageId} for Rule '{RuleName}' has AllowedSenderUserIds but sender is not a PeerUser ({SenderType}). Skipping.",
                        messageId, ruleName, senderPeer?.GetType().Name ?? "Null");
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
                        _logger.LogDebug("ShouldProcessMessageBasedOnFilters: Message {MessageId} for Rule '{RuleName}' is from BLOCKED sender {SenderId}. Skipping.",
                            messageId, ruleName, userSender.user_id);
                        return false;
                    }
                    _logger.LogTrace("ShouldProcessMessageBasedOnFilters: Message {MessageId} for Rule '{RuleName}' is NOT from a blocked sender {SenderId}. Proceeding with filter check.",
                        messageId, ruleName, userSender.user_id);
                }
            }
            _logger.LogTrace("ShouldProcessMessageBasedOnFilters: Message {MessageId} passed all active filters for Rule '{RuleName}'.", messageId, ruleName);
            return true;
        }



        private async Task ProcessCustomSendAsync(
               InputPeer fromPeer,
               InputPeer toPeer,
               int sourceMessageId,
               Domain.Features.Forwarding.Entities.ForwardingRule rule,
               string initialMessageContentFromOrchestrator,
               TL.MessageEntity[]? initialEntitiesFromOrchestrator,
               InputMedia? inputMediaToSendFromOrchestrator, // NEW: مدیا را مستقیماً دریافت می‌کنیم
               CancellationToken cancellationToken)
        {
            _logger.LogInformation("ProcessCustomSendAsync: >>> Starting for MsgID {SourceMessageId} (Rule: '{RuleName}'). FromPeer: {FromPeerIdString}. ToPeer: {ToPeerIdString}. Content from Orchestrator Preview: '{OrchestratorContentPreview}'. Has Input Media From Orchestrator: {HasInputMedia}",
                sourceMessageId, rule.RuleName, GetInputPeerTypeAndIdForLogging(fromPeer), GetInputPeerTypeAndIdForLogging(toPeer), TruncateString(initialMessageContentFromOrchestrator, 50), inputMediaToSendFromOrchestrator != null);
   
            TL.Message? originalMessageForMedia = null;

            _logger.LogDebug("ProcessCustomSendAsync: Attempting GetMessagesAsync for MsgID {SourceMessageId} from Peer: {FromPeerTypeAndId} (primarily for media and full message details).",
                sourceMessageId, GetInputPeerTypeAndIdForLogging(fromPeer));

            Messages_MessagesBase messagesBaseResponse = null;
            try
            {
                messagesBaseResponse = await _userApiClient.GetMessagesAsync(fromPeer, sourceMessageId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProcessCustomSendAsync: Error fetching full message details for media from API for MsgID {SourceMessageId}. Proceeding without media.", sourceMessageId);
                // اگر خطایی در دریافت اطلاعات مدیا رخ داد، ادامه می‌دهیم ولی بدون مدیا
            }

            if (messagesBaseResponse != null)
            {
                IReadOnlyList<MessageBase>? msgListSource = null;
                if (messagesBaseResponse is Messages_Messages messagesMessages)
                {
                    msgListSource = messagesMessages.messages;
                }
                else if (messagesBaseResponse is Messages_ChannelMessages channelMessages)
                {
                    msgListSource = channelMessages.messages;
                }

                if (msgListSource != null && msgListSource.Any())
                {
                    originalMessageForMedia = msgListSource.OfType<TL.Message>().FirstOrDefault(m => m.ID == sourceMessageId);
                    if (originalMessageForMedia != null)
                    {
                        _logger.LogInformation("ProcessCustomSendAsync: Full TL.Message (ID: {OriginalMessageId}, Type: {OriginalMessageType}) retrieved for media and full details. Text preview from API: '{MessageTextPreview}'. Rule: '{RuleName}'",
                            originalMessageForMedia.id, originalMessageForMedia.GetType().Name, TruncateString(originalMessageForMedia.message, 50), rule.RuleName);
                    }
                    else
                    {
                        _logger.LogWarning("ProcessCustomSendAsync: Message with ID {SourceMessageId} not found or not TL.Message in GetMessagesAsync response. Cannot get full details for media handling. Rule: '{RuleName}'. List contents: {ListContent}",
                            sourceMessageId, rule.RuleName, string.Join(";", msgListSource.Select(m => $"ID:{m.ID} Type:{m.GetType().Name}")));
                    }
                }
                else
                {
                    _logger.LogWarning("ProcessCustomSendAsync: GetMessagesAsync returned empty or null message list for MsgID {SourceMessageId}. Rule: '{RuleName}'.",
                        sourceMessageId, rule.RuleName);
                }
            }
            else
            {
                _logger.LogWarning("ProcessCustomSendAsync: GetMessagesAsync returned NULL for MsgID {SourceMessageId}. FromPeer: {FromPeerTypeAndId}. Rule: '{RuleName}'. No media can be extracted.",
                    sourceMessageId, GetInputPeerTypeAndIdForLogging(fromPeer), rule.RuleName);
            }


            // 2. Prepare content for sending based on EditOptions
            // از محتوای پیام و انتشاراتی که از ارکستریتور مستقیماً دریافت کرده‌ایم استفاده می‌کنیم.
            string newCaption = initialMessageContentFromOrchestrator;
            TL.MessageEntity[]? newEntities = initialEntitiesFromOrchestrator?.ToArray();

            // Apply edit options
            if (rule.EditOptions != null)
            {
                _logger.LogInformation("ProcessCustomSendAsync: Applying edit options for Rule: '{RuleName}'. Original Text (from Orchestrator) Preview: '{InitialTextPreview}'.",
                    rule.RuleName, TruncateString(newCaption, 50));
                // اگر DropMediaCaptions فعال باشد، کپشن را از بین می‌بریم.
                // متد ApplyEditOptions باید به گونه‌ای تغییر کند که MessageMedia را به عنوان ورودی نگیرد.
                // یا صرفا برای بررسی DropMediaCaptions از آن استفاده کند
                (newCaption, newEntities) = ApplyEditOptions(newCaption, newEntities, rule.EditOptions, null); // Pass null for originalMedia, as media logic is now outside this method.
                if (rule.EditOptions.DropMediaCaptions && inputMediaToSendFromOrchestrator != null)
                {
                    newCaption = string.Empty;
                    newEntities = null;
                    _logger.LogInformation("ProcessCustomSendAsync: DropMediaCaptions is TRUE and media is present. Caption cleared.");
                }

                _logger.LogInformation("ProcessCustomSendAsync: After ApplyEditOptions. Final Text Preview: '{FinalTextPreview}'. Rule: '{RuleName}'",
                    TruncateString(newCaption, 50), rule.RuleName);
            }
            else
            {
                _logger.LogDebug("ProcessCustomSendAsync: No EditOptions defined for Rule: '{RuleName}'. Using original caption/entities.", rule.RuleName);
            }
            // The final media to send is simply what we received from the orchestrator
            InputMedia? finalMediaToSend = inputMediaToSendFromOrchestrator;
            // Final check before sending message
            if (string.IsNullOrEmpty(newCaption) && finalMediaToSend == null)
            {
                _logger.LogWarning("ProcessCustomSendAsync: After all edits, final caption is empty AND no media prepared to send for MsgID {SourceMessageId}. Rule: '{RuleName}'. Skipping SendMessageAsync.", sourceMessageId, rule.RuleName);
                return;
            }
            await _userApiClient.SendMessageAsync(
               toPeer,
               newCaption, // This is the potentially modified text/caption
               entities: newEntities, // This is the potentially modified entities
               media: finalMediaToSend, // This is the media to send (now directly from orchestrator)
               noWebpage: rule.EditOptions?.RemoveLinks ?? false
           );
            _logger.LogInformation("ProcessCustomSendAsync: Custom message sent for original MsgID {SourceMessageId} to Target {TargetChannelId} via Rule '{RuleName}'.",
                sourceMessageId, GetInputPeerIdValueForLogging(toPeer), rule.RuleName);

            // Decide on media inclusion for SendMessageAsync using the fetched originalMessageForMedia
            // This is the crucial section to fix for media
            if (originalMessageForMedia?.media != null)
            {
                _logger.LogDebug("ProcessCustomSendAsync: Preparing original media from full fetched message for sending as final media for Rule: '{RuleName}'. Media Type: {MediaType}", rule.RuleName, originalMessageForMedia.media.GetType().Name);

                if (originalMessageForMedia.media is MessageMediaPhoto mmp && mmp.photo is Photo p)
                {
                    // Ensure file_reference is handled correctly (can be null if media is recent or not yet processed by Telegram)
                    // It's safer to provide an empty byte array than null for file_reference when constructing InputPhoto.
                    finalMediaToSend = new InputMediaPhoto
                    {
                        id = new InputPhoto { id = p.id, access_hash = p.access_hash, file_reference = p.file_reference ?? Array.Empty<byte>() },
                        // caption = newCaption, // Caption is passed separately to SendMessageAsync
                        // entities = newEntities // Entities are passed separately to SendMessageAsync
                    };
                    _logger.LogInformation("ProcessCustomSendAsync: Prepared InputMediaPhoto for sending. Photo ID: {PhotoId}", p.id);
                }
                else if (originalMessageForMedia.media is MessageMediaDocument mmd && mmd.document is Document d)
                {
                    // Similar handling for documents
                    finalMediaToSend = new InputMediaDocument
                    {
                        id = new InputDocument { id = d.id, access_hash = d.access_hash, file_reference = d.file_reference ?? Array.Empty<byte>() },
                        // caption = newCaption, // Caption is passed separately to SendMessageAsync
                        // entities = newEntities // Entities are passed separately to SendMessageAsync
                    };
                    _logger.LogInformation("ProcessCustomSendAsync: Prepared InputMediaDocument for sending. Document ID: {DocumentId}", d.id);
                }
                // Add more media types if you need to support them (e.g., MessageMediaWebPage, MessageMediaContact, etc.)
                // These might not be directly convertible to InputMediaPhoto/InputMediaDocument and might require different SendMessageAsync overloads.
                else
                {
                    _logger.LogWarning("ProcessCustomSendAsync: Unsupported media type {MediaType} for custom send for Rule '{RuleName}'. Media will NOT be re-sent.",
                        originalMessageForMedia.media.GetType().Name, rule.RuleName);
                }
            }


            // 3. Final check before sending message.
            // If there's no text content but there IS media, we should still send it.
            // If there's no text and no media, then skip.
            if (string.IsNullOrEmpty(newCaption) && finalMediaToSend == null)
            {
                _logger.LogWarning("ProcessCustomSendAsync: After all edits, final caption is empty AND no media prepared to send for MsgID {SourceMessageId}. Rule: '{RuleName}'. Skipping SendMessageAsync.", sourceMessageId, rule.RuleName);
                return;
            }

            // 4. Send the final message
            _logger.LogInformation("ProcessCustomSendAsync: Calling SendMessageAsync to ToPeer {ToPeerIdString}. Final Caption Length: {CaptionLength}, Media Present: {MediaPresent}, NoWebpagePreview: {NoWebpagePreview}. Rule: '{RuleName}'",
                GetInputPeerTypeAndIdForLogging(toPeer), newCaption.Length, finalMediaToSend != null, rule.EditOptions?.RemoveLinks ?? false, rule.RuleName);

            await _userApiClient.SendMessageAsync(
                toPeer,
                newCaption, // This is the potentially modified text/caption
                entities: newEntities, // This is the potentially modified entities
                media: finalMediaToSend, // This is the media to send (might be null if no media or not supported)
                noWebpage: rule.EditOptions?.RemoveLinks ?? false // Controls web preview for links
            );

            _logger.LogInformation("ProcessCustomSendAsync: Custom message sent for original MsgID {SourceMessageId} to Target {TargetChannelId} via Rule '{RuleName}'.",
                sourceMessageId, GetInputPeerIdValueForLogging(toPeer), rule.RuleName);
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
            bool dropAuthor = rule.EditOptions?.DropAuthor ?? rule.EditOptions?.RemoveSourceForwardHeader ?? false;
            bool noForwards = rule.EditOptions?.NoForwards ?? false;
            bool dropMediaCaptionsSetting = rule.EditOptions?.DropMediaCaptions ?? false;

            if (dropMediaCaptionsSetting)
            {
                _logger.LogWarning("ProcessSimpleForwardAsync: DropMediaCaptions is true for Rule '{RuleName}' but simple forward cannot drop captions. Consider custom send or removing this option for simple forwards.", rule.RuleName);
                // For simple forward, cannot drop media captions. This option will be ignored by Telegram API for ForwardMessages.
            }


            _logger.LogDebug("ProcessSimpleForwardAsync: Parameters for Rule '{RuleName}': DropAuthor={DropAuthor}, NoForwards={NoForwards}",
                rule.RuleName, dropAuthor, noForwards);

            await _userApiClient.ForwardMessagesAsync(
                toPeer: toPeer,
                messageIds: new[] { sourceMessageId },
                fromPeer: fromPeer,
                dropAuthor: dropAuthor,
                noForwards: noForwards,
                dropMediaCaptions: false // WTelegramClient.ForwardMessagesAsync does not take this. Captions are handled by custom send.
                                         // Or, if your ITelegramUserApiClient.ForwardMessagesAsync takes it, ensure the implementation handles it
                                         // (likely by doing a custom send if this is true). For a true simple forward, this is false.
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

        // ... (TruncateString, GetInputPeerTypeAndIdForLogging, GetInputPeerIdValueForLogging, ProcessSimpleForwardAsync, CloneEntityWithOffset, DefaultCaseHandler) ...
    




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
    }
}