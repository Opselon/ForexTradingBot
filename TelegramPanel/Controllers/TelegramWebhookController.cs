using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types; // برای Update
// using Telegram.Bot.Types.ReplyMarkups; // در این کنترلر مستقیماً استفاده نمی‌شود، اما ممکن است در جای دیگر لازم باشد
using TelegramPanel.Queue;    // برای ITelegramUpdateChannel
using TelegramPanel.Settings; // برای TelegramPanelSettings

namespace TelegramPanel.Controllers // یا namespace پروژه WebAPI شما
{
    /// <summary>
    /// کنترلر API برای دریافت آپدیت‌های Webhook از سرورهای Telegram.
    /// این کنترلر به عنوان نقطه ورود آپدیت‌ها عمل کرده و آن‌ها را برای پردازش ناهمزمان
    /// در یک صف (<see cref="ITelegramUpdateChannel"/>) قرار می‌دهد.
    /// مسیر پیش‌فرض این کنترلر معمولاً "/api/telegramwebhook" است.
    /// </summary>
    [ApiController]
    [Route("api/telegramwebhook")] //  این مسیر باید با آدرسی که به تلگرام برای Webhook داده‌اید، مطابقت داشته باشد.
    public class TelegramWebhookController : ControllerBase
    {
        private readonly ILogger<TelegramWebhookController> _logger;
        private readonly ITelegramUpdateChannel _updateChannel; // کانال صف برای ارسال آپدیت‌ها
        private readonly TelegramPanelSettings _settings;       // تنظیمات پنل تلگرام (شامل WebhookSecretToken)

        /// <summary>
        /// سازنده کنترلر Webhook تلگرام.
        /// وابستگی‌های لازم از طریق Dependency Injection تزریق می‌شوند.
        /// </summary>
        /// <param name="logger">سرویس لاگینگ برای ثبت وقایع و خطاها.</param>
        /// <param name="updateChannel">کانال صف برای ارسال آپدیت‌های دریافتی.</param>
        /// <param name="settingsOptions">تنظیمات پنل تلگرام، شامل توکن مخفی Webhook.</param>
        public TelegramWebhookController(
            ILogger<TelegramWebhookController> logger,
            ITelegramUpdateChannel updateChannel,
            IOptions<TelegramPanelSettings> settingsOptions) // استفاده از IOptions برای دسترسی به تنظیمات
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _updateChannel = updateChannel ?? throw new ArgumentNullException(nameof(updateChannel));
            _settings = settingsOptions?.Value ?? throw new ArgumentNullException(nameof(settingsOptions), "TelegramPanelSettings cannot be null.");
        }

