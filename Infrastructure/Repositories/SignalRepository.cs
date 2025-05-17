using Application.Common.Interfaces; // برای ISignalRepository و IAppDbContext
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace Infrastructure.Persistence.Repositories
{
    /// <summary>
    /// پیاده‌سازی Repository برای موجودیت سیگنال (Signal).
    /// از AppDbContext برای تعامل با پایگاه داده استفاده می‌کند.
    /// </summary>
    public class SignalRepository : ISignalRepository
    {
        private readonly IAppDbContext _context;

        public SignalRepository(IAppDbContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public async Task<Signal?> GetByIdWithDetailsAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return await _context.Signals
                .Include(s => s.Category)  // بارگذاری دسته‌بندی مرتبط
                .Include(s => s.Analyses)  // بارگذاری تحلیل‌های مرتبط
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        }

        public async Task<Signal?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return await _context.Signals
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        }

        public async Task<IEnumerable<Signal>> GetAllWithCategoryAsync(CancellationToken cancellationToken = default)
        {
            return await _context.Signals
                .Include(s => s.Category)
                .OrderByDescending(s => s.CreatedAt) // مثال: مرتب‌سازی بر اساس جدیدترین
                .ToListAsync(cancellationToken);
        }

        public async Task<IEnumerable<Signal>> FindWithCategoryAsync(Expression<Func<Signal, bool>> predicate, CancellationToken cancellationToken = default)
        {
            return await _context.Signals
                .Where(predicate)
                .Include(s => s.Category)
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync(cancellationToken);
        }

        public async Task<IEnumerable<Signal>> GetRecentSignalsAsync(int count, CancellationToken cancellationToken = default)
        {
            return await _context.Signals
                .Include(s => s.Category)
                .OrderByDescending(s => s.CreatedAt)
                .Take(count)
                .ToListAsync(cancellationToken);
        }

        public async Task<IEnumerable<Signal>> GetSignalsByCategoryIdAsync(Guid categoryId, CancellationToken cancellationToken = default)
        {
            return await _context.Signals
                .Where(s => s.CategoryId == categoryId)
                .Include(s => s.Category)
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync(cancellationToken);
        }

        public async Task<IEnumerable<Signal>> GetSignalsBySymbolAsync(string symbol, CancellationToken cancellationToken = default)
        {
            // مقایسه case-insensitive برای نماد
            var normalizedSymbol = symbol.ToUpperInvariant();
            return await _context.Signals
                .Where(s => s.Symbol.ToUpper() == normalizedSymbol)
                .Include(s => s.Category)
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync(cancellationToken);
        }

        public async Task AddAsync(Signal signal, CancellationToken cancellationToken = default)
        {
            if (signal == null) throw new ArgumentNullException(nameof(signal));
            await _context.Signals.AddAsync(signal, cancellationToken);
            // SaveChangesAsync در Unit of Work / Service
        }

        public Task UpdateAsync(Signal signal, CancellationToken cancellationToken = default)
        {
            if (signal == null) throw new ArgumentNullException(nameof(signal));
            _context.Signals.Entry(signal).State = EntityState.Modified;
            // SaveChangesAsync در Unit of Work / Service
            return Task.CompletedTask;
        }

        public async Task DeleteAsync(Signal signal, CancellationToken cancellationToken = default)
        {
            if (signal == null) throw new ArgumentNullException(nameof(signal));
            _context.Signals.Remove(signal);
            await Task.CompletedTask; // به تعویق انداختن حذف واقعی تا SaveChangesAsync
        }

        public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var signalToDelete = await GetByIdAsync(id, cancellationToken);
            if (signalToDelete != null)
            {
                _context.Signals.Remove(signalToDelete);
            }
            // SaveChangesAsync در Unit of Work / Service
        }
    }
}