using Application.Common.Interfaces; // برای ICryptoPayApiClient, IUserRepository, ISubscriptionRepository, IAppDbContext
using Application.DTOs.CryptoPay;
using Application.Interfaces;
using AutoMapper;
using Domain.Entities; // برای Subscription, Transaction
using Microsoft.Extensions.Logging;
using Shared.Results;
using System.Text.Json;

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

        /// <summary>
        /// Asynchronously creates a new cryptocurrency payment invoice via the CryptoPay API
        /// for a specific user and plan. Handles user existence check and integrates with
        /// the transaction repository to log pending payments. Handles potential API and data access errors.
        /// </summary>
        /// <param name="userId">The ID of the user initiating the payment.</param>
        /// <param name="planId">The ID of the subscription plan.</param>
        /// <param name="selectedCryptoAsset">The selected cryptocurrency asset (e.g., "USDT").</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A Result object containing the created invoice DTO on success, or errors on failure.</returns>
        public async Task<Result<CryptoPayInvoiceDto>> CreateCryptoPaymentInvoiceAsync(
            Guid userId, Guid planId, string selectedCryptoAsset, CancellationToken cancellationToken = default)
        {
            // Input validation (basic checks)
            if (userId == Guid.Empty || planId == Guid.Empty || string.IsNullOrWhiteSpace(selectedCryptoAsset))
            {
                _logger.LogWarning("Attempted to create invoice with invalid input. UserID: {UserId}, PlanID: {PlanId}, Asset: {Asset}",
                    userId, planId, selectedCryptoAsset);
                return Result<CryptoPayInvoiceDto>.Failure("Invalid input provided for invoice creation.");
            }

            _logger.LogInformation("Attempting to create CryptoPay invoice for UserID: {UserId}, PlanID: {PlanId}, Asset: {Asset}",
                userId, planId, selectedCryptoAsset);

            try
            {
                // Fetch the user to ensure they exist and get details for description. Potential database interaction.
                var user = await _userRepository.GetByIdAsync(userId, cancellationToken);
                if (user == null)
                {
                    _logger.LogWarning("User not found for UserID: {UserId} during invoice creation.", userId);
                    return Result<CryptoPayInvoiceDto>.Failure("User not found."); // Functional failure: user does not exist
                }

                // --- Business Logic: Get Plan Details ---
                // This part should retrieve the actual price and duration from your PlanRepository or configuration.
                // Example placeholder:
                // var plan = await _planRepository.GetByIdAsync(planId, cancellationToken); // Potential DB call
                // if (plan == null)
                // {
                //     _logger.LogWarning("Plan not found for PlanID: {PlanId} during invoice creation for UserID: {UserId}.", planId, userId);
                //     return Result<CryptoPayInvoiceDto>.Failure("Subscription plan not found."); // Functional failure: plan does not exist
                // }
                decimal planPrice = 125.0m; // Placeholder - replace with actual plan price
                string planDescription = $"Subscription to Premium Plan (1 Month) for {user.Username}"; // Placeholder - replace with actual plan description
                                                                                                        // ----------------------------------------

                string internalOrderId = $"SUB-{planId}-{userId}-{DateTime.UtcNow.Ticks}"; // Unique internal order ID

                // Prepare the invoice creation request payload. Potential serialization error point.
                var invoiceRequest = new CreateCryptoPayInvoiceRequestDto
                {
                    Asset = selectedCryptoAsset,
                    Amount = planPrice.ToString("F2"), // Format to 2 decimal places
                    Description = planDescription,
                    Payload = JsonSerializer.Serialize(new { UserId = userId, PlanId = planId, OrderId = internalOrderId }), // Custom data for Webhook
                    PaidBtnName = "callback", // Or "open_bot", "subscribe", "buy" etc.
                    PaidBtnUrl = $"https://t.me/YourBotUsername?start={internalOrderId}", // URL for the button after payment
                    ExpiresInSeconds = 3600 // Invoice valid for 1 hour (example)
                };

                // Call the CryptoPay API to create the invoice. **CRITICAL point of failure (Network/API).**
                var invoiceResult = await _cryptoPayApiClient.CreateInvoiceAsync(invoiceRequest, cancellationToken);

                // Handle the functional result from the CryptoPay API client.
                if (invoiceResult.Succeeded && invoiceResult.Data != null)
                {
                    _logger.LogInformation("CryptoPay invoice created successfully. InvoiceID: {InvoiceId}, BotPayUrl: {PayUrl}, InternalOrderID: {InternalOrderId}",
                        invoiceResult.Data.InvoiceId, invoiceResult.Data.BotInvoiceUrl, internalOrderId);

                    // Create a pending transaction record in your system.
                    var pendingTransaction = new Transaction
                    {
                        Id = Guid.NewGuid(),
                        UserId = userId,
                        Amount = planPrice, // Store in your base currency if applicable
                        Type = Domain.Enums.TransactionType.SubscriptionPayment, // Or a specific PendingPayment type
                        Description = $"Pending CryptoPay payment for {planDescription}. Invoice ID: {invoiceResult.Data.InvoiceId}",
                        PaymentGatewayInvoiceId = invoiceResult.Data.InvoiceId.ToString(),
                        Status = "Pending", // Custom status field
                        Timestamp = DateTime.UtcNow,
                        // Add other relevant fields like PlanId, CryptoAsset, etc.
                    };

                    // Add the pending transaction to the repository and save changes. **Potential database interaction/failure.**
                    await _transactionRepository.AddAsync(pendingTransaction, cancellationToken);
                    _ = await _context.SaveChangesAsync(cancellationToken); // Save the pending transaction record
                    _logger.LogDebug("Pending transaction logged for invoice {InvoiceId} with ID {TransactionId}.", invoiceResult.Data.InvoiceId, pendingTransaction.Id);

                    // Return the successful result received from the API client.
                    return invoiceResult;
                }
                else
                {
                    // CryptoPay API client reported a functional error (e.g., invalid asset).
                    _logger.LogError("CryptoPay API reported functional error while creating invoice for UserID: {UserId}. Errors: {Errors}",
                        userId, string.Join(", ", invoiceResult.Errors ?? ["No specific errors reported by API client."]));

                    // Return the failure result with errors from the API client.
                    return Result<CryptoPayInvoiceDto>.Failure(invoiceResult.Errors ?? ["Failed to create payment invoice."]);
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically.
                _logger.LogInformation(ex, "CryptoPay invoice creation for UserID {UserId} was cancelled.", userId);
                throw; // Re-throw cancellation.
            }
            // Catch specific exceptions if needed (e.g., JsonException for serialization errors, DbException for DB errors).
            // catch (JsonException jsonEx)
            // {
            //     _logger.LogError(jsonEx, "JSON serialization error during CryptoPay invoice creation for UserID {UserId}.", userId);
            //     return Result<CryptoPayInvoiceDto>.Failure($"Internal error: Failed to prepare invoice data. ({jsonEx.Message})");
            // }
            // catch (HttpRequestException httpEx) // Catch network errors before API responds
            // {
            //     _logger.LogError(httpEx, "Network error calling CryptoPay API for UserID {UserId}.", userId);
            //     return Result<CryptoPayInvoiceDto>.Failure($"Could not communicate with payment gateway. Please try again later. ({httpEx.Message})");
            // }
            // catch (DbException dbEx) // Catch database errors during transaction logging
            // {
            //     _logger.LogError(dbEx, "Database error while logging pending transaction for UserID {UserId}.", userId);
            //     // Note: Invoice might have been created successfully in CryptoPay, but logging failed.
            //     // This requires a more complex compensation/logging strategy if critical.
            //     return Result<CryptoPayInvoiceDto>.Failure($"Payment initiated but failed to record transaction. Please contact support.");
            // }
            catch (Exception ex)
            {
                // Catch any other unexpected technical exceptions (e.g., errors within _cryptoPayApiClient not converted to Result, other DB errors).
                // Log the critical technical error.
                _logger.LogError(ex, "An unexpected error occurred during CryptoPay invoice creation process for UserID {UserId}.", userId);

                // Return a generic failure result for unhandled technical errors.
                return Result<CryptoPayInvoiceDto>.Failure($"An unexpected error occurred while creating payment invoice. Please try again later.");
                // Optionally include ex.Message for logging but not usually in the user-facing message.
                // return Result<CryptoPayInvoiceDto>.Failure($"An unexpected error occurred: {ex.Message}");
            }
        }


        /// <summary>
        /// Asynchronously checks the status of a specific CryptoPay invoice by its ID.
        /// Handles potential API errors during status retrieval.
        /// </summary>
        /// <param name="invoiceId">The ID of the invoice to check.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A Result object containing the invoice DTO if found and status retrieved, or errors on failure.</returns>
        public async Task<Result<CryptoPayInvoiceDto>> CheckInvoiceStatusAsync(long invoiceId, CancellationToken cancellationToken = default)
        {
            // Basic validation (optional - invoiceId > 0 is usually implied by long type, but good practice)
            if (invoiceId <= 0)
            {
                _logger.LogWarning("Attempted to check status for invalid InvoiceID: {InvoiceId}", invoiceId);
                return Result<CryptoPayInvoiceDto>.Failure("Invalid invoice ID.");
            }

            _logger.LogInformation("Checking status for CryptoPay InvoiceID: {InvoiceId}", invoiceId);

            try
            {
                // Prepare the request to get invoice(s) by ID.
                var request = new GetCryptoPayInvoicesRequestDto { InvoiceIds = invoiceId.ToString() };

                // Call the CryptoPay API to get invoice information. **CRITICAL point of failure (Network/API/Parsing).**
                var result = await _cryptoPayApiClient.GetInvoicesAsync(request, cancellationToken);

                // Handle the functional result from the CryptoPay API client.
                if (result.Succeeded && result.Data != null && result.Data.Any())
                {
                    // If successful and data found, return the first invoice (should be the one requested).
                    var invoice = result.Data.First();
                    _logger.LogInformation("Status for CryptoPay InvoiceID {InvoiceId} is {Status}", invoiceId, invoice.Status);
                    return Result<CryptoPayInvoiceDto>.Success(invoice);
                }
                else
                {
                    // CryptoPay API client reported a functional error or invoice not found/data is empty.
                    _logger.LogWarning("Could not retrieve status or invoice not found for CryptoPay InvoiceID {InvoiceId}. Errors: {Errors}",
                        invoiceId, string.Join(", ", result.Errors ?? ["No specific errors reported by API client."]));

                    // Return a failure result with errors from the API client or a default message.
                    return Result<CryptoPayInvoiceDto>.Failure(result.Errors.Any() ? result.Errors : ["Invoice not found or failed to retrieve status."]);
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically.
                _logger.LogInformation(ex, "CryptoPay invoice status check for InvoiceID {InvoiceId} was cancelled.", invoiceId);
                throw; // Re-throw cancellation.
            }
            // Catch specific exceptions if needed (e.g., JsonException for parsing errors, HttpRequestException for network errors).
            // catch (JsonException jsonEx)
            // {
            //     _logger.LogError(jsonEx, "JSON parsing error during CryptoPay invoice status check for InvoiceID {InvoiceId}.", invoiceId);
            //     return Result<CryptoPayInvoiceDto>.Failure($"Internal error: Failed to parse invoice data. ({jsonEx.Message})");
            // }
            // catch (HttpRequestException httpEx) // Catch network errors before API responds
            // {
            //     _logger.LogError(httpEx, "Network error calling CryptoPay API for InvoiceID {InvoiceId}.", invoiceId);
            //     return Result<CryptoPayInvoiceDto>.Failure($"Could not communicate with payment gateway to check status. Please try again. ({httpEx.Message})");
            // }
            catch (Exception ex)
            {
                // Catch any other unexpected technical exceptions (e.g., errors within _cryptoPayApiClient not converted to Result, other unexpected issues).
                // Log the critical technical error.
                _logger.LogError(ex, "An unexpected error occurred during CryptoPay invoice status check process for InvoiceID {InvoiceId}.", invoiceId);

                // Return a generic failure result for unhandled technical errors.
                return Result<CryptoPayInvoiceDto>.Failure($"An unexpected error occurred while checking invoice status. Please try again later.");
                // Optionally include ex.Message for logging but not usually in the user-facing message.
                // return Result<CryptoPayInvoiceDto>.Failure($"An unexpected error occurred: {ex.Message}");
            }
        }
    }
}