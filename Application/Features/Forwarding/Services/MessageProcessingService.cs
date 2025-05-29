using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Application.Common.Interfaces;
using Application.Features.Forwarding.Interfaces;
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
            CancellationToken cancellationToken)
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

            // ... (needsCustomSend logic using rule.EditOptions)
            _logger.LogDebug(">>>> JOB_ACTIONS: Rule EditOptions Prepend: '{Prepend}', Append: '{Append}', RemoveLinks: {RemoveLinks}",
                rule.EditOptions?.PrependText, rule.EditOptions?.AppendText, rule.EditOptions?.RemoveLinks);

            if (needsCustomSend && rule.EditOptions != null)
            {
                _logger.LogInformation(">>>> JOB_ACTIONS: Processing custom send for MsgID {SourceMsgId}", sourceMessageId);
                await ProcessCustomSendAsync(fromPeer, toPeer, sourceMessageId, rule, cancellationToken);
            }
            else
            {
                _logger.LogInformation(">>>> JOB_ACTIONS: Processing simple forward for MsgID {SourceMsgId}", sourceMessageId);
                await ProcessSimpleForwardAsync(fromPeer, toPeer, sourceMessageId, rule, cancellationToken);
            }

            if (needsCustomSend && rule.EditOptions != null)
            {
                await ProcessCustomSendAsync(fromPeer, toPeer, sourceMessageId, rule, cancellationToken);
            }
            else
            {
                await ProcessSimpleForwardAsync(fromPeer, toPeer, sourceMessageId, rule, cancellationToken);
            }
        }


        private async Task ProcessCustomSendAsync(
            InputPeer fromPeer,
            InputPeer toPeer,
            int sourceMessageId,
            ForwardingRule rule,
            CancellationToken cancellationToken)
        {
            var messagesBase = await _userApiClient.GetMessagesAsync(fromPeer, sourceMessageId);
            if (messagesBase is not Messages_Messages messages || !messages.messages.Any())
            {
                _logger.LogWarning(
                    "Could not retrieve original message {SourceMsgId} from {RawSourcePeerId} for custom send.",
                    sourceMessageId, fromPeer.ID);
                return;
            }

            // Check if the original message is part of a media group
            var originalMessages = messages.messages.Cast<Message>().Where(m => m.grouped_id != null).ToList();

            if (originalMessages.Any())
            {
                _logger.LogWarning(
                    "Handling media group for message {SourceMsgId} with EditOptions.",
                    sourceMessageId);

                var mediaGroupToSend = new List<InputMediaWithCaption>();
                foreach (var msg in originalMessages.OrderBy(m => m.ID)) // Process in order
                {
                    // Check if the message in the group actually has media
                    if (msg.media == null) continue;

                    string newCaption = msg.message ?? "";
                    MessageEntity[]? newEntities = msg.entities?.ToArray();

                    // Apply edit options to the caption of each media item
                    (newCaption, newEntities) = ApplyEditOptions(newCaption, newEntities, rule.EditOptions, msg.media);

                    var inputMedia = CreateInputMedia(msg.media);
                    if (inputMedia != null)
                    {
                        mediaGroupToSend.Add(new InputMediaWithCaption
                        {
                            Media = inputMedia,
                            Caption = newCaption,
                            Entities = newEntities
                        });
                    }
                }

                if (mediaGroupToSend.Any())
                {
                    // Convert InputMediaWithCaption to InputSingleMedia for SendMediaGroupAsync
                    var inputSingleMediaArray = mediaGroupToSend.Select(item => new InputSingleMedia
                    {
                        media = item.Media,
                        message = item.Caption,
                        entities = item.Entities,
                        random_id = WTelegram.Helpers.RandomLong() // Use the same random ID for the group in TelegramUserApiClient
                    }).ToArray();
                    await _userApiClient.SendMediaGroupAsync(toPeer, inputSingleMediaArray, null, false, null, false, null); // Adjust other parameters as needed
                    _logger.LogInformation(
                        "Custom media group sent for original MsgID {SourceMsgId} to Target {TargetChannelId} via Rule '{RuleName}'.",
                        sourceMessageId, toPeer.ID, rule.RuleName);
                }
                else
                {
                    _logger.LogWarning(
                       "Media group for message {SourceMsgId} had no valid media after processing. Skipping forwarding.",
                        sourceMessageId);
                }
            }
            else // Not a media group, process as a single message
            {
                var originalMessage = messages.messages.FirstOrDefault(m => m.ID == sourceMessageId) as Message;
                if (originalMessage == null || (string.IsNullOrWhiteSpace(originalMessage.message) && originalMessage.media == null))
                {
                    _logger.LogWarning(
                        "Message {SourceMsgId} has no content (empty text and no media) or not found for custom send. Skipping forwarding.",
                        sourceMessageId);
                    return;
                }

                string newCaption = originalMessage.message ?? "";
                MessageEntity[]? newEntities = originalMessage.entities?.ToArray();

                (newCaption, newEntities) = ApplyEditOptions(newCaption, newEntities, rule.EditOptions, originalMessage.media);

                InputMedia? finalMediaToSend = originalMessage.media != null ? CreateInputMedia(originalMessage.media) : null;

                await _userApiClient.SendMessageAsync(toPeer, newCaption, entities: newEntities, media: finalMediaToSend, noWebpage: rule.EditOptions.RemoveLinks);
            }
        }

        private async Task ProcessSimpleForwardAsync(
            InputPeer fromPeer,
            InputPeer toPeer,
            int sourceMessageId,
            ForwardingRule rule,
            CancellationToken cancellationToken)
        {
            await _userApiClient.ForwardMessagesAsync(
                toPeer,
                new[] { sourceMessageId },
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
                    id = new InputPhoto { id = p.id, access_hash = p.access_hash, file_reference = p.file_reference }
                },
                MessageMediaDocument mmd when mmd.document is Document d => new InputMediaDocument
                {
                    id = new InputDocument { id = d.id, access_hash = d.access_hash, file_reference = d.file_reference }
                },
                _ => null
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


    }
}