using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using Application.Features.Forwarding.Interfaces;
using System.Text.RegularExpressions;
using Domain.Features.Forwarding.Entities;
using Hangfire;
using System.Text.Json;
using Application.Common.Interfaces;
using Telegram.Bot.Types.Enums;
using TL;

namespace TelegramPanel.Infrastructure.Services
{
    public class MessageForwardingService
    {
        private readonly ILogger<MessageForwardingService> _logger;
        // private readonly IForwardingJobActions _forwardingJobActions; // No longer directly injecting this
        // private readonly List<Infrastructure.Settings.ForwardingRule> _forwardingRules; // REMOVE THIS
        private readonly INotificationJobScheduler _jobScheduler; // Assuming this is for Hangfire's IBackgroundJobClient
        private readonly IForwardingService _appForwardingService; // << INJECT THIS

        public MessageForwardingService(
            ILogger<MessageForwardingService> logger,
            // IForwardingJobActions forwardingJobActions, // REMOVE
            // IOptions<List<Infrastructure.Settings.ForwardingRule>> forwardingRules, // REMOVE
            IForwardingService appForwardingService, // << ADD
            INotificationJobScheduler jobScheduler)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            // _forwardingJobActions = forwardingJobActions; // REMOVE
            _appForwardingService = appForwardingService ?? throw new ArgumentNullException(nameof(appForwardingService));
            _jobScheduler = jobScheduler ?? throw new ArgumentNullException(nameof(jobScheduler));
            // _forwardingRules = forwardingRules?.Value ?? new List<Infrastructure.Settings.ForwardingRule>(); // REMOVE

            _logger.LogInformation("MessageForwardingService initialized to use database-driven rules via IForwardingService.");
        }

