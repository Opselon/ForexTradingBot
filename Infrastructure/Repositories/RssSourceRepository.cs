using Application.Common.Interfaces; // برای IRssSourceRepository و IAppDbContext
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Infrastructure.Persistence.Repositories
{
    /// <summary>
    /// پیاده‌سازی Repository برای موجودیت منبع RSS (RssSource).
    /// از AppDbContext برای تعامل با پایگاه داده استفاده می‌کند.
    /// </summary>
    public class RssSourceRepository : IRssSourceRepository
    {
        private readonly IAppDbContext _context;

        public RssSourceRepository(IAppDbContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public async Task<RssSource?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return await _context.RssSources
                .FirstOrDefaultAsync(rs => rs.Id == id, cancellationToken);
        }

        public async Task<RssSource?> GetByUrlAsync(string url, CancellationToken cancellationToken = default)
        {
            // URL ها باید به صورت نرمالایز شده مقایسه شوند (مثلاً بدون در نظر گرفتن http/https یا www)
            // اما برای سادگی فعلی، مقایسه مستقیم انجام می‌شود. در عمل، نرمال‌سازی URL مهم است.
            var trimmedUrl = url.Trim();
            return await _context.RssSources
                .FirstOrDefaultAsync(rs => rs.Url == trimmedUrl, cancellationToken);
        }

        public async Task<IEnumerable<RssSource>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return await _context.RssSources
                .OrderBy(rs => rs.SourceName)
                .ToListAsync(cancellationToken);
        }

        public async Task<IEnumerable<RssSource>> GetActiveSourcesAsync(CancellationToken cancellationToken = default)
        {
            return await _context.RssSources
                .Where(rs => rs.IsActive)
                .OrderBy(rs => rs.SourceName)
                .ToListAsync(cancellationToken);
        }

        public async Task AddAsync(RssSource rssSource, CancellationToken cancellationToken = default)
        {
            if (rssSource == null) throw new ArgumentNullException(nameof(rssSource));
            rssSource.Url = rssSource.Url.Trim(); // Trim URL
            rssSource.SourceName = rssSource.SourceName.Trim(); // Trim Name
            await _context.RssSources.AddAsync(rssSource, cancellationToken);
            // SaveChangesAsync در Unit of Work / Service
        }

        public Task UpdateAsync(RssSource rssSource, CancellationToken cancellationToken = default)
        {
            if (rssSource == null) throw new ArgumentNullException(nameof(rssSource));
            rssSource.Url = rssSource.Url.Trim();
            rssSource.SourceName = rssSource.SourceName.Trim();
            rssSource.UpdatedAt = DateTime.UtcNow; // به‌روزرسانی UpdatedAt اگر در مدل RssSource وجود داشته باشد
            _context.RssSources.Entry(rssSource).State = EntityState.Modified;
            // SaveChangesAsync در Unit of Work / Service
            return Task.CompletedTask;
        }

        public async Task DeleteAsync(RssSource rssSource, CancellationToken cancellationToken = default)
        {
            if (rssSource == null) throw new ArgumentNullException(nameof(rssSource));
            _context.RssSources.Remove(rssSource);
            await Task.CompletedTask;
            // SaveChangesAsync در Unit of Work / Service
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var sourceToDelete = await GetByIdAsync(id, cancellationToken);
            if (sourceToDelete == null)
            {
                return false; // پیدا نشد
            }
            await DeleteAsync(sourceToDelete, cancellationToken);
            return true; // آماده برای ذخیره و حذف
        }

        public async Task<bool> ExistsByUrlAsync(string url, Guid? excludeId = null, CancellationToken cancellationToken = default)
        {
            var trimmedUrl = url.Trim();
            var query = _context.RssSources.Where(rs => rs.Url == trimmedUrl);

            if (excludeId.HasValue)
            {
                query = query.Where(rs => rs.Id != excludeId.Value);
            }

            return await query.AnyAsync(cancellationToken);
        }

        // پیاده‌سازی متدهای اضافی در صورت نیاز:
        // public async Task UpdateLastFetchedAtAsync(Guid id, DateTime fetchedAt, CancellationToken cancellationToken = default)
        // {
        //     var source = await GetByIdAsync(id, cancellationToken);
        //     if (source != null)
        //     {
        //         source.LastFetchedAt = fetchedAt; // فرض کنید LastFetchedAt در مدل RssSource وجود دارد
        //         source.UpdatedAt = DateTime.UtcNow;
        //         _context.RssSources.Update(source);
        //         // SaveChangesAsync در Unit of Work / Service
        //     }
        // }
    }
}