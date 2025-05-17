using Application.DTOs.CryptoPay; //  DTO های خاص CryptoPay را در ادامه می‌سازیم
using Shared.Results; // برای Result<T>

namespace Application.Common.Interfaces
{
    public interface ICryptoPayApiClient
    {
        /// <summary>
        /// یک فاکتور جدید در Crypto Pay ایجاد می‌کند.
        /// </summary>
        Task<Result<CryptoPayInvoiceDto>> CreateInvoiceAsync(CreateCryptoPayInvoiceRequestDto request, CancellationToken cancellationToken = default);

        /// <summary>
        /// لیستی از فاکتورهای ایجاد شده را دریافت می‌کند.
        /// </summary>
        Task<Result<IEnumerable<CryptoPayInvoiceDto>>> GetInvoicesAsync(GetCryptoPayInvoicesRequestDto? request = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// اطلاعات پایه اپلیکیشن Crypto Pay را دریافت می‌کند (برای تست توکن).
        /// </summary>
        Task<Result<CryptoPayAppInfoDto>> GetMeAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// موجودی حساب اپلیکیشن Crypto Pay را دریافت می‌کند.
        /// </summary>
        Task<Result<IEnumerable<CryptoPayBalanceDto>>> GetBalanceAsync(CancellationToken cancellationToken = default);

        //  می‌توانید متدهای دیگری برای transfer, getExchangeRates و ... اضافه کنید.
    }
}