// File: Infrastructure/Services/UserApiForwardingOrchestrator.cs
using Application.Common.Interfaces; // For ITelegramUserApiClient
using Application.Features.Forwarding.Services; // For IForwardingService
// Domain.Features.Forwarding.Entities - Assuming used by IForwardingService
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection; // For IServiceProvider, CreateScope, GetRequiredService
using System; // For IServiceProvider, ArgumentNullException, Exception
using System.Threading.Tasks; // For Task.Run
using TL; // For Update, Message, Peer types

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

            _userApiClient.OnCustomUpdateReceived += HandleUserApiUpdateAsync; // Subscribing event handler
            _logger.LogInformation("UserApiForwardingOrchestrator initialized and subscribed to OnCustomUpdateReceived from User API.");
        }

        // Retaining `async void` as this is a common pattern for event handlers.
        // The core logic is offloaded to Task.Run to avoid blocking and to manage exceptions.
        private void HandleUserApiUpdateAsync(Update update)
        {
            #region Initial Checks and Task Offloading
            // Defensive null check for the incoming update.
            if (update == null)
            {
                _logger.LogWarning("UserApiForwardingOrchestrator: Received a null update. Skipping processing.");
                return;
            }

            // Log the initial reception of the update type before offloading.
            _logger.LogDebug("UserApiForwardingOrchestrator: Received TL.Update of type {UpdateType}. Offloading processing.", update.GetType().Name);

            // Offload the processing to a background thread.
            // This prevents blocking the User API client's event loop and isolates errors.
            _ = Task.Run(async () =>
            {
                // System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew(); // For performance diagnostics
                try
                {
                    _logger.LogDebug("UserApiForwardingOrchestrator: Async processing task started for update (Type: {UpdateType}).", update.GetType().Name);

                    #region Message Extraction (Original Structure)
                    // Using the original if-else if structure for message and peer extraction.
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
                    // Note: Consider if other update types like UpdateEditMessage should be processed.

                    // Validate if a message and peer were successfully extracted.
                    if (messageToProcess == null || sourceApiPeer == null)
                    {
                        _logger.LogTrace("UserApiForwardingOrchestrator: Update (Type: {UpdateType}) is not a new message to process or message/peer content is null. Skipping task.", update.GetType().Name);
                        return; // Exit the task for this update.
                    }
                    #endregion

                    #region Source ID Determination (Original Structure)
                    // Determine the positive source ID using the original if-else if structure.
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
                        // Business rule: Messages from individual users might be ignored.
                        _logger.LogDebug("UserApiForwardingOrchestrator: Message from PeerUser, UserID: {UserId}. Not typically forwarded by channel/group rules. Skipping task.", spu.user_id);
                        return; // Exit the task.
                    }
                    // If sourceApiPeer is none of the above (e.g., null or an unknown type), currentSourcePositiveId remains 0.

                    // Validate the determined source ID.
                    if (currentSourcePositiveId <= 0) // Catches PeerUser (if not returned above) or other invalid scenarios
                    {
                        _logger.LogWarning("UserApiForwardingOrchestrator: Could not determine a valid positive source channel/chat ID from Peer: {PeerString} (Resolved ID: {ResolvedId}). Skipping task.",
                                           sourceApiPeer.ToString(), currentSourcePositiveId);
                        return; // Exit the task.
                    }
                    #endregion

                    #region Rule-Specific ID Transformation
                    // Transform the positive ID to the format used for matching forwarding rules.
                    // Using a specific long literal for the prefix part of the ID.
                    long sourceIdForMatchingRules = -100_000_000_000L - currentSourcePositiveId;

                    _logger.LogInformation("UserApiForwardingOrchestrator: Message ready for service processing. MsgID: {MessageId}, API Source Positive ID: {ApiSourcePositiveId}, Rule Matching Source ID: {RuleMatchSourceId}",
                                           messageToProcess.id,
                                           currentSourcePositiveId,
                                           sourceIdForMatchingRules);
                    #endregion

                    #region Service Invocation within a DI Scope
                    // Create a DI scope to resolve IForwardingService with correct lifetime.
                    // 'using' ensures proper disposal of the scope and its services.
                    // Security Reminder: IForwardingService and dependencies must handle data safely.
                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var forwardingService = scope.ServiceProvider.GetRequiredService<IForwardingService>();

                        // Asynchronously process the message via the forwarding service.
                        await forwardingService.ProcessMessageAsync(sourceIdForMatchingRules, messageToProcess.id);

                        _logger.LogDebug("UserApiForwardingOrchestrator: Message (MsgID: {MessageId}, RuleMatchSourceId: {RuleMatchSourceId}) successfully submitted to forwarding service.",
                                         messageToProcess.id, sourceIdForMatchingRules);
                    }
                    catch (ObjectDisposedException odEx) // Handle cases where DI provider/scope is disposed (app shutdown)
                    {
                        _logger.LogWarning(odEx, "UserApiForwardingOrchestrator: DI Scope/Service operation failed due to object disposal (likely app shutdown). MsgID: {MessageId}, RuleMatchSourceId: {RuleMatchSourceId}",
                                           messageToProcess?.id, sourceIdForMatchingRules);
                    }
                    catch (Exception serviceEx) // Catch errors from IForwardingService or its dependencies.
                    {
                        _logger.LogError(serviceEx, "UserApiForwardingOrchestrator: Error during IForwardingService invocation for MsgID: {MessageId}, RuleMatchSourceId: {RuleMatchSourceId}.",
                                         messageToProcess?.id, sourceIdForMatchingRules);
                        // The task for this message will end here; other messages are unaffected.
                    }
                    #endregion
                }
                catch (Exception ex) // Catch-all for any unhandled errors within the Task.Run lambda.
                {
                    // Critical fallback to prevent silent failures of the async task.
                    string updateDebugInfo = "Update object was null or could not be stringified.";
                    if (update != null) // 'update' is captured from the outer scope
                    {
                        try { updateDebugInfo = update.ToString(); }
                        catch { /* Safeguard against ToString() throwing an exception */ }
                    }

                    _logger.LogCritical(ex, "UserApiForwardingOrchestrator: Unhandled critical exception during asynchronous processing of update type {UpdateType}. Update Snippet: {UpdateDebugSnippet}",
                                       update?.GetType().Name, // update might be null if an error occurs before it's used within the task.
                                       updateDebugInfo.Substring(0, Math.Min(updateDebugInfo.Length, 250))); // Log a snippet for diagnostics
                    // Consider additional critical error alerting if necessary.
                }
                finally
                {
                    // stopwatch.Stop();
                    // _logger.LogTrace("UserApiForwardingOrchestrator: Async processing task finished for update (Type: {UpdateType}). Elapsed: {ElapsedMilliseconds}ms.",
                    //                  update?.GetType().Name, stopwatch.ElapsedMilliseconds);
                }
            }); // End of Task.Run
            #endregion
        }
    }
}