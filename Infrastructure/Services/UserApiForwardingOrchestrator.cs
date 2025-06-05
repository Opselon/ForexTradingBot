// File: Infrastructure/Services/UserApiForwardingOrchestrator.cs

#region Usings
using Application.Common.Interfaces; // For ITelegramUserApiClient
using Application.Features.Forwarding.Interfaces; // For IForwardingService, InputMediaWithCaption
using Hangfire; // For IBackgroundJobClient
using Microsoft.Extensions.Logging; // For ILogger
using System; // For EventArgs, ArgumentNullException, TimeSpan, Guid
using System.Collections.Concurrent; // For ConcurrentDictionary
using System.Collections.Generic; // For List, Dictionary, ICollection
using System.Linq; // For LINQ extensions like .Any(), .FirstOrDefault(), .Select()
using System.Text; // For StringBuilder (if used for logging)
using System.Text.RegularExpressions; // For Regex (if used for message processing)
using System.Threading; // For CancellationToken, CancellationTokenSource
using System.Threading.Tasks; // For Task, Task.Run, Task.Delay, Task.CompletedTask
using TL; // Telegram API types: Update, Message, Peer, InputMedia, InputMediaPhoto, InputMediaDocument, etc.
using Polly; // For Polly resilience policies
using Polly.Retry; // For RetryPolicy
#endregion

namespace Infrastructure.Services
{
    /// <summary>
    /// Orchestrates the forwarding of messages received from the Telegram User API to various
    /// target channels based on predefined forwarding rules. It handles real-time updates,
    /// aggregates media groups, and enqueues processing jobs into Hangfire for reliable,
    /// asynchronous execution.
    /// </summary>
    public class UserApiForwardingOrchestrator
    {
        private readonly ITelegramUserApiClient _userApiClient;
        private readonly IServiceProvider _serviceProvider; // Used for dependency resolution if needed by Hangfire jobs
        private readonly ILogger<UserApiForwardingOrchestrator> _logger;
        private readonly IBackgroundJobClient _backgroundJobClient;

        // Concurrent dictionary to buffer parts of media groups until they are complete or timed out.
        // Key: Telegram's `grouped_id` (long)
        // Value: Custom buffer object holding collected media items and a CancellationTokenSource for timeout.
        private readonly ConcurrentDictionary<long, MediaGroupBuffer> _mediaGroupBuffers = new();

        // The maximum duration to wait for all parts of a media group to arrive.
        private readonly TimeSpan _mediaGroupTimeout = TimeSpan.FromSeconds(2);

        // Telegram's official limit for items in a single media group (album).
        private const int MaxMediaGroupSize = 10;

        // Polly retry policy for robustly enqueuing Hangfire jobs.
        // It specifically retries the `BackgroundJobClient.Enqueue` call itself.
        private readonly RetryPolicy<string> _enqueueRetryPolicy;

        /// <summary>
        /// Initializes a new instance of the <see cref="UserApiForwardingOrchestrator"/> class.
        /// Subscribes to the Telegram User API's custom update received event to begin message processing.
        /// </summary>
        /// <param name="userApiClient">The Telegram User API client for receiving updates.</param>
        /// <param name="serviceProvider">The service provider for resolving dependencies within the application scope.</param>
        /// <param name="backgroundJobClient">The Hangfire background job client for enqueuing tasks.</param>
        /// <param name="logger">The logger for recording application events and errors.</param>
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

            _userApiClient.OnCustomUpdateReceived += HandleUserApiUpdateAsync; // Subscribe to the update event
            _logger.LogInformation("UserApiForwardingOrchestrator initialized and subscribed to OnCustomUpdateReceived from User API.");

