// File: Infrastructure/Services/UserApiForwardingOrchestrator.cs
using Application.Common.Interfaces; // For ITelegramUserApiClient
using Hangfire; // For Update, Message, Peer types
// Domain.Features.Forwarding.Entities - Assuming used by IForwardingService
using Microsoft.Extensions.Logging;
using TL;

namespace Infrastructure.Services
{
    public class UserApiForwardingOrchestrator
    {
        private readonly ITelegramUserApiClient _userApiClient;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<UserApiForwardingOrchestrator> _logger;
        private readonly IBackgroundJobClient _backgroundJobClient;
        public UserApiForwardingOrchestrator(
            ITelegramUserApiClient userApiClient,
            IServiceProvider serviceProvider,
            IBackgroundJobClient backgroundJobClient,
            ILogger<UserApiForwardingOrchestrator> logger)
        {
            _backgroundJobClient = backgroundJobClient ?? throw new ArgumentNullException(nameof(backgroundJobClient));
            _userApiClient = userApiClient ?? throw new ArgumentNullException(nameof(userApiClient));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _userApiClient.OnCustomUpdateReceived += HandleUserApiUpdateAsync; // Subscribing event handler
            _logger.LogInformation("UserApiForwardingOrchestrator initialized and subscribed to OnCustomUpdateReceived from User API.");
        }

