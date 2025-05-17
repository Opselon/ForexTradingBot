using Application.Common.Interfaces; // برای ISignalCategoryRepository و IAppDbContext
using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories
{
    /// <summary>
    /// پیاده‌سازی Repository برای موجودیت دسته‌بندی سیگنال (SignalCategory).
    /// از AppDbContext برای تعامل با پایگاه داده استفاده می‌کند.
    /// </summary>
    public class SignalCategoryRepository : ISignalCategoryRepository
    {
        private readonly IAppDbContext _context;

        public SignalCategoryRepository(IAppDbContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public async Task<SignalCategory?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return await _context.SignalCategories
                // .Include(sc => sc.Signals) // معمولاً هنگام خواندن یک دسته، نیازی به تمام سیگنال‌های آن نیست.
                .FirstOrDefaultAsync(sc => sc.Id == id, cancellationToken);
        }

        public async Task<SignalCategory?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
        {
            // مقایسه case-insensitive برای نام دسته
            var normalizedName = name.Trim().ToLowerInvariant();
            return await _context.SignalCategories
                .FirstOrDefaultAsync(sc => sc.Name.ToLower() == normalizedName, cancellationToken);
        }

        public async Task<IEnumerable<SignalCategory>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return await _context.SignalCategories
                .OrderBy(sc => sc.Name) // مرتب‌سازی بر اساس نام
                .ToListAsync(cancellationToken);
        }

        public async Task AddAsync(SignalCategory category, CancellationToken cancellationToken = default)
        {
            if (category == null) throw new ArgumentNullException(nameof(category));
            // اطمینان از اینکه نام trim شده و شاید نرمال‌سازی دیگری روی آن انجام شود
            category.Name = category.Name.Trim();
            await _context.SignalCategories.AddAsync(category, cancellationToken);
            // SaveChangesAsync در Unit of Work / Service
        }

        public Task UpdateAsync(SignalCategory category, CancellationToken cancellationToken = default)
        {
            if (category == null) throw new ArgumentNullException(nameof(category));
            category.Name = category.Name.Trim();
            _context.SignalCategories.Entry(category).State = EntityState.Modified;
            // SaveChangesAsync در Unit of Work / Service
            return Task.CompletedTask;
        }

        public async Task DeleteAsync(SignalCategory category, CancellationToken cancellationToken = default)
        {
            if (category == null) throw new ArgumentNullException(nameof(category));

            // بررسی وابستگی‌ها قبل از حذف (اگر OnDeleteBehavior.Restrict تنظیم شده باشد، EF Core این کار را انجام می‌دهد)
            // bool hasSignals = await _context.Signals.AnyAsync(s => s.CategoryId == category.Id, cancellationToken);
            // if (hasSignals)
            // {
            //     throw new InvalidOperationException("Cannot delete category with associated signals.");
            // }
            _context.SignalCategories.Remove(category);
            await Task.CompletedTask;
            // SaveChangesAsync در Unit of Work / Service
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var categoryToDelete = await GetByIdAsync(id, cancellationToken);
            if (categoryToDelete == null)
            {
                return false; // پیدا نشد
            }
            await DeleteAsync(categoryToDelete, cancellationToken);
            return true; // آماده برای ذخیره و حذف
        }

        public async Task<bool> ExistsByNameAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default)
        {
            var normalizedName = name.Trim().ToLowerInvariant();
            var query = _context.SignalCategories.Where(sc => sc.Name.ToLower() == normalizedName);

            if (excludeId.HasValue)
            {
                query = query.Where(sc => sc.Id != excludeId.Value);
            }

            return await query.AnyAsync(cancellationToken);
        }
    }
}