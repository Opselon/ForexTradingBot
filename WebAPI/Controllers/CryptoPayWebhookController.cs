// File: WebAPI/Controllers/CryptoPayWebhookController.cs
#region Usings
using Application.DTOs.CryptoPay;     // ✅ برای CryptoPayInvoiceDto و CryptoPayWebhookUpdateDto
using Application.Interfaces;         // ✅ برای IPaymentConfirmationService
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Shared.Settings;                // ✅ برای CryptoPaySettings (از پروژه Shared)
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
        [RequestSizeLimit(10_240)] // 10 KB limit
        public async Task<IActionResult> Post(
            [FromBody] CryptoPayWebhookUpdateDto webhookUpdate, // ✅ استفاده از DTO صحیح
            [FromHeader(Name = "crypto-pay-api-signature")] string? signatureHeader,
            CancellationToken cancellationToken)
        {
            // ... (منطق اعتبارسنجی امضا که قبلاً داشتیم، با استفاده از rawRequestBody) ...
            string rawRequestBody;
            Request.EnableBuffering(); //  اطمینان از فعال بودن در Program.cs
            Request.Body.Position = 0;
            using (StreamReader reader = new(Request.Body, Encoding.UTF8, true, 1024, true))
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
                Shared.Results.Result processingResult = await _paymentConfirmationService.ProcessSuccessfulCryptoPayPaymentAsync(webhookUpdate.Payload, cancellationToken);
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
            // 1. Guard Clauses for improved readability and early exit.
            if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrWhiteSpace(appApiToken))
            {
                _logger.LogWarning("Signature verification skipped: header or app token is missing.");
                return false;
            }

            try
            {
                byte[] secretKeyBytes;
                using (var sha256 = SHA256.Create())
                {
                    secretKeyBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(appApiToken));
                }

                using var hmac = new HMACSHA256(secretKeyBytes);
                byte[] bodyBytes = Encoding.UTF8.GetBytes(rawRequestBody);
                byte[] computedHashBytes = hmac.ComputeHash(bodyBytes);
                string computedHashHex = Convert.ToHexString(computedHashBytes).ToLowerInvariant();

                // 2. Perform comparison on the original, non-sanitized header.
                bool isValid = computedHashHex.Equals(signatureHeader.ToLowerInvariant(), StringComparison.Ordinal);

                if (!isValid)
                {
                    // 3. VULNERABILITY REMEDIATION (ENHANCED)
                    // Sanitize the received header using a whitelist regex before logging to prevent injection.
                    // This ensures only valid hexadecimal characters are logged.
                    var sanitizedSignatureHeader = SanitizeHexForLogging(signatureHeader);

                    _logger.LogWarning(
                        "CryptoPay signature mismatch. Computed: {ComputedHash}, Received (Sanitized): {SanitizedReceivedHash}",
                        computedHashHex,
                        sanitizedSignatureHeader);
                }

                return isValid;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during CryptoPay signature verification.");
                return false; // Fail securely in case of any exception.
            }
        }
        /// <summary>
        /// Sanitizes a string for logging by removing any characters that are not
        /// valid in a hexadecimal string (a-f, 0-9). This is a robust "whitelist"
        /// approach against log forging.
        /// </summary>
        /// <param name="inputValue">The potentially unsafe string to sanitize.</param>
        /// <returns>A sanitized string containing only hexadecimal characters.</returns>
        private static string SanitizeHexForLogging(string inputValue)
        {
            if (string.IsNullOrEmpty(inputValue))
            {
                return string.Empty;
            }
            // Whitelist: Allow only characters from 'a' through 'f' (case-insensitive) and '0' through '9'.
            // Anything else is removed.
            return Regex.Replace(inputValue, "[^a-fA-F0-9]", string.Empty);
        }

        [HttpGet]
        public IActionResult Get()
        {
            return Ok(new { Status = "CryptoPay Webhook Endpoint is Active" });
        }
        #endregion
    }
}