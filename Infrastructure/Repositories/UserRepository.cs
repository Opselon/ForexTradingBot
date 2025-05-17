using Application.Common.Interfaces; // برای IUserRepository و IAppDbContext
using Domain.Entities;
using Microsoft.EntityFrameworkCore; // برای متدهای EF Core مانند FirstOrDefaultAsync, ToListAsync و ...
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace Infrastructure.Persistence.Repositories
{
    /// <summary>
    /// پیاده‌سازی Repository برای موجودیت کاربر (User).
    /// از AppDbContext برای تعامل با پایگاه داده استفاده می‌کند.
    /// </summary>
    public class UserRepository : IUserRepository
    {
        private readonly IAppDbContext _context;

        public UserRepository(IAppDbContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public async Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return await _context.Users
                .Include(u => u.TokenWallet) // مثال: بارگذاری موجودیت مرتبط
                                             // .Include(u => u.Subscriptions) // در صورت نیاز
                .FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        }

        public async Task<User?> GetByTelegramIdAsync(string telegramId, CancellationToken cancellationToken = default)
        {
            return await _context.Users
                .Include(u => u.TokenWallet)
                .FirstOrDefaultAsync(u => u.TelegramId == telegramId, cancellationToken);
        }

        public async Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            return await _context.Users
                .Include(u => u.TokenWallet)
                .FirstOrDefaultAsync(u => u.Email.ToLower() == email.ToLower(), cancellationToken); // مقایسه case-insensitive
        }

        public async Task<IEnumerable<User>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return await _context.Users
                .Include(u => u.TokenWallet)
                .ToListAsync(cancellationToken);
        }

        public async Task<IEnumerable<User>> FindAsync(Expression<Func<User, bool>> predicate, CancellationToken cancellationToken = default)
        {
            return await _context.Users
                .Where(predicate)
                .Include(u => u.TokenWallet)
                .ToListAsync(cancellationToken);
        }

        public async Task AddAsync(User user, CancellationToken cancellationToken = default)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));
            await _context.Users.AddAsync(user, cancellationToken);
            // SaveChangesAsync باید در سطح Unit of Work یا سرویس فراخوانی شود.
        }

        public Task UpdateAsync(User user, CancellationToken cancellationToken = default)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));
            // EF Core به طور خودکار تغییرات موجودیت‌های ردیابی شده را تشخیص می‌دهد.
            // _context.Users.Update(user); // این معمولاً لازم نیست اگر موجودیت قبلاً ردیابی شده باشد.
            // اگر موجودیت ردیابی نشده، باید آن را Attach و State آن را Modified کنید.
            _context.Users.Entry(user).State = EntityState.Modified; // یک راه برای اطمینان از علامت‌گذاری به عنوان ویرایش شده
            return Task.CompletedTask;
            // SaveChangesAsync باید در سطح Unit of Work یا سرویس فراخوانی شود.
        }

        public async Task DeleteAsync(User user, CancellationToken cancellationToken = default)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));
            _context.Users.Remove(user);
            await Task.CompletedTask; // به تعویق انداختن حذف واقعی تا SaveChangesAsync
        }

        public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var userToDelete = await GetByIdAsync(id, cancellationToken);
            if (userToDelete != null)
            {
                _context.Users.Remove(userToDelete);
            }
            // SaveChangesAsync باید در سطح Unit of Work یا سرویس فراخوانی شود.
        }

        public async Task<bool> ExistsByEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            return await _context.Users.AnyAsync(u => u.Email.ToLower() == email.ToLower(), cancellationToken);
        }

        public async Task<bool> ExistsByTelegramIdAsync(string telegramId, CancellationToken cancellationToken = default)
        {
            return await _context.Users.AnyAsync(u => u.TelegramId == telegramId, cancellationToken);
        }

        // پیاده‌سازی متدهای اضافی در صورت نیاز
        // public async Task<User?> GetUserWithSubscriptionsAsync(Guid userId, CancellationToken cancellationToken = default)
        // {
        //     return await _context.Users
        //         .Include(u => u.Subscriptions)
        //         .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        // }
    }
}