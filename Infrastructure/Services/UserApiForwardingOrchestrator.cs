// File: Infrastructure/Services/UserApiForwardingOrchestrator.cs
using Application.Common.Interfaces; // For ITelegramUserApiClient
using Application.Features.Forwarding.Interfaces;
// using Domain.Features.Forwarding.Entities; // Assuming used by IForwardingService
// Hangfire - Update, Message, Peer are types from TLSharp or TeleSharp library,
// not Hangfire directly, but they are used within the Hangfire job context.
using Hangfire;
using Microsoft.Extensions.Logging;
using TL;
using System.Collections.Concurrent;

namespace Infrastructure.Services
{
    public class UserApiForwardingOrchestrator
    {
        // سرویس کلاینت تلگرام یوزر ای‌پی‌آی برای دریافت آپدیت‌ها.
        private readonly ITelegramUserApiClient _userApiClient;
        // ارائه دهنده سرویس برای حل وابستگی‌ها.
        private readonly IServiceProvider _serviceProvider;
        // لاگر برای ثبت وقایع و خطاها.
        private readonly ILogger<UserApiForwardingOrchestrator> _logger;
        // کلاینت بک‌گراند جاب برای صف‌بندی وظایف.
        private readonly IBackgroundJobClient _backgroundJobClient;

        // NEW FIELDS FOR MEDIA GROUP HANDLING:
        private readonly ConcurrentDictionary<long, MediaGroupBuffer> _mediaGroupBuffers = new();
        private readonly TimeSpan _mediaGroupTimeout = TimeSpan.FromSeconds(2); // Wait 2 seconds for all parts of a media group
        private const int MaxMediaGroupSize = 10; // Telegram limit is typically 10 items in a single media group
        /// <summary>
        /// سازنده کلاس UserApiForwardingOrchestrator.
        /// وابستگی‌ها را تزریق کرده و در رویداد دریافت آپدیت سفارشی از User API مشترک می‌شود.
        /// </summary>
        // پارامترهای سازنده.
        public UserApiForwardingOrchestrator(
            ITelegramUserApiClient userApiClient,
            IServiceProvider serviceProvider,
            IBackgroundJobClient backgroundJobClient,
            ILogger<UserApiForwardingOrchestrator> logger)
        {
            // بررسی نال بودن و تزریق وابستگی‌ها.
            _backgroundJobClient = backgroundJobClient ?? throw new ArgumentNullException(nameof(backgroundJobClient));
            _userApiClient = userApiClient ?? throw new ArgumentNullException(nameof(userApiClient));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _userApiClient.OnCustomUpdateReceived += HandleUserApiUpdateAsync; // Subscribing event handler
            _logger.LogInformation("UserApiForwardingOrchestrator initialized and subscribed to OnCustomUpdateReceived from User API.");
            // لاگ ثبت اشتراک در رویداد.
        }
        // NEW CLASS: Buffer to hold information about an incomplete media group
        public class MediaGroupBuffer
        {
            public List<InputMediaWithCaption> Items { get; } = new List<InputMediaWithCaption>();
            // CancellationTokenSource to manage the timeout for this specific media group
            public CancellationTokenSource CancellationTokenSource { get; set; } = new CancellationTokenSource();
            public long PeerId { get; set; } // The ID of the source peer for this media group
            public int ReplyToMsgId { get; set; } // The message ID this group replies to (0 if none)
            public Peer? SenderPeer { get; set; } // The original sender peer for filtering (needed for filtering rules)
            public long OriginalMessageId { get; set; } // Store the ID of the first message received for the group for logging/Hangfire job ID context
        }
        /// <summary>
        /// متد هندل کننده رویداد دریافت آپدیت از User API.
        /// هر آپدیت را در یک تسک جدید پردازش می‌کند تا از بلاک شدن هندلر رویداد اصلی جلوگیری شود.
        /// از الگوی `async void` برای هندلر رویداد استفاده شده است.
        /// </summary>
        /// <param name="update">آبجکت Update دریافت شده از تلگرام.</param>
        // Retaining `async void` as this is a common pattern for event handlers.



