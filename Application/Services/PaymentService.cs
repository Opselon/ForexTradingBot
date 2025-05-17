using Application.Common.Interfaces; // برای ICryptoPayApiClient, IUserRepository, ISubscriptionRepository, IAppDbContext
using Application.DTOs;
using Application.DTOs.CryptoPay;
using Application.Interfaces;
using AutoMapper;
using Domain.Entities; // برای Subscription, Transaction
using Microsoft.Extensions.Logging;
using Shared.Results;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Application.Services
{
    public class PaymentService : IPaymentService
    {
        private readonly ICryptoPayApiClient _cryptoPayApiClient;
        private readonly IUserRepository _userRepository; // برای دریافت اطلاعات کاربر
        private readonly ISubscriptionRepository _subscriptionRepository; // برای ایجاد اشتراک پس از پرداخت
        private readonly ITransactionRepository _transactionRepository; // برای ثبت تراکنش پرداخت
        private readonly IAppDbContext _context; // Unit of Work
        private readonly IMapper _mapper;
        private readonly ILogger<PaymentService> _logger;

        // شما به یک راه برای دریافت اطلاعات پلن‌ها (قیمت، مدت اعتبار و ...) نیاز دارید
        // private readonly IPlanRepository _planRepository; یا یک سرویس مشابه

        public PaymentService(
            ICryptoPayApiClient cryptoPayApiClient,
            IUserRepository userRepository,
            ISubscriptionRepository subscriptionRepository,
            ITransactionRepository transactionRepository,
            IAppDbContext context,
            IMapper mapper,
            ILogger<PaymentService> logger)
        {
            _cryptoPayApiClient = cryptoPayApiClient;
            _userRepository = userRepository;
            _subscriptionRepository = subscriptionRepository;
            _transactionRepository = transactionRepository;
            _context = context;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<Result<CryptoPayInvoiceDto>> CreateCryptoPaymentInvoiceAsync(
            Guid userId, Guid planId, string selectedCryptoAsset, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Attempting to create CryptoPay invoice for UserID: {UserId}, PlanID: {PlanId}, Asset: {Asset}",
                userId, planId, selectedCryptoAsset);

            var user = await _userRepository.GetByIdAsync(userId, cancellationToken);
            if (user == null)
            {
                _logger.LogWarning("User not found for UserID: {UserId} during invoice creation.", userId);
                return Result<CryptoPayInvoiceDto>.Failure("User not found.");
            }

            //  دریافت اطلاعات پلن (قیمت و ...) از PlanRepository یا یک منبع دیگر
            //  فرض می‌کنیم قیمت پلن 10 USDT است و یک ماه اعتبار دارد.
            //  این بخش باید با سیستم مدیریت پلن‌های شما جایگزین شود.
            decimal planPrice = 10.0m; //  به عنوان مثال
            string planDescription = $"Subscription to Premium Plan (1 Month) for {user.Username}";
            string internalOrderId = $"SUB-{planId}-{userId}-{DateTime.UtcNow.Ticks}"; // یک شناسه سفارش داخلی

            var invoiceRequest = new CreateCryptoPayInvoiceRequestDto
            {
                Asset = selectedCryptoAsset, // "USDT", "TON", "BTC" و ...
                Amount = planPrice.ToString("F2"), // فرمت با دو رقم اعشار
                Description = planDescription,
                Payload = JsonSerializer.Serialize(new { UserId = userId, PlanId = planId, OrderId = internalOrderId }), // داده‌های سفارشی برای Webhook
                PaidBtnName = "callback",
                PaidBtnUrl = $"https://t.me/YourBotUsername?start={internalOrderId}",
                ExpiresInSeconds = 3600 // فاکتور برای ۱ ساعت معتبر است (مثال)
            };

            var invoiceResult = await _cryptoPayApiClient.CreateInvoiceAsync(invoiceRequest, cancellationToken);

            if (invoiceResult.Succeeded && invoiceResult.Data != null)
            {
                _logger.LogInformation("CryptoPay invoice created successfully. InvoiceID: {InvoiceId}, BotPayUrl: {PayUrl}",
                    invoiceResult.Data.InvoiceId, invoiceResult.Data.BotInvoiceUrl);

                // می‌توانید یک رکورد اولیه برای تراکنش یا سفارش در سیستم خودتان با وضعیت "PendingPayment" ایجاد کنید.
                // و InvoiceId کریپتو پی را در آن ذخیره کنید.
                var pendingTransaction = new Transaction
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Amount = planPrice,
                    Type = Domain.Enums.TransactionType.SubscriptionPayment, // یا یک نوع PendingPayment جدید
                    Description = $"Pending CryptoPay payment for {planDescription}. Invoice ID: {invoiceResult.Data.InvoiceId}",
                    PaymentGatewayInvoiceId = invoiceResult.Data.InvoiceId.ToString(), // فیلد جدید برای شناسه فاکتور درگاه
                    Status = "Pending", // فیلد جدید برای وضعیت تراکنش
                    Timestamp = DateTime.UtcNow

                };
                await _transactionRepository.AddAsync(pendingTransaction, cancellationToken);
                await _context.SaveChangesAsync(cancellationToken); // ذخیره تراکنش معلق
            }
            else
            {
                _logger.LogError("Failed to create CryptoPay invoice for UserID: {UserId}. Errors: {Errors}",
                    userId, string.Join(", ", invoiceResult.Errors));
            }
            return invoiceResult;
        }

        public async Task<Result<CryptoPayInvoiceDto>> CheckInvoiceStatusAsync(long invoiceId, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Checking status for CryptoPay InvoiceID: {InvoiceId}", invoiceId);
            // برای بررسی وضعیت، معمولاً از getInvoices با invoice_ids استفاده می‌شود
            var request = new GetCryptoPayInvoicesRequestDto { InvoiceIds = invoiceId.ToString() };
            var result = await _cryptoPayApiClient.GetInvoicesAsync(request, cancellationToken);

            if (result.Succeeded && result.Data != null && result.Data.Any())
            {
                var invoice = result.Data.First();
                _logger.LogInformation("Status for CryptoPay InvoiceID {InvoiceId} is {Status}", invoiceId, invoice.Status);
                return Result<CryptoPayInvoiceDto>.Success(invoice);
            }
            _logger.LogWarning("Could not retrieve status or invoice not found for CryptoPay InvoiceID {InvoiceId}. Errors: {Errors}", invoiceId, string.Join(", ", result.Errors));
            return Result<CryptoPayInvoiceDto>.Failure(result.Errors.Any() ? result.Errors : new List<string> { "Invoice not found or failed to retrieve status." });
        }
    }
}