            // Initialize the Polly retry policy for enqueueing Hangfire jobs.
            // This policy is designed for fast, short retries because `Enqueue` should be a quick operation.
            _enqueueRetryPolicy = Policy<string> // Specifies the return type is string (Hangfire Job ID)
                .Handle<Exception>(ex =>
                {
                    // Log the exception details for transient failures during enqueueing.
                    _logger.LogWarning(ex, "Polly: Transient error occurred while attempting to enqueue Hangfire job in Orchestrator. Retrying...");
                    return true; // Retry on any exception for now; refine for specific transient exceptions if needed.
                })
                .WaitAndRetry(new[] // Fast, short retries for enqueueing operations
                {
                    TimeSpan.FromMilliseconds(50),  // First retry after 50ms
                    TimeSpan.FromMilliseconds(100), // Second retry after 100ms
                    TimeSpan.FromMilliseconds(200), // Third retry after 200ms
                    TimeSpan.FromMilliseconds(400)  // Fourth (final) retry after 400ms
                }, (exception, timeSpan, retryCount, context) =>
                {
                    // No specific action needed here beyond what `Handle` already logs.
                    // The primary logging is in the `Handle` predicate.
                    // This `onRetry` action is still useful for general logging of retry attempts.
                });
        }

        #region Media Group Buffer Internal Class
        /// <summary>
        /// Internal class to buffer information about an incomplete media group.
        /// Stores collected media items and a CancellationTokenSource for managing the group's timeout.
        /// </summary>
        private class MediaGroupBuffer
        {
            /// <summary>
            /// Gets the list of media items collected for this group, each with its caption and entities.
            /// </summary>
            public List<InputMediaWithCaption> Items { get; } = new List<InputMediaWithCaption>();

            /// <summary>
            /// Gets or sets the <see cref="CancellationTokenSource"/> used to manage the timeout
            /// for this specific media group. When a new part of the group arrives, this token
            /// is cancelled and a new one is set to extend the timeout.
            /// </summary>
            public CancellationTokenSource CancellationTokenSource { get; set; } = new CancellationTokenSource();

            /// <summary>
            /// Gets or sets the ID of the source peer (channel/chat) from which this media group originated.
            /// </summary>
            public long PeerId { get; set; }

            /// <summary>
            /// Gets or sets the message ID to which this media group replies (0 if none).
            /// (Note: Extracting `reply_to_msg_id` from TL.MessageReplyHeader requires more complex parsing.)
            /// </summary>
            public int ReplyToMsgId { get; set; }

            /// <summary>
            /// Gets or sets the original sender peer for filtering rules.
            /// </summary>
            public Peer? SenderPeer { get; set; }

            /// <summary>
            /// Gets or sets the ID of the first message received for this group.
            /// Used for logging and as a reference for the Hangfire job.
            /// </summary>
            public long OriginalMessageId { get; set; }
        }
        #endregion

