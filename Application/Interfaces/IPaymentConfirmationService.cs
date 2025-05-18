using Application.DTOs.CryptoPay; // برای CryptoPayInvoiceDto
using Shared.Results;             // برای Result

namespace Application.Interfaces // ✅ Namespace صحیح
{
    /// <summary>
    /// اینترفیس برای سرویسی که مسئول پردازش تاییدیه‌های پرداخت موفق از درگاه‌های پرداخت است.
    /// </summary>
    public interface IPaymentConfirmationService
    {
        /// <summary>
        /// یک فاکتور پرداخت شده از CryptoPay را پردازش می‌کند.
        /// این متد باید وضعیت تراکنش داخلی را به‌روز کند، اشتراک کاربر را فعال نماید
        /// و در صورت نیاز به کاربر اطلاع دهد.
        /// </summary>
        /// <param name="paidInvoice">اطلاعات فاکتور پرداخت شده از CryptoPay.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>یک <see cref="Result"/> که نشان‌دهنده موفقیت یا شکست عملیات پردازش است.</returns>
        Task<Result> ProcessSuccessfulCryptoPayPaymentAsync(CryptoPayInvoiceDto paidInvoice, CancellationToken cancellationToken = default);
        // نام متد را به ProcessSuccessfulCryptoPayPaymentAsync تغییر دادم تا مشخص‌تر باشد
    }
}