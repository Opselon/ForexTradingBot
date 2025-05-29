// File: Infrastructure/Services/UserApiForwardingOrchestrator.cs
using Application.Common.Interfaces; // For ITelegramUserApiClient
using Application.Features.Forwarding.Interfaces;
// using Domain.Features.Forwarding.Entities; // Assuming used by IForwardingService
// Hangfire - Update, Message, Peer are types from TLSharp or TeleSharp library,
// not Hangfire directly, but they are used within the Hangfire job context.
using Hangfire;
using Microsoft.Extensions.Logging;
using TL;

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

        /// <summary>
        /// متد هندل کننده رویداد دریافت آپدیت از User API.
        /// هر آپدیت را در یک تسک جدید پردازش می‌کند تا از بلاک شدن هندلر رویداد اصلی جلوگیری شود.
        /// از الگوی `async void` برای هندلر رویداد استفاده شده است.
        /// </summary>
        /// <param name="update">آبجکت Update دریافت شده از تلگرام.</param>
        // Retaining `async void` as this is a common pattern for event handlers.
        private void HandleUserApiUpdateAsync(Update update)
        {
            // اجرای پردازش آپدیت در یک تسک پس‌زمینه جداگانه.
            _ = Task.Run(async () =>
            {

                TL.Message? messageToProcess = null;
                Peer? sourceApiPeer = null;
                long messageIdForLog = 0;
                string messageContent = string.Empty;
                TL.MessageEntity[]? messageEntities = null;
                Peer? senderPeerForFilter = null;
                // متغیر برای ذخیره اطلاعات مدیا گروه‌بندی شده.

                // CHANGED: From InputMedia? to List<InputMediaWithCaption>?
                List<InputMediaWithCaption>? mediaGroupItems = null;
                // متغیر برای نوع آپدیت جهت لاگ‌برداری.

                string updateTypeForLog = update?.GetType().Name ?? "NullUpdateType";
                string messageContentPreview = "[N/A]";

                try
                {
                    // بررسی نال بودن آپدیت.
                    if (update == null)
                    {
                        _logger.LogWarning("ORCHESTRATOR_TASK: Received null update. Skipping.");
                        // لاگ هشدار و خروج در صورت نال بودن.

                        return;
                    }

                    if (update is UpdateNewMessage unm)
                    {
                        messageToProcess = unm.message as TL.Message;
                    }
                    else if (update is UpdateNewChannelMessage uncm)
                    {
                        messageToProcess = uncm.message as TL.Message;
                    }
                    // ADDED: Handling for edited messages. Adjust as needed if you don't want to process edits.
                    else if (update is UpdateEditMessage uem)
                    {
                        messageToProcess = uem.message as TL.Message;
                        _logger.LogInformation("ORCHESTRATOR_TASK: Received UpdateEditMessage for MsgID {MsgId}.", messageToProcess?.id ?? 0);
                        // لاگ اطلاعاتی برای آپدیت پیام ویرایش شده.

                    }
                    else if (update is UpdateEditChannelMessage uecm)
                    {
                        messageToProcess = uecm.message as TL.Message;
                        _logger.LogInformation("ORCHESTRATOR_TASK: Received UpdateEditChannelMessage for MsgID {MsgId}.", messageToProcess?.id ?? 0);
                        // لاگ اطلاعاتی برای آپدیت پیام ویرایش شده در کانال.
                    }
                    // END ADDED
                    else
                    {
                        _logger.LogDebug("ORCHESTRATOR_TASK: Received unhandled update type: {UpdateType}. Skipping.", updateTypeForLog);
                        return;
                    }

                    // بررسی نال بودن پیام استخراج شده.
                    if (messageToProcess == null)
                    {
                        _logger.LogWarning("ORCHESTRATOR_TASK: Processed update type {UpdateType} but messageToProcess is null. Skipping.", updateTypeForLog);
                        return;
                    }

                    sourceApiPeer = messageToProcess.peer_id;
                    // استخراج شناسه پیام برای لاگ.
                    messageIdForLog = messageToProcess.id;
                    // استخراج فرستنده پیام برای فیلتر کردن احتمالی.
                    senderPeerForFilter = messageToProcess.from_id;
                    // استخراج محتوا و انتیتی‌های پیام.
                    messageContent = messageToProcess.message ?? string.Empty;
                    messageEntities = messageToProcess.entities?.ToArray();
                    // بررسی وجود مدیا در پیام.
                    // CHANGED LOGIC: Prepare InputMedia and wrap it in a List<InputMediaWithCaption>
                    if (messageToProcess.media != null)
                    {
                        _logger.LogTrace("ORCHESTRATOR_TASK: Detected media in message {MsgId}. Attempting to prepare InputMedia.", messageToProcess.id);
                        InputMedia? preparedMedia = null;

                        if (messageToProcess.media is MessageMediaPhoto mmp && mmp.photo is Photo p)
                        // پردازش مدیا از نوع عکس.
                        {
                            preparedMedia = new InputMediaPhoto
                            {
                                id = new InputPhoto { id = p.id, access_hash = p.access_hash, file_reference = p.file_reference ?? Array.Empty<byte>() }
                            };
                            _logger.LogTrace("ORCHESTRATOR_TASK: Prepared InputMediaPhoto for MsgID {MsgId}. Photo ID: {PhotoId}", messageToProcess.id, p.id);
                        }
                        else if (messageToProcess.media is MessageMediaDocument mmd && mmd.document is Document d)
                        {
                            // پردازش مدیا از نوع سند (ویدئو، فایل و ...).
                            preparedMedia = new InputMediaDocument
                            {
                                id = new InputDocument { id = d.id, access_hash = d.access_hash, file_reference = d.file_reference ?? Array.Empty<byte>() }
                            };
                            _logger.LogTrace("ORCHESTRATOR_TASK: Prepared InputMediaDocument for MsgID {MsgId}. Document ID: {DocumentId}", messageToProcess.id, d.id);
                        }
                        else
                        {
                            _logger.LogWarning("ORCHESTRATOR_TASK: Unsupported media type {MediaType} in message {MsgId}. Cannot prepare InputMedia.", messageToProcess.media.GetType().Name, messageToProcess.id);
                        }

                        // بسته‌بندی مدیا و کپشن در لیست InputMediaWithCaption.
                        if (preparedMedia != null)
                        {
                            // Wrap the single prepared media in a List<InputMediaWithCaption>
                            mediaGroupItems = new List<InputMediaWithCaption>
                            {
                                new InputMediaWithCaption
                                {

                                    Media = preparedMedia,
                                    Caption = messageToProcess.message, // Original caption from the message
                                    Entities = messageToProcess.entities?.ToArray() // Original entities from the message
                                    
                                }
                            };
                            // IMPORTANT NOTE ON MEDIA ALBUMS:
                            // The current orchestrator is processing each UpdateNewMessage/UpdateNewChannelMessage separately.
                            // If Telegram sends a media album (multiple photos/videos in one group), it sends *multiple*
                            // UpdateNewMessage/UpdateNewChannelMessage updates, each with a `media_group_id`.
                            // To send them as a single album in the target channel, you MUST implement a caching/aggregation
                            // mechanism here (e.g., using ConcurrentDictionary and a timer) to collect all messages
                            // with the same `media_group_id` before enqueuing a *single* Hangfire job for the entire group.
                            // Otherwise, each media item from an album will be sent as a separate message.
                        }
                    }
                    // پایان منطق تغییر یافته برای آماده‌سازی مدیا.
                    // END CHANGED LOGIC FOR MEDIA PREPARATION

                    messageContentPreview = TruncateString(messageContent, 50);
                    // لاگ اطلاعات پیام استخراج شده.
                    _logger.LogInformation("ORCHESTRATOR_TASK: Extracted new message. MsgID: {MsgId}, SourcePeerType: {PeerType}, SourcePeerIDValue: {PeerIdValue}. Message Content Preview: '{MsgContentPreview}'. Has Media: {HasMedia}. Sender Peer: {SenderPeer}",
                        messageToProcess.id, sourceApiPeer?.GetType().Name, GetPeerIdValue(sourceApiPeer), messageContentPreview, messageToProcess.media != null, senderPeerForFilter?.ToString() ?? "N/A");

                    // Filter out messages from PeerUser (direct user messages) if you don't want to forward them.
                    if (sourceApiPeer is PeerUser userPeer)
                    {
                        _logger.LogDebug("ORCHESTRATOR_TASK: Message from PeerUser (direct user message). UserID: {UserId}. SKIPPING automatic forwarding enqueue.", userPeer.user_id);
                        return;
                    }

                    // گرفتن شناسه عددی و مثبت Peer منبع.
                    long currentSourcePositiveId = GetPeerIdValue(sourceApiPeer);

                    if (currentSourcePositiveId == 0)
                    {
                        _logger.LogWarning("ORCHESTRATOR_TASK: Could not determine a valid positive source ID from Peer: {PeerString} for Hangfire enqueue. SKIPPING.", sourceApiPeer.ToString());
                        return;
                    }

                    // For rule matching, if your DB stores channel IDs as negative (-100xxxx),
                    // you would convert currentSourcePositiveId back.
                    // شناسه‌های مورد نیاز برای تطابق قوانین و ارسال به API.
                    // Assuming GetRulesBySourceChannelAsync expects the *positive* ID, like currentSourcePositiveId.
                    long sourceIdForMatchingRules = currentSourcePositiveId;
                    long rawSourcePeerIdForApi = currentSourcePositiveId;

                    _logger.LogInformation("ORCHESTRATOR_TASK: Enqueuing to Hangfire IForwardingService.ProcessMessageAsync for MsgID: {MsgId}. RuleMatchingID: {RuleMatchID}, RawApiPeerID (Positive): {RawApiPeerID}. Content Preview: '{ContentPreview}'. Has Media Group: {HasMediaGroup}. Sender Peer: {SenderPeer}",
                                           messageToProcess.id, sourceIdForMatchingRules, rawSourcePeerIdForApi, messageContentPreview, mediaGroupItems != null && mediaGroupItems.Any(), senderPeerForFilter?.ToString() ?? "N/A");

                    // CHANGED: Pass mediaGroupItems instead of inputMediaForJob
                    // صف‌بندی وظیفه پردازش پیام در Hangfire.
                    _backgroundJobClient.Enqueue<IForwardingService>(service =>
                        service.ProcessMessageAsync(
                            sourceIdForMatchingRules,
                            messageToProcess.id,
                            rawSourcePeerIdForApi,
                            messageContent,
                            messageEntities,
                            senderPeerForFilter,
                            mediaGroupItems, // CHANGED: Pass the list of media items
                            CancellationToken.None
                        ));
                    // END CHANGED

                    // لاگ موفقیت‌آمیز بودن صف‌بندی.
                    _logger.LogInformation("ORCHESTRATOR_TASK: Successfully enqueued job to Hangfire for IForwardingService.ProcessMessageAsync. MsgID: {MsgId}.", messageToProcess.id);
                }
                catch (Exception ex)
                {
                    // مدیریت استثناها در حین پردازش آپدیت یا صف‌بندی.
                    // اطلاعات برای لاگ‌برداری در صورت بروز خطا.
                    string finalMessageIdForLog = messageIdForLog != 0 ? messageIdForLog.ToString() : "N/A";
                    string finalUpdateTypeForLog = updateTypeForLog ?? "N/A";
                    string finalMessageContentPreview = messageContentPreview ?? "N/A";

                    _logger.LogCritical(ex, "ORCHESTRATOR_TASK: CRITICAL EXCEPTION during Hangfire Enqueue for MsgID: {MsgId}. UpdateType: {UpdateType}. Message Content: '{MsgContentPreview}'.",
                                       finalMessageIdForLog, finalUpdateTypeForLog, finalMessageContentPreview);
                }
            });

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