using Application.Common.Interfaces; // برای IUserRepository, ITokenWalletRepository, ISubscriptionRepository, IAppDbContext
using Application.DTOs;             // برای UserDto, RegisterUserDto, UpdateUserDto, SubscriptionDto
using Application.Interfaces;       // برای IUserService
using AutoMapper;                   // برای IMapper
using Domain.Entities;
using Domain.Enums;                 // برای UserLevel
using Microsoft.Extensions.Logging; // برای ILogger
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
            _logger.LogInformation("Attempting to register new user. TelegramID: {TelegramId}, Email: {Email}, Username: {Username}",
                registerDto.TelegramId, registerDto.Email, registerDto.Username);

            // مرحله ۱: اعتبارسنجی‌های مربوط به کسب‌وکار (تکراری نبودن)
            if (await _userRepository.ExistsByEmailAsync(registerDto.Email, cancellationToken))
            {
                _logger.LogWarning("Registration failed: Email {Email} already exists.", registerDto.Email);
                throw new InvalidOperationException($"A user with the email '{registerDto.Email}' already exists."); // استفاده از Exception مناسب‌تر
            }
            if (await _userRepository.ExistsByTelegramIdAsync(registerDto.TelegramId, cancellationToken))
            {
                _logger.LogWarning("Registration failed: Telegram ID {TelegramId} already exists.", registerDto.TelegramId);
                throw new InvalidOperationException($"A user with the Telegram ID '{registerDto.TelegramId}' already exists.");
            }

            // مرحله ۲: ایجاد موجودیت User با استفاده از سازنده‌ای که TokenWallet را هم مقداردهی اولیه می‌کند یا به صورت دستی
            // اگر از سازنده User(username, telegramId, email) که TokenWallet را هم می‌سازد، استفاده می‌کنید:
            var user = new User(registerDto.Username, registerDto.TelegramId, registerDto.Email);
            // در این حالت، user.Id, user.CreatedAt, user.Level, user.TokenWallet.UserId, user.TokenWallet.Balance,
            // user.TokenWallet.CreatedAt, user.TokenWallet.UpdatedAt توسط سازنده User و TokenWallet.Create مقداردهی شده‌اند.

            // اگر از سازنده پیش‌فرض User و AutoMapper استفاده می‌کنید و TokenWallet را جدا می‌سازید:

            user.Id = Guid.NewGuid();
            user.Level = UserLevel.Free;
            user.CreatedAt = DateTime.UtcNow;
            user.EnableGeneralNotifications = true; // پیش‌فرض‌ها از سازنده User می‌آیند
            user.EnableRssNewsNotifications = true;
            user.EnableVipSignalNotifications = false;

            // ایجاد TokenWallet با استفاده از متد فکتوری
            // UserId را به TokenWallet.Create پاس می‌دهیم
            user.TokenWallet = TokenWallet.Create(user.Id, initialBalance: 0m);



            _logger.LogDebug("New User entity created. UserID: {UserId}, TokenWalletID: {TokenWalletId}", user.Id, user.TokenWallet.Id);

            // مرحله ۳: اضافه کردن User و TokenWallet به Repository ها
            // ترتیب مهم نیست چون SaveChanges در انتها انجام می‌شود، اما معمولاً والد (User) اول اضافه می‌شود.
            await _userRepository.AddAsync(user, cancellationToken);
            // TokenWallet به صورت Cascade با User اضافه می‌شود اگر رابطه به درستی در DbContext پیکربندی شده باشد
            // و user.TokenWallet مقدار داشته باشد.
            // اما برای اطمینان یا اگر می‌خواهید جداگانه کنترل کنید:
            await _tokenWalletRepository.AddAsync(user.TokenWallet, cancellationToken);

            // مرحله ۴: ذخیره تمام تغییرات در یک تراکنش واحد
            // این شامل User و TokenWallet (و هر موجودیت دیگری که در این عملیات تغییر کرده) می‌شود.
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("User {Username} (ID: {UserId}) and their TokenWallet (ID: {TokenWalletId}) registered and saved successfully.",
                user.Username, user.Id, user.TokenWallet.Id);

            // مرحله ۵: خواندن مجدد کاربر با جزئیات (برای شامل کردن TokenWallet مپ شده) و برگرداندن DTO
            // این کار اطمینان می‌دهد که تمام مقادیر تولید شده توسط دیتابیس (اگر وجود دارد) و روابط به درستی بارگذاری شده‌اند.
            // متد GetByIdAsync در IUserRepository باید TokenWallet را Include کند.
            var createdUserWithDetails = await _userRepository.GetByIdAsync(user.Id, cancellationToken);
            if (createdUserWithDetails == null)
            {
                _logger.LogCritical("CRITICAL: Failed to retrieve newly created user {UserId} immediately after registration and SaveChanges.", user.Id);
                throw new InvalidOperationException("User registration seemed successful, but the user could not be retrieved. Please contact support.");
            }

            var userDto = _mapper.Map<UserDto>(createdUserWithDetails);

            // ActiveSubscription برای کاربر جدید null خواهد بود مگر اینکه منطقی برای ایجاد اشتراک پیش‌فرض وجود داشته باشد.
            // این بخش می‌تواند توسط یک سرویس دیگر یا در مرحله بعدی جریان کاربر انجام شود.
            userDto.ActiveSubscription = null; // به صراحت null تنظیم می‌کنیم

            return userDto;
        }

        public async Task UpdateUserAsync(Guid userId, UpdateUserDto updateDto, CancellationToken cancellationToken = default) // ✅ نوع بازگشتی به Task تغییر کرد
        {
            _logger.LogInformation("Attempting to update user with ID: {UserId}", userId);
            var user = await _userRepository.GetByIdAsync(userId, cancellationToken);
            if (user == null)
            {
                _logger.LogWarning("User with ID {UserId} not found for update.", userId);
                // در اینجا بهتر است یک Exception سفارشی throw کنید تا فراخواننده متوجه شود کاربر پیدا نشده
                // throw new NotFoundException(nameof(User), userId);
                // یا اگر نمی‌خواهید Exception ایجاد کنید، می‌توانید یک Result<bool> یا مشابه برگردانید
                // اما چون اینترفیس Task است، فعلاً Exception مناسب‌تر است یا اینکه بدون خطا خارج شوید (که خوب نیست).
                // برای این مثال، فرض می‌کنیم اگر کاربر پیدا نشد، یک Exception رخ می‌دهد.
                // اگر Exception نمی‌خواهید، باید نوع بازگشتی اینترفیس را هم تغییر دهید.
                throw new InvalidOperationException($"User with ID {userId} not found for update.");
            }

            // بررسی تکراری بودن ایمیل جدید (اگر تغییر کرده)
            if (!string.IsNullOrWhiteSpace(updateDto.Email) &&
                user.Email != null && //  اطمینان از اینکه user.Email null نیست
                !user.Email.Equals(updateDto.Email, StringComparison.OrdinalIgnoreCase))
            {
                if (await _userRepository.ExistsByEmailAsync(updateDto.Email, cancellationToken))
                {
                    _logger.LogWarning("Update failed for UserID {UserId}: New email {Email} already exists.", userId, updateDto.Email);
                    throw new InvalidOperationException($"Another user with email {updateDto.Email} already exists.");
                }
            }

            // AutoMapper فیلدهای غیر null از updateUserDto را به user مپ می‌کند
            // (با فرض اینکه مپینگ UpdateUserDto به User با .ForAllMembers(opts => opts.Condition(...)) پیکربندی شده)
            _mapper.Map(updateDto, user);
            user.UpdatedAt = DateTime.UtcNow; // ✅ آپدیت کردن فیلد UpdatedAt

            // _userRepository.UpdateAsync(user, cancellationToken); // این معمولاً لازم نیست چون EF Core تغییرات را ردیابی می‌کند.
            // Repository.UpdateAsync می‌تواند فقط _context.Entry(entity).State = EntityState.Modified باشد.
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