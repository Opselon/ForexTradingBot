using Application.Common.Interfaces; // برای ISubscriptionRepository و IAppDbContext
using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories
{
    /// <summary>
    /// پیاده‌سازی Repository برای موجودیت اشتراک (Subscription).
    /// از AppDbContext برای تعامل با پایگاه داده استفاده می‌کند.
    /// </summary>
    public class SubscriptionRepository : ISubscriptionRepository
    {
        private readonly IAppDbContext _context;

        public SubscriptionRepository(IAppDbContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public async Task<Subscription?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return await _context.Subscriptions
                .Include(s => s.User) // بارگذاری کاربر مرتبط (اختیاری، بسته به نیاز)
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        }

        public async Task<IEnumerable<Subscription>> GetSubscriptionsByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            return await _context.Subscriptions
                .Where(s => s.UserId == userId)
                .OrderByDescending(s => s.EndDate) // نمایش جدیدترین یا فعال‌ترین‌ها ابتدا
                .ToListAsync(cancellationToken);
        }

        public async Task<Subscription?> GetActiveSubscriptionByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            return await _context.Subscriptions
                .Where(s => s.UserId == userId && s.StartDate <= now && s.EndDate >= now)
                .OrderByDescending(s => s.EndDate) // اگر چندین اشتراک فعال همزمان وجود داشته باشد (نباید اتفاق بیفتد معمولاً)، جدیدترین را برمی‌گرداند
                .FirstOrDefaultAsync(cancellationToken);
        }

        public async Task<bool> HasActiveSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            return await _context.Subscriptions
                .AnyAsync(s => s.UserId == userId && s.StartDate <= now && s.EndDate >= now, cancellationToken);
        }

        public async Task AddAsync(Subscription subscription, CancellationToken cancellationToken = default)
        {
            if (subscription == null) throw new ArgumentNullException(nameof(subscription));
            await _context.Subscriptions.AddAsync(subscription, cancellationToken);
            // SaveChangesAsync در Unit of Work / Service
        }

        public Task UpdateAsync(Subscription subscription, CancellationToken cancellationToken = default)
        {
            if (subscription == null) throw new ArgumentNullException(nameof(subscription));
            _context.Subscriptions.Entry(subscription).State = EntityState.Modified;
            // SaveChangesAsync در Unit of Work / Service
            return Task.CompletedTask;
        }

        public async Task DeleteAsync(Subscription subscription, CancellationToken cancellationToken = default)
        {
            if (subscription == null) throw new ArgumentNullException(nameof(subscription));
            _context.Subscriptions.Remove(subscription);
            await Task.CompletedTask;
            // SaveChangesAsync در Unit of Work / Service
        }
    }
}