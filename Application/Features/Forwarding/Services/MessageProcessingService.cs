using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Application.Common.Interfaces;
using Domain.Features.Forwarding.Entities;
using Domain.Features.Forwarding.ValueObjects;
using Microsoft.Extensions.Logging;
using TL;

namespace Application.Features.Forwarding.Services
{
    public class MessageProcessingService
    {
        private readonly ILogger<MessageProcessingService> _logger;
        private readonly ITelegramUserApiClient _userApiClient;

        public MessageProcessingService(
            ILogger<MessageProcessingService> logger,
            ITelegramUserApiClient userApiClient)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userApiClient = userApiClient ?? throw new ArgumentNullException(nameof(userApiClient));
        }



public async Task ProcessAndRelayMessageAsync(
    int sourceMessageId,
    long rawSourcePeerId, // This should be the ID TelegramUserApiClient can use to fetch the message
    long targetChannelId,
    Domain.Features.Forwarding.Entities.ForwardingRule rule,
    CancellationToken cancellationToken = default)
        {
            _logger.LogInformation(">>>> JOB_ACTIONS: ProcessAndRelay MsgID {SourceMsgId} from RawSourcePeer {RawSourcePeerId} to Target {TargetChannelId} via Rule '{RuleName}'",
                sourceMessageId, rawSourcePeerId, targetChannelId, rule.RuleName);

            // Rule null/disabled check already done.
            bool needsCustomSend = rule.EditOptions != null &&
                     (!string.IsNullOrEmpty(rule.EditOptions.PrependText) ||
                      !string.IsNullOrEmpty(rule.EditOptions.AppendText) ||
                      (rule.EditOptions.TextReplacements != null && rule.EditOptions.TextReplacements.Any()) ||
                       rule.EditOptions.RemoveLinks ||
                       rule.EditOptions.StripFormatting ||
                      !string.IsNullOrEmpty(rule.EditOptions.CustomFooter));

            // Fetch the original message object to inspect media type, grouped_id, etc.
            // Assuming ResolvePeerAsync is synchronous or can be awaited outside GetMessagesAsync
            var messagesBase = await _userApiClient.GetMessagesAsync(await _userApiClient.ResolvePeerAsync(rawSourcePeerId), sourceMessageId, cancellationToken);
            if (messagesBase is not Messages_Messages { messages: var msgList } || !msgList.Any())
            {
                _logger.LogWarning(
                    ">>>> JOB_ACTIONS: Could not retrieve original message {SourceMsgId} from RawSourcePeer {RawSourcePeerId}. Skipping.",
                    sourceMessageId, rawSourcePeerId);
                return;
            }

            var originalMessage = msgList.FirstOrDefault(m => m.ID == sourceMessageId) as Message;
            if (originalMessage == null)
            {
                _logger.LogWarning(
                    ">>>> JOB_ACTIONS: Retrieved message {SourceMsgId} is not of type TL.Message or not found in the list. Skipping.",
                    sourceMessageId);
                return;
            }

            _logger.LogTrace(">>>> JOB_ACTIONS: Original message {SourceMsgId} content: Text present: {HasText}, Media present: {HasMedia}, Media Type: {MediaType}, GroupedId: {GroupedId}",
                sourceMessageId, !string.IsNullOrWhiteSpace(originalMessage.message), originalMessage.media != null, originalMessage.media?.GetType().Name, originalMessage.grouped_id);

            // Check if the message should be filtered based on media type
            if (!ShouldForwardMediaType(originalMessage.media, rule.FilterOptions?.AllowedMediaTypes))
            {
                _logger.LogInformation(">>>> JOB_ACTIONS: Message {SourceMsgId} media type is filtered out by rule '{RuleName}'. Skipping.",
                    sourceMessageId, rule.RuleName);
                return;
            }

            // Check if the message is part of a media group and if the rule says to ignore grouped messages
            if (originalMessage.grouped_id.HasValue && !(rule.FilterOptions?.ForwardMediaGroupsAsSingle ?? false))
            {
                 _logger.LogInformation(">>>> JOB_ACTIONS: Message {SourceMsgId} is part of a media group but rule '{RuleName}' is configured to not forward media groups as single or ignores grouped messages. Skipping.",
                    sourceMessageId, rule.RuleName);

                return;
            }

    
                _logger.LogDebug(">>>> JOB_ACTIONS: Resolving FromPeer: {RawSourcePeerId}", rawSourcePeerId);
                var fromPeer = await _userApiClient.ResolvePeerAsync(rawSourcePeerId); // e.g. -100123...
                _logger.LogDebug(">>>> JOB_ACTIONS: Resolving ToPeer: {TargetChannelId}", targetChannelId);
                var toPeer = await _userApiClient.ResolvePeerAsync(targetChannelId);   // e.g. -100789... or a user ID

                if (fromPeer == null || toPeer == null)
                {
                    _logger.LogError(">>>> JOB_ACTIONS: Could not resolve FromPeer (Resolved: {FromPeerResolved}) or ToPeer (Resolved: {ToPeerResolved}). RawSourcePeerId: {RawSourcePeerId}, TargetId: {TargetChannelId} for MsgID {SourceMsgId}.",
                        fromPeer?.ToString(), toPeer?.ToString(), rawSourcePeerId, targetChannelId, sourceMessageId);
                    throw new InvalidOperationException("Could not resolve source or target peer");
            }
                _logger.LogInformation(">>>> JOB_ACTIONS: FromPeer resolved to: {FromPeerString}, ToPeer resolved to: {ToPeerString}", fromPeer.ToString(), toPeer.ToString());

            // Determine if we are processing a single message or a media group
            if (originalMessage.grouped_id.HasValue && (rule.FilterOptions?.ForwardMediaGroupsAsSingle ?? false))
            {
                // Media group processing
                 _logger.LogInformation(">>>> JOB_ACTIONS: Processing media group for message {SourceMsgId} (Group ID: {GroupId}) via Rule '{RuleName}'",
                     sourceMessageId, originalMessage.grouped_id.Value, rule.RuleName);
                var mediaGroupMessages = await GetMediaGroupAsync(fromPeer, originalMessage.grouped_id.Value, cancellationToken);

                if (mediaGroupMessages != null && mediaGroupMessages.Any())
                {
                     // For albums, we typically do a simple forward as custom sending albums is more complex and less common.
                     // If custom sending is needed for albums, we will now attempt to handle it.
                     // Note: Applying different captions/entities per photo in an album
                     // is supported by SendMediaGroupAsync, but applying general text edits
                     // (prepend/append/replace) to each caption independently is not
                     // straightforward and might not be desired. We will apply the edits to
                     // each message's original caption/entities before creating the InputMedia.
                    if (needsCustomSend)
                    {
                    }
                    else
                    {
                        await ProcessSimpleForwardAsync(fromPeer, toPeer, mediaGroupMessages.Select(m => m.ID).ToArray(), rule, cancellationToken);
                    }
                }
            }
            else // Process as a single message
            {
                await ProcessCustomSendAsync(fromPeer, toPeer, new List<Message> { originalMessage }, rule, cancellationToken);
            }
        }

