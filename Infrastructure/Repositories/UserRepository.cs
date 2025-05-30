// File: Infrastructure/Persistence/Repositories/UserRepository.cs

#region Usings
// Standard .NET & NuGet
// Project specific
using Application.Common.Interfaces; // For IUserRepository and IAppDbContext
using Domain.Entities;             // For User, Subscription, UserPreference entities
using Microsoft.EntityFrameworkCore; // For EF Core specific methods
using Microsoft.Extensions.Logging; // Added for logging capabilities
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions; // For Expression<Func<User, bool>>
using System.Threading;
using System.Threading.Tasks;
// using Domain.Enums; // Assuming UserLevel enum exists - uncomment if used
#endregion

namespace Infrastructure.Persistence.Repositories
{
    public class UserRepository : IUserRepository
    {
        private readonly IAppDbContext _context;
        private readonly ILogger<UserRepository> _logger;

        public UserRepository(IAppDbContext context, ILogger<UserRepository> logger)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task<IEnumerable<User>> GetUsersForNewsNotificationAsync(
           Guid? newsItemSignalCategoryId,
           bool isNewsItemVipOnly,
           CancellationToken cancellationToken = default)
        {
            _logger.LogInformation(
                "UserRepository: Fetching users for news notification. CategoryId: {CategoryId}, IsVipOnly: {IsVip}",
                newsItemSignalCategoryId, isNewsItemVipOnly);

            IQueryable<User> query = _context.Users
                                          .Where(u => u.EnableRssNewsNotifications == true);
            // Optional: query = query.Where(u => u.IsActive == true); // If User has an IsActive flag

            if (isNewsItemVipOnly)
            {
                _logger.LogDebug("UserRepository: Applying VIP filter for news notification.");
                var now = DateTime.UtcNow;
                query = query.Where(u => u.Subscriptions.Any(s =>
                                            s.StartDate <= now &&
                                            s.EndDate >= now &&
                                            // s.IsActive == true && // REMOVED: Assumed Subscription does not have IsActive
                                            IsConsideredVipSubscriptionByPlan(s)
                                        ));
            }

            if (newsItemSignalCategoryId.HasValue)
            {
                Guid categoryId = newsItemSignalCategoryId.Value;
                _logger.LogDebug("UserRepository: Applying category preference filter for CategoryId: {CategoryId}", categoryId);
                query = query.Where(u =>
                    !u.Preferences.Any() ||
                    u.Preferences.Any(p => p.CategoryId == categoryId) // REMOVED: p.IsActive == true (Assumed UserSignalPreference does not have IsActive)
                );
            }

            _logger.LogDebug("UserRepository: Executing query to retrieve eligible users.");
            var eligibleUsers = await query
                                      .AsNoTracking()
                                      .Distinct()
                                      .ToListAsync(cancellationToken);

            _logger.LogInformation("UserRepository: Found {UserCount} eligible users for news notification.", eligibleUsers.Count);
            return eligibleUsers;
        }

        private static bool IsConsideredVipSubscriptionByPlan(Subscription subscription)
        {
            // ⚠️ --- CRITICAL PLACEHOLDER --- ⚠️
            // Replace this with your actual logic.
            // This simplified version assumes ANY active (by date) subscription is VIP if 'isNewsItemVipOnly' is true.
            // You MUST tailor this to your specific plan/VIP structure.
            // Example based on Plan having an IsVip property (assuming Subscription has a Plan navigation property):
            // return subscription.Plan != null && subscription.Plan.IsVip;
            // Or based on SubscriptionType enum:
            // return subscription.Type == SubscriptionType.Premium || subscription.Type == SubscriptionType.Vip;

            return true; // Placeholder: If code reaches here and subscription dates are valid, assume VIP.
        }

        /// <inheritdoc />
        public async Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _logger.LogTrace("UserRepository: Fetching user by ID: {UserId}.", id);
            return await _context.Users
                .Include(u => u.TokenWallet)
                .FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        }