        #region Event Handler: HandleUserApiUpdateAsync (FIXED SWITCH CASE & reply_to_msg_id)
        /// <summary>
        /// Handles updates received from the Telegram User API.
        /// This method processes each update in a new detached task to prevent blocking
        /// the main event handler thread, ensuring responsiveness of the Telegram client.
        /// </summary>
        /// <param name="update">The <see cref="TL.Update"/> object received from Telegram.</param>
        /// <remarks>
        /// This method uses the `_ = Task.Run(async () => ...)` pattern. This is a common
        /// "fire-and-forget" pattern for event handlers that need to perform asynchronous work
        /// without blocking the event source. While `async void` is also used for event handlers,
        /// `Task.Run` here provides an explicit offload to the thread pool for the entire
        /// processing logic, further ensuring the client's update loop remains unblocked.
        /// </remarks>
        private void HandleUserApiUpdateAsync(Update update)
        {
            // Detach the update processing to a new task to prevent blocking the Telegram client's update loop.
            // Using _ = Task.Run(...) to avoid "await" in async void for better fire-and-forget.
            _ = Task.Run(async () =>
            {
                TL.Message? messageToProcess = null;
                Peer? sourceApiPeer = null;
                long originalMessageId = 0; // The actual ID of the message received
                string messageContent = string.Empty;
                TL.MessageEntity[]? messageEntities = null;
                Peer? senderPeerForFilter = null;
                string updateTypeDebug = update?.GetType().Name ?? "NullUpdateType"; // For error/debug logs

                try
                {
                    if (update == null)
                    {
                        _logger.LogWarning("Received a null update from Telegram API. Skipping processing.");
                        return;
                    }

                    // --- FIX: Unreachable Switch Case / Order of Update Types ---
                    // Process specific update types first (e.g., channel edits before general new messages).
                    // UpdateNewChannelMessage inherits from UpdateNewMessage. So, check for it first,
                    // or combine if handling is identical.
                    // For edits, TL.Message has `edited: bool` flag or `edit_date` property.
                    // We directly process UpdateEdit* types to ensure edits trigger forwarding.
                    switch (update)
                    {
                        case UpdateNewChannelMessage uncm:
                            messageToProcess = uncm.message as TL.Message;
                            break;
                        case UpdateNewMessage unm: // This will catch UpdateNewChannelMessage if not already handled
                            messageToProcess = unm.message as TL.Message;
                            break;
                        case UpdateEditChannelMessage uecm:
                            messageToProcess = uecm.message as TL.Message;
                            break; // Process edits as new to trigger forwarding
                        case UpdateEditMessage uem:
                            messageToProcess = uem.message as TL.Message;
                            break; // Process edits as new to trigger forwarding
                        // Handle other specific update types if needed, or default to skip.
                        default:
                            _logger.LogTrace("Skipping unhandled Telegram update type: {UpdateType}", updateTypeDebug);
                            return; // Silently skip unhandled Update types (e.g., reactions, pins) for performance
                    }

                    if (messageToProcess == null)
                    {
                        _logger.LogTrace("Telegram update type {UpdateType} did not contain a TL.Message object. Skipping.", updateTypeDebug);
                        return; // Silently skip if no message was extracted
                    }

                    sourceApiPeer = messageToProcess.peer_id;
                    originalMessageId = messageToProcess.id; // Capture original message ID
                    senderPeerForFilter = messageToProcess.from_id; // Sender of the message, for filtering rules
                    messageContent = messageToProcess.message ?? string.Empty;
                    messageEntities = messageToProcess.entities?.ToArray();

                    long currentSourcePositiveId = GetPeerIdValue(sourceApiPeer);

                    // --- Early Filtering for Performance ---
                    // Filter out direct user messages or messages from bots early if the orchestrator is meant for channels/groups only.
                    if (sourceApiPeer is PeerUser userPeer && userPeer.user_id > 0) // Check for PeerUser and valid ID
                    {
                        // You might also check if userPeer.bot is true to ignore bots
                        _logger.LogTrace("Skipping message {OriginalMessageId} from direct user/bot {UserId}. Orchestrator focuses on channel/chat updates.", originalMessageId, userPeer.user_id);
                        return; // Skip if source is a user (assuming forwarding from channels/groups only)
                    }

                    if (currentSourcePositiveId == 0)
                    {
                        _logger.LogWarning("Could not extract a valid positive Peer ID from source: {SourcePeerType}. Skipping message {OriginalMessageId}.", sourceApiPeer?.GetType().Name ?? "Null", originalMessageId);
                        return; // Silent skip if source Peer ID is invalid or cannot be extracted.
                    }

                    // --- Media Group Aggregation Logic (Reliability and Efficiency) ---
                    // Process as a media group only if a 'grouped_id' exists AND media is present.
                    // This logic guarantees atomic forwarding of full albums.
                    if (messageToProcess.grouped_id != 0 && messageToProcess.media != null)
                    {
                        long mediaGroupId = messageToProcess.grouped_id;

                        InputMedia? preparedMedia = CreateInputMedia(messageToProcess.media);
                        if (preparedMedia == null)
                        {
                            _logger.LogWarning("Unsupported media type for grouped message {OriginalMessageId} (Group ID: {GroupId}). Skipping this media item.", originalMessageId, mediaGroupId);
                            return; // Skip if media type is not supported
                        }

                        InputMediaWithCaption currentMediaItem = new InputMediaWithCaption
                        {
                            Media = preparedMedia,
                            Caption = messageToProcess.message,
                            Entities = messageToProcess.entities?.ToArray()
                        };

                        // Atomically get or add the MediaGroupBuffer for this unique media group ID.
                        // All parts of the same album will land in the same buffer.
                        // The `AddOrUpdate` pattern with `GetOrAdd` in a ConcurrentDictionary is crucial for thread safety.
                        MediaGroupBuffer buffer = _mediaGroupBuffers.GetOrAdd(mediaGroupId, (id) =>
                        {
                            _logger.LogDebug("Creating new media group buffer for Group ID {GroupId} (first message ID: {OriginalMessageId}).", mediaGroupId, messageToProcess.id);
                            return new MediaGroupBuffer
                            {
                                PeerId = currentSourcePositiveId,
                                // --- FIX: Accessing reply_to_msg_id safely ---
                                // messageToProcess.reply_to is MessageReplyHeaderBase. We need to cast to MessageReplyHeader.
                                ReplyToMsgId = (messageToProcess.reply_to as MessageReplyHeader)?.reply_to_msg_id ?? 0,
                                SenderPeer = senderPeerForFilter,
                                OriginalMessageId = messageToProcess.id // Stores ID of the very first message encountered for this group
                            };
                        });

                        // Protect shared list access and CancellationTokenSource management with a lock
                        // The lock ensures that only one thread modifies the buffer at a time for a given mediaGroupId.
                        lock (buffer)
                        {
                            buffer.Items.Add(currentMediaItem);
                            _logger.LogTrace("Added item to media group {GroupId}. Current items: {ItemCount}/{MaxSize}.", mediaGroupId, buffer.Items.Count, MaxMediaGroupSize);

                            if (buffer.Items.Count >= MaxMediaGroupSize)
                            {
                                _logger.LogDebug("Media group {GroupId} reached max size {MaxSize}. Triggering processing immediately.", mediaGroupId, MaxMediaGroupSize);
                                buffer.CancellationTokenSource.Cancel(); // Force cancellation to trigger processing
                            }
                            else
                            {
                                // Reset the timeout for the group: a new message means extend the wait time.
                                // Cancel the old token source and create a new one to effectively reset the timer.
                                buffer.CancellationTokenSource.Cancel();
                                buffer.CancellationTokenSource.Dispose(); // Dispose the old one to free resources
                                buffer.CancellationTokenSource = new CancellationTokenSource(); // Create a brand new CTS

                                // Schedule new timeout task without awaiting to keep the handler non-blocking.
                                // The _ = prefix prevents a compiler warning for unawaited tasks in async void methods.
                                _ = ProcessMediaGroupAfterDelay(mediaGroupId, buffer.CancellationTokenSource.Token).ConfigureAwait(false);
                            }
                        }
                    }
                    else // Not a media group OR no media. Enqueue as a single job immediately for fastest processing.
                    {
                        List<InputMediaWithCaption>? singleMediaList = null;
                        if (messageToProcess.media != null)
                        {
                            InputMedia? singlePreparedMedia = CreateInputMedia(messageToProcess.media);
                            if (singlePreparedMedia != null)
                            {
                                singleMediaList = new List<InputMediaWithCaption> // Encapsulate single media in a list
                                {
                                    new InputMediaWithCaption {
                                        Media = singlePreparedMedia,
                                        Caption = messageToProcess.message,
                                        Entities = messageToProcess.entities?.ToArray()
                                    }
                                };
                            }
                            else
                            {
                                _logger.LogWarning("Unsupported media type for single message {OriginalMessageId}. Skipping media item.", originalMessageId);
                                // If media is unsupported, clear singleMediaList, message might still have text content
                                singleMediaList = null;
                            }
                        }

                        // Enqueue the job for processing single message or text-only.
                        EnqueueForwardingJob(
                            currentSourcePositiveId,
                            originalMessageId,
                            currentSourcePositiveId, // rawApiPeerId for message retrieval will be same as source
                            messageContent,
                            messageEntities,
                            senderPeerForFilter,
                            singleMediaList);

                        _logger.LogInformation("Enqueued single message job for MsgID {OriginalMessageId} (Source {SourceId}). Content preview: '{ContentPreview}'",
                            originalMessageId, currentSourcePositiveId, TruncateString(messageContent, 50));
                    }
                }
                catch (Exception ex) // Catch-all for orchestration failures, must log critical errors
                {
                    _logger.LogCritical(ex, "ORCHESTRATOR_TASK_FATAL_ERROR: Unhandled exception in main update processing loop for UpdateType: {UpdateType}, MsgID: {MsgId}. Check data integrity or Telegram client health.",
                                     updateTypeDebug, originalMessageId);
                    // No re-throw, as this is an event handler in a Task.Run context; just log.
                }
            });
        }
        #endregion

