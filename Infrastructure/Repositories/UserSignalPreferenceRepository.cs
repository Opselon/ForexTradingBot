using Application.Common.Interfaces; // برای IUserSignalPreferenceRepository و IAppDbContext
using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories
{
    /// <summary>
    /// پیاده‌سازی Repository برای موجودیت تنظیمات برگزیده سیگنال کاربر (UserSignalPreference).
    /// </summary>
    public class UserSignalPreferenceRepository : IUserSignalPreferenceRepository
    {
        private readonly IAppDbContext _context;

        public UserSignalPreferenceRepository(IAppDbContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public async Task<UserSignalPreference?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return await _context.UserSignalPreferences
                .Include(usp => usp.Category) // بارگذاری اطلاعات دسته
                                              // .Include(usp => usp.User) // معمولاً نیازی نیست، چون با UserId کار می‌کنیم
                .FirstOrDefaultAsync(usp => usp.Id == id, cancellationToken);
        }

        public async Task<IEnumerable<UserSignalPreference>> GetPreferencesByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            return await _context.UserSignalPreferences
                .Where(usp => usp.UserId == userId)
                .Include(usp => usp.Category) // برای نمایش نام دسته و ...
                .OrderBy(usp => usp.Category.Name) // مرتب‌سازی بر اساس نام دسته
                .ToListAsync(cancellationToken);
        }

        public async Task<bool> IsUserSubscribedToCategoryAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken = default)
        {
            return await _context.UserSignalPreferences
                .AnyAsync(usp => usp.UserId == userId && usp.CategoryId == categoryId, cancellationToken);
        }

        public async Task AddAsync(UserSignalPreference preference, CancellationToken cancellationToken = default)
        {
            if (preference == null)
            {
                throw new ArgumentNullException(nameof(preference));
            }

            // بررسی اینکه آیا این ترکیب قبلاً وجود دارد یا خیر (اختیاری، بستگی به منطق شما)
            bool exists = await IsUserSubscribedToCategoryAsync(preference.UserId, preference.CategoryId, cancellationToken);
            if (!exists)
            {
                _ = await _context.UserSignalPreferences.AddAsync(preference, cancellationToken);
                // SaveChangesAsync در Unit of Work / Service
            }
            // اگر وجود داشته باشد، می‌توان یک Exception یا یک نتیجه خاص برگرداند.
        }

        public async Task SetUserPreferencesAsync(Guid userId, IEnumerable<Guid> categoryIds, CancellationToken cancellationToken = default)
        {
            // 1. حذف تنظیمات برگزیده موجود کاربر
            var existingPreferences = await _context.UserSignalPreferences
                .Where(usp => usp.UserId == userId)
                .ToListAsync(cancellationToken);

            if (existingPreferences.Any())
            {
                _context.UserSignalPreferences.RemoveRange(existingPreferences);
            }

            // 2. اضافه کردن تنظیمات برگزیده جدید
            if (categoryIds != null && categoryIds.Any())
            {
                var newPreferences = categoryIds.Select(categoryId => new UserSignalPreference
                {
                    Id = Guid.NewGuid(), // اگر Id توسط دیتابیس تولید نمی‌شود
                    UserId = userId,
                    CategoryId = categoryId,
                    CreatedAt = DateTime.UtcNow
                });
                await _context.UserSignalPreferences.AddRangeAsync(newPreferences, cancellationToken);
            }
            // SaveChangesAsync در Unit of Work / Service
        }


        public async Task DeleteAsync(UserSignalPreference preference, CancellationToken cancellationToken = default)
        {
            if (preference == null)
            {
                throw new ArgumentNullException(nameof(preference));
            }

            _ = _context.UserSignalPreferences.Remove(preference);
            await Task.CompletedTask;
            // SaveChangesAsync در Unit of Work / Service
        }

        public async Task<bool> DeleteAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken = default)
        {
            var preferenceToDelete = await _context.UserSignalPreferences
                .FirstOrDefaultAsync(usp => usp.UserId == userId && usp.CategoryId == categoryId, cancellationToken);

            if (preferenceToDelete != null)
            {
                _ = _context.UserSignalPreferences.Remove(preferenceToDelete);
                return true; // آماده برای ذخیره و حذف
            }
            return true; // رکوردی برای حذف وجود نداشت، پس عملیات موفقیت آمیز تلقی می‌شود
        }

        public async Task<IEnumerable<Guid>> GetUserIdsByCategoryIdAsync(Guid categoryId, CancellationToken cancellationToken = default)
        {
            return await _context.UserSignalPreferences
                .Where(usp => usp.CategoryId == categoryId)
                .Select(usp => usp.UserId)
                .Distinct() // برای جلوگیری از شناسه‌های تکراری کاربر (اگرچه در این مدل نباید اتفاق بیفتد)
                .ToListAsync(cancellationToken);
        }
    }
}