        /// <inheritdoc />
        public async Task<User?> GetByTelegramIdAsync(string telegramId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(telegramId))
            {
                _logger.LogWarning("UserRepository: GetByTelegramIdAsync called with null or empty telegramId.");
                return null;
            }
            _logger.LogTrace("UserRepository: Fetching user by TelegramID: {TelegramId}.", telegramId);
            return await _context.Users
                .Include(u => u.TokenWallet)
                .FirstOrDefaultAsync(u => u.TelegramId == telegramId, cancellationToken);
        }

        /// <inheritdoc />
        public async Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                _logger.LogWarning("UserRepository: GetByEmailAsync called with null or empty email.");
                return null;
            }
            string lowerEmail = email.ToLowerInvariant();
            _logger.LogTrace("UserRepository: Fetching user by Email (case-insensitive): {Email}.", lowerEmail);
            return await _context.Users
                .Include(u => u.TokenWallet)
                .FirstOrDefaultAsync(u => u.Email.ToLower() == lowerEmail, cancellationToken); // Using ToLower() for Email comparison
        }

        /// <inheritdoc />
        public async Task<IEnumerable<User>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogTrace("UserRepository: Fetching all users, AsNoTracking.");
            return await _context.Users
                .AsNoTracking()
                .Include(u => u.TokenWallet)
                .OrderBy(u => u.Username)
                .ToListAsync(cancellationToken);
        }

        /// <inheritdoc />
        public async Task<IEnumerable<User>> FindAsync(Expression<Func<User, bool>> predicate, CancellationToken cancellationToken = default)
        {
            if (predicate == null)
            {
                _logger.LogError("UserRepository: FindAsync called with a null predicate.");
                throw new ArgumentNullException(nameof(predicate));
            }
            _logger.LogTrace("UserRepository: Finding users with predicate, AsNoTracking.");
            return await _context.Users
                .Where(predicate)
                .AsNoTracking()
                .Include(u => u.TokenWallet)
                .ToListAsync(cancellationToken);
        }

        /// <inheritdoc />
        public async Task AddAsync(User user, CancellationToken cancellationToken = default)
        {
            if (user == null)
            {
                _logger.LogError("UserRepository: Attempted to add a null User object.");
                throw new ArgumentNullException(nameof(user));
            }
            _logger.LogInformation("UserRepository: Adding new user. Username: {Username}, Email: {Email}.", user.Username, user.Email);
            await _context.Users.AddAsync(user, cancellationToken);
        }

        /// <inheritdoc />
        public Task UpdateAsync(User user, CancellationToken cancellationToken = default)
        {
            if (user == null)
            {
                _logger.LogError("UserRepository: Attempted to update with a null User object.");
                throw new ArgumentNullException(nameof(user));
            }
            _logger.LogInformation("UserRepository: Marking user for update. UserID: {UserId}, Username: {Username}.", user.Id, user.Username);
            var entry = _context.Users.Entry(user);
            if (entry.State == EntityState.Detached)
            {
                _logger.LogTrace("UserRepository: Attaching detached User entity (ID: {UserId}) for update.", user.Id);
                _context.Users.Attach(user);
            }
            entry.State = EntityState.Modified;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task DeleteAsync(User user, CancellationToken cancellationToken = default)
        {
            if (user == null)
            {
                _logger.LogError("UserRepository: Attempted to delete a null User object.");
                throw new ArgumentNullException(nameof(user));
            }
            _logger.LogInformation("UserRepository: Marking user for deletion. UserID: {UserId}, Username: {Username}.", user.Id, user.Username);
            _context.Users.Remove(user);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) // Return type changed to Task
        {
            _logger.LogInformation("UserRepository: Attempting to delete user by ID: {UserId}.", id);
            var userToDelete = await _context.Users.FindAsync(new object[] { id }, cancellationToken);

            if (userToDelete == null)
            {
                _logger.LogWarning("UserRepository: User with ID {UserId} not found for deletion.", id);
                return; // Simply return if not found, as per Task (void) signature
            }
            _context.Users.Remove(userToDelete);
            _logger.LogInformation("UserRepository: User (ID: {UserId}, Username: {Username}) marked for deletion.", userToDelete.Id, userToDelete.Username);
            // SaveChangesAsync will be called by UoW/Service. No return true/false needed for Task.
        }

        /// <inheritdoc />
        public async Task<bool> ExistsByEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(email)) return false;
            string lowerEmail = email.ToLowerInvariant();
            _logger.LogTrace("UserRepository: Checking existence by Email (case-insensitive): {Email}.", lowerEmail);
            return await _context.Users.AnyAsync(u => u.Email.ToLower() == lowerEmail, cancellationToken); // Using ToLower() for Email comparison
        }

        /// <inheritdoc />
        public async Task<bool> ExistsByTelegramIdAsync(string telegramId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(telegramId)) return false;
            _logger.LogTrace("UserRepository: Checking existence by TelegramID: {TelegramId}.", telegramId);
            return await _context.Users.AnyAsync(u => u.TelegramId == telegramId, cancellationToken);
        }

        /// <inheritdoc />
        public async Task DeleteAndSaveAsync(User user, CancellationToken cancellationToken)
        {
            if (user == null)
            {
                _logger.LogError("UserRepository: Attempted to delete and save a null User object.");
                throw new ArgumentNullException(nameof(user));
            }
            _logger.LogInformation("UserRepository: Marking user for deletion and immediate save. UserID: {UserId}, Username: {Username}.", user.Id, user.Username);
            _context.Users.Remove(user);
            // Now, save the changes immediately
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("UserRepository: Successfully deleted user (ID: {UserId}) and saved changes.", user.Id);
        }

        /// <inheritdoc />
        public async Task<IEnumerable<User>> GetUsersWithNotificationSettingAsync(
            Expression<Func<User, bool>> notificationPredicate,
            CancellationToken cancellationToken = default)
        {
            if (notificationPredicate == null)
            {
                _logger.LogError("UserRepository: GetUsersWithNotificationSettingAsync called with a null predicate.");
                throw new ArgumentNullException(nameof(notificationPredicate));
            }
            _logger.LogTrace("UserRepository: Fetching users with specific notification predicate, AsNoTracking.");
            return await _context.Users
                .Where(notificationPredicate)
                .AsNoTracking()
                // .Where(u => u.IsActive) // Optional global filter
                .ToListAsync(cancellationToken);
        }
    }
}