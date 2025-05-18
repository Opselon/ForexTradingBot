using Application.Common.Interfaces; // برای ITokenWalletRepository و IAppDbContext
using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories
{
    /// <summary>
    /// پیاده‌سازی Repository برای موجودیت کیف پول توکن (TokenWallet).
    /// از AppDbContext برای تعامل با پایگاه داده استفاده می‌کند.
    /// </summary>
    public class TokenWalletRepository : ITokenWalletRepository
    {
        private readonly IAppDbContext _context;

        public TokenWalletRepository(IAppDbContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public async Task<TokenWallet?> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            return await _context.TokenWallets
                // .Include(tw => tw.User) // معمولاً لازم نیست چون از طریق UserId به کاربر دسترسی داریم
                .FirstOrDefaultAsync(tw => tw.UserId == userId, cancellationToken);
        }

        public async Task<TokenWallet?> GetByIdAsync(Guid walletId, CancellationToken cancellationToken = default)
        {
            return await _context.TokenWallets
                .FirstOrDefaultAsync(tw => tw.Id == walletId, cancellationToken);
        }

        public async Task AddAsync(TokenWallet tokenWallet, CancellationToken cancellationToken = default)
        {
            if (tokenWallet == null) throw new ArgumentNullException(nameof(tokenWallet));
            await _context.TokenWallets.AddAsync(tokenWallet, cancellationToken);
            // SaveChangesAsync در Unit of Work / Service
        }

        public Task UpdateAsync(TokenWallet tokenWallet, CancellationToken cancellationToken = default)
        {
            if (tokenWallet == null) throw new ArgumentNullException(nameof(tokenWallet));

            // اطمینان از اینکه UpdatedAt به‌روز می‌شود
            tokenWallet.UpdatedAt = DateTime.UtcNow;
            _context.TokenWallets.Entry(tokenWallet).State = EntityState.Modified;
            // SaveChangesAsync در Unit of Work / Service
            return Task.CompletedTask;
        }

        public async Task<bool> IncreaseBalanceAsync(Guid userId, decimal amount, CancellationToken cancellationToken = default)
        {
            if (amount <= 0)
            {
                // یا throw new ArgumentOutOfRangeException(nameof(amount), "Amount to increase must be positive.");
                return false; // یا یک نتیجه مشخص برای عملیات ناموفق
            }

            var wallet = await GetByUserIdAsync(userId, cancellationToken);
            if (wallet == null)
            {
                return false; // کیف پول پیدا نشد
            }

            // در اینجا باید به کنترل همزمانی (Concurrency Control) توجه ویژه شود.
            // یک روش ساده استفاده از EF Core's built-in concurrency token (RowVersion/Timestamp) است.
            // روش دیگر استفاده از تراکنش‌های پایگاه داده با سطح ایزولاسیون مناسب
            // یا قفل‌های خوش‌بینانه/بدبینانه است.

            // مثال ساده (بدون کنترل همزمانی پیشرفته):
            wallet.Balance += amount;
            wallet.UpdatedAt = DateTime.UtcNow;

            _context.TokenWallets.Update(wallet); // یا .Entry(wallet).State = EntityState.Modified;
            // SaveChangesAsync باید در Unit of Work / Service فراخوانی شود.
            // نتیجه نهایی موفقیت، پس از SaveChangesAsync مشخص می‌شود.
            // برای این متد، بهتر است خود SaveChangesAsync را فراخوانی کنیم و نتیجه را برگردانیم
            // یا مسئولیت مدیریت موفقیت/شکست را به سرویس واگذار کنیم.

            // فرض می‌کنیم این متد بخشی از یک Unit of Work بزرگتر است
            // و فقط وضعیت را برای ذخیره‌سازی آماده می‌کند.
            // بنابراین، بازگشت true در اینجا به معنای آماده بودن برای ذخیره است.
            return true;
        }

        public async Task<bool> DecreaseBalanceAsync(Guid userId, decimal amount, CancellationToken cancellationToken = default)
        {
            if (amount <= 0)
            {
                return false;
            }

            var wallet = await GetByUserIdAsync(userId, cancellationToken);
            if (wallet == null)
            {
                return false; // کیف پول پیدا نشد
            }

            if (wallet.Balance < amount)
            {
                return false; // موجودی کافی نیست
            }

            // مشابه IncreaseBalanceAsync، کنترل همزمانی مهم است.
            wallet.Balance -= amount;
            wallet.UpdatedAt = DateTime.UtcNow;

            _context.TokenWallets.Update(wallet);
            return true; // آماده برای ذخیره
        }
    }
}