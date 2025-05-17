using Application.Common.Interfaces; // برای ITransactionRepository و IAppDbContext
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Infrastructure.Persistence.Repositories
{
    /// <summary>
    /// پیاده‌سازی Repository برای موجودیت تراکنش (Transaction).
    /// از AppDbContext برای تعامل با پایگاه داده استفاده می‌کند.
    /// </summary>
    public class TransactionRepository : ITransactionRepository
    {
        private readonly IAppDbContext _context;

        public TransactionRepository(IAppDbContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public async Task<Transaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return await _context.Transactions
                .Include(t => t.User) // بارگذاری کاربر مرتبط (اختیاری)
                .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        }

        public async Task<IEnumerable<Transaction>> GetTransactionsByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            return await _context.Transactions
                .Where(t => t.UserId == userId)
                .OrderByDescending(t => t.Timestamp) // نمایش جدیدترین تراکنش‌ها ابتدا
                .ToListAsync(cancellationToken);
        }

        public async Task<IEnumerable<Transaction>> GetFilteredTransactionsByUserIdAsync(
            Guid userId,
            TransactionType? type = null,
            DateTime? startDate = null,
            DateTime? endDate = null,
            CancellationToken cancellationToken = default)
        {
            var query = _context.Transactions.Where(t => t.UserId == userId);

            if (type.HasValue)
            {
                query = query.Where(t => t.Type == type.Value);
            }

            if (startDate.HasValue)
            {
                query = query.Where(t => t.Timestamp >= startDate.Value);
            }

            if (endDate.HasValue)
            {
                // برای شامل کردن کل روز endDate، معمولاً آن را به پایان روز تنظیم می‌کنیم
                var inclusiveEndDate = endDate.Value.Date.AddDays(1).AddTicks(-1);
                query = query.Where(t => t.Timestamp <= inclusiveEndDate);
            }

            return await query
                .OrderByDescending(t => t.Timestamp)
                .ToListAsync(cancellationToken);
        }
        public async Task<Transaction?> GetByPaymentGatewayInvoiceIdAsync(string paymentGatewayInvoiceId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(paymentGatewayInvoiceId))
            {
                return null;
            }

            return await _context.Transactions
                .Include(t => t.User) //  بارگذاری کاربر مرتبط (اختیاری، اما ممکن است مفید باشد)
                .FirstOrDefaultAsync(t => t.PaymentGatewayInvoiceId == paymentGatewayInvoiceId, cancellationToken);
        }
        public async Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default)
        {
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            await _context.Transactions.AddAsync(transaction, cancellationToken);
            // SaveChangesAsync در Unit of Work / Service
        }
    }
}