        // Handles custom sending for a single message or a media group
        private async Task ProcessCustomSendAsync(
            InputPeer fromPeer,
            InputPeer toPeer,
            List<Message> messages, // Accept list of messages for media groups
            ForwardingRule rule,
            CancellationToken cancellationToken)
        {
            if (messages.Count == 0)
            {
                _logger.LogWarning(">>>> JOB_ACTIONS: ProcessCustomSendAsync received an empty message list for Target {TargetChannelId}. Skipping.", toPeer.ID);
                return;
            }

            var firstMessage = messages.First(); // Caption and entities will be applied to the first item in an album

            if (messages.Count > 1) // Handle media group
            {
                var inputMediaList = new List<InputMedia>();
                foreach (var msg in messages)
                {
                    if (msg.media != null)
                    {
                        var inputMedia = CreateInputMedia(msg.media);
                        if (inputMedia != null)
                        {
                            string newCaption = msg.message ?? "";
                            MessageEntity[]? newEntities = msg.entities?.ToArray();

                            // Apply edit options to each message's caption/entities within the group
                            (newCaption, newEntities) = ApplyEditOptions(newCaption, newEntities, rule.EditOptions, msg.media);

                            if (inputMedia is InputMediaPhoto imp)
                            {
                                if (inputMedia is InputMediaDocument imd) imd.caption = newCaption;
 imp.Caption = newCaption;
                                if (inputMedia is InputMediaDocument imd) imd.entities = newEntities;
                                // Need to handle other InputMedia types if their constructors/properties support caption/entities
                            }
                            inputMediaList.Add(inputMedia);
                        }
                    }
                }

                if (inputMediaList.Any())
                {
                     _logger.LogInformation(
                        "Sending media group with {MediaCount} items for original MsgID {SourceMsgId} to Target {TargetChannelId} via Rule '{RuleName}'.",
                        messages.First().ID, inputMediaList.Count, toPeer.ID, rule.RuleName);

                    // Assuming SendMediaGroupAsync exists in your ITelegramUserApiClient
                    await _userApiClient.SendMediaGroupAsync(toPeer, inputMediaList.ToArray()); // Assuming SendMediaGroupAsync exists
                }
            }
            else // Handle single message
            {
                string newCaption = firstMessage.message ?? "";
                MessageEntity[]? newEntities = firstMessage.entities?.ToArray();

                // Apply edit options to the single message's caption/entities
                (newCaption, newEntities) = ApplyEditOptions(newCaption, newEntities, rule.EditOptions, firstMessage.media);
                InputMedia? finalMediaToSend = firstMessage.media != null ? CreateInputMedia(firstMessage.media) : null;

                await _userApiClient.SendMessageAsync(toPeer, newCaption, entities: newEntities, media: finalMediaToSend, noWebpage: rule.EditOptions.RemoveLinks);

                _logger.LogInformation(
                    "Custom message sent for original MsgID {SourceMsgId} to Target {TargetChannelId} via Rule '{RuleName}'.",
                    firstMessage.ID, toPeer.ID, rule.RuleName);
            }

            _logger.LogInformation(
                // This log might be redundant after the more specific logs above
                "Custom message sent for original MsgID {SourceMsgId} to Target {TargetChannelId} via Rule '{RuleName}'.",
                sourceMessageId, toPeer.ID, rule.RuleName);
        }

