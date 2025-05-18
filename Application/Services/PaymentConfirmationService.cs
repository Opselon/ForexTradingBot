// File: Application/Services/PaymentConfirmationService.cs
#region Usings
using Application.Common.Interfaces; // برای ITransactionRepository, IAppDbContext, INotificationService ✅
using Application.DTOs;             // برای CreateSubscriptionDto
using Application.DTOs.CryptoPay;   // برای CryptoPayInvoiceDto
using Application.Interface;
using Application.Interfaces;       // برای IPaymentConfirmationService, ISubscriptionService, IUserService
using Microsoft.Extensions.Logging;
using Shared.Results;               // برای Result
using System.Text.Json;
// ❌ حذف using های مربوط به Telegram.Bot و TelegramPanel.Formatters
#endregion

namespace Application.Services // ✅ Namespace: Application.Services
{
    public class PaymentConfirmationService : IPaymentConfirmationService
    {
        #region Private Readonly Fields
        private readonly ILogger<PaymentConfirmationService> _logger;
        private readonly ITransactionRepository _transactionRepository;
        private readonly ISubscriptionService _subscriptionService;
        private readonly IUserService _userService;
        private readonly INotificationService _notificationService; // ✅ استفاده از اینترفیس عمومی
        private readonly IAppDbContext _context;
        #endregion

        #region Constructor
        public PaymentConfirmationService(
            ILogger<PaymentConfirmationService> logger,
            ITransactionRepository transactionRepository,
            ISubscriptionService subscriptionService,
            IUserService userService,
            INotificationService notificationService, // ✅ تزریق شد
            IAppDbContext context)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _transactionRepository = transactionRepository ?? throw new ArgumentNullException(nameof(transactionRepository));
            _subscriptionService = subscriptionService ?? throw new ArgumentNullException(nameof(subscriptionService));
            _userService = userService ?? throw new ArgumentNullException(nameof(userService));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }
        #endregion

        #region IPaymentConfirmationService Implementation
        public async Task<Result> ProcessSuccessfulCryptoPayPaymentAsync(CryptoPayInvoiceDto paidInvoice, CancellationToken cancellationToken = default)
        {
            // ... (منطق داخلی متد که قبلاً داشتیم، تا قبل از ارسال پیام) ...
            if (paidInvoice == null) return Result.Failure("Paid invoice data cannot be null.");
            using (_logger.BeginScope(new Dictionary<string, object?> { ["CryptoPayInvoiceId"] = paidInvoice.InvoiceId }))
            {
                _logger.LogInformation("Processing successful CryptoPay payment. Status: {Status}", paidInvoice.Status);
                if (paidInvoice.Status?.ToLowerInvariant() != "paid")
                    return Result.Failure($"Invoice status is '{paidInvoice.Status}', expected 'paid'.");

                var internalTransaction = await _transactionRepository.GetByPaymentGatewayInvoiceIdAsync(paidInvoice.InvoiceId.ToString(), cancellationToken);
                if (internalTransaction == null)
                    return Result.Failure($"Internal transaction not found for CryptoPay Invoice ID {paidInvoice.InvoiceId}.");
                if (internalTransaction.Status?.Equals("Completed", StringComparison.OrdinalIgnoreCase) == true)
                    return Result.Success("Transaction already processed.");

                InvoicePayloadData? payloadData = null;
                if (!string.IsNullOrWhiteSpace(internalTransaction.PaymentGatewayPayload)) // مطمئن شوید این فیلد در Transaction.cs هست
                {
                    try { payloadData = JsonSerializer.Deserialize<InvoicePayloadData>(internalTransaction.PaymentGatewayPayload); }
                    catch (JsonException jsonEx)
                    {
                        _logger.LogError(jsonEx, "Error deserializing internal payload for transaction {TransactionId}", internalTransaction.Id);
                        return Result.Failure("Error processing internal transaction payload data.");
                    }
                }
                if (payloadData == null || payloadData.UserId == Guid.Empty || payloadData.PlanId == Guid.Empty)
                    return Result.Failure("Essential internal payload data (UserId, PlanId) is missing.");

                internalTransaction.Status = "Completed";
                internalTransaction.PaidAt = DateTime.UtcNow;
                internalTransaction.PaymentGatewayResponse = JsonSerializer.Serialize(paidInvoice);

                string planName = "Premium Plan (Example)"; //  باید از payloadData.PlanId تعیین شود
                int planDurationInMonths = 1;              //  باید از payloadData.PlanId تعیین شود

                var createSubscriptionDto = new CreateSubscriptionDto
                {
                    UserId = payloadData.UserId,
                    StartDate = DateTime.UtcNow,
                    EndDate = DateTime.UtcNow.AddMonths(planDurationInMonths),
                };
                var subscriptionDto = await _subscriptionService.CreateSubscriptionAsync(createSubscriptionDto, cancellationToken);
                if (subscriptionDto == null)
                    return Result.Failure("Subscription activation failed after payment confirmation.");

                _logger.LogInformation("Subscription {SubscriptionId} activated for UserID {UserId} for plan '{PlanName}'.",
                   subscriptionDto.Id, payloadData.UserId, planName);

                // ... (به‌روزرسانی UserLevel اگر لازم است) ...

                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Database changes saved for CryptoPay InvoiceID {CryptoPayInvoiceId}.", paidInvoice.InvoiceId);

                // --- ✅ ارسال نوتیفیکیشن با استفاده از INotificationService ---
                var userForNotification = await _userService.GetUserByIdAsync(payloadData.UserId, cancellationToken);
                if (userForNotification != null) //  TelegramId باید در UserDto باشد
                {
                    //  متن پیام می‌تواند شامل Markdown پایه باشد. پیاده‌سازی INotificationService مسئول فرمت‌بندی نهایی است.
                    string successMessage = $"🎉 Congratulations, {userForNotification.Username}!\n\n" +
                                         $"Your payment for the *{planName}* plan was successful. " +
                                         $"Your subscription is now active until *{subscriptionDto.EndDate:yyyy-MM-dd}*.\n\n" +
                                         "Thank you for subscribing! Type /menu to explore.";
                    try
                    {
                        //  true برای useRichText نشان می‌دهد که پیام حاوی Markdown است.
                        await _notificationService.SendNotificationAsync(userForNotification.TelegramId, successMessage, true, CancellationToken.None);
                        _logger.LogInformation("Success notification dispatched for UserID {UserId} (RecipientID: {RecipientId}).", payloadData.UserId, userForNotification.TelegramId);
                    }
                    catch (Exception notifyEx)
                    {
                        _logger.LogError(notifyEx, "Failed to send success notification to UserID {UserId} (RecipientID: {RecipientId}) after payment.", payloadData.UserId, userForNotification.TelegramId);
                    }
                }
                return Result.Success("Payment processed and subscription activated successfully.");
            }
        }
        #endregion

        #region Private Helper Classes
        private class InvoicePayloadData // این کلاس را در صورت نیاز به فایل جداگانه منتقل کنید
        {
            public Guid UserId { get; set; }
            public Guid PlanId { get; set; }
            public string? OrderId { get; set; }
        }
        #endregion
    }
}