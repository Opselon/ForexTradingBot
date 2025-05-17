using Application.Common.Interfaces;
using Application.DTOs;
using Application.Interface;
using AutoMapper;
using Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Application.Services
{
    public class SubscriptionService : ISubscriptionService
    {
        private readonly ISubscriptionRepository _subscriptionRepository;
        private readonly IUserRepository _userRepository;
        private readonly IMapper _mapper;
        private readonly IAppDbContext _context;
        private readonly ILogger<SubscriptionService> _logger;

        public SubscriptionService(
            ISubscriptionRepository subscriptionRepository,
            IUserRepository userRepository,
            IMapper mapper,
            IAppDbContext context,
            ILogger<SubscriptionService> logger)
        {
            _subscriptionRepository = subscriptionRepository ?? throw new ArgumentNullException(nameof(subscriptionRepository));
            _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<SubscriptionDto> CreateSubscriptionAsync(CreateSubscriptionDto createSubscriptionDto, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Attempting to create subscription for UserID {UserId}", createSubscriptionDto.UserId);
            var user = await _userRepository.GetByIdAsync(createSubscriptionDto.UserId, cancellationToken);
            if (user == null)
            {
                _logger.LogWarning("User with ID {UserId} not found for creating subscription.", createSubscriptionDto.UserId);
                throw new Exception($"User with ID {createSubscriptionDto.UserId} not found."); // یا NotFoundException
            }

            //  منطق بیشتر:
            //  - بررسی اینکه آیا کاربر در حال حاضر اشتراک فعال همپوشان دارد.
            //  - اگر از PlanId استفاده می‌کنید، اعتبار PlanId را بررسی کنید.
            //  - بررسی پرداخت (اگر این سرویس مسئول آن است یا از سرویس پرداخت دیگری فراخوانی می‌شود).

            var subscription = _mapper.Map<Subscription>(createSubscriptionDto);
            subscription.Id = Guid.NewGuid();
            subscription.CreatedAt = DateTime.UtcNow;

            await _subscriptionRepository.AddAsync(subscription, cancellationToken);
            //  به‌روزرسانی UserLevel کاربر بر اساس اشتراک جدید (اگر لازم است)
            // user.Level = ... (منطق تعیین سطح بر اساس اشتراک)
            // await _userRepository.UpdateAsync(user, cancellationToken);
            
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Subscription with ID {SubscriptionId} created successfully for UserID {UserId}", subscription.Id, subscription.UserId);

            return _mapper.Map<SubscriptionDto>(subscription);
        }

        public async Task<SubscriptionDto?> GetActiveSubscriptionByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Fetching active subscription for UserID {UserId}", userId);
            var subscription = await _subscriptionRepository.GetActiveSubscriptionByUserIdAsync(userId, cancellationToken);
            if (subscription == null)
            {
                _logger.LogDebug("No active subscription found for UserID {UserId}", userId);
                return null;
            }
            return _mapper.Map<SubscriptionDto>(subscription);
        }

        public async Task<IEnumerable<SubscriptionDto>> GetUserSubscriptionsAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Fetching all subscriptions for UserID {UserId}", userId);
            var subscriptions = await _subscriptionRepository.GetSubscriptionsByUserIdAsync(userId, cancellationToken);
            return _mapper.Map<IEnumerable<SubscriptionDto>>(subscriptions);
        }

        public async Task<SubscriptionDto?> GetSubscriptionByIdAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Fetching subscription with ID {SubscriptionId}", subscriptionId);
            var subscription = await _subscriptionRepository.GetByIdAsync(subscriptionId, cancellationToken);
            if (subscription == null)
            {
                _logger.LogWarning("Subscription with ID {SubscriptionId} not found.", subscriptionId);
                return null;
            }
            return _mapper.Map<SubscriptionDto>(subscription);
        }
    }
}