// File: WebAPI/Controllers/CryptoPayWebhookController.cs
#region Usings
using Application.DTOs.CryptoPay;     // ✅ برای CryptoPayInvoiceDto و CryptoPayWebhookUpdateDto
using Application.Interfaces;         // ✅ برای IPaymentConfirmationService
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Settings;                // ✅ برای CryptoPaySettings (از پروژه Shared)
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#endregion

namespace WebAPI.Controllers // ✅ Namespace صحیح
{
    [ApiController]
    [Route("api/cryptopaywebhook")]
    public class CryptoPayWebhookController : ControllerBase
    {
        #region Private Readonly Fields
        private readonly ILogger<CryptoPayWebhookController> _logger;
        private readonly CryptoPaySettings _cryptoPaySettings;
        private readonly IPaymentConfirmationService _paymentConfirmationService;
        #endregion

        #region Constructor
        public CryptoPayWebhookController(
            ILogger<CryptoPayWebhookController> logger,
            IOptions<CryptoPaySettings> cryptoPaySettingsOptions,
            IPaymentConfirmationService paymentConfirmationService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cryptoPaySettings = cryptoPaySettingsOptions?.Value ?? throw new ArgumentNullException(nameof(cryptoPaySettingsOptions));
            _paymentConfirmationService = paymentConfirmationService ?? throw new ArgumentNullException(nameof(paymentConfirmationService));
        }
        #endregion

        #region Action Methods
        [HttpPost]
        public async Task<IActionResult> Post(
            [FromBody] CryptoPayWebhookUpdateDto webhookUpdate, // ✅ استفاده از DTO صحیح
            [FromHeader(Name = "crypto-pay-api-signature")] string? signatureHeader,
            CancellationToken cancellationToken)
        {
            // ... (منطق اعتبارسنجی امضا که قبلاً داشتیم، با استفاده از rawRequestBody) ...
            string rawRequestBody;
            Request.EnableBuffering(); //  اطمینان از فعال بودن در Program.cs
            Request.Body.Position = 0;
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8, true, 1024, true))
            {
                rawRequestBody = await reader.ReadToEndAsync(cancellationToken);
            }
            Request.Body.Position = 0;

            if (!string.IsNullOrWhiteSpace(_cryptoPaySettings.ApiToken))
            {
                if (!VerifyCryptoPaySignature(rawRequestBody, signatureHeader, _cryptoPaySettings.ApiToken))
                {
                    _logger.LogWarning("Invalid CryptoPay webhook signature.");
                    return Unauthorized(new { ErrorMessage = "Invalid webhook signature." });
                }
                _logger.LogInformation("CryptoPay webhook signature validated successfully.");
            }
            else
            {
                _logger.LogDebug("CryptoPay webhook signature validation skipped (API token or secret not fully configured for verification).");
            }


            if (webhookUpdate?.UpdateType == "invoice_paid" && webhookUpdate.Payload != null)
            {
                _logger.LogInformation("Processing 'invoice_paid' webhook for CryptoPay InvoiceID: {InvoiceId}",
                    webhookUpdate.Payload.InvoiceId);
                var processingResult = await _paymentConfirmationService.ProcessSuccessfulCryptoPayPaymentAsync(webhookUpdate.Payload, cancellationToken);
                if (processingResult.Succeeded)
                {
                    return Ok();
                }
                else
                {
                    _logger.LogError("Failed to process successful payment for InvoiceID {InvoiceId}. Errors: {Errors}",
                        webhookUpdate.Payload.InvoiceId, string.Join(", ", processingResult.Errors));
                    return Ok(); //  همچنان OK به CryptoPay
                }
            }
            else
            {
                _logger.LogInformation("Received CryptoPay webhook of type '{UpdateType}' or payload is null. No action taken.", webhookUpdate?.UpdateType);
                return Ok();
            }
        }

        private bool VerifyCryptoPaySignature(string rawRequestBody, string? signatureHeader, string appApiToken)
        {
            // ... (کد VerifyCryptoPaySignature که قبلاً داشتیم) ...
            if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrWhiteSpace(appApiToken)) return false;
            try
            {
                byte[] secretKeyBytes;
                using (var sha256 = SHA256.Create()) { secretKeyBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(appApiToken)); }
                using (var hmac = new HMACSHA256(secretKeyBytes))
                {
                    byte[] bodyBytes = Encoding.UTF8.GetBytes(rawRequestBody);
                    byte[] computedHashBytes = hmac.ComputeHash(bodyBytes);
                    string computedHashHex = Convert.ToHexString(computedHashBytes).ToLowerInvariant();
                    bool isValid = computedHashHex.Equals(signatureHeader.ToLowerInvariant(), StringComparison.Ordinal);
                    if (!isValid) _logger.LogWarning("CryptoPay signature mismatch. Computed: {Computed}, Received: {Received}", computedHashHex, signatureHeader.ToLowerInvariant());
                    return isValid;
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "Exception during CryptoPay signature verification."); return false; }
        }

        [HttpGet]
        public IActionResult Get()
        {
            return Ok(new { Status = "CryptoPay Webhook Endpoint is Active" });
        }
        #endregion
    }
}