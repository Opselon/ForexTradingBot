// File: Infrastructure/Persistence/Repositories/SubscriptionRepository.cs

#region Usings
// Standard .NET & NuGet
using System;
using System.Collections.Generic;
using System.Linq; // For Where, OrderByDescending etc.
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore; // For EF Core specific methods

// Project specific
using Application.Common.Interfaces; // For ISubscriptionRepository and IAppDbContext
using Domain.Entities;             // For Subscription entity (and potentially User if included)
using Microsoft.Extensions.Logging; // Added for logging capabilities
#endregion

namespace Infrastructure.Persistence.Repositories
{
    /// <summary>
    /// Implements IRssSourceRepository providing data access methods for RssSource entities
    /// using Entity Framework Core. Focuses on performance, robustness, and maintainability.
    /// </summary>
    public class SubscriptionRepository : ISubscriptionRepository
    {
        private readonly IAppDbContext _context;
        private readonly ILogger<SubscriptionRepository> _logger; // Added logger

        public SubscriptionRepository(IAppDbContext context, ILogger<SubscriptionRepository> logger) // Added logger
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger)); // Added logger
        }

        #region Read Operations

        /// <inheritdoc />
        public async Task<Subscription?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            // Optional: Eager load User. If User is frequently needed with Subscription, this is good.
            // Otherwise, consider a separate method like GetByIdWithDetailsAsync or make Include optional.
            // For now, respecting the original Include.
            _logger.LogTrace("SubscriptionRepository: Fetching subscription by ID: {SubscriptionId}, with User.", id);
            return await _context.Subscriptions
                .Include(s => s.User) // Consider performance implications of eager loading.
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        }


        /// <summary>
        /// Retrieves a subscription by its ID, optionally including related entities.
        /// </summary>
        /// <param name="id">The subscription ID.</param>
        /// <param name="includeUser">Whether to include the related User entity.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The subscription, or null if not found.</returns>
        public async Task<Subscription?> GetByIdAsync(Guid id, bool includeUser, CancellationToken cancellationToken = default)
        {
            _logger.LogTrace("SubscriptionRepository: Fetching subscription by ID: {SubscriptionId}, IncludeUser: {IncludeUserFlag}.", id, includeUser);
            IQueryable<Subscription> query = _context.Subscriptions;

            if (includeUser)
            {
                query = query.Include(s => s.User);
            }

            // Performance: AsNoTracking if this is purely for display.
            // Assuming it might be used for updates, so tracking is kept.
            // If you have distinct use cases, one with AsNoTracking for reads can be added.
            return await query.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        }


        /// <inheritdoc />
        public async Task<IEnumerable<Subscription>> GetSubscriptionsByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            // Performance: Using AsNoTracking as this is likely for display/listing.
            _logger.LogTrace("SubscriptionRepository: Fetching all subscriptions for UserID: {UserId}, AsNoTracking.", userId);
            return await _context.Subscriptions
                .Where(s => s.UserId == userId)
                .AsNoTracking() // Good for read-only scenarios
                .OrderByDescending(s => s.EndDate) // Shows newest/most relevant first
                .ThenByDescending(s => s.StartDate) // Secondary sort for consistent ordering
                .ToListAsync(cancellationToken);
        }

        /// <inheritdoc />
        public async Task<Subscription?> GetActiveSubscriptionByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow; // Consistency: Use a single 'now' value for the query.
            _logger.LogTrace("SubscriptionRepository: Fetching active subscription for UserID: {UserId} as of {CurrentTimeUtc}.", userId, now);

            // Performance: The Where clause should leverage database indexes on UserId, StartDate, EndDate.
            // A composite index on (UserId, StartDate, EndDate) would be highly beneficial.
            // OrderByDescending(s => s.EndDate) is okay for disambiguation if multiple actives (should be rare).
            // Optional: Include User if often needed with the active subscription.
            return await _context.Subscriptions
                .Include(s => s.User) // Eager load User if it's commonly needed with the active subscription. Remove if not.
                .Where(s => s.UserId == userId && s.StartDate <= now && s.EndDate >= now) // Added IsActive flag check
                .OrderByDescending(s => s.EndDate) // Prioritize subscription ending later
                .ThenByDescending(s => s.Id) // Ensure deterministic ordering if EndDates are identical
                .FirstOrDefaultAsync(cancellationToken);
        }

        /// <inheritdoc />
        public async Task<bool> HasActiveSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            _logger.LogTrace("SubscriptionRepository: Checking for active subscription for UserID: {UserId} as of {CurrentTimeUtc}.", userId, now);

            // Performance: AnyAsync is very efficient (translates to SQL EXISTS).
            // Ensure indexes on UserId, StartDate, EndDate, and IsActive.
            return await _context.Subscriptions
                .AnyAsync(s => s.UserId == userId && s.StartDate <= now && s.EndDate >= now, cancellationToken); // Added IsActive flag check
        }

        #endregion

        #region Write Operations

        /// <inheritdoc />
        public async Task AddAsync(Subscription subscription, CancellationToken cancellationToken = default)
        {
            // Robustness: Parameter validation.
            if (subscription == null)
            {
                _logger.LogError("SubscriptionRepository: Attempted to add a null Subscription object.");
                throw new ArgumentNullException(nameof(subscription));
            }

            // Robustness: Validate essential date logic if not handled by domain/service layer.
            // For example, EndDate must be after StartDate.
            if (subscription.EndDate < subscription.StartDate)
            {
                _logger.LogError("SubscriptionRepository: Attempted to add a Subscription where EndDate ({EndDate}) is before StartDate ({StartDate}).",
                                 subscription.EndDate, subscription.StartDate);
                throw new ArgumentException("Subscription EndDate cannot be before StartDate.", nameof(subscription));
            }

            // Robustness: Set timestamps if managed by application.
            var now = DateTime.UtcNow;
            subscription.CreatedAt = now; // Assuming CreatedAt property exists
            subscription.UpdatedAt = now; // Assuming UpdatedAt property exists


            await _context.Subscriptions.AddAsync(subscription, cancellationToken);
            // SaveChangesAsync is expected to be called by a Unit of Work or service layer.
        }

        /// <inheritdoc />
        public Task UpdateAsync(Subscription subscription, CancellationToken cancellationToken = default)
        {
            // Robustness: Parameter validation.
            if (subscription == null)
            {
                _logger.LogError("SubscriptionRepository: Attempted to update with a null Subscription object.");
                throw new ArgumentNullException(nameof(subscription));
            }

            if (subscription.EndDate < subscription.StartDate)
            {
                _logger.LogError("SubscriptionRepository: Attempted to update a Subscription where EndDate ({EndDate}) is before StartDate ({StartDate}). SubscriptionID: {SubscriptionId}",
                                 subscription.EndDate, subscription.StartDate, subscription.Id);
                throw new ArgumentException("Subscription EndDate cannot be before StartDate.", nameof(subscription));
            }

            // Robustness: Update 'UpdatedAt' timestamp.
            subscription.UpdatedAt = DateTime.UtcNow; // Assuming UpdatedAt property exists
            // The IsActive flag logic might be more complex and handled in a service layer,
            // e.g., based on current date or manual cancellation. Here, we assume it's set correctly by the caller.


            // EF Core tracks changes on entities fetched from the context.
            // Explicitly setting state is useful if entity was created outside or attached from detached state.
            var entry = _context.Subscriptions.Entry(subscription);
            if (entry.State == EntityState.Detached)
            {
                _logger.LogTrace("SubscriptionRepository: Attaching detached subscription entity (ID: {SubscriptionId}) for update.", subscription.Id);
                _context.Subscriptions.Attach(subscription);
            }
            entry.State = EntityState.Modified;

            return Task.CompletedTask;
            // SaveChangesAsync is expected to be called by a Unit of Work or service layer.
        }

        /// <inheritdoc />
        public Task DeleteAsync(Subscription subscription, CancellationToken cancellationToken = default)
        {
            // Robustness: Parameter validation.
            if (subscription == null)
            {
                _logger.LogError("SubscriptionRepository: Attempted to delete a null Subscription object.");
                throw new ArgumentNullException(nameof(subscription));
            }
            _logger.LogInformation("SubscriptionRepository: Marking subscription for deletion. ID: {SubscriptionId}, UserID: {UserId}",
                                   subscription.Id, subscription.UserId);
            _context.Subscriptions.Remove(subscription);
            return Task.CompletedTask;
            // SaveChangesAsync is expected to be called by a Unit of Work or service layer.
        }

        /// <summary>
        /// Deletes a subscription by its ID.
        /// This is a common pattern but often not directly exposed if soft delete or other logic is involved.
        /// Consider if a `SetInactiveAsync(Guid id)` might be more appropriate than hard delete.
        /// </summary>
        public async Task<bool> DeleteByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("SubscriptionRepository: Attempting to delete subscription by ID: {SubscriptionId}", id);
            var subscriptionToDelete = await _context.Subscriptions.FindAsync(new object[] { id }, cancellationToken); // More direct than GetByIdAsync if no Includes are needed.

            if (subscriptionToDelete == null)
            {
                _logger.LogWarning("SubscriptionRepository: Subscription with ID {SubscriptionId} not found for deletion.", id);
                return false;
            }

            _context.Subscriptions.Remove(subscriptionToDelete);
            _logger.LogInformation("SubscriptionRepository: Subscription ID {SubscriptionId} marked for deletion.", id);
            // Actual deletion occurs on SaveChangesAsync by Unit of Work/Service.
            return true;
        }

        #endregion
    }
}