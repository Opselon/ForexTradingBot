using Application.DTOs.CryptoPay; // برای CreateCryptoPayInvoiceRequestDto و CryptoPayInvoiceDto
using Shared.Results;

namespace Application.Interfaces
{
    public interface IPaymentService
    {
        /// <summary>
        /// یک فاکتور پرداخت با CryptoPay برای یک محصول یا پلن خاص ایجاد می‌کند.
        /// </summary>
        /// <param name="userId">شناسه کاربری که پرداخت را انجام می‌دهد.</param>
        /// <param name="planId">شناسه پلن یا محصول مورد نظر.</param>
        /// <param name="selectedCryptoAsset">ارز دیجیتالی که کاربر برای پرداخت انتخاب کرده (مثلاً "USDT").</param>
        /// <param name="cancellationToken"></param>
        /// <returns>اطلاعات فاکتور ایجاد شده شامل لینک پرداخت.</returns>
        Task<Result<CryptoPayInvoiceDto>> CreateCryptoPaymentInvoiceAsync(
            Guid userId,
            Guid planId, // یا یک مدل Product/Plan DTO
            string selectedCryptoAsset,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// وضعیت یک فاکتور CryptoPay را بررسی می‌کند.
        /// </summary>
        Task<Result<CryptoPayInvoiceDto>> CheckInvoiceStatusAsync(long invoiceId, CancellationToken cancellationToken = default);

        // می‌توانید متدهای دیگری برای مدیریت Webhook های CryptoPay (پرداخت موفق) اضافه کنید.
    }
}