        public async Task HandleMessageAsync(Telegram.Bot.Types.Message message, CancellationToken cancellationToken = default)
        {
            if (message == null)
            {
                _logger.LogWarning("TelegramPanel.MessageForwardingService: Received null message. Skipping.");
                return;
            }

            // 1. Extract content and entities from Telegram.Bot.Types.Message
            string messageContent = message.Text ?? message.Caption ?? string.Empty;
            Telegram.Bot.Types.MessageEntity[]? telegramBotEntities = message.Entities ?? message.CaptionEntities;

            TL.MessageEntity[]? tlMessageEntities = null;
            if (telegramBotEntities != null && telegramBotEntities.Any())
            {
                var convertedEntities = new System.Collections.Generic.List<TL.MessageEntity>();
                foreach (var entity in telegramBotEntities)
                {
                    var tlEntity = ConvertTelegramBotEntityToTLEntity(entity);
                    if (tlEntity != null)
                    {
                        convertedEntities.Add(tlEntity);
                    }
                    else
                    {
                        _logger.LogWarning("TelegramPanel.MessageForwardingService: Failed to convert Telegram.Bot entity type {EntityType} for message {MessageId}", entity.Type, message.MessageId);
                    }
                }
                tlMessageEntities = convertedEntities.ToArray();
            }

            // 2. Extract sender Peer for filtering
            TL.Peer? tlSenderPeer = null;
            if (message.From != null)
            {
                tlSenderPeer = new TL.PeerUser { user_id = message.From.Id };
                _logger.LogTrace("TelegramPanel.MessageForwardingService: Sender is a User: {UserId}", message.From.Id);
            }
            else if (message.SenderChat != null)
            {
                long senderChatId = message.SenderChat.Id;
                if (message.SenderChat.Type == Telegram.Bot.Types.Enums.ChatType.Channel || message.SenderChat.Type == Telegram.Bot.Types.Enums.ChatType.Supergroup)
                {
                    long positiveChannelId = senderChatId;
                    if (positiveChannelId.ToString().StartsWith("-100"))
                    {
                        positiveChannelId = Math.Abs(senderChatId) - 1000000000000L;
                    }
                    else
                    {
                        positiveChannelId = Math.Abs(senderChatId);
                    }
                    tlSenderPeer = new TL.PeerChannel { channel_id = positiveChannelId };
                    _logger.LogTrace("TelegramPanel.MessageForwardingService: Sender is a Channel/Supergroup: {ChannelId} (Original Telegram.Bot ID: {OriginalId})", positiveChannelId, senderChatId);
                }
                else if (message.SenderChat.Type == Telegram.Bot.Types.Enums.ChatType.Group)
                {
                    tlSenderPeer = new TL.PeerChat { chat_id = Math.Abs(senderChatId) };
                    _logger.LogTrace("TelegramPanel.MessageForwardingService: Sender is a Group: {ChatId} (Original Telegram.Bot ID: {OriginalId})", Math.Abs(senderChatId), senderChatId);
                }
                _logger.LogTrace("TelegramPanel.MessageForwardingService: Sender is a Chat Type: {ChatType} (Telegram.Bot ID: {ChatId})", message.SenderChat.Type, message.SenderChat.Id);
            }
            else
            {
                _logger.LogTrace("TelegramPanel.MessageForwardingService: Sender information (From/SenderChat) is null for message {MessageId}. Skipping sender peer for filter.", message.MessageId);
            }

            // 3. Extract and convert Media to TL.InputMedia and then to List<InputMediaWithCaption>
            // IMPORTANT: Direct conversion of Telegram.Bot.Types.FileId (Photo.FileId, Video.FileId, Document.FileId)
            // to WTelegramClient's TL.InputPhoto / TL.InputDocument (which require 'id', 'access_hash', 'file_reference')
            // is NOT possible without re-downloading the file and re-uploading it via WTelegramClient.
            // WTelegramClient's IDs are internal and different from Telegram.Bot's FileIds.
            // So, `tlInputMedia` as a simple conversion will likely be null or result in invalid media.
            List<InputMediaWithCaption>? mediaGroupItems = null;

            // Handling for a single media item
            TL.InputMedia? currentPreparedMedia = null;
            if (message.Photo != null && message.Photo.Any())
            {
                var largestPhoto = message.Photo.OrderByDescending(p => p.Width * p.Height).FirstOrDefault();
                if (largestPhoto != null)
                {
                    _logger.LogWarning("TelegramPanel.MessageForwardingService: Attempting to create InputMediaPhoto from Telegram.Bot PhotoSize (FileId: {FileId}). Direct conversion to TL.InputMedia's 'id', 'access_hash', 'file_reference' is NOT supported. Media will likely NOT be sent correctly.", largestPhoto.FileId);
                    // Create a dummy InputMediaPhoto. This will likely fail in WTelegramClient API unless it resolves FileId internally (which it won't).
                    // The correct way is to download largestPhoto.FileId and then upload it with WTelegramClient.
                    currentPreparedMedia = new TL.InputMediaPhoto { id = new InputPhoto { id = 0, access_hash = 0, file_reference = Array.Empty<byte>() } };
                }
            }
            else if (message.Video != null)
            {
                _logger.LogWarning("TelegramPanel.MessageForwardingService: Attempting to create InputMediaDocument from Telegram.Bot Video (FileId: {FileId}). Direct conversion is NOT supported. Media will likely NOT be sent correctly.", message.Video.FileId);
                currentPreparedMedia = new TL.InputMediaDocument { id = new InputDocument { id = 0, access_hash = 0, file_reference = Array.Empty<byte>() } };
            }
            else if (message.Document != null)
            {
                _logger.LogWarning("TelegramPanel.MessageForwardingService: Attempting to create InputMediaDocument from Telegram.Bot Document (FileId: {FileId}). Direct conversion is NOT supported. Media will likely NOT be sent correctly.", message.Document.FileId);
                currentPreparedMedia = new TL.InputMediaDocument { id = new InputDocument { id = 0, access_hash = 0, file_reference = Array.Empty<byte>() } };
            }
            // IMPORTANT: If you need to handle media groups from Telegram.Bot, you'll need to check message.MediaGroupId
            // and implement a caching mechanism (like a ConcurrentDictionary with a timer) to collect all parts of the media group
            // before enqueuing *one* Hangfire job that passes the complete list of InputMediaWithCaption.
            // The current code sends each media item as a separate job, which will result in individual messages.

            if (currentPreparedMedia != null)
            {
                mediaGroupItems = new System.Collections.Generic.List<InputMediaWithCaption>
               {
                   new InputMediaWithCaption
                   {
                       Media = currentPreparedMedia,
                       Caption = messageContent, // Use combined content for caption
                       Entities = tlMessageEntities // Use combined entities
                   }
               };
            }


            var telegramApiSourceChatId = message.Chat.Id;
            _logger.LogInformation(
                "TelegramPanel.MessageForwardingService: Processing message {MessageId} from chat {SourceChannelId} (Bot API Type: {MessageType}). Content Preview: '{ContentPreview}'. Has Media: {HasMedia}. Sender Peer: {SenderPeer}",
                message.MessageId, telegramApiSourceChatId, message.Type, TruncateString(messageContent, 50), mediaGroupItems != null && mediaGroupItems.Any(), tlSenderPeer?.ToString() ?? "N/A");

            long sourceIdForMatchingRules;
            long positiveSourceId = Math.Abs(telegramApiSourceChatId); // Get positive ID

            if (telegramApiSourceChatId < 0) // Common for channels/supergroups/groups from Telegram.Bot
            {
                if (telegramApiSourceChatId.ToString().StartsWith("-100")) // Supergroup/Channel
                {
                    sourceIdForMatchingRules = telegramApiSourceChatId; // Already in the right format for DB rule matching
                    _logger.LogDebug("TelegramPanel.MessageForwardingService: Source is a Channel/Supergroup. Using ID {SourceId} for rule matching.", sourceIdForMatchingRules);
                }
                else // Regular group chat (Telegram.Bot IDs are negative for groups, e.g., -12345)
                {
                    // Your DB rules use -100_000_000_000L - positiveId for groups.
                    sourceIdForMatchingRules = -100_000_000_000L - positiveSourceId;
                    _logger.LogWarning("TelegramPanel.MessageForwardingService: Source is a GroupChat. Converting ID {TelegramApiId} to {MatchingId} for rule matching. Verify storage format for group rules.", telegramApiSourceChatId, sourceIdForMatchingRules);
                }
            }
            else // Probably a user, which we usually don't forward from automatically (unless configured)
            {
                _logger.LogDebug("TelegramPanel.MessageForwardingService: Message from user chat {TelegramApiId}. Skipping automatic forwarding enqueue.", telegramApiSourceChatId);
                return;
            }


            var applicableDbRules = (await _appForwardingService.GetRulesBySourceChannelAsync(sourceIdForMatchingRules, cancellationToken))
                                   .Where(r => r.IsEnabled)
                                   .ToList();

            if (!applicableDbRules.Any())
            {
                _logger.LogDebug("TelegramPanel.MessageForwardingService: No active DB forwarding rules found for source (matching ID {MatchingId}). Skipping.", sourceIdForMatchingRules);
                return;
            }

            _logger.LogInformation("TelegramPanel.MessageForwardingService: Found {RuleCount} applicable DB rules for source (matching ID {MatchingId})",
                applicableDbRules.Count, sourceIdForMatchingRules);

            foreach (var dbRule in applicableDbRules) // dbRule is Domain.Features.Forwarding.Entities.ForwardingRule
            {
                try
                {
                    _logger.LogInformation("TelegramPanel.MessageForwardingService: Processing DB rule '{RuleName}' for message {MessageId}", dbRule.RuleName, message.MessageId);

                    foreach (var targetChannelId in dbRule.TargetChannelIds)
                    {
                        _logger.LogInformation(
                            "TelegramPanel.MessageForwardingService: Scheduling forwarding job for message {MessageId} to target {TargetChannelId} using DB rule '{RuleName}'. Content Preview: '{ContentPreview}'. Has Media: {HasMedia}",
                            message.MessageId, targetChannelId, dbRule.RuleName, TruncateString(messageContent, 50), mediaGroupItems != null && mediaGroupItems.Any());

                        long rawSourcePeerIdForJob = positiveSourceId;

                        var jobId = _jobScheduler.Enqueue<IForwardingJobActions>(job =>
                            job.ProcessAndRelayMessageAsync(
                                message.MessageId,       // Correctly an int
                                rawSourcePeerIdForJob,   // Pass the positive source ID for WTelegramClient to resolve
                                targetChannelId,         // DB rule target ID
                                dbRule,                  // Domain Rule object
                                messageContent,          // Pass the extracted message content
                                tlMessageEntities,       // Pass the converted TL message entities
                                tlSenderPeer,            // Pass the converted TL sender peer
                                mediaGroupItems,         // CHANGED: Pass the List<InputMediaWithCaption>
                                CancellationToken.None   // CancellationToken for Hangfire job
                            ));

                        _logger.LogInformation(
                            "TelegramPanel.MessageForwardingService: Successfully scheduled forwarding job {JobId} for message {MessageId} to channel {TargetChannelId}",
                            jobId, message.MessageId, targetChannelId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "TelegramPanel.MessageForwardingService: Error scheduling/processing DB rule {RuleName} for message {MessageId} from source {SourceChannelId}",
                        dbRule.RuleName, message.MessageId, telegramApiSourceChatId);
                }
            }
        }



        // Helper method to convert Telegram.Bot.Types.MessageEntity to TL.MessageEntity
        // This is a crucial mapping function.
        private TL.MessageEntity? ConvertTelegramBotEntityToTLEntity(Telegram.Bot.Types.MessageEntity tbEntity)
        {
            try
            {
                return tbEntity.Type switch
                {
                    MessageEntityType.Bold => new TL.MessageEntityBold { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.Italic => new TL.MessageEntityItalic { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.Underline => new TL.MessageEntityUnderline { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.Strikethrough => new TL.MessageEntityStrike { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.Spoiler => new TL.MessageEntitySpoiler { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.Code => new TL.MessageEntityCode { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.Pre => new TL.MessageEntityPre { Offset = tbEntity.Offset, Length = tbEntity.Length, language = tbEntity.Language },
                    MessageEntityType.TextLink => new TL.MessageEntityTextUrl { Offset = tbEntity.Offset, Length = tbEntity.Length, url = tbEntity.Url ?? "" },
                    MessageEntityType.Url => new TL.MessageEntityUrl { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.Mention => new TL.MessageEntityMention { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.Hashtag => new TL.MessageEntityHashtag { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.Cashtag => new TL.MessageEntityCashtag { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.BotCommand => new TL.MessageEntityBotCommand { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.Email => new TL.MessageEntityEmail { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.PhoneNumber => new TL.MessageEntityPhone { Offset = tbEntity.Offset, Length = tbEntity.Length },
                    MessageEntityType.TextMention => new TL.MessageEntityMentionName { Offset = tbEntity.Offset, Length = tbEntity.Length, user_id = tbEntity.User?.Id ?? 0 },
                    MessageEntityType.Blockquote => new TL.MessageEntityBlockquote { Offset = tbEntity.Offset, Length = tbEntity.Length, flags = 0 }, // Adjust flags if needed
              
                    // Add more mappings as new types are supported or needed
                    _ => null // Return null for unsupported types or throw an exception if strict
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to convert Telegram.Bot MessageEntity type {EntityType} to TL.MessageEntity. Entity Offset: {Offset}, Length: {Length}",
                    tbEntity.Type, tbEntity.Offset, tbEntity.Length);
                return null;
            }
        }







        // Helper function to truncate strings for logging
        private string TruncateString(string? str, int maxLength)
        {
            if (string.IsNullOrEmpty(str)) return "[null_or_empty]";
            return str.Length <= maxLength ? str : str.Substring(0, maxLength) + "...";
        }

        private bool ShouldProcessMessage(Telegram.Bot.Types.Message message, ForwardingRule rule)
        {
            if (rule.FilterOptions == null)
            {
                return true;
            }

            var filterOptions = rule.FilterOptions;

            // Check message type
            if (filterOptions.AllowedMessageTypes != null && filterOptions.AllowedMessageTypes.Any())
            {
                var messageType = message.Type.ToString();
                if (!filterOptions.AllowedMessageTypes.Contains(messageType))
                {
                    _logger.LogDebug("Message type {MessageType} not in allowed types for rule {RuleName}",
                        messageType, rule.RuleName);
                    return false;
                }
            }

            // Check message text content
            if (!string.IsNullOrEmpty(filterOptions.ContainsText))
            {
                var messageText = message.Text ?? message.Caption ?? string.Empty;
                if (filterOptions.ContainsTextIsRegex)
                {
                    try
                    {
                        var regex = new Regex(filterOptions.ContainsText, 
                            (RegexOptions)filterOptions.ContainsTextRegexOptions);
                        if (!regex.IsMatch(messageText))
                        {
                            _logger.LogDebug("Message text does not match regex pattern for rule {RuleName}",
                                rule.RuleName);
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing regex pattern for rule {RuleName}", rule.RuleName);
                        return false;
                    }
                }
                else if (!messageText.Contains(filterOptions.ContainsText, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Message text does not contain required text for rule {RuleName}",
                        rule.RuleName);
                    return false;
                }
            }

            // Check message length
            var textLength = (message.Text ?? message.Caption ?? string.Empty).Length;
            if (filterOptions.MinMessageLength.HasValue && textLength < filterOptions.MinMessageLength.Value)
            {
                _logger.LogDebug("Message length {Length} is less than minimum {MinLength} for rule {RuleName}",
                    textLength, filterOptions.MinMessageLength.Value, rule.RuleName);
                return false;
            }
            if (filterOptions.MaxMessageLength.HasValue && textLength > filterOptions.MaxMessageLength.Value)
            {
                _logger.LogDebug("Message length {Length} is greater than maximum {MaxLength} for rule {RuleName}",
                    textLength, filterOptions.MaxMessageLength.Value, rule.RuleName);
                return false;
            }

            // Check sender restrictions
            if (message.From != null)
            {
                if (filterOptions.AllowedSenderUserIds != null && filterOptions.AllowedSenderUserIds.Any() &&
                    !filterOptions.AllowedSenderUserIds.Contains(message.From.Id))
                {
                    _logger.LogDebug("Sender {SenderId} not in allowed senders for rule {RuleName}",
                        message.From.Id, rule.RuleName);
                    return false;
                }

                if (filterOptions.BlockedSenderUserIds != null && filterOptions.BlockedSenderUserIds.Any() &&
                    filterOptions.BlockedSenderUserIds.Contains(message.From.Id))
                {
                    _logger.LogDebug("Sender {SenderId} is blocked for rule {RuleName}",
                        message.From.Id, rule.RuleName);
                    return false;
                }
            }

            return true;
        }
    }
} 