        /// <summary>
        /// Endpoint اصلی برای دریافت آپدیت‌ها از تلگرام از طریق متد POST.
        /// آپدیت دریافتی را اعتبارسنجی کرده و در صورت موفقیت، آن را به صف پردازش ارسال می‌کند.
        /// </summary>
        /// <param name="update">آبجکت آپدیت که از تلگرام به صورت JSON در بدنه درخواست ارسال می‌شود.</param>
        /// <param name="secretTokenHeader">مقدار هدر "X-Telegram-Bot-Api-Secret-Token" که توسط تلگرام ارسال می‌شود (در صورت تنظیم Secret Token).</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات در صورت بسته شدن درخواست توسط کلاینت (تلگرام).</param>
        /// <returns>
        /// <see cref="OkResult"/> (HTTP 200) در صورت موفقیت در صف‌بندی آپدیت.
        /// <see cref="StatusCodeResult"/> با کد 401 (Unauthorized) اگر Secret Token نامعتبر باشد.
        /// <see cref="BadRequestObjectResult"/> (HTTP 400) اگر آپدیت دریافتی null باشد.
        /// <see cref="StatusCodeResult"/> با کد 503 (Service Unavailable) اگر صف بسته شده باشد.
        /// <see cref="StatusCodeResult"/> با کد 499 (Client Closed Request) اگر درخواست کنسل شود.
        /// <see cref="StatusCodeResult"/> با کد 500 (Internal Server Error) برای سایر خطاهای پیش‌بینی نشده.
        /// </returns>
        [HttpPost]
        public async Task<IActionResult> Post(
       [FromBody] Update update,
       [FromHeader(Name = "X-Telegram-Bot-Api-Secret-Token")] string? secretTokenHeader,
       CancellationToken cancellationToken)
        {
            // ...
            if (!string.IsNullOrWhiteSpace(_settings.WebhookSecretToken))
            {
                if (_settings.WebhookSecretToken != secretTokenHeader)
                {
                    // ==========================================================
                    // VULNERABILITY REMEDIATION
                    // ==========================================================
                    // 1. Sanitize the user-provided header to prevent log forging (CRLF Injection).
                    string sanitizedTokenHeader = (secretTokenHeader ?? "NULL")
                                                   .Replace(Environment.NewLine, "[NL]")
                                                   .Replace("\n", "[NL]")
                                                   .Replace("\r", "[CR]");

                    // 2. Mask the expected token to avoid leaking secrets in logs.
                    string maskedExpectedToken = string.IsNullOrWhiteSpace(_settings.WebhookSecretToken)
                                                  ? "NOT_SET"
                                                  : $"{_settings.WebhookSecretToken[..Math.Min(4, _settings.WebhookSecretToken.Length)]}...";

                    // 3. Log the sanitized and masked data.
                    _logger.LogWarning(
                        "Invalid Webhook Secret Token received from IP {RemoteIpAddress}. Expected (Masked): '{ExpectedToken}', Got (Sanitized): '{ActualToken}'",
                        HttpContext.Connection.RemoteIpAddress?.ToString() ?? "N/A",
                        maskedExpectedToken,
                        sanitizedTokenHeader
                    );
                    // ==========================================================

                    return StatusCode(StatusCodes.Status401Unauthorized, new { ErrorMessage = "Invalid secret token." });
                }
                _logger.LogDebug("Webhook Secret Token validated successfully.");
            }
            // مرحله ۲: بررسی null بودن آپدیت دریافتی
            if (update == null)
            {
                _logger.LogWarning("Received a null update object from Telegram webhook. IP: {RemoteIpAddress}",
                    HttpContext.Connection.RemoteIpAddress?.ToString() ?? "N/A");
                return BadRequest(new { ErrorMessage = "Null update object received." });
            }

            _logger.LogInformation(
                "Webhook received Update. ID: {UpdateId}, Type: {UpdateType}, From UserID: {UserId}. Attempting to write to processing channel.",
                update.Id,
                update.Type,
                update.Message?.From?.Id ?? update.CallbackQuery?.From?.Id //  لاگ کردن شناسه کاربر اگر موجود باشد
            );

            try
            {
                // مرحله ۳: نوشتن آپدیت در کانال (صف) برای پردازش ناهمزمان در پس‌زمینه.
                // این کار باعث می‌شود که به سرعت به تلگرام پاسخ HTTP 200 OK داده شود.
                await _updateChannel.WriteAsync(update, cancellationToken);

                _logger.LogDebug("Update ID {UpdateId} successfully written to the channel.", update.Id);
                // پاسخ HTTP 200 OK به تلگرام نشان می‌دهد که آپدیت با موفقیت دریافت شده است.
                // تلگرام انتظار پاسخ سریع دارد، در غیر این صورت ممکن است دوباره سعی کند آپدیت را ارسال کند.
                return Ok();
            }
            catch (System.Threading.Channels.ChannelClosedException ex)
            {
                // مرحله ۴: مدیریت خطای بسته بودن کانال صف.
                // این اتفاق ممکن است در زمان خاموش شدن برنامه رخ دهد.
                _logger.LogError(ex,
                    "Failed to write update to channel because it was closed. Update ID: {UpdateId}. This might happen during application shutdown.",
                    update.Id);
                // HTTP 503 Service Unavailable به تلگرام اطلاع می‌دهد که سرویس موقتاً در دسترس نیست.
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { ErrorMessage = "Service is currently unavailable, please try again later." });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // مرحله ۵: مدیریت کنسل شدن درخواست توسط کلاینت (تلگرام) یا برنامه.
                _logger.LogInformation(
                    "Webhook request was cancelled while trying to write Update ID: {UpdateId} to channel. This could be due to client closing connection or application shutdown.",
                    update.Id);
                // HTTP 499 Client Closed Request (یک کد غیر استاندارد اما گاهی استفاده می‌شود).
                // یا می‌توانید یک کد استانداردتر مانند 503 برگردانید.
                return StatusCode(499, new { ErrorMessage = "Request was cancelled." });
            }
            catch (Exception ex)
            {
                // مرحله ۶: مدیریت سایر خطاهای پیش‌بینی نشده هنگام نوشتن در صف.
                _logger.LogCritical(ex, // لاگ کردن به عنوان Critical چون این خطا نباید رخ دهد.
                    "An unexpected error occurred while writing Update ID: {UpdateId} to the processing channel.",
                    update.Id);
                // HTTP 500 Internal Server Error.
                return StatusCode(StatusCodes.Status500InternalServerError, new { ErrorMessage = "An internal server error occurred." });
            }
        }

        /// <summary>
        /// Endpoint GET برای بررسی سلامت و فعال بودن Webhook Endpoint.
        /// این متد برای تست دستی یا توسط سیستم‌های مانیتورینگ قابل استفاده است.
        /// </summary>
        /// <returns><see cref="OkObjectResult"/> با پیام موفقیت.</returns>
        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult Get()
        {
            _logger.LogInformation("GET request received on Webhook endpoint. Endpoint is active.");
            return Ok(new { Message = "Telegram Webhook Endpoint is active and listening." });
        }
    }
}