#region Usings
using Application.Common.Interfaces;
using Application.Features.Forwarding.Interfaces;
using Domain.Features.Forwarding.Entities;
using Domain.Features.Forwarding.ValueObjects;
using Hangfire;
using Hangfire.Server; // Ensure this is present for PerformContext
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TL;
using Telegram.Bot.Types; // For Telegram.Bot.Types.Message
using Telegram.Bot.Types.Enums; // For ChatType, MessageEntityType

#endregion

namespace TelegramPanel.Infrastructure.Services
{
    public class MessageForwardingService
    {
        private readonly ILogger<MessageForwardingService> _logger;
        private readonly INotificationJobScheduler _jobScheduler;
        private readonly IForwardingService _appForwardingService;
        private readonly RetryPolicy<string> _enqueueRetryPolicy;

        public MessageForwardingService(
            ILogger<MessageForwardingService> logger,
            IForwardingService appForwardingService,
            INotificationJobScheduler jobScheduler)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appForwardingService = appForwardingService ?? throw new ArgumentNullException(nameof(appForwardingService));
            _jobScheduler = jobScheduler ?? throw new ArgumentNullException(nameof(jobScheduler));

            _logger.LogInformation("MessageForwardingService initialized for forwarding without re-download/re-upload of media.");

            _enqueueRetryPolicy = Policy<string>
                .Handle<Exception>(ex =>
                {
                    _logger.LogWarning(ex, "Polly[Enqueue]: Error occurred while attempting to enqueue Hangfire job. Retrying...");
                    return true;
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

        public async Task HandleMessageAsync(Telegram.Bot.Types.Message message, CancellationToken cancellationToken = default)
        {
            if (message == null)
            {
                _logger.LogWarning("MessageForwardingService: Received null message. Skipping.");
                return;
            }

            int originalMessageId = message.MessageId;
            string currentMessageContent = message.Text ?? message.Caption ?? string.Empty;
            Telegram.Bot.Types.MessageEntity[]? currentTelegramBotEntities = message.Entities ?? message.CaptionEntities;

            TL.MessageEntity[]? tlMessageEntities = null;
            if (currentTelegramBotEntities != null && currentTelegramBotEntities.Any())
            {
                var convertedEntities = new List<TL.MessageEntity>();
                foreach (var entity in currentTelegramBotEntities)
                {
                    var tlEntity = ConvertTelegramBotEntityToTLEntity(entity, currentMessageContent);
                    if (tlEntity != null) convertedEntities.Add(tlEntity);
                    else _logger.LogWarning("MessageForwardingService: Failed to convert Telegram.Bot entity type {EntityType} for message {OriginalMessageId}", entity.Type, originalMessageId);
                }
                tlMessageEntities = convertedEntities.ToArray();
            }

            TL.Peer? tlSenderPeer = GetSenderPeer(message);

            // --- Media Group Aggregation (simplified, still no download/upload) ---
            List<InputMediaWithCaption>? jobMediaGroupItems = null; // This will now typically remain null or hold placeholders if actual media isn't handled.

            // The main logic for handling media messages without re-download/re-upload is to rely on ForwardMessagesAsync.
            // If the rule requires custom sends (e.g., NoForwards = true), media will be skipped.
            if (message.MediaGroupId != null)
            {
                _logger.LogInformation("MessageForwardingService: Message {OriginalMessageId} is part of a media group ({MediaGroupId}). Full media group processing (aggregation, download, upload) is NOT implemented here, will attempt direct forwarding if rule allows.", originalMessageId, message.MediaGroupId);
                // For albums, direct forwarding (ProcessSimpleForwardAsync) is the ideal path if no other edits apply.
                // If the rule forces a custom send (e.g., NoForwards=true), media will be ignored/skipped as re-upload is disabled.
                // A true album processing would need aggregation + download/upload + SendMediaGroupAsync.
            }
            else if (message.Photo != null || message.Video != null || message.Document != null || message.Sticker != null || message.Animation != null || message.Voice != null || message.Audio != null)
            {
                // This means it's a single media message.
                // If the rule allows direct forwarding, media will be handled.
                // If the rule forces custom send, media will be skipped as re-upload is disabled.
                _logger.LogDebug("MessageForwardingService: Message {OriginalMessageId} is a single media message. Media will be forwarded directly if rule permits, otherwise skipped.", originalMessageId);
            }
            // --- End Media Handling Logic ---


            long sourceIdForMatchingRules = message.Chat.Id;

            // Handle various chat types for matching rules.
            if (message.Chat.Type == ChatType.Private)
            {
                _logger.LogDebug("MessageForwardingService: Message from private chat {TelegramApiId}. Skipping automatic forwarding enqueue as private chats are typically not sources for rules.", message.Chat.Id);
                return;
            }
            else if (message.Chat.Type == ChatType.Channel || message.Chat.Type == ChatType.Supergroup)
            {
                _logger.LogDebug("MessageForwardingService: Source is a Channel/Supergroup. Using ID {SourceId} for rule matching.", sourceIdForMatchingRules);
            }
            else if (message.Chat.Type == ChatType.Group)
            {
                _logger.LogWarning("MessageForwardingService: Source is a Basic Group Chat {TelegramApiId}. Ensure its ID {SourceIdForMatchingRules} matches rule DB format for matching. This might require a transformation if your DB stores IDs differently for basic groups.", message.Chat.Id, sourceIdForMatchingRules);
            }
            else
            {
                _logger.LogWarning("MessageForwardingService: Unhandled source chat type {ChatType} (Telegram.Bot ID: {ChatId}). Skipping automatic forwarding enqueue.", message.Chat.Type, message.Chat.Id);
                return;
            }

            _logger.LogInformation(
                "MessageForwardingService: Processing message {OriginalMessageId} from chat {SourceChannelId} (Bot API Type: {MessageType}). Content Preview: '{ContentPreview}'. Has Media: {HasMedia}. Sender Peer: {SenderPeerType}",
                originalMessageId, sourceIdForMatchingRules, message.Type, TruncateString(currentMessageContent, 50), message.Photo != null || message.Video != null || message.Document != null, tlSenderPeer?.GetType().Name ?? "N/A"); // Check message.Photo/Video/Document for media presence


            var applicableDbRules = (await _appForwardingService.GetRulesBySourceChannelAsync(sourceIdForMatchingRules, cancellationToken))
                                   .Where(r => r.IsEnabled)
                                   .ToList();

            if (!applicableDbRules.Any())
            {
                _logger.LogDebug("MessageForwardingService: No active DB forwarding rules found for source (matching ID {MatchingId}). Skipping.", sourceIdForMatchingRules);
                return;
            }

            _logger.LogInformation("MessageForwardingService: Found {RuleCount} applicable DB rules for source (matching ID {MatchingId})",
                applicableDbRules.Count, sourceIdForMatchingRules);

            long rawSourcePeerIdForJob = message.From?.Id ?? Math.Abs(message.Chat.Id);


            foreach (var dbRule in applicableDbRules)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested(); // Check cancellation per rule

                    if (dbRule.TargetChannelIds == null || !dbRule.TargetChannelIds.Any())
                    {
                        _logger.LogWarning("MessageForwardingService: Rule '{RuleName}' has no target channels defined. Skipping this rule.", dbRule.RuleName);
                        continue;
                    }

                    // --- Determine if a direct Forward is possible OR if custom send (with media skipping) is required ---
                    // directForwardPossible: true if NoForwards is FALSE and no other text edits would force a custom send.
                    bool directForwardPossible = !(dbRule.EditOptions?.NoForwards ?? false) &&
                                                 string.IsNullOrEmpty(dbRule.EditOptions?.PrependText) &&
                                                 string.IsNullOrEmpty(dbRule.EditOptions?.AppendText) &&
                                                 (dbRule.EditOptions?.TextReplacements == null || !dbRule.EditOptions.TextReplacements.Any()) &&
                                                 !(dbRule.EditOptions?.RemoveLinks ?? false) &&
                                                 !(dbRule.EditOptions?.StripFormatting ?? false) &&
                                                 string.IsNullOrEmpty(dbRule.EditOptions?.CustomFooter) &&
                                                 !(dbRule.EditOptions?.DropMediaCaptions ?? false); // If DropMediaCaptions is true, it forces custom send because text content is modified.

                    // If a custom send is NOT needed, we prioritize simple forward.
                    if (directForwardPossible)
                    {
                        // This is the simplest path: no edits, just forward.
                        // This also handles media and captions seamlessly as Telegram servers do the work.
                        _logger.LogDebug("MessageForwardingService: Rule '{RuleName}' allows direct forwarding. Enqueuing direct forward job for message {MessageId}.", dbRule.RuleName, originalMessageId);
                        var jobId = _enqueueRetryPolicy.Execute(
                            (pollyContext) =>
                            {
                                return _jobScheduler.Enqueue<IForwardingJobActions>(processor =>
                                    processor.ProcessAndRelayMessageAsync(
                                        originalMessageId,
                                        rawSourcePeerIdForJob, // Source peer ID for WTelegramClient
                                        dbRule.TargetChannelIds.FirstOrDefault(), // Target channel ID from rule
                                        dbRule, // The rule
                                        currentMessageContent, // Original content (will be ignored by simple forward)
                                        tlMessageEntities, // Original entities (will be ignored by simple forward)
                                        tlSenderPeer, // Original senderPeer (will be ignored by simple forward)
                                        null, // No mediaGroupItems needed for simple forward
                                        CancellationToken.None,
                                        null! // PerformContext
                                    ));
                            },
                            new Context($"EnqueueDirectForward_Msg:{originalMessageId}|Rule:{dbRule.RuleName}|Target:{dbRule.TargetChannelIds.FirstOrDefault()}")
                        );
                        _logger.LogInformation("MessageForwardingService: Successfully scheduled direct forwarding job {JobId} for message {MessageId}.", jobId, originalMessageId);
                    }
                    else
                    {
                        // Custom send is needed due to edits or NoForwards=true.
                        // If NoForwards is true, media will be skipped.
                        // If DropMediaCaptions is true, it forces a custom send. The content will be empty unless other prepends/appends add text.

                        // We must pass the original message content and entities,
                        // and potentially `null` for mediaGroupItems IF we're not supporting re-upload.
                        List<InputMediaWithCaption>? mediaForCustomSend = null;

                        if (dbRule.EditOptions?.NoForwards == true && (message.Photo != null || message.Video != null || message.Document != null))
                        {
                            _logger.LogWarning("MessageForwardingService: Rule '{RuleName}' has NoForwards enabled and message {OriginalMessageId} has media. Media will be SKIPPED because re-download/re-upload is disabled.", dbRule.RuleName, originalMessageId);
                            // mediaForCustomSend remains null, so ProcessCustomSendAsync won't attempt to send media.
                        }
                        else if (dbRule.EditOptions?.DropMediaCaptions == true && (message.Photo != null || message.Video != null || message.Document != null))
                        {
                            _logger.LogDebug("MessageForwardingService: Rule '{RuleName}' has DropMediaCaptions enabled for message {OriginalMessageId}. This forces custom send. Media will be implicitly handled by its lack of caption or direct presence.", dbRule.RuleName, originalMessageId);
                            // Media is here, but its caption will be dropped. ProcessCustomSendAsync still needs to know media exists.
                            // If you were to enable re-upload, you'd populate `mediaForCustomSend` here by calling `TryPrepareSingleMediaAsync`.
                            // Without re-upload, we'll let ProcessCustomSendAsync handle the text and simply skip the media.
                        }

                        _logger.LogDebug("MessageForwardingService: Rule '{RuleName}' requires custom send. Enqueuing custom send job for message {MessageId}.", dbRule.RuleName, originalMessageId);

                        var jobId = _enqueueRetryPolicy.Execute(
                            (pollyContext) =>
                            {
                                return _jobScheduler.Enqueue<IForwardingJobActions>(processor =>
                                    processor.ProcessAndRelayMessageAsync(
                                        originalMessageId,
                                        rawSourcePeerIdForJob,
                                        dbRule.TargetChannelIds.FirstOrDefault(),
                                        dbRule,
                                        currentMessageContent, // Pass the original content and entities
                                        tlMessageEntities,
                                        tlSenderPeer,
                                        mediaForCustomSend, // This will be null, explicitly indicating no media from this path.
                                        CancellationToken.None,
                                        null! // PerformContext
                                    ));
                            },
                            new Context($"EnqueueCustomSend_Msg:{originalMessageId}|Rule:{dbRule.RuleName}|Target:{dbRule.TargetChannelIds.FirstOrDefault()}")
                        );
                        _logger.LogInformation("MessageForwardingService: Successfully scheduled custom send job {JobId} for message {MessageId}.", jobId, originalMessageId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "MessageForwardingService: Error scheduling job for rule {RuleName} for message {MessageId} from source {SourceChannelId}. This will cause the main job to retry.",
                        dbRule.RuleName, originalMessageId, sourceIdForMatchingRules);
                    throw; // Re-throw to make the parent job (ProcessMessageAsync) retry this whole rule.
                }
            }
        }

        // Helper method to convert Telegram.Bot.Types.MessageEntity to TL.MessageEntity
        private TL.MessageEntity? ConvertTelegramBotEntityToTLEntity(Telegram.Bot.Types.MessageEntity tbEntity, string messageContent)
        {
            try
            {
                if (tbEntity.Offset < 0 || tbEntity.Length < 0 || tbEntity.Offset + tbEntity.Length > messageContent.Length)
                {
                    _logger.LogWarning("MessageForwardingService: Invalid entity offset/length for type {EntityType}. Offset: {Offset}, Length: {Length}, Message Length: {MessageLength}. Entity will be skipped.",
                        tbEntity.Type, tbEntity.Offset, tbEntity.Length, messageContent.Length);
                    return null;
                }

                return tbEntity.Type switch
                {
                    MessageEntityType.Bold => new TL.MessageEntityBold { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.Italic => new TL.MessageEntityItalic { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.Underline => new TL.MessageEntityUnderline { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.Strikethrough => new TL.MessageEntityStrike { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.Spoiler => new TL.MessageEntitySpoiler { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.Code => new TL.MessageEntityCode { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.Pre => new TL.MessageEntityPre { Offset = tbEntity.Offset, Length = tbEntity.Length, language = tbEntity.Language },
                    MessageEntityType.TextLink => new TL.MessageEntityTextUrl { Offset = tbEntity.Offset, Length = tbEntity.Length, url = tbEntity.Url?.ToString() ?? "" },
                    MessageEntityType.Url => new TL.MessageEntityUrl { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.Mention => new TL.MessageEntityMention { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.Hashtag => new TL.MessageEntityHashtag { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.Cashtag => new TL.MessageEntityCashtag { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.BotCommand => new TL.MessageEntityBotCommand { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.Email => new TL.MessageEntityEmail { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.PhoneNumber => new TL.MessageEntityPhone { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.TextMention => new TL.MessageEntityMentionName { Offset = tbEntity.Offset, Length = tbEntity.Length, user_id = tbEntity.User?.Id ?? 0 },
                    MessageEntityType.Blockquote => new TL.MessageEntityBlockquote { Offset = tbEntity.Offset, Length = tbEntity.Length, flags = 0 },
                  //  MessageEntityType.CustomEmoji => new TL.MessageEntityCustomEmoji { Offset = tbEntity.Offset, Length = tbEntity.Length, document_id = tbEntity.CustomEmojiId ?? 0 },
                    _ => null
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to convert Telegram.Bot MessageEntity type {EntityType} to TL.MessageEntity. Entity Offset: {Offset}, Length: {Length}. Content preview: '{ContentPreview}'",
                    tbEntity.Type, tbEntity.Offset, tbEntity.Length, TruncateString(messageContent, 50));
                return null;
            }
        }

        // Helper function to truncate strings for logging
        private string TruncateString(string? str, int maxLength)
        {
            if (string.IsNullOrEmpty(str)) return "[null_or_empty]";
            return str.Length <= maxLength ? str : str.Substring(0, maxLength) + "...";
        }

        // Helper to get Peer from Telegram.Bot.Types.Message.From or .SenderChat
        private TL.Peer? GetSenderPeer(Telegram.Bot.Types.Message message)
        {
            if (message.From != null)
            {
                return new TL.PeerUser { user_id = message.From.Id };
            }
            if (message.SenderChat != null)
            {
                if (message.SenderChat.Type == ChatType.Channel || message.SenderChat.Type == ChatType.Supergroup)
                {
                    return new TL.PeerChannel { channel_id = Math.Abs(message.SenderChat.Id) };
                }
                if (message.SenderChat.Type == ChatType.Group)
                {
                    return new TL.PeerChat { chat_id = Math.Abs(message.SenderChat.Id) };
                }
            }
            return null;
        }
    }
}