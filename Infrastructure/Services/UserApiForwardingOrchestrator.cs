// File: Infrastructure/Services/UserApiForwardingOrchestrator.cs
using Application.Common.Interfaces;
using Application.Features.Forwarding.Services;
using Domain.Features.Forwarding.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using TL;

namespace Infrastructure.Services
{
    public class UserApiForwardingOrchestrator
    {
        private readonly ITelegramUserApiClient _userApiClient;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<UserApiForwardingOrchestrator> _logger;

        public UserApiForwardingOrchestrator(
            ITelegramUserApiClient userApiClient,
            IServiceProvider serviceProvider,
            ILogger<UserApiForwardingOrchestrator> logger)
        {
            _userApiClient = userApiClient ?? throw new ArgumentNullException(nameof(userApiClient));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _userApiClient.OnCustomUpdateReceived += HandleUserApiUpdateAsync;
            _logger.LogInformation("UserApiForwardingOrchestrator initialized and subscribed to OnCustomUpdateReceived from User API.");
        }

        private async void HandleUserApiUpdateAsync(Update update)
        {
            _logger.LogDebug("UserApiForwardingOrchestrator: Received TL.Update of type {UpdateType}.", update.GetType().Name);

            Message? messageToProcess = null;
            Peer? sourceApiPeer = null;

            if (update is UpdateNewMessage unm && unm.message is Message msg1)
            {
                messageToProcess = msg1;
                sourceApiPeer = msg1.peer_id;
                _logger.LogInformation("UserApiForwardingOrchestrator: Processing UpdateNewMessage. MsgID: {MsgId}, FromPeer: {PeerId}", msg1.id, sourceApiPeer?.ToString());
            }
            else if (update is UpdateNewChannelMessage uncm && uncm.message is Message msg2)
            {
                messageToProcess = msg2;
                sourceApiPeer = msg2.peer_id;
                _logger.LogInformation("UserApiForwardingOrchestrator: Processing UpdateNewChannelMessage. MsgID: {MsgId}, FromChannel: {PeerId}", msg2.id, sourceApiPeer?.ToString());
            }

            if (messageToProcess == null || sourceApiPeer == null)
            {
                _logger.LogTrace("UserApiForwardingOrchestrator: Update type {UpdateType} not a new message or message content is null. Skipping.", update.GetType().Name);
                return;
            }

            long currentSourcePositiveId = 0;

            if (sourceApiPeer is PeerChannel scp)
            {
                currentSourcePositiveId = scp.channel_id;
            }
            else if (sourceApiPeer is PeerChat scc)
            {
                currentSourcePositiveId = scc.chat_id;
            }
            else if (sourceApiPeer is PeerUser spu)
            {
                _logger.LogDebug("UserApiForwardingOrchestrator: Message from PeerUser, ID: {UserId}. Not typically forwarded by channel rules.", spu.user_id);
                return;
            }

            if (currentSourcePositiveId == 0)
            {
                _logger.LogDebug("UserApiForwardingOrchestrator: Could not determine a valid positive source channel/chat ID from Peer: {PeerString}", sourceApiPeer.ToString());
                return;
            }

            long sourceIdForMatchingRules = -1000000000000 - currentSourcePositiveId;
            _logger.LogInformation("UserApiForwardingOrchestrator: Message from API Source ID: {ApiSourceId} (Converted for rule matching: {RuleMatchSourceId}), Msg ID: {MessageId}",
                currentSourcePositiveId, 
                sourceIdForMatchingRules, 
                messageToProcess.id);

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var forwardingService = scope.ServiceProvider.GetRequiredService<IForwardingService>();
                await forwardingService.ProcessMessageAsync(sourceIdForMatchingRules, messageToProcess.id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message {MessageId} from channel {ChannelId}", messageToProcess.id, sourceIdForMatchingRules);
            }
        }
    }
}