using Application.Common.Interfaces; // برای IUserRepository, ITokenWalletRepository, ISubscriptionRepository, IAppDbContext
using Application.DTOs;             // برای UserDto, RegisterUserDto, UpdateUserDto, SubscriptionDto
using Application.Interfaces;       // برای IUserService
using AutoMapper;                   // برای IMapper
using Domain.Entities;
using Domain.Enums;                 // برای UserLevel
using Microsoft.Extensions.Logging; // برای ILogger
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
// using Application.Common.Exceptions; // برای NotFoundException, ValidationException (توصیه می‌شود)

namespace Application.Services // ✅ Namespace صحیح برای پیاده‌سازی سرویس‌ها
{
    /// <summary>
    /// پیاده‌سازی سرویس مدیریت کاربران.
    /// از Repository ها برای تعامل با داده‌ها و از AutoMapper برای تبدیل DTO استفاده می‌کند.
    /// </summary>
    public class UserService : IUserService
    {
        private readonly IUserRepository _userRepository;
        private readonly ITokenWalletRepository _tokenWalletRepository;
        private readonly ISubscriptionRepository _subscriptionRepository; // برای پر کردن ActiveSubscription
        private readonly IMapper _mapper;
        private readonly IAppDbContext _context; // به عنوان Unit of Work برای SaveChangesAsync
        private readonly ILogger<UserService> _logger;

        public UserService(
            IUserRepository userRepository,
            ITokenWalletRepository tokenWalletRepository,
            ISubscriptionRepository subscriptionRepository, // تزریق شد
            IMapper mapper,
            IAppDbContext context,
            ILogger<UserService> logger)
        {
            _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            _tokenWalletRepository = tokenWalletRepository ?? throw new ArgumentNullException(nameof(tokenWalletRepository));
            _subscriptionRepository = subscriptionRepository ?? throw new ArgumentNullException(nameof(subscriptionRepository)); // مقداردهی شد
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<UserDto?> GetUserByTelegramIdAsync(string telegramId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(telegramId))
            {
                _logger.LogWarning("Attempted to get user with null or empty Telegram ID.");
                return null;
            }

            _logger.LogInformation("Fetching user by Telegram ID: {TelegramId}", telegramId);
            var user = await _userRepository.GetByTelegramIdAsync(telegramId, cancellationToken); // فرض: این متد TokenWallet را Include می‌کند

            if (user == null)
            {
                _logger.LogWarning("User with Telegram ID {TelegramId} not found.", telegramId);
                return null;
            }

            _logger.LogInformation("User with Telegram ID {TelegramId} found: {Username}", telegramId, user.Username);
            var userDto = _mapper.Map<UserDto>(user);

            // پر کردن اشتراک فعال به صورت دستی
            var activeSubscriptionEntity = await _subscriptionRepository.GetActiveSubscriptionByUserIdAsync(user.Id, cancellationToken);
            if (activeSubscriptionEntity != null)
            {
                userDto.ActiveSubscription = _mapper.Map<SubscriptionDto>(activeSubscriptionEntity);
            }

            return userDto;
        }

        public async Task<List<UserDto>> GetAllUsersAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Fetching all users.");
            var users = await _userRepository.GetAllAsync(cancellationToken); // فرض: این متد TokenWallet را Include می‌کند

            var userDtos = new List<UserDto>();
            foreach (var user in users)
            {
                var userDto = _mapper.Map<UserDto>(user);
                var activeSubscriptionEntity = await _subscriptionRepository.GetActiveSubscriptionByUserIdAsync(user.Id, cancellationToken);
                if (activeSubscriptionEntity != null)
                {
                    userDto.ActiveSubscription = _mapper.Map<SubscriptionDto>(activeSubscriptionEntity);
                }
                userDtos.Add(userDto);
            }
            return userDtos;
        }

        public async Task<UserDto?> GetUserByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Fetching user by ID: {UserId}", id);
            var user = await _userRepository.GetByIdAsync(id, cancellationToken); // فرض: این متد TokenWallet را Include می‌کند

            if (user == null)
            {
                _logger.LogWarning("User with ID {UserId} not found.", id);
                return null;
            }
            _logger.LogInformation("User with ID {UserId} found: {Username}", id, user.Username);
            var userDto = _mapper.Map<UserDto>(user);