        private async Task ProcessSimpleForwardAsync(
            InputPeer fromPeer,
            InputPeer toPeer,            
            IEnumerable<Message> messages, // Accept list of messages
            ForwardingRule rule,
            CancellationToken cancellationToken)
        {
            await _userApiClient.ForwardMessagesAsync(
                toPeer,
                messages.Select(m => m.ID).ToArray(), // Extract IDs of all messages in the group
                fromPeer,
                dropAuthor: rule.EditOptions?.RemoveSourceForwardHeader ?? false,
                noForwards: rule.EditOptions?.RemoveSourceForwardHeader ?? false);

            _logger.LogInformation(
                "Message {SourceMsgId} forwarded to Target {TargetChannelId} via Rule '{RuleName}'.",
                sourceMessageId, toPeer.ID, rule.RuleName);
        }

        private (string text, MessageEntity[]? entities) ApplyEditOptions(
            string initialText,
            MessageEntity[]? initialEntities,
            MessageEditOptions options,
            MessageMedia? originalMedia)
        {
            if (string.IsNullOrWhiteSpace(initialText) && originalMedia == null)
            {
                return (string.Empty, null);
            }

            var newTextBuilder = new StringBuilder(initialText);
            List<MessageEntity>? currentEntities = initialEntities?.ToList();

            if (options.StripFormatting)
            {
                currentEntities = null;
                _logger.LogTrace("Stripping formatting, entities cleared.");
            }

            if (options.TextReplacements != null && options.TextReplacements.Any())
            {
                string tempText = newTextBuilder.ToString();
                bool textChangedByReplace = false;
                foreach (var rep in options.TextReplacements)
                {
                    if (string.IsNullOrEmpty(rep.Find)) continue;
                    string oldTextBeforeReplace = tempText;
                    tempText = rep.IsRegex
                        ? Regex.Replace(tempText, rep.Find, rep.ReplaceWith ?? "", rep.RegexOptions)
                        : tempText.Replace(rep.Find, rep.ReplaceWith ?? "", StringComparison.OrdinalIgnoreCase);
                    if (oldTextBeforeReplace != tempText) textChangedByReplace = true;
                }
                if (textChangedByReplace)
                {
                    newTextBuilder = new StringBuilder(tempText);
                    if (currentEntities != null && currentEntities.Any())
                    {
                        _logger.LogWarning("Text replacements occurred, clearing message entities. Implement entity adjustment if needed.");
                        currentEntities = null;
                    }
                }
            }

            string finalText = newTextBuilder.ToString();

            if (!string.IsNullOrEmpty(options.PrependText) && !string.IsNullOrWhiteSpace(finalText))
            {
                finalText = options.PrependText + finalText;
                if (currentEntities != null)
                {
                    int offset = options.PrependText.Length;
                    var adjustedEntities = new List<MessageEntity>();
                    foreach (var e in currentEntities)
                    {
                        adjustedEntities.Add(CloneEntityWithOffset(e, offset));
                    }
                    currentEntities = adjustedEntities;
                }
            }

            string textToAppend = !string.IsNullOrEmpty(options.AppendText) ? options.AppendText :
                                  !string.IsNullOrEmpty(options.CustomFooter) ? options.CustomFooter :
                                  string.Empty;
            if (!string.IsNullOrEmpty(textToAppend) && !string.IsNullOrWhiteSpace(finalText))
            {
                finalText += textToAppend;
            }

            if (options.RemoveLinks && currentEntities != null)
            {
                currentEntities.RemoveAll(e => e is MessageEntityUrl || e is MessageEntityTextUrl);
                _logger.LogTrace("Link entities removed.");
            }

            return (finalText, currentEntities?.ToArray());
        }

