// File: TelegramPanel/Infrastructure/TelegramBotService.cs
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;  // ✅ برای ApiRequestException
using Telegram.Bot.Polling; // ✅ برای IUpdateHandler, DefaultUpdateHandlerOptions
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums; // ✅ برای UpdateType
using TelegramPanel.Queue;
using TelegramPanel.Settings;

namespace TelegramPanel.Infrastructure
{
    public class TelegramBotService : IHostedService, IUpdateHandler // ✅ پیاده‌سازی IUpdateHandler
    {
        private readonly ILogger<TelegramBotService> _logger;
        private readonly ITelegramBotClient _botClient;
        private readonly TelegramPanelSettings _settings;
        private readonly ITelegramUpdateChannel _updateChannel;
        private CancellationTokenSource? _cancellationTokenSourceForPolling; // جداگانه برای Polling

        public TelegramBotService(
            ILogger<TelegramBotService> logger,
            ITelegramBotClient botClient,
            IOptions<TelegramPanelSettings> settingsOptions,
            ITelegramUpdateChannel updateChannel)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
            _settings = settingsOptions?.Value ?? throw new ArgumentNullException(nameof(settingsOptions));
            _updateChannel = updateChannel ?? throw new ArgumentNullException(nameof(updateChannel));
        }

        public async Task StartAsync(CancellationToken hostCancellationToken)
        {
            _cancellationTokenSourceForPolling = CancellationTokenSource.CreateLinkedTokenSource(hostCancellationToken);
            User? me = null; // برای نگهداری اطلاعات ربات

            #region Bot Information Retrieval
            try
            {
                _logger.LogInformation("Attempting to connect to Telegram and get bot information...");
                me = await _botClient.GetMe(cancellationToken: _cancellationTokenSourceForPolling.Token);
                _logger.LogInformation("Successfully connected. Bot Service starting for: {BotUsername} (ID: {BotId})", me.Username, me.Id);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Bot service startup was canceled during GetMeAsync.");
                return; // اگر عملیات قبل از اتصال کنسل شد، ادامه ندهید.
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to get bot info (GetMeAsync). Bot token might be invalid, network issues, or Telegram API is down. Bot service will not start.");
                return; // بدون اطلاعات ربات، ادامه کار ممکن نیست.
            }
            #endregion

            // bool useWebhookMode = _settings.UseWebhook && !string.IsNullOrWhiteSpace(_settings.WebhookAddress);
            bool useWebhookMode = false;
            bool webhookSuccessfullySet = false;

            if (useWebhookMode)
            {
                #region Webhook Setup Attempt
                _logger.LogInformation("Webhook usage is enabled in settings. Attempting to configure Webhook to address: {WebhookAddress}", _settings.WebhookAddress);
                try
                {
                    // ابتدا هرگونه Webhook قبلی را حذف می‌کنیم تا از تداخل جلوگیری شود.
                    await TryDeleteWebhookAsync(_cancellationTokenSourceForPolling.Token, "Preparing for new Webhook setup.");

                    var allowedUpdatesForWebhook = _settings.AllowedUpdates?.ToArray() ?? Array.Empty<UpdateType>();

                    await _botClient.SetWebhook(
                        url: _settings.WebhookAddress!, // Non-null due to IsNullOrWhiteSpace check
                        allowedUpdates: allowedUpdatesForWebhook,
                        dropPendingUpdates: _settings.DropPendingUpdatesOnWebhookSet,
                        secretToken: _settings.WebhookSecretToken,
                        cancellationToken: _cancellationTokenSourceForPolling.Token);

                    var webhookInfo = await _botClient.GetWebhookInfo(cancellationToken: _cancellationTokenSourceForPolling.Token);
                    if (webhookInfo != null && webhookInfo.Url.Equals(_settings.WebhookAddress, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Webhook configured successfully to: {WebhookAddress}. Pending updates: {PendingUpdates}. Last error: {LastErrorMsg} at {LastErrorDate}",
                            webhookInfo.Url, webhookInfo.PendingUpdateCount, webhookInfo.LastErrorMessage ?? "None", webhookInfo.LastErrorDate?.ToLocalTime().ToString() ?? "N/A");
                        webhookSuccessfullySet = true;
                    }
                    else
                    {
                        _logger.LogWarning("Webhook URL was set, but GetWebhookInfo verification failed. Actual URL: '{ActualUrl}', Configured: '{ConfiguredUrl}', Last Error: '{LastError}'. Will fall back to polling.",
                            webhookInfo?.Url ?? "Not Set", _settings.WebhookAddress, webhookInfo?.LastErrorMessage ?? "N/A");
                        // تلاش برای حذف Webhook ناموفق، چون می‌خواهیم به Polling برویم.
                        await TryDeleteWebhookAsync(_cancellationTokenSourceForPolling.Token, "Webhook verification failed after setting, preparing for polling.");
                    }
                }
                catch (Exception ex) // شامل ApiRequestException
                {
                    _logger.LogError(ex, "Failed to set or verify webhook at {WebhookAddress}. Error: {ErrorMessage}. Will fall back to polling.",
                        _settings.WebhookAddress, ex.Message);
                    // تلاش برای حذف Webhook اگر تنظیم آن با خطا مواجه شد.
                    await TryDeleteWebhookAsync(_cancellationTokenSourceForPolling.Token, "Webhook setup failed, preparing for polling.");
                }
                #endregion
            }
            else // UseWebhook is false or WebhookAddress is not configured
            {
                _logger.LogInformation("Webhook usage is disabled or WebhookAddress is not configured in settings. Bot will use polling.");
                // اطمینان از اینکه هیچ Webhook فعالی وجود ندارد قبل از شروع Polling.
                await TryDeleteWebhookAsync(_cancellationTokenSourceForPolling.Token, "Ensuring no previous Webhook is active before starting Polling mode.");
            }

            // اگر تنظیم Webhook ناموفق بود یا از ابتدا برای Polling پیکربندی شده بود
            if (!webhookSuccessfullySet)
            {
                #region Polling Setup
                _logger.LogInformation("Starting Polling for bot {BotUsername}...", me.Username);
                try
                {
                    var allowedUpdatesForPolling = _settings.AllowedUpdates?.ToArray() ?? Array.Empty<UpdateType>();
                    var receiverOptions = new ReceiverOptions
                    {
                        AllowedUpdates = allowedUpdatesForPolling,
                        //  اگر نیاز به مدیریت offset دارید، این بخش باید با دقت بیشتری بررسی شود.
                        //  کتابخانه ممکن است به طور خودکار آخرین آپدیت‌ها را دریافت کند.
                    };

                    _botClient.StartReceiving(
                        updateHandler: this, // این کلاس IUpdateHandler را پیاده‌سازی می‌کند
                        receiverOptions: receiverOptions,
                        cancellationToken: _cancellationTokenSourceForPolling.Token // استفاده از CancellationToken داخلی
                    );
                    _logger.LogInformation("Polling started successfully for bot: {BotUsername}.", me.Username);
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "CRITICAL: Failed to start polling for bot {BotUsername}. The bot may not receive updates via polling.", me.Username);
                    // در این حالت، اگر Webhook هم تنظیم نشده باشد، ربات کار نخواهد کرد.
                }
                #endregion
            }
            else // Webhook با موفقیت تنظیم شده است
            {
                _logger.LogInformation("Webhook is active. Polling will not be started.");
            }
        }


        /// <summary>
        /// تلاش می‌کند Webhook فعلی را حذف کند و نتیجه را لاگ می‌کند.
        /// </summary>
        /// <summary>
        /// تلاش می‌کند Webhook فعلی را حذف کند و نتیجه را لاگ می‌کند.
        /// </summary>
        private async Task TryDeleteWebhookAsync(CancellationToken cancellationToken, string reasonForDeletion)
        {
            _logger.LogInformation("Attempting to delete existing webhook. Reason: {Reason}", reasonForDeletion);
            try
            {
                // بررسی اینکه آیا Webhook ای اصلاً تنظیم شده است
                var currentWebhookInfo = await _botClient.GetWebhookInfo(cancellationToken);
                if (!string.IsNullOrEmpty(currentWebhookInfo.Url))
                {
                    await _botClient.DeleteWebhook(dropPendingUpdates: true, cancellationToken: cancellationToken);
                    _logger.LogInformation("Webhook previously set to '{PreviousUrl}' was deleted successfully.", currentWebhookInfo.Url);
                }
                else
                {
                    _logger.LogInformation("No active webhook was set, so no deletion was necessary.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete webhook (or verify its absence). This might be an issue if a webhook was previously set and now switching to polling.");
            }
        }


        #region IUpdateHandler Implementation (for Polling)


        // پیاده‌سازی متدهای IUpdateHandler برای Polling
        /// <summary>
        /// این متد توسط مکانیزم Polling کتابخانه Telegram.Bot برای هر آپدیت جدید فراخوانی می‌شود.
        /// مسئولیت آن ارسال آپدیت به صف پردازش داخلی است.
        /// </summary>
        /// <summary>
        /// این متد توسط مکانیزم Polling کتابخانه Telegram.Bot برای هر آپدیت جدید فراخوانی می‌شود.
        /// مسئولیت آن ارسال آپدیت به صف پردازش داخلی (<see cref="ITelegramUpdateChannel"/>) است.
        /// </summary>
        /// <param name="botClient">کلاینت ربات که آپدیت را دریافت کرده است (معمولاً همان <see cref="_botClient"/>).</param>
        /// <param name="update">آبجکت آپدیت دریافتی از تلگرام.</param>
        /// <param name="cancellationToken">توکنی که توسط حلقه Polling پاس داده می‌شود و نشان‌دهنده درخواست توقف Polling است.</param>
        // متد HandleUpdateAsync بدون تغییر باقی می‌ماند (امضای آن صحیح است)
        public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            // ... (کد قبلی شما برای HandleUpdateAsync) ...
            if (botClient == null) throw new ArgumentNullException(nameof(botClient));
            if (update == null)
            {
                _logger.LogWarning("Polling: HandleUpdateAsync received a null update object.");
                return;
            }

            var userId = update.Message?.From?.Id ?? update.CallbackQuery?.From?.Id;
            var logScopeProps = new Dictionary<string, object?>
            {
                ["Source"] = "Polling",
                ["UpdateId"] = update.Id,
                ["UpdateType"] = update.Type,
                ["TelegramUserId"] = userId
            };

            using (_logger.BeginScope(logScopeProps))
            {
                _logger.LogDebug("Polling received update. Attempting to write to the processing channel.");
                try
                {
                    await _updateChannel.WriteAsync(update, cancellationToken);
                    _logger.LogDebug("Update successfully written to channel from polling.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Write to update channel was canceled for an update (polling cancellation requested).");
                }
                catch (System.Threading.Channels.ChannelClosedException ex)
                {
                    _logger.LogError(ex, "Failed to write update to channel from polling because the channel is closed. This might occur during application shutdown.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An unexpected error occurred while writing update from polling to the processing channel.");
                }
            }
        }


        #endregion


        /// <summary>
        /// این متد توسط مکانیزم Polling کتابخانه Telegram.Bot هنگام بروز خطا در فرآیند Polling فراخوانی می‌شود.
        /// </summary>
        /// <param name="botClient">کلاینت ربات.</param>
        /// <param name="exception">Exception رخ داده.</param>
        /// <param name="source">منبع خطا در حلقه Polling (مثلاً از GetUpdates, HandleUpdate, یا HandleError).</param> // ✅ پارامتر جدید
        /// <param name="cancellationToken">توکن کنسل شدن Polling.</param>
        public Task HandleErrorAsync( // ✅ نام متد صحیح است
            ITelegramBotClient botClient,
            Exception exception,
            HandleErrorSource source, // ✅ پارامتر HandleErrorSource اضافه شد
            CancellationToken cancellationToken)
        {
            var errorMessage = exception switch
            {
                ApiRequestException apiRequestException =>
                    $"Telegram API Error during polling (Source: {source}): Code=[{apiRequestException.ErrorCode}], Message='{apiRequestException.Message}'. Parameters: {apiRequestException.Parameters}",
                _ => $"Polling Exception (Source: {source}): Type='{exception.GetType().FullName}', Message='{exception.Message}'"
            };

            // لاگ کردن خود Exception برای جزئیات کامل (شامل StackTrace)
            _logger.LogError(exception, "An error occurred during Telegram Bot polling: {FormattedErrorMessage}", errorMessage);

            // برای خطاهای بحرانی مانند توکن نامعتبر (401 Unauthorized) یا مسدود شدن ربات توسط کاربر (403 Forbidden),
            // Polling باید متوقف شود چون ادامه آن بی‌فایده یا مضر است.
            if (exception is ApiRequestException apiEx)
            {
                if (apiEx.ErrorCode == 401) // Unauthorized
                {
                    _logger.LogCritical("CRITICAL POLLING ERROR: Unauthorized (401). Bot token is likely invalid or revoked. Stopping polling. Source: {ErrorSource}", source);
                    _cancellationTokenSourceForPolling.Cancel(); // درخواست توقف حلقه Polling
                }
                else if (apiEx.ErrorCode == 403) // Forbidden
                {
                    _logger.LogWarning("POLLING INFO: Forbidden (403). Bot might be blocked by a user or group. Error: {ApiMessage}. Source: {ErrorSource}", apiEx.Message, source);
                    // در این حالت، Polling می‌تواند ادامه یابد چون ممکن است برای کاربران دیگر کار کند.
                    // اما اگر این خطا به طور مداوم برای تمام آپدیت‌ها رخ دهد، نشان‌دهنده مشکل بزرگتری است.
                }
                else if (apiEx.ErrorCode == 429) // Too Many Requests
                {
                    _logger.LogWarning("POLLING WARNING: Too Many Requests (429). Bot is hitting API rate limits. Consider increasing polling interval or reducing API calls. Error: {ApiMessage}. Source: {ErrorSource}", apiEx.Message, source);
                    // کتابخانه Polling معمولاً به طور خودکار با backoff سعی در ادامه کار می‌کند.
                }
            }
            // برای سایر خطاها، کتابخانه Polling معمولاً به طور خودکار با یک تاخیر کوتاه، مجدداً سعی در دریافت آپدیت‌ها می‌کند.
            // اگر خطاها مداوم باشند، باید بررسی شوند.
            return Task.CompletedTask;
        }




        /// <summary>
        /// با توقف سرویس، Webhook (اگر تنظیم شده بود) را حذف می‌کند و Polling را متوقف می‌نماید.
        /// </summary>
        /// <summary>
        /// این متد هنگام درخواست توقف برنامه توسط هاست .NET Core فراخوانی می‌شود.
        /// مسئول توقف Polling و حذف Webhook (در صورت تنظیم) است.
        /// </summary>
        public async Task StopAsync(CancellationToken hostCancellationToken) // توکن کنسل شدن از هاست
        {
            _logger.LogInformation("Bot Service StopAsync called. Initiating shutdown procedures...");

            // اگر از Webhook استفاده می‌کردیم و برنامه در حال خاموش شدن است، بهتر است Webhook را حذف کنیم
            // تا تلگرام دیگر آپدیت به آدرس مرده ارسال نکند.
            if (_settings.UseWebhook && !string.IsNullOrWhiteSpace(_settings.WebhookAddress))
            {
                // از یک CancellationToken جدید برای این عملیات استفاده کنید چون hostCancellationToken ممکن است زودتر منقضی شود.
                // یا می‌توانید از یک CancellationTokenSource با Timeout استفاده کنید.
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // Timeout 10 ثانیه
                await TryDeleteWebhookAsync(cleanupCts.Token, "Application shutting down.");
            }

            // CancellationTokenSource داخلی را که برای Polling (و سایر عملیات StartAsync) استفاده شده، کنسل کنید.
            if (_cancellationTokenSourceForPolling != null && !_cancellationTokenSourceForPolling.IsCancellationRequested)
            {
                _logger.LogInformation("Requesting cancellation of internal operations (e.g., polling).");
                _cancellationTokenSourceForPolling.Cancel();
            }

            _cancellationTokenSourceForPolling?.Dispose(); // Dispose کردن CancellationTokenSource
            _logger.LogInformation("Bot Service has completed its stopping procedures.");
        }
    }
}