        // Retaining `async void` as this is a common pattern for event handlers.
        private void HandleUserApiUpdateAsync(Update update)
        {
            _ = Task.Run(async () =>
            {
                TL.Message? messageToProcess = null;
                Peer? sourceApiPeer = null;
                long messageIdForLog = 0;
                string messageContent = string.Empty;
                TL.MessageEntity[]? messageEntities = null;
                Peer? senderPeerForFilter = null;
                InputMedia? inputMediaForJob = null; // NEW: To store prepared InputMedia

                string updateTypeForLog = update?.GetType().Name ?? "NullUpdateType";
                string messageContentPreview = "[N/A]";

                try
                {
                    if (update == null) { /* ... */ return; }

                    if (update is UpdateNewMessage unm)
                    {
                        messageToProcess = unm.message as TL.Message;
                    }
                    else if (update is UpdateNewChannelMessage uncm)
                    {
                        messageToProcess = uncm.message as TL.Message;
                    }
                    else { /* ... */ return; }

                    if (messageToProcess == null) { /* ... */ return; }

                    sourceApiPeer = messageToProcess.peer_id;
                    messageIdForLog = messageToProcess.id;
                    senderPeerForFilter = messageToProcess.from_id;

                    messageContent = messageToProcess.message ?? string.Empty;
                    messageEntities = messageToProcess.entities?.ToArray();

                    // --- NEW LOGIC: Prepare InputMedia directly from the Update ---
                    if (messageToProcess.media != null)
                    {
                        _logger.LogTrace("ORCHESTRATOR_TASK: Detected media in message {MsgId}. Attempting to prepare InputMedia.", messageToProcess.id);
                        if (messageToProcess.media is MessageMediaPhoto mmp && mmp.photo is Photo p)
                        {
                            inputMediaForJob = new InputMediaPhoto
                            {
                                id = new InputPhoto { id = p.id, access_hash = p.access_hash, file_reference = p.file_reference ?? Array.Empty<byte>() }
                            };
                            _logger.LogTrace("ORCHESTRATOR_TASK: Prepared InputMediaPhoto for MsgID {MsgId}. Photo ID: {PhotoId}", messageToProcess.id, p.id);
                        }
                        else if (messageToProcess.media is MessageMediaDocument mmd && mmd.document is Document d)
                        {
                            inputMediaForJob = new InputMediaDocument
                            {
                                id = new InputDocument { id = d.id, access_hash = d.access_hash, file_reference = d.file_reference ?? Array.Empty<byte>() }
                            };
                            _logger.LogTrace("ORCHESTRATOR_TASK: Prepared InputMediaDocument for MsgID {MsgId}. Document ID: {DocumentId}", messageToProcess.id, d.id);
                        }
                        else
                        {
                            _logger.LogWarning("ORCHESTRATOR_TASK: Unsupported media type {MediaType} in message {MsgId}. Cannot prepare InputMedia.", messageToProcess.media.GetType().Name, messageToProcess.id);
                        }
                    }
                    // --- END NEW LOGIC ---

                    messageContentPreview = TruncateString(messageContent, 50);
                    _logger.LogInformation("ORCHESTRATOR_TASK: Extracted new message. MsgID: {MsgId}, SourcePeerType: {PeerType}, SourcePeerIDValue: {PeerIdValue}. Message Content Preview: '{MsgContentPreview}'. Has Media: {HasMedia}. Sender Peer: {SenderPeer}",
                        messageToProcess.id, sourceApiPeer?.GetType().Name, GetPeerIdValue(sourceApiPeer), messageContentPreview, messageToProcess.media != null, senderPeerForFilter?.ToString() ?? "N/A");

                    // Filter out messages from PeerUser (direct user messages)
                    if (sourceApiPeer is PeerUser userPeer)
                    {
                        _logger.LogDebug("ORCHESTRATOR_TASK: Message from PeerUser (direct user message). UserID: {UserId}. SKIPPING automatic forwarding enqueue.", userPeer.user_id);
                        return;
                    }

                    long currentSourcePositiveId = GetPeerIdValue(sourceApiPeer);

                    if (currentSourcePositiveId == 0)
                    {
                        _logger.LogWarning("ORCHESTRATOR_TASK: Could not determine a valid positive source ID from Peer: {PeerString} for Hangfire enqueue. SKIPPING.", sourceApiPeer.ToString());
                        return;
                    }

                    long sourceIdForMatchingRules = -100_000_000_000L - currentSourcePositiveId;
                    long rawSourcePeerIdForApi = currentSourcePositiveId;

                    _logger.LogInformation("ORCHESTRATOR_TASK: Enqueuing to Hangfire IForwardingService.ProcessMessageAsync for MsgID: {MsgId}. RuleMatchingID: {RuleMatchID}, RawApiPeerID (Positive): {RawApiPeerID}. Content Preview: '{ContentPreview}'. Has Input Media: {HasInputMedia}. Sender Peer: {SenderPeer}",
                                           messageToProcess.id, sourceIdForMatchingRules, rawSourcePeerIdForApi, messageContentPreview, inputMediaForJob != null, senderPeerForFilter?.ToString() ?? "N/A");

                    _backgroundJobClient.Enqueue<IForwardingService>(service =>
                        service.ProcessMessageAsync(
                            sourceIdForMatchingRules,
                            messageToProcess.id,
                            rawSourcePeerIdForApi,
                            messageContent,
                            messageEntities,
                            senderPeerForFilter,
                            inputMediaForJob, // NEW: Pass the prepared InputMedia
                            CancellationToken.None
                        ));

                    _logger.LogInformation("ORCHESTRATOR_TASK: Successfully enqueued job to Hangfire for IForwardingService.ProcessMessageAsync. MsgID: {MsgId}.", messageToProcess.id);
                }
                catch (Exception ex)
                {
                    string finalMessageIdForLog = messageIdForLog != 0 ? messageIdForLog.ToString() : "N/A";
                    string finalUpdateTypeForLog = updateTypeForLog ?? "N/A";
                    string finalMessageContentPreview = messageContentPreview ?? "N/A";

                    _logger.LogCritical(ex, "ORCHESTRATOR_TASK: CRITICAL EXCEPTION during Hangfire Enqueue for MsgID: {MsgId}. UpdateType: {UpdateType}. Message Content: '{MsgContentPreview}'.",
                                       finalMessageIdForLog, finalUpdateTypeForLog, finalMessageContentPreview);
                }
            });
        
        }

      

        // Helper function to truncate strings for logging
        private string TruncateString(string? str, int maxLength)
        {
            if (string.IsNullOrEmpty(str)) return "[null_or_empty]";
            return str.Length <= maxLength ? str : str.Substring(0, maxLength) + "...";
        }

        // متد کمکی برای گرفتن شناسه عددی از انواع Peer
        private long GetPeerIdValue(Peer? peer)
        {
            return peer switch
            {
                PeerUser user => user.user_id,
                PeerChat chat => chat.chat_id,
                PeerChannel channel => channel.channel_id,
                _ => 0
            };
        }
    }
}