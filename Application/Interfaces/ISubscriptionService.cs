using Application.DTOs;

namespace Application.Interface
{
    public interface ISubscriptionService
    {
        Task<SubscriptionDto> CreateSubscriptionAsync(CreateSubscriptionDto createSubscriptionDto, CancellationToken cancellationToken = default);
        Task<SubscriptionDto?> GetActiveSubscriptionByUserIdAsync(Guid userId, CancellationToken cancellationToken = default); // تغییر نام برای وضوح
        Task<IEnumerable<SubscriptionDto>> GetUserSubscriptionsAsync(Guid userId, CancellationToken cancellationToken = default);
        Task<SubscriptionDto?> GetSubscriptionByIdAsync(Guid subscriptionId, CancellationToken cancellationToken = default);
        // Task CancelSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default);
    }
}