        #region Private Helper Methods (FIXED InputMedia ID & other minor refinements)
        /// <summary>
        /// Manages the time window for collecting all parts of a media group.
        /// Once the timeout expires or the group reaches its maximum size,
        /// the collected items are enqueued for processing.
        /// </summary>
        /// <param name="mediaGroupId">The unique ID of the media group.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to cancel the delay if a new item arrives.</param>
        private async Task ProcessMediaGroupAfterDelay(long mediaGroupId, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogDebug("Scheduling media group processing for Group ID {GroupId} with timeout of {Timeout}ms.", mediaGroupId, _mediaGroupTimeout.TotalMilliseconds);
                // Await Task.Delay. If cancellation occurs, OperationCanceledException is thrown.
                await Task.Delay(_mediaGroupTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // This is an expected cancellation: either a new item arrived, max size was reached, or app is shutting down.
                _logger.LogDebug("Media group processing delay for Group ID {GroupId} was cancelled. Not triggering processing from this instance.", mediaGroupId);
                return; // Do not process this instance, a newer timer is running or it was forcibly triggered.
            }
            catch (Exception ex) // Catch unexpected errors during delay
            {
                _logger.LogError(ex, "ORCHESTRATOR_TASK_ERROR: Unexpected error during media group delay for Group ID {GroupId}.", mediaGroupId);
                return;
            }

            // After the delay, attempt to process the collected group.
            _logger.LogDebug("Attempting to process media group {GroupId} after timeout.", mediaGroupId);
            // Atomically remove from buffer to ensure only one task processes it.
            if (_mediaGroupBuffers.TryRemove(mediaGroupId, out MediaGroupBuffer? buffer))
            {
                // Find the first valid media item in the buffer to use its caption as the album's main caption.
                // Telegram uses the caption of the first media item for the entire album.
                // --- FIX: Proper sorting by `media.id` from InputMediaPhoto/Document ---
                // The `id` property is on the *specific* InputPhoto/InputDocument types, not InputMedia base.
                // We need to safely cast and access.
                var firstMediaItem = buffer.Items
                    .OrderBy(item => GetInputMediaId(item.Media)) // Use helper to get sortable ID
                    .FirstOrDefault(item => item.Media != null);

                if (firstMediaItem != null && buffer.Items.Any(item => item.Media != null)) // Ensure buffer is not empty of valid media
                {
                    string albumOverallCaption = firstMediaItem.Caption ?? string.Empty;
                    TL.MessageEntity[]? albumOverallEntities = firstMediaItem.Entities;

                    _logger.LogInformation("Enqueuing media group job for Group ID {GroupId} with {ItemCount} items. Album Caption: '{AlbumCaptionPreview}'",
                        mediaGroupId, buffer.Items.Count(item => item.Media != null), TruncateString(albumOverallCaption, 50));

                    EnqueueForwardingJob(
                        buffer.PeerId,
                        buffer.OriginalMessageId, // The ID of the first message of the group
                        buffer.PeerId, // Source peer is the same for all messages in the group
                        albumOverallCaption, // Pass the overall album caption
                        albumOverallEntities, // Pass the overall album entities
                        buffer.SenderPeer,
                        buffer.Items.Where(item => item.Media != null).ToList()); // Pass the filtered list of valid media items
                }
                else
                {
                    _logger.LogError("ORCHESTRATOR_TASK_ERROR: Media group {GroupId} was triggered for processing but contained no valid media items. Original message ID: {OriginalMsgId}.", mediaGroupId, buffer.OriginalMessageId);
                }
            }
            else
            {
                // This 'else' indicates that another task (e.g., max size reached) already processed and removed the buffer.
                _logger.LogTrace("Media group {GroupId} was already removed from buffer. Skipping processing from this timer instance.", mediaGroupId);
            }
        }