            var activeSubscriptionEntity = await _subscriptionRepository.GetActiveSubscriptionByUserIdAsync(user.Id, cancellationToken);
            if (activeSubscriptionEntity != null)
            {
                userDto.ActiveSubscription = _mapper.Map<SubscriptionDto>(activeSubscriptionEntity);
            }
            return userDto;
        }

        public async Task<UserDto> RegisterUserAsync(RegisterUserDto registerDto, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Attempting to register new user with Telegram ID: {TelegramId}, Email: {Email}", registerDto.TelegramId, registerDto.Email);

            // ۱. اعتبارسنجی‌های اولیه (FluentValidation این کار را در Command Handler انجام می‌دهد)
            // در اینجا می‌توانیم اعتبارسنجی‌های مربوط به کسب‌وکار را انجام دهیم:
            if (await _userRepository.ExistsByEmailAsync(registerDto.Email, cancellationToken))
            {
                _logger.LogWarning("Registration failed: Email {Email} already exists.", registerDto.Email);
                throw new Exception($"User with email {registerDto.Email} already exists."); // یا ValidationException
            }
            if (await _userRepository.ExistsByTelegramIdAsync(registerDto.TelegramId, cancellationToken))
            {
                _logger.LogWarning("Registration failed: Telegram ID {TelegramId} already exists.", registerDto.TelegramId);
                throw new Exception($"User with Telegram ID {registerDto.TelegramId} already exists."); // یا ValidationException
            }

            // ۲. مپ کردن DTO به موجودیت User
            var user = _mapper.Map<User>(registerDto);
            user.Id = Guid.NewGuid(); // تولید شناسه
            user.Level = UserLevel.Free; // سطح پیش‌فرض
            user.CreatedAt = DateTime.UtcNow;

            // ۳. اضافه کردن کاربر به Repository
            await _userRepository.AddAsync(user, cancellationToken);

            // ۴. ایجاد کیف پول توکن برای کاربر جدید
            var tokenWallet = new TokenWallet
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Balance = 0, // موجودی اولیه
               // CreatedAt = DateTime.UtcNow, // اگر در مدل TokenWallet دارید
                UpdatedAt = DateTime.UtcNow
            };
            await _tokenWalletRepository.AddAsync(tokenWallet, cancellationToken);

            // ۵. ذخیره تمام تغییرات در یک تراکنش واحد
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("User {Username} (ID: {UserId}) registered successfully with TokenWallet ID: {TokenWalletId}", user.Username, user.Id, tokenWallet.Id);

            // ۶. خواندن مجدد کاربر با جزئیات (برای شامل کردن TokenWallet مپ شده) و برگرداندن DTO
            var createdUserWithDetails = await _userRepository.GetByIdAsync(user.Id, cancellationToken);
            if (createdUserWithDetails == null)
            {
                // این نباید اتفاق بیفتد اگر SaveChangesAsync موفقیت‌آمیز بوده
                _logger.LogError("Failed to retrieve newly created user {UserId} after registration.", user.Id);
                throw new Exception("Failed to retrieve user after registration.");
            }

            var userDto = _mapper.Map<UserDto>(createdUserWithDetails);
            // ActiveSubscription برای کاربر جدید null خواهد بود مگر اینکه بلافاصله یک اشتراک ایجاد شود.
            userDto.ActiveSubscription = null;
            return userDto;
        }

        public async Task UpdateUserAsync(Guid userId, UpdateUserDto updateDto, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Attempting to update user with ID: {UserId}", userId);
            var user = await _userRepository.GetByIdAsync(userId, cancellationToken); // GetByIdAsync باید TokenWallet را هم Include کند اگر قرار است در DTO باشد

            if (user == null)
            {
                _logger.LogWarning("User with ID {UserId} not found for update.", userId);
                throw new Exception($"User with ID {userId} not found."); // یا NotFoundException
            }

            // بررسی تکراری بودن ایمیل جدید (اگر تغییر کرده)
            if (!string.IsNullOrWhiteSpace(updateDto.Email) && !user.Email.Equals(updateDto.Email, StringComparison.OrdinalIgnoreCase))
            {
                if (await _userRepository.ExistsByEmailAsync(updateDto.Email, cancellationToken))
                {
                    _logger.LogWarning("Update failed for UserID {UserId}: New email {Email} already exists.", userId, updateDto.Email);
                    throw new Exception($"Another user with email {updateDto.Email} already exists.");
                }
            }

            // مپ کردن فیلدهای غیر null از DTO به موجودیت User
            // (با فرض اینکه مپینگ UpdateUserDto به User با .ForAllMembers(opts => opts.Condition(...)) پیکربندی شده)
            _mapper.Map(updateDto, user);
            // user.UpdatedAt = DateTime.UtcNow; // اگر فیلد UpdatedAt در User دارید

            // _userRepository.UpdateAsync(user, cancellationToken); // EF Core تغییرات را ردیابی می‌کند. این متد در Repository می‌تواند خالی باشد.
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("User with ID {UserId} updated successfully.", userId);
        }

        public async Task DeleteUserAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Attempting to delete user with ID: {UserId}", id);
            var user = await _userRepository.GetByIdAsync(id, cancellationToken);

            if (user == null)
            {
                _logger.LogWarning("User with ID {UserId} not found for deletion. No action taken.", id);
                // throw new NotFoundException(nameof(User), id); // یا به سادگی بازگردید اگر "عدم وجود" به معنی "عملیات انجام شده" است
                return;
            }

            // قبل از حذف کاربر، می‌توانید منطق دیگری را نیز اجرا کنید
            // مانند حذف اشتراک‌ها، غیرفعال کردن سیگنال‌ها و ... (بستگی به رفتار OnDelete در DbContext دارد)

            await _userRepository.DeleteAsync(user, cancellationToken); // یا _userRepository.DeleteAsync(id, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("User with ID {UserId} deleted successfully.", id);
        }
    }
}