        private void HandleUserApiUpdateAsync(Update update)
        {
            _ = Task.Run(async () => // Detached task to prevent deadlocks or blocking
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
                    if (update == null) return; // Silent skip for null updates

                    // Extract the TL.Message object from various update types
                    // IMPORTANT: TelegramUserApiClient.HandleUpdatesBaseAsync is responsible for creating a robust
                    // TL.Message object for UpdateShortMessage and UpdateShortChatMessage, ensuring .media and .grouped_id are present if applicable.
                    if (update is UpdateNewMessage unm) messageToProcess = unm.message as TL.Message;
                    else if (update is UpdateNewChannelMessage uncm) messageToProcess = uncm.message as TL.Message;
                    else if (update is UpdateEditMessage uem) messageToProcess = uem.message as TL.Message; // Treat edits as new to trigger forwarding
                    else if (update is UpdateEditChannelMessage uecm) messageToProcess = uecm.message as TL.Message; // Treat edits as new to trigger forwarding
                    else return; // Silently skip unhandled Update types (e.g., updates for reactions, pins, etc.)

                    if (messageToProcess == null) return; // Silently skip if no message was extracted

                    sourceApiPeer = messageToProcess.peer_id;
                    originalMessageId = messageToProcess.id; // Capture original message ID
                    senderPeerForFilter = messageToProcess.from_id;
                    messageContent = messageToProcess.message ?? string.Empty;
                    messageEntities = messageToProcess.entities?.ToArray();

                    // Filter out direct user messages or bots early (configurable, assumed for "channels only" scenario)
                    // No log: optimizing for speed.
                    if (sourceApiPeer is PeerUser) return;

                    long currentSourcePositiveId = GetPeerIdValue(sourceApiPeer);
                    if (currentSourcePositiveId == 0) return; // Silent skip if source Peer ID is invalid or cannot be extracted.

                    // --- Powerful Media Group Aggregation Logic (Reliability and Efficiency) ---
                    // Process as a media group only if a 'grouped_id' exists AND media is present.
                    // This logic guarantees atomic forwarding of full albums.
                    if (messageToProcess.grouped_id != 0 && messageToProcess.media != null)
                    {
                        long mediaGroupId = messageToProcess.grouped_id;

                        InputMedia? preparedMedia = CreateInputMedia(messageToProcess.media);
                        if (preparedMedia == null) return; // Silent skip if media type is not supported

                        InputMediaWithCaption currentMediaItem = new InputMediaWithCaption // Using shared DTO
                        {
                            Media = preparedMedia,
                            Caption = messageToProcess.message,
                            Entities = messageToProcess.entities?.ToArray()
                        };

                        // Atomically get or add the MediaGroupBuffer for this unique media group ID.
                        // All parts of the same album will land in the same buffer.
                        MediaGroupBuffer buffer = _mediaGroupBuffers.GetOrAdd(mediaGroupId, _ =>
                        {
                            // Initialize buffer only if this is the *first* message received for this group
                            return new MediaGroupBuffer
                            {
                                PeerId = currentSourcePositiveId,
                                ReplyToMsgId = 0, // Reply_to_msg_id needs careful extraction from TL.MessageReplyHeader or set to 0. Simplest for compilation now.
                                SenderPeer = senderPeerForFilter,
                                OriginalMessageId = messageToProcess.id // Stores ID of the very first message encountered for this group
                            };
                        });

                        lock (buffer.Items) // Protect shared list access for adding items
                        {
                            buffer.Items.Add(currentMediaItem);
                            if (buffer.Items.Count >= MaxMediaGroupSize)
                            {
                                // If max size reached, trigger processing immediately to avoid waiting for timeout
                                buffer.CancellationTokenSource.Cancel();
                            }
                            else
                            {
                                // Reset the timeout for the group: new message means extend the wait time.
                                buffer.CancellationTokenSource.Cancel();
                                buffer.CancellationTokenSource.Dispose();
                                buffer.CancellationTokenSource = new CancellationTokenSource();
                                _ = ProcessMediaGroupAfterDelay(mediaGroupId, buffer.CancellationTokenSource.Token); // Schedule new timeout task
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


        // Manages the time window for collecting all parts of a media group.
        private async Task ProcessMediaGroupAfterDelay(long mediaGroupId, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(_mediaGroupTimeout, cancellationToken); // Wait for remaining group parts or until cancelled
            }
            catch (OperationCanceledException)
            {
                // This is expected: a new item arrived, max size was reached, or app is shutting down.
                return; // Do not process this instance, a newer timer is running or it was forcibly triggered.
            }
            catch (Exception ex) // Catch unexpected errors during delay
            {
                _logger.LogError(ex, "ORCHESTRATOR_TASK_ERROR: Unexpected error during media group delay for Group ID {GroupId}.", mediaGroupId);
                return;
            }

            // Timeout occurred; process the collected group (should be complete or maximum size now).
            if (_mediaGroupBuffers.TryRemove(mediaGroupId, out MediaGroupBuffer? buffer)) // Atomically remove from buffer
            {
                if (buffer.Items.Any()) // Ensure buffer is not empty before enqueueing
                {
                    EnqueueForwardingJob(
                        buffer.PeerId,
                        buffer.OriginalMessageId, // The ID of the first message of the group
                        buffer.PeerId, // Source peer is the same
                        "", // Album captions are handled within individual InputMediaWithCaption items
                        null,
                        buffer.SenderPeer,
                        buffer.Items); // Pass the entire aggregated list of media items
                }
                else // Log if group was empty despite trigger (e.g. all media types unsupported)
                {
                    _logger.LogError("ORCHESTRATOR_TASK_ERROR: Media group {GroupId} was triggered for processing but contained no valid media items. Original message ID: {OriginalMsgId}.", mediaGroupId, buffer.OriginalMessageId);
                }
            }
            // No need for 'else' here if TryRemove failed, another process likely got it first or it never existed.
        }


        // Helper to convert Telegram's MessageMedia object to an InputMedia for sending.
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
                _ => null // Return null for unsupported media types
            };
        }


        // Enqueues the core processing work to Hangfire for reliable background execution and retries.
        // It's non-blocking and optimized for speed.
        private void EnqueueForwardingJob(
            long sourceIdForMatchingRules,
            long originalMessageId,
            long rawApiPeerId,
            string messageContent,
            TL.MessageEntity[]? messageEntities,
            Peer? senderPeerForFilter,
            List<InputMediaWithCaption>? mediaItems) // Uses shared DTO
        {
            // BackgroundJob.Enqueue immediately adds the job to the database queue.
            // Hangfire takes over from here (worker picks it up, retries if fails).
            _backgroundJobClient.Enqueue<IForwardingService>(service =>
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
            // Removed LogInformation for speed, rely on Hangfire Dashboard for job status and IDs.
        }


        // Helper function to truncate strings for logging
        /// <summary>
        /// یک رشته را برای نمایش در لاگ کوتاه می‌کند.
        /// </summary>
        /// <param name="str">رشته ورودی.</param>
        /// <param name="maxLength">حداکثر طول مجاز برای رشته.</param>
        /// <returns>رشته کوتاه شده یا نشانگر نال/خالی بودن.</returns>
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