        /// <summary>
        /// Converts a Telegram <see cref="TL.MessageMedia"/> object to an <see cref="TL.InputMedia"/> object
        /// suitable for sending via the Telegram API.
        /// </summary>
        /// <param name="media">The <see cref="TL.MessageMedia"/> object from the received message.</param>
        /// <returns>An <see cref="TL.InputMedia"/> object if the media type is supported; otherwise, <see langword="null"/>.</returns>
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
                _ => null // Return null for unsupported media types (e.g., stickers, voice notes, gifs without document, etc.)
            };
        }

        /// <summary>
        /// Enqueues the core message forwarding work into Hangfire for reliable background execution.
        /// This method is non-blocking and optimized for speed by leveraging Hangfire's queuing mechanism.
        /// </summary>
        /// <param name="sourceIdForMatchingRules">The ID of the source channel/chat used for matching forwarding rules.</param>
        /// <param name="originalMessageId">The original message ID from Telegram.</param>
        /// <param name="rawApiPeerId">The raw peer ID of the source for Telegram API calls (e.g., to fetch message details).</param>
        /// <param name="messageContent">The text content of the message (caption for media).</param>
        /// <param name="messageEntities">The entities (formatting) of the message content.</param>
        /// <param name="senderPeerForFilter">The original sender peer for filtering rules.</param>
        /// <param name="mediaItems">A list of media items if it's a media message or a media group.</param>
        private void EnqueueForwardingJob(
            long sourceIdForMatchingRules,
            long originalMessageId,
            long rawApiPeerId,
            string messageContent,
            TL.MessageEntity[]? messageEntities,
            Peer? senderPeerForFilter,
            List<InputMediaWithCaption>? mediaItems)
        {
            // Use the Polly retry policy to make the enqueue operation itself robust.
            // _backgroundJobClient.Enqueue is generally very fast and non-blocking,
            // but this adds a layer of resilience if Hangfire's storage is temporarily unavailable.
            _enqueueRetryPolicy.Execute(() =>
            {
                // BackgroundJob.Enqueue immediately adds the job to the database queue with minimal overhead.
                // Hangfire takes over from here (worker picks it up, retries if fails).
                return _backgroundJobClient.Enqueue<IForwardingService>(service =>
                    service.ProcessMessageAsync(
                        sourceIdForMatchingRules,
                        originalMessageId,
                        rawApiPeerId,
                        messageContent,
                        messageEntities,
                        senderPeerForFilter,
                        mediaItems,
                        CancellationToken.None // Hangfire manages CancellationToken for jobs; pass None from enqueue point
                    ));
            });
            // Removed LogInformation for speed, rely on Hangfire Dashboard for job status and IDs.
            // The logs within ProcessMessageAsync itself and its sub-components will indicate success/failure.
        }

        /// <summary>
        /// Helper function to truncate strings for logging purposes.
        /// </summary>
        /// <param name="str">The input string.</param>
        /// <param name="maxLength">The maximum desired length for the truncated string.</param>
        /// <returns>The truncated string or an indicator for null/empty input.</returns>
        private string TruncateString(string? str, int maxLength)
        {
            if (string.IsNullOrEmpty(str)) return "[null_or_empty]";
            return str.Length <= maxLength ? str : str[..maxLength] + "..."; // Using C# 8.0 range operator for brevity
        }

        /// <summary>
        /// Extracts the positive numerical ID from various Telegram Peer types.
        /// </summary>
        /// <param name="peer">The Telegram Peer object.</param>
        /// <returns>The positive numerical ID of the peer, or 0 if the peer type is unrecognized or has no valid ID.</returns>
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

        /// <summary>
        /// Helper method to safely extract the numerical ID from a TL.InputMedia object.
        /// Used for sorting media group items to maintain original order.
        /// </summary>
        /// <param name="inputMedia">The InputMedia object.</param>
        /// <returns>The numerical ID (long) of the media, or 0 if it's null or an unrecognized type.</returns>
        private long GetInputMediaId(InputMedia? inputMedia)
        {
            return inputMedia switch
            {
                InputMediaPhoto imp when imp.id is InputPhoto ip => ip.id,
                InputMediaDocument imd when imd.id is InputDocument idoc => idoc.id,
                _ => 0
            };
        }
        #endregion
    }
}