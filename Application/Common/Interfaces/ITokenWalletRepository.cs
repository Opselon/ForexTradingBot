using Domain.Entities; // برای TokenWallet و User
using Domain.ValueObjects; // برای TokenAmount (اگر از آن در اینترفیس استفاده می‌کنیم)
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Application.Common.Interfaces
{
    /// <summary>
    /// اینترفیس برای Repository موجودیت کیف پول توکن (TokenWallet).
    /// عملیات مربوط به دسترسی و مدیریت داده‌های کیف پول توکن کاربران را تعریف می‌کند.
    /// </summary>
    public interface ITokenWalletRepository
    {
        /// <summary>
        /// کیف پول توکن یک کاربر خاص را بر اساس شناسه کاربر به صورت ناهمزمان پیدا می‌کند.
        /// </summary>
        /// <param name="userId">شناسه کاربری که کیف پول برای او جستجو می‌شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>موجودیت کیف پول توکن در صورت یافتن؛ در غیر این صورت null.</returns>
        Task<TokenWallet?> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک کیف پول توکن را بر اساس شناسه خود کیف پول به صورت ناهمزمان پیدا می‌کند.
        /// </summary>
        /// <param name="walletId">شناسه کیف پول توکن.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>موجودیت کیف پول توکن در صورت یافتن؛ در غیر این صورت null.</returns>
        Task<TokenWallet?> GetByIdAsync(Guid walletId, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک کیف پول توکن جدید را به صورت ناهمزمان به پایگاه داده اضافه می‌کند.
        /// معمولاً هنگام ایجاد کاربر جدید فراخوانی می‌شود.
        /// </summary>
        /// <param name="tokenWallet">موجودیت کیف پول توکنی که باید اضافه شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task AddAsync(TokenWallet tokenWallet, CancellationToken cancellationToken = default);

        /// <summary>
        /// اطلاعات یک کیف پول توکن موجود را به صورت ناهمزمان در پایگاه داده به‌روزرسانی می‌کند.
        /// این متد عمدتاً برای به‌روزرسانی موجودی (Balance) و UpdatedAt استفاده می‌شود.
        /// </summary>
        /// <param name="tokenWallet">موجودیت کیف پول توکنی با اطلاعات به‌روز شده.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task UpdateAsync(TokenWallet tokenWallet, CancellationToken cancellationToken = default);

        /// <summary>
        /// موجودی کیف پول یک کاربر را با مقدار مشخصی افزایش می‌دهد.
        /// این متد باید با دقت و با در نظر گرفتن همزمانی (concurrency) پیاده‌سازی شود.
        /// </summary>
        /// <param name="userId">شناسه کاربر.</param>
        /// <param name="amount">مقداری که باید به موجودی اضافه شود (باید مثبت باشد).</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>True اگر عملیات موفقیت‌آمیز بود، در غیر این صورت false (مثلاً اگر کیف پول پیدا نشد).</returns>
        Task<bool> IncreaseBalanceAsync(Guid userId, decimal amount, CancellationToken cancellationToken = default);

        /// <summary>
        /// موجودی کیف پول یک کاربر را با مقدار مشخصی کاهش می‌دهد.
        /// این متد باید بررسی کند که آیا موجودی کافی است یا خیر و با در نظر گرفتن همزمانی پیاده‌سازی شود.
        /// </summary>
        /// <param name="userId">شناسه کاربر.</param>
        /// <param name="amount">مقداری که باید از موجودی کسر شود (باید مثبت باشد).</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>True اگر عملیات موفقیت‌آمیز بود، در غیر این صورت false (مثلاً اگر کیف پول پیدا نشد یا موجودی کافی نبود).</returns>
        Task<bool> DecreaseBalanceAsync(Guid userId, decimal amount, CancellationToken cancellationToken = default);

        // حذف کیف پول معمولاً همراه با حذف کاربر است و توسط Cascade Delete مدیریت می‌شود،
        // بنابراین متد DeleteAsync جداگانه برای TokenWallet ممکن است لازم نباشد.
        // Task DeleteAsync(TokenWallet tokenWallet, CancellationToken cancellationToken = default);
    }
}