        private InputMedia? CreateInputMedia(MessageMedia media)
        {
            return media switch
            {
                MessageMediaPhoto mmp when mmp.photo is Photo p => new InputMediaPhoto
                {
                    id = new InputPhoto { id = p.id, access_hash = p.access_hash, file_reference = p.file_reference },
                    // Add other relevant properties if needed, e.g., caption, entities, flags
                    Caption = string.Empty, // Assuming default empty caption
 Entities = Array.Empty<MessageEntity>() // Assuming default empty entities
                },
                MessageMediaDocument mmd when mmd.document is Document d => new InputMediaDocument
                {
                    id = new InputDocument { id = d.id, access_hash = d.access_hash, file_reference = d.file_reference },
                    // Add other relevant properties if needed, e.g., caption, entities, flags
                    Caption = string.Empty, // Assuming default empty caption
                    Entities = Array.Empty<MessageEntity>() // Assuming default empty entities
                },
                MessageMediaSticker mms when mms.document is Document sd => new InputMediaDocument // Stickers are documents
                {
                    id = new InputDocument { id = sd.id, access_hash = sd.access_hash, file_reference = sd.file_reference },
 // Stickers don't typically have captions or entities when sent as media
                },
                MessageMediaAnimation mma when mma.document is Document ad => new InputMediaDocument // Animations are documents
                {
                    id = new InputDocument { id = ad.id, access_hash = ad.access_hash, file_reference = ad.file_reference },
                    // Add other relevant properties if needed, e.g., caption, entities, flags
                },
                 MessageMediaVideo mmv when mmv.document is Document vd => new InputMediaDocument // Videos can be documents
                {
                    id = new InputDocument { id = vd.id, access_hash = vd.access_hash, file_reference = vd.file_reference },
                    // Add other relevant properties if needed, e.g., caption, entities, flags, duration, w, h
                    Caption = string.Empty, // Assuming default empty caption
                    Entities = Array.Empty<MessageEntity>() // Assuming default empty entities
                },
                MessageMediaUnsupported or MessageMediaEmpty => null, // Do not forward unsupported or empty media
                _ => DefaultCreateInputMediaCaseHandler(media) // Handle other potential media types


            };
        }

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
                MessageEntityUrl _ => new MessageEntityUrl { Offset = newOffset, Length = newLength },
                MessageEntityTextUrl textUrl => new MessageEntityTextUrl { Offset = newOffset, Length = newLength, url = textUrl.url },
                MessageEntityMentionName mentionName => new MessageEntityMentionName { Offset = newOffset, Length = newLength, user_id = mentionName.user_id },
                MessageEntityCustomEmoji customEmoji => new MessageEntityCustomEmoji { Offset = newOffset, Length = newLength, document_id = customEmoji.document_id },
                MessageEntityBlockquote blockquote => new MessageEntityBlockquote { Offset = newOffset, Length = newLength, flags = blockquote.flags },
                MessageEntityHashtag _ => new MessageEntityHashtag { Offset = newOffset, Length = newLength },
                _ => DefaultCaseHandler(oldEntity, offsetDelta)
            };
        }

        private MessageEntity DefaultCaseHandler(MessageEntity oldEntity, int offsetDelta)
        {
            _logger.LogWarning(
                "CloneEntityWithOffset: Unhandled entity type {EntityType}. Original entity returned. Offset adjustment WILL BE INCORRECT if text was prepended.",
                oldEntity.GetType().Name);
            try
            {
                if (Activator.CreateInstance(oldEntity.GetType()) is MessageEntity newGenericEntity)
                {
                    newGenericEntity.Offset = oldEntity.Offset + offsetDelta;
                    newGenericEntity.Length = oldEntity.Length;
                    return newGenericEntity;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create instance for unhandled entity type {EntityType} in DefaultCaseHandler.", oldEntity.GetType().Name);
            }
            return oldEntity;
        }

        private InputMedia? DefaultCreateInputMediaCaseHandler(MessageMedia media)
        {
            _logger.LogWarning(
                "CreateInputMedia: Unhandled media type {MediaType}. Returning null, media will not be forwarded.",
                media.GetType().Name);
            return null;
        }

        // TODO: Implement GetMediaGroupAsync - This method is needed to fetch all messages in an album
        private Task<List<Message>> GetMediaGroupAsync(InputPeer fromPeer, long groupedId, CancellationToken cancellationToken)
        { throw new NotImplementedException("GetMediaGroupAsync is not implemented yet."); }
    }
} 