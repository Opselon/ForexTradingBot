using Application.Common.Interfaces; // برای ISignalAnalysisRepository و IAppDbContext
using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories
{
    /// <summary>
    /// پیاده‌سازی Repository برای موجودیت تحلیل سیگنال (SignalAnalysis).
    /// از AppDbContext برای تعامل با پایگاه داده استفاده می‌کند.
    /// </summary>
    public class SignalAnalysisRepository : ISignalAnalysisRepository
    {
        private readonly IAppDbContext _context;

        public SignalAnalysisRepository(IAppDbContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public async Task<SignalAnalysis?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return await _context.SignalAnalyses
                // .Include(sa => sa.Signal) // بارگذاری سیگنال مرتبط (اختیاری)
                .FirstOrDefaultAsync(sa => sa.Id == id, cancellationToken);
        }

        public async Task<IEnumerable<SignalAnalysis>> GetAnalysesBySignalIdAsync(Guid signalId, CancellationToken cancellationToken = default)
        {
            return await _context.SignalAnalyses
                .Where(sa => sa.SignalId == signalId)
                .OrderByDescending(sa => sa.CreatedAt) // نمایش جدیدترین تحلیل‌ها ابتدا
                .ToListAsync(cancellationToken);
        }

        public async Task AddAsync(SignalAnalysis analysis, CancellationToken cancellationToken = default)
        {
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));
            await _context.SignalAnalyses.AddAsync(analysis, cancellationToken);
            // SaveChangesAsync در Unit of Work / Service
        }

        public Task UpdateAsync(SignalAnalysis analysis, CancellationToken cancellationToken = default)
        {
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));
            // اگر فیلد UpdatedAt در SignalAnalysis وجود داشت، اینجا به‌روز می‌شد.
            // analysis.UpdatedAt = DateTime.UtcNow;
            _context.SignalAnalyses.Entry(analysis).State = EntityState.Modified;
            // SaveChangesAsync در Unit of Work / Service
            return Task.CompletedTask;
        }

        public async Task DeleteAsync(SignalAnalysis analysis, CancellationToken cancellationToken = default)
        {
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));
            _context.SignalAnalyses.Remove(analysis);
            await Task.CompletedTask;
            // SaveChangesAsync در Unit of Work / Service
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var analysisToDelete = await GetByIdAsync(id, cancellationToken);
            if (analysisToDelete == null)
            {
                return false; // پیدا نشد
            }
            await DeleteAsync(analysisToDelete, cancellationToken);
            return true; // آماده برای ذخیره و حذف
        }
    }
}