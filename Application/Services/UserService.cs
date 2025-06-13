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
    /// <summary>پیاده‌سازی سرویس مدیریت کاربران و انجام عملیات مربوط به کاربران از جمله ثبت‌نام، به‌روزرسانی، حذف و بازیابی اطلاعات آن‌ها.
    /// این سرویس از Repository ها برای دسترسی به داده‌ها و از AutoMapper برای نگاشت بین موجودیت‌ها و اشیاء انتقال داده (DTOs) استفاده می‌کند.</summary>
    public class UserService : IUserService
    {
        private readonly IUserRepository _userRepository;
        private readonly ITokenWalletRepository _tokenWalletRepository;
        private readonly ISubscriptionRepository _subscriptionRepository; // برای پر کردن ActiveSubscription
        private readonly IMapper _mapper;
        private readonly IAppDbContext _context; // به عنوان Unit of Work برای SaveChangesAsync
        private readonly ILogger<UserService> _logger;
        private readonly ICacheService _cacheService; // ✅ NEW: Inject the cache service
        /// <summary>
        /// Initializes a new instance of the <see cref="UserService"/> class.
        /// This class provides services for managing user-related operations,
        /// including retrieval, registration, update, and deletion,
        /// interacting with user, token wallet, and subscription repositories.
        /// </summary>
        public UserService(
            IUserRepository userRepository,
            ITokenWalletRepository tokenWalletRepository,
            ISubscriptionRepository subscriptionRepository,
            IMapper mapper,
            IAppDbContext context, ICacheService cacheService,
            ILogger<UserService> logger)
        {
            _cacheService = cacheService;
            _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            _tokenWalletRepository = tokenWalletRepository ?? throw new ArgumentNullException(nameof(tokenWalletRepository));
            _subscriptionRepository = subscriptionRepository ?? throw new ArgumentNullException(nameof(subscriptionRepository));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }



        /// <summary>
        /// Asynchronously retrieves a user by their Telegram ID and maps them to a DTO.
        /// Includes active subscription information if available.
        /// Handles potential data access and mapping errors.
        /// </summary>
        /// <param name="telegramId">The Telegram ID of the user to retrieve.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A UserDto object if the user is found, otherwise null. Throws an exception on critical failure.</returns>
        // NOTE: The return type is UserDto?, indicating that null means "user not found",
        // while an exception means a critical error occurred during retrieval/processing.
        public async Task<UserDto?> GetUserByTelegramIdAsync(string telegramId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(telegramId))
            {
                _logger.LogWarning("Attempted to get user with null or empty Telegram ID.");
                return null;
            }

            // Define a unique cache key for this user.
            string cacheKey = $"user:telegram_id:{telegramId}";

            try
            {
                // 1. CACHE-ASIDE: First, try to get the user DTO from the cache.
                var cachedUserDto = await _cacheService.GetAsync<UserDto>(cacheKey);

                if (cachedUserDto != null)
                {
                    _logger.LogInformation("CACHE HIT: User with Telegram ID {TelegramId} found in cache.", telegramId);
                    return cachedUserDto;
                }

                // 2. CACHE MISS: If not in cache, get from the database.
                _logger.LogInformation("CACHE MISS: Fetching user by Telegram ID {TelegramId} from database.", telegramId);
                var user = await _userRepository.GetByTelegramIdAsync(telegramId, cancellationToken);

                if (user == null)
                {
                    _logger.LogWarning("User with Telegram ID {TelegramId} not found in database.", telegramId);
                    return null;
                }

                _logger.LogInformation("User with Telegram ID {TelegramId} found in DB: {Username}", telegramId, user.Username);
                var userDto = _mapper.Map<UserDto>(user);
                var activeSubscriptionEntity = await _subscriptionRepository.GetActiveSubscriptionByUserIdAsync(user.Id, cancellationToken);

                if (activeSubscriptionEntity != null)
                {
                    userDto.ActiveSubscription = _mapper.Map<SubscriptionDto>(activeSubscriptionEntity);
                }

                // 3. Set the fully populated DTO into the cache for next time.
                // Give it an expiration, e.g., 1 hour, so data doesn't get stale.
                await _cacheService.SetAsync(cacheKey, userDto, TimeSpan.FromHours(1));
                _logger.LogInformation("User with Telegram ID {TelegramId} DTO set into cache.", telegramId);

                return userDto;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred while fetching user with Telegram ID {TelegramId}.", telegramId);
                throw new ApplicationException($"An error occurred while retrieving user {telegramId}.", ex);
            }
        }

        /// <summary>
        /// Asynchronously retrieves all users from the system and maps them to DTOs.
        /// Includes active subscription information for each user.
        /// Handles potential data access and mapping errors.
        /// </summary>
        /// <param name="cancellationToken">The token to cancel the operation.</param>
        /// <returns>A list of user DTOs on success, or throws an exception on critical failure.</returns>
        // NOTE: The return type is List<UserDto>, which means this method *cannot* return a Failure result.
        // If an error occurs, the common pattern is to *throw* the exception or wrap it in a custom exception.
        // If you intend to return a Result<List<UserDto>> as in previous examples, the return type should change.
        // Assuming List<UserDto> return type means throwing exceptions is the intended error handling for this method.
        public async Task<List<UserDto>> GetAllUsersAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Fetching all users.");

            try
            {
                // Fetch all users from the repository. Potential database interaction point.
                // Assumed: This method includes TokenWallet information.
                var users = await _userRepository.GetAllAsync(cancellationToken);

                var userDtos = new List<UserDto>();
                foreach (var user in users)
                {
                    // Map User entity to UserDto. Potential mapping error point.
                    var userDto = _mapper.Map<UserDto>(user);

                    // Fetch active subscription for the user. Another potential database interaction point.
                    var activeSubscriptionEntity = await _subscriptionRepository.GetActiveSubscriptionByUserIdAsync(user.Id, cancellationToken);

                    if (activeSubscriptionEntity != null)
                    {
                        // Map Subscription entity to SubscriptionDto. Potential mapping error point.
                        userDto.ActiveSubscription = _mapper.Map<SubscriptionDto>(activeSubscriptionEntity);
                    }

                    userDtos.Add(userDto);
                }

                _logger.LogDebug("Successfully fetched and mapped {UserCount} users.", userDtos.Count);
                return userDtos;
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically.
                _logger.LogInformation(ex, "Fetching all users was cancelled.");
                throw; // Re-throw the cancellation exception as it's an expected part of the flow.
            }
            // Catch specific database exceptions first if possible (e.g., SqlException, DbException etc.)
            // catch (DbException dbEx)
            // {
            //     _logger.LogError(dbEx, "Database error while fetching all users.");
            //     // Wrap and throw a higher-level exception or re-throw.
            //     throw new ApplicationException("Database error occurred while retrieving users.", dbEx);
            // }
            // Catch mapping exceptions specifically if desired.
            // catch (AutoMapperMappingException mapEx)
            // {
            //     _logger.LogError(mapEx, "Mapping error while processing user data.");
            //     throw new ApplicationException("Error processing user data.", mapEx);
            // }
            catch (Exception ex)
            {
                // Catch any other unexpected exceptions (general database errors, mapping errors, etc.)
                // Log the general error.
                _logger.LogError(ex, "An unexpected error occurred while fetching and mapping all users.");

                // Depending on your application's error handling strategy:
                // Option 1 (Standard for methods returning List<T> on success): Wrap and throw a higher-level exception.
                // This indicates to the caller that the operation failed and data could not be returned.
                throw new ApplicationException("An error occurred while retrieving user list.", ex);

                // Option 2 (If you were returning Result<List<UserDto>>): Return a Failure result.
                // return Result<List<UserDto>>.Failure($"An error occurred while retrieving user list: {ex.Message}");
            }
        }

        /// <summary>
        /// Asynchronously retrieves a user by their unique internal ID and maps them to a DTO.
        /// Includes active subscription information if available.
        /// Handles potential data access and mapping errors.
        /// </summary>
        /// <param name="id">The unique internal ID (Guid) of the user.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A UserDto object if the user is found, otherwise null. Throws an exception on critical failure.</returns>
        // NOTE: The return type is UserDto?, indicating that null means "user not found",
        // while an exception means a critical error occurred during retrieval/processing.
        public async Task<UserDto?> GetUserByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            // Basic validation for Guid.Empty if needed, though GetByIdAsync usually handles it.
            // if (id == Guid.Empty) { ... log warning and return null or throw ArgumentException ... }

            _logger.LogInformation("Fetching user by ID: {UserId}", id);

            try
            {
                // Fetch user from the repository by internal ID. Potential database interaction.
                // Assumed: This method includes TokenWallet information.
                var user = await _userRepository.GetByIdAsync(id, cancellationToken);

                // Handle case where user is not found (normal outcome).
                if (user == null)
                {
                    _logger.LogWarning("User with ID {UserId} not found.", id);
                    return null; // Return null as the user was not found.
                }

                _logger.LogInformation("User with ID {UserId} found: {Username}", id, user.Username);

                // Map User entity to UserDto. Potential mapping error point.
                var userDto = _mapper.Map<UserDto>(user);

                // Fetch active subscription for the user. Another potential database interaction.
                var activeSubscriptionEntity = await _subscriptionRepository.GetActiveSubscriptionByUserIdAsync(user.Id, cancellationToken);

                if (activeSubscriptionEntity != null)
                {
                    // Map Subscription entity to SubscriptionDto. Potential mapping error point.
                    userDto.ActiveSubscription = _mapper.Map<SubscriptionDto>(activeSubscriptionEntity);
                }

                // Return the successfully created UserDto.
                return userDto;
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically.
                _logger.LogInformation(ex, "Fetching user by ID {UserId} was cancelled.", id);
                throw; // Re-throw the cancellation exception.
            }
            // Catch specific database or mapping exceptions if desired.
            // catch (DbException dbEx)
            // {
            //     _logger.LogError(dbEx, "Database error while fetching user with ID {UserId}.", id);
            //     throw new ApplicationException($"Database error occurred while retrieving user {id}.", dbEx);
            // }
            // catch (AutoMapperMappingException mapEx)
            // {
            //     _logger.LogError(mapEx, "Mapping error while processing user data for ID {UserId}.", id);
            //     throw new ApplicationException($"Error processing user data for user {id}.", mapEx);
            // }
            catch (Exception ex)
            {
                // Catch any other unexpected technical exceptions (general database errors, mapping errors, etc.)
                // Log the general error with context.
                _logger.LogError(ex, "An unexpected error occurred while fetching or processing user with ID {UserId}.", id);

                // Depending on your error handling strategy:
                // Option 1 (Standard for methods returning T? on success/not found): Wrap and throw a higher-level exception.
                throw new ApplicationException($"An error occurred while retrieving user {id}.", ex);

                // Option 2 (Less common with T? return): Return null (generally AVOID for technical errors).
                // return null;
            }
        }






        // ✅✅ ADD THE FULL IMPLEMENTATION OF THE NEW METHOD ✅✅
        public async Task MarkUserAsUnreachableAsync(string telegramId, string reason, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Marking user {TelegramId} as unreachable. Reason: {Reason}", telegramId, reason);

                // 1. Find the user by their Telegram ID.
                var user = await _userRepository.GetByTelegramIdAsync(telegramId, cancellationToken);
                if (user == null)
                {
                    _logger.LogWarning("Could not mark user as unreachable: User with Telegram ID {TelegramId} not found.", telegramId);
                    return;
                }

                // 2. Change their notification settings.
                // This is a "soft delete" - we keep the user but stop sending them messages.
                user.EnableGeneralNotifications = false;
                user.EnableRssNewsNotifications = false;
                user.EnableVipSignalNotifications = false;
                user.UpdatedAt = DateTime.UtcNow; // Update the timestamp

                // 3. Save the changes to the database.
                await _userRepository.UpdateAsync(user, cancellationToken);

                // 4. IMPORTANT: Invalidate the user's cache!
                string cacheKey = $"user:telegram_id:{telegramId}";
                await _cacheService.RemoveAsync(cacheKey);

                _logger.LogInformation("Successfully marked user {TelegramId} as unreachable and invalidated cache.", telegramId);
            }
            catch (Exception ex)
            {
                // This is a background, non-critical operation. We should log the error but not let it crash the calling process.
                _logger.LogError(ex, "An error occurred while trying to mark user {TelegramId} as unreachable.", telegramId);
            }
        }






        /// <summary>
        /// Registers a new user in the system, including creating their token wallet.
        /// Handles business validation (uniqueness checks) and potential data access/mapping errors.
        /// </summary>
        /// <param name="registerDto">User registration details.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The DTO of the newly registered user.</returns>
        /// <exception cref="InvalidOperationException">Thrown if user with given email or Telegram ID already exists.</exception>
        /// <exception cref="ApplicationException">Thrown on critical technical errors during registration.</exception>
        public async Task<UserDto> RegisterUserAsync(RegisterUserDto registerDto, CancellationToken cancellationToken = default)
        {
            // Input validation (often done by validators before reaching service, but check for critical null/empty)
            if (registerDto == null)
            {
                throw new ArgumentNullException(nameof(registerDto));
            }
            if (string.IsNullOrWhiteSpace(registerDto.TelegramId) || string.IsNullOrWhiteSpace(registerDto.Email) || string.IsNullOrWhiteSpace(registerDto.Username))
            {
                _logger.LogWarning("Registration attempted with invalid/incomplete data. TelegramID: {TelegramId}, Email: {Email}, Username: {Username}",
                    registerDto.TelegramId, registerDto.Email, registerDto.Username);
                throw new ArgumentException("Registration data is incomplete.");
            }

            _logger.LogInformation("Attempting to register new user. TelegramID: {TelegramId}, Email: {Email}, Username: {Username}",
                registerDto.TelegramId, registerDto.Email, registerDto.Username);

            try
            {
                // Step 1: Business validation (uniqueness checks). Potential database calls.
                if (await _userRepository.ExistsByEmailAsync(registerDto.Email, cancellationToken))
                {
                    _logger.LogWarning("Registration failed: Email {Email} already exists.", registerDto.Email);
                    // Throw specific business exception
                    throw new InvalidOperationException($"A user with the email '{registerDto.Email}' already exists.");
                }
                if (await _userRepository.ExistsByTelegramIdAsync(registerDto.TelegramId, cancellationToken))
                {
                    _logger.LogWarning("Registration failed: Telegram ID {TelegramId} already exists.", registerDto.TelegramId);
                    // Throw specific business exception
                    throw new InvalidOperationException($"A user with the Telegram ID '{registerDto.TelegramId}' already exists.");
                }

                // Step 2: Create User entity and TokenWallet entity
                var user = new User(registerDto.Username, registerDto.TelegramId, registerDto.Email)
                {
                    // Set other properties here if constructor doesn't cover all
                    Id = Guid.NewGuid(), // Assuming constructor doesn't set ID, otherwise remove
                    Level = UserLevel.Free,
                    CreatedAt = DateTime.UtcNow,
                    EnableGeneralNotifications = true,
                    EnableRssNewsNotifications = true,
                    EnableVipSignalNotifications = false
                };

                // Create TokenWallet entity using factory method
                user.TokenWallet = TokenWallet.Create(user.Id, initialBalance: 0m);

                _logger.LogDebug("New User entity created in memory. UserID: {UserId}, TokenWalletID: {TokenWalletId}", user.Id, user.TokenWallet.Id);

                // Step 3: Add entities to Repositories (marks them for insertion)
                await _userRepository.AddAsync(user, cancellationToken);
                // Add TokenWallet explicitly if not configured for cascade insert or for clarity
                await _tokenWalletRepository.AddAsync(user.TokenWallet, cancellationToken);

                // Step 4: Save all changes in a single transaction. **CRITICAL point of failure.**
                _ = await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("User {Username} (ID: {UserId}) and their TokenWallet (ID: {TokenWalletId}) registered and saved successfully.",
                    user.Username, user.Id, user.TokenWallet.Id);

                // Step 5: Retrieve the created user with details (like TokenWallet included)
                // This step itself is a potential database call and can fail.
                var createdUserWithDetails = await _userRepository.GetByIdAsync(user.Id, cancellationToken);
                if (createdUserWithDetails == null)
                {
                    // This is a severe consistency error. Log and throw.
                    _logger.LogCritical("CRITICAL: Failed to retrieve newly created user {UserId} immediately after registration and SaveChanges. Data inconsistency suspected.", user.Id);
                    throw new ApplicationException("User registration seemed successful, but the user could not be retrieved. Data inconsistency.");
                }

                // Step 6: Map the entity with details to UserDto. Potential mapping error point.
                var userDto = _mapper.Map<UserDto>(createdUserWithDetails);

                // Set ActiveSubscription manually as a new user has none by default.
                userDto.ActiveSubscription = null;

                _logger.LogInformation("Registration process completed successfully for user {UserId}.", user.Id);
                return userDto;
            }
            catch (InvalidOperationException) // Catch specific business rule exceptions that were thrown explicitly
            {
                // Re-throw the business exception as it indicates a known business rule violation.
                throw;
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically.
                _logger.LogInformation(ex, "User registration for TelegramID {TelegramId} was cancelled.", registerDto.TelegramId);
                throw; // Re-throw cancellation.
            }
            // Catch specific database/ORM exceptions if you want to differentiate them in logs or handling.
            // catch (DbUpdateException dbEx) // Example for Entity Framework concurrency or constraint errors
            // {
            //     _logger.LogError(dbEx, "Database update error during user registration for TelegramID {TelegramId}.", registerDto.TelegramId);
            //     throw new ApplicationException("Database error during user registration.", dbEx);
            // }
            // catch (AutoMapperMappingException mapEx) // Example for mapping errors
            // {
            //      _logger.LogError(mapEx, "Mapping error after successful user registration for TelegramID {TelegramId}.", registerDto.TelegramId);
            //      throw new ApplicationException("Error processing user data after registration.", mapEx);
            // }
            catch (Exception ex)
            {
                // Catch any other unexpected technical exceptions (network, configuration, general database errors not caught above, etc.)
                // Log the critical technical error.
                _logger.LogError(ex, "An unexpected error occurred during user registration process for TelegramID {TelegramId}.", registerDto.TelegramId);

                // Throw a generic application exception indicating a critical failure.
                // The caller should catch this and inform the user about a technical problem.
                throw new ApplicationException("An unexpected error occurred during user registration. Please try again later.", ex);
            }
        }

        /// <summary>
        /// Asynchronously updates an existing user's information.
        /// Handles business validation (e.g., unique email) and potential data access/mapping errors.
        /// </summary>
        /// <param name="userId">The ID of the user to update.</param>
        /// <param name="updateDto">User update information.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <exception cref="NotFoundException">Thrown if the user with the given ID is not found.</exception> // Example of a specific custom exception
        /// <exception cref="InvalidOperationException">Thrown if update data violates business rules (e.g., duplicate email).</exception>
        /// <exception cref="ApplicationException">Thrown on critical technical errors during the update process.</exception>
        public async Task UpdateUserAsync(Guid userId, UpdateUserDto updateDto, CancellationToken cancellationToken = default)
        {
            if (updateDto == null) throw new ArgumentNullException(nameof(updateDto));

            _logger.LogInformation("Attempting to update user with ID: {UserId}", userId);

            try
            {
                var user = await _userRepository.GetByIdAsync(userId, cancellationToken);
                if (user == null)
                {
                    throw new InvalidOperationException($"User with ID {userId} not found for update.");
                }

                // --- Cache Invalidation ---
                // We must invalidate the cache BEFORE saving changes to prevent a race condition
                // where another request might read the old data and re-cache it.
                string cacheKey = $"user:telegram_id:{user.TelegramId}";
                await _cacheService.RemoveAsync(cacheKey);
                _logger.LogInformation("Invalidated cache for user {TelegramId} due to update.", user.TelegramId);
                // --- End Cache Invalidation ---

                if (!string.IsNullOrWhiteSpace(updateDto.Email) &&
                    !user.Email.Equals(updateDto.Email, StringComparison.OrdinalIgnoreCase))
                {
                    if (await _userRepository.ExistsByEmailAsync(updateDto.Email, cancellationToken))
                    {
                        throw new InvalidOperationException($"Another user with email {updateDto.Email} already exists.");
                    }
                }

                _mapper.Map(updateDto, user);
                user.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("User with ID {UserId} updated successfully in DB.", userId);
            }
            catch (Exception ex)
            {
                // Simplified catch block for brevity
                _logger.LogError(ex, "An error occurred during user update for UserID {UserId}.", userId);
                throw;
            }
        }


        /// <summary>
        /// Asynchronously deletes a user from the system by their unique internal ID.
        /// Handles cases where the user is not found and potential data access errors.
        /// </summary>
        /// <param name="id">The ID of the user to delete.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        /// <exception cref="ApplicationException">Thrown on critical technical errors during the deletion process.</exception>
        public async Task DeleteUserAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Attempting to delete user with ID: {UserId}", id);

            try
            {
                var user = await _userRepository.GetByIdAsync(id, cancellationToken);
                if (user == null)
                {
                    _logger.LogWarning("User with ID {UserId} not found for deletion. Operation considered successful.", id);
                    return;
                }

                // --- Cache Invalidation ---
                string cacheKey = $"user:telegram_id:{user.TelegramId}";
                await _cacheService.RemoveAsync(cacheKey);
                _logger.LogInformation("Invalidated cache for user {TelegramId} due to deletion.", user.TelegramId);
                // --- End Cache Invalidation ---

                await _userRepository.DeleteAsync(user, cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("User with ID {UserId} deleted successfully.", id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during user deletion for UserID {UserId}.", id);
                throw;
            }
       
    }
    }
}