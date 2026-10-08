// File: Application/Services/UserService.cs

#region Usings
using Application.Common.Interfaces; // For IUserRepository, ITokenWalletRepository, ISubscriptionRepository, IAppDbContext, ICacheService
using Application.DTOs;             // For UserDto, RegisterUserDto, UpdateUserDto, SubscriptionDto
using Application.Interfaces;       // For IUserService
using AutoMapper;                   // For IMapper
using Domain.Entities;              // For User, TokenWallet, Subscription
using Microsoft.Extensions.Logging;
using Shared.Extensions; // For ILogger
// Remove if not directly used: using StackExchange.Redis;
// Remove if not directly used: using Microsoft.Extensions.Caching.Distributed;
#endregion

namespace Application.Services
{
    /// <summary>
    /// Implements the service for managing user-related operations,
    /// including retrieval, registration, update, and deletion,
    /// interacting with user, token wallet, and subscription repositories,
    /// and utilizing Redis caching for performance.
    /// </summary>
    public class UserService : IUserService
    {
        private readonly IUserRepository _userRepository;
        private readonly ITokenWalletRepository _tokenWalletRepository;
        private readonly ISubscriptionRepository _subscriptionRepository; // For populating ActiveSubscription
        private readonly IMapper _mapper;
        private readonly IAppDbContext _context; // As Unit of Work for SaveChangesAsync
        private readonly ILogger<UserService> _logger;
        private readonly ICacheService _cacheService; // Properly injected and used for caching
        private readonly ILoggingSanitizer _logSanitizer;
        /// <summary>
        /// Initializes a new instance of the <see cref="UserService"/> class.
        /// </summary>
        public UserService(ILoggingSanitizer logSanitizer,
            IUserRepository userRepository,
            ITokenWalletRepository tokenWalletRepository,
            ISubscriptionRepository subscriptionRepository,
            IMapper mapper,
            IAppDbContext context,
            ICacheService cacheService, // Correctly injected
            ILogger<UserService> logger)
        {
            _logSanitizer = logSanitizer ?? throw new ArgumentNullException(nameof(logSanitizer));
            _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            _tokenWalletRepository = tokenWalletRepository ?? throw new ArgumentNullException(nameof(tokenWalletRepository));
            _subscriptionRepository = subscriptionRepository ?? throw new ArgumentNullException(nameof(subscriptionRepository));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService)); // Assigned injected service
        }

        /// <summary>
        /// Asynchronously retrieves a user by their Telegram ID, attempting to use Redis cache first.
        /// If not found in cache, fetches from the database, maps to DTO, and caches the result.
        /// Includes active subscription information if available.
        /// </summary>
        /// <param name="telegramId">The Telegram ID of the user to retrieve.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A UserDto object if the user is found, otherwise null. Throws an exception on critical failure.</returns>
        public async Task<UserDto?> GetUserByTelegramIdAsync(string telegramId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(telegramId))
            {
                _logger.LogWarning("UserService: GetUserByTelegramIdAsync called with null or empty telegramId.");
                return null;
            }

            string cacheKey = $"user:telegram_id:{telegramId}";

            // --- STRATEGY 1: ATTEMPT TO USE CACHE ---
            try
            {
                UserDto? cachedUserDto = await _cacheService.GetAsync<UserDto>(cacheKey);
                if (cachedUserDto != null)
                {
                    _logger.LogInformation("CACHE HIT: User with Telegram ID {TelegramId} found in cache.", telegramId);
                    return cachedUserDto;
                }
                _logger.LogTrace("CACHE MISS: User with Telegram ID {TelegramId} not found in cache.", telegramId);
            }
            catch (Exception ex)
            {
                // Log cache failure but continue to the database.
                _logger.LogError(ex, "UserService: Cache read failed for key {CacheKey}. Falling back to database.", cacheKey);
            }

            // --- STRATEGY 2: FALLBACK TO DATABASE ---
            try
            {
                _logger.LogInformation("DATABASE FETCH: Getting user by Telegram ID {TelegramId} from database.", telegramId);
                User? user = await _userRepository.GetByTelegramIdAsync(telegramId, cancellationToken);

                if (user == null)
                {
                    _logger.LogWarning("User with Telegram ID {TelegramId} not found in database.", telegramId);
                    return null; // User not found, this is a valid outcome.
                }

                // Map the retrieved User entity to UserDto.
                UserDto userDto = _mapper.Map<UserDto>(user);

                // Fetch and map the active subscription.
                Subscription? activeSubscriptionEntity = await _subscriptionRepository.GetActiveSubscriptionByUserIdAsync(user.Id, cancellationToken);
                if (activeSubscriptionEntity != null)
                {
                    userDto.ActiveSubscription = _mapper.Map<SubscriptionDto>(activeSubscriptionEntity);
                }

                // --- ATTEMPT TO WRITE TO CACHE ---
                await _cacheService.SetAsync(cacheKey, userDto, TimeSpan.FromHours(1)); // Cache for 1 hour
                _logger.LogInformation("CACHE WRITE: User {TelegramId} DTO set into cache.", telegramId);

                return userDto;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected database or mapping error occurred while fetching user with Telegram ID {TelegramId}.", telegramId);
                // Wrap and re-throw as an ApplicationException to indicate a critical failure.
                throw new ApplicationException($"An error occurred while retrieving user {telegramId}.", ex);
            }
        }

        /// <summary>
        /// Asynchronously retrieves all users from the system, fetching their active subscriptions,
        /// and maps them to DTOs. Caches the list of users.
        /// </summary>
        /// <param name="cancellationToken">The token to cancel the operation.</param>
        /// <returns>A list of user DTOs on success, or throws an exception on critical failure.</returns>
        public async Task<List<UserDto>> GetAllUsersAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Fetching all users.");
            string allUsersCacheKey = "users:all"; // Cache key for all users

            // --- STRATEGY 1: ATTEMPT TO USE CACHE ---
            try
            {
                List<UserDto>? cachedUsers = await _cacheService.GetAsync<List<UserDto>>(allUsersCacheKey);
                if (cachedUsers != null)
                {
                    _logger.LogInformation("CACHE HIT: All users found in cache.");
                    return cachedUsers;
                }
                _logger.LogTrace("CACHE MISS: All users not found in cache.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cache read failed for key {CacheKey}. Falling back to database.", allUsersCacheKey);
            }

            // --- STRATEGY 2: FALLBACK TO DATABASE ---
            try
            {
                IEnumerable<User> users = await _userRepository.GetAllAsync(cancellationToken);
                List<UserDto> userDtos = [];

                foreach (User user in users)
                {
                    UserDto userDto = _mapper.Map<UserDto>(user);
                    Subscription? activeSubscriptionEntity = await _subscriptionRepository.GetActiveSubscriptionByUserIdAsync(user.Id, cancellationToken);
                    if (activeSubscriptionEntity != null)
                    {
                        userDto.ActiveSubscription = _mapper.Map<SubscriptionDto>(activeSubscriptionEntity);
                    }
                    userDtos.Add(userDto);
                }

                // --- ATTEMPT TO WRITE TO CACHE ---
                await _cacheService.SetAsync(allUsersCacheKey, userDtos, TimeSpan.FromMinutes(30)); // Cache all users for 30 minutes
                _logger.LogInformation("CACHE WRITE: All users set into cache.");

                _logger.LogDebug("Successfully fetched and mapped {UserCount} users.", userDtos.Count);
                return userDtos;
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(ex, "Fetching all users was cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred while fetching and mapping all users.");
                throw new ApplicationException("An error occurred while retrieving user list.", ex);
            }
        }

        /// <summary>
        /// Asynchronously retrieves a user by their internal ID, attempting to use cache first.
        /// If not found in cache, fetches from the database, maps to DTO, and caches the result.
        /// Includes active subscription information if available.
        /// </summary>
        /// <param name="id">The unique internal ID (Guid) of the user.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A UserDto object if the user is found, otherwise null. Throws an exception on critical failure.</returns>
        public async Task<UserDto?> GetUserByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Fetching user by ID: {UserId}", id);

            string cacheKey = $"user:id:{id}"; // Cache key for fetching by ID

            // --- STRATEGY 1: ATTEMPT TO USE CACHE ---
            try
            {
                UserDto? cachedUserDto = await _cacheService.GetAsync<UserDto>(cacheKey);
                if (cachedUserDto != null)
                {
                    _logger.LogInformation("CACHE HIT: User with ID {UserId} found in cache.", id);
                    return cachedUserDto;
                }
                _logger.LogTrace("CACHE MISS: User with ID {UserId} not found in cache.", id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cache read failed for key {CacheKey}. Falling back to database.", cacheKey);
            }

            // --- STRATEGY 2: FALLBACK TO DATABASE ---
            try
            {
                User? user = await _userRepository.GetByIdAsync(id, cancellationToken);

                if (user == null)
                {
                    _logger.LogWarning("User with ID {UserId} not found in database.", id);
                    return null;
                }

                UserDto userDto = _mapper.Map<UserDto>(user);
                Subscription? activeSubscriptionEntity = await _subscriptionRepository.GetActiveSubscriptionByUserIdAsync(user.Id, cancellationToken);
                if (activeSubscriptionEntity != null)
                {
                    userDto.ActiveSubscription = _mapper.Map<SubscriptionDto>(activeSubscriptionEntity);
                }

                // --- ATTEMPT TO WRITE TO CACHE ---
                await _cacheService.SetAsync(cacheKey, userDto, TimeSpan.FromHours(1)); // Cache for 1 hour
                _logger.LogInformation("CACHE WRITE: User {UserId} DTO set into cache.", id);

                return userDto;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected database or mapping error occurred while fetching user with ID {UserId}.", id);
                throw new ApplicationException($"An error occurred while retrieving user {id}.", ex);
            }
        }

        /// <summary>
        /// Marks a user as unreachable by disabling their notification settings.
        /// Also invalidates their cache entry.
        /// </summary>
        /// <param name="telegramId">The Telegram ID of the user to mark.</param>
        /// <param name="reason">The reason for marking the user as unreachable.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task MarkUserAsUnreachableAsync(string telegramId, string reason, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(telegramId))
            {
                _logger.LogWarning("MarkUserAsUnreachableAsync called with null or empty telegramId.");
                return;
            }

            try
            {
                _logger.LogInformation("Marking user {TelegramId} as unreachable. Reason: {Reason}", telegramId, reason);

                User? user = await _userRepository.GetByTelegramIdAsync(telegramId, cancellationToken);
                if (user == null)
                {
                    _logger.LogWarning("Could not mark user as unreachable: User with Telegram ID {TelegramId} not found.", telegramId);
                    return;
                }

                // Disable all notifications.
                user.EnableGeneralNotifications = false;
                user.EnableRssNewsNotifications = false;
                user.EnableVipSignalNotifications = false;
                user.UpdatedAt = DateTime.UtcNow;

                await _userRepository.UpdateAsync(user, cancellationToken);

                // Invalidate the user's cache.
                string cacheKey = $"user:telegram_id:{telegramId}";
                _ = await _cacheService.RemoveAsync(cacheKey);

                _logger.LogInformation("Successfully marked user {TelegramId} as unreachable and invalidated cache.", telegramId);
            }
            catch (Exception ex)
            {
                // This is a background, non-critical operation. Log the error but don't propagate it.
                _logger.LogError(ex, "An error occurred while trying to mark user {TelegramId} as unreachable.", telegramId);
            }
        }

        /// <summary>
        /// Asynchronously registers a new user with the provided information.
        /// Invalidates the user's cache entry if the registration is successful.
        /// </summary>
        /// <param name="registerDto">User registration information.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="userEntityToRegister">Optional pre-constructed User entity. If null, one will be created from the DTO.</param>
        /// <returns>The registered user's DTO.</returns>
        /// <exception cref="InvalidOperationException">Thrown if a user with the same email already exists or if business validation fails.</exception>
        /// <exception cref="ApplicationException">Thrown on critical technical errors during registration.</exception>
        public async Task<UserDto> RegisterUserAsync(RegisterUserDto registerDto, CancellationToken cancellationToken = default, User? userEntityToRegister = null)
        {
            if (registerDto == null)
            {
                throw new ArgumentNullException(nameof(registerDto));
            }

            // --- Sanitize Sensitive Data for Logging ---
            // SECURITY: Sanitize all sensitive information before logging to prevent exposure of private information
            string sanitizedTelegramId = _logSanitizer.Sanitize(registerDto.TelegramId);
            string sanitizedUsername = _logSanitizer.Sanitize(registerDto.Username);
            string sanitizedEmail = _logSanitizer.Sanitize(registerDto.Email);

            _logger.LogInformation("UserService: Registering user (provided entity). TelegramId: {SanitizedTelegramId}, Username: {SanitizedUsername}, Email: {SanitizedEmail}",
                sanitizedTelegramId, sanitizedUsername, sanitizedEmail);

            // Check if user already exists by email
            if (await _userRepository.ExistsByEmailAsync(registerDto.Email, cancellationToken))
            {
                throw new InvalidOperationException($"A user with email '{sanitizedEmail}' already exists.");
            }

            // Panel path guard: bot handlers check this upstream, the panel does not.
            if (await _userRepository.ExistsByTelegramIdAsync(registerDto.TelegramId, cancellationToken))
            {
                throw new InvalidOperationException($"A user with Telegram ID '{sanitizedTelegramId}' already exists.");
            }

            try
            {
                // Panel/API path: no pre-built entity (bot handlers pass one).
                // Build it from the DTO so POST /api/users works standalone.
                Domain.Entities.User entity = userEntityToRegister ?? new Domain.Entities.User
                {
                    Id = Guid.NewGuid(),
                    Username = registerDto.Username,
                    TelegramId = registerDto.TelegramId,
                    Email = registerDto.Email,
                    Level = Domain.Enums.UserLevel.Free,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    EnableGeneralNotifications = true,
                    EnableVipSignalNotifications = false,
                    EnableRssNewsNotifications = true,
                    PreferredLanguage = "en",
                };
                if (entity.TokenWallet == null)
                {
                    entity.TokenWallet = new Domain.Entities.TokenWallet(
                        Guid.NewGuid(), entity.Id, 0.0m, true, DateTime.UtcNow, DateTime.UtcNow);
                }
                userEntityToRegister = entity;

                // Add entities to repositories (marks them for insertion).
                // The repository will use the IDs already present on the entities.
                await _userRepository.AddAsync(userEntityToRegister!, cancellationToken);

                // Explicitly add TokenWallet if User.AddAsync doesn't handle it via cascade or if it's managed separately.
                if (userEntityToRegister?.TokenWallet != null)
                {
                    //     await _tokenWalletRepository.AddAsync(userEntityToRegister.TokenWallet, cancellationToken);
                }
                else
                {
                    // This indicates a problem: a user was passed without a wallet.
                    _logger.LogError("Critical: User entity {UserId} passed to RegisterUserAsync has no TokenWallet.", userEntityToRegister!.Id);
                    throw new InvalidOperationException("User registration failed: TokenWallet is missing.");
                }

                // Save all changes in a single transaction.
                _ = await _context.SaveChangesAsync(cancellationToken);

                // --- Cache Invalidation/Update ---
                // After successful save, the user is "new". Remove any stale cache entry (though unlikely for new users)
                // and then effectively re-cache the newly created user's DTO.
                _ = await _cacheService.RemoveAsync($"user:telegram_id:{userEntityToRegister.TelegramId}");
                _ = await _cacheService.RemoveAsync($"user:id:{userEntityToRegister.Id}");

                // Map the created entity to a DTO to return.
                UserDto registeredUserDto = MapToUserDto(userEntityToRegister);

                // Ensure the cache is populated with the new user's DTO.
                await _cacheService.SetAsync($"user:telegram_id:{registeredUserDto.TelegramId}", registeredUserDto, TimeSpan.FromHours(1));
                await _cacheService.SetAsync($"user:id:{registeredUserDto.Id}", registeredUserDto, TimeSpan.FromHours(1));

                // SECURITY: Sanitize username in success log
                string sanitizedRegisteredUsername = _logSanitizer.Sanitize(registeredUserDto.Username);
                _logger.LogInformation("User {SanitizedUsername} (ID: {UserId}) registered and saved successfully. TokenWallet ID: {TokenWalletId}",
                    sanitizedRegisteredUsername, registeredUserDto.Id, registeredUserDto.TokenWallet?.Id);

                return registeredUserDto;
            }
            catch (ArgumentException) { throw; } // Re-throw known argument exceptions
            catch (InvalidOperationException) { throw; } // Re-throw known operation exceptions
            catch (RepositoryException dbEx)
            {
                _logger.LogError(dbEx, "UserService: Repository error during registration for TelegramId {SanitizedTelegramId}.", sanitizedTelegramId);
                throw new ApplicationException($"An error occurred during user registration. Please try again later. (Repo Error)", dbEx);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserService: Unexpected error during user registration for TelegramId {SanitizedTelegramId}.", sanitizedTelegramId);
                throw new ApplicationException($"An unexpected error occurred during user registration. Please try again later. (Service Error)", ex);
            }
        }

        /// <summary>
        /// Asynchronously updates an existing user's information.
        /// Invalidates the user's cache entry if the update is successful.
        /// </summary>
        /// <param name="userId">The ID of the user to update.</param>
        /// <param name="updateDto">User update information.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <exception cref="InvalidOperationException">Thrown if the user is not found or if the update data violates business rules (e.g., duplicate email).</exception>
        /// <exception cref="ApplicationException">Thrown on critical technical errors during the update process.</exception>
        public async Task UpdateUserAsync(Guid userId, UpdateUserDto updateDto, CancellationToken cancellationToken = default)
        {
            if (updateDto == null)
            {
                throw new ArgumentNullException(nameof(updateDto));
            }

            _logger.LogInformation("Attempting to update user with ID: {UserId}", userId);

            try
            {
                User? user = await _userRepository.GetByIdAsync(userId, cancellationToken);
                if (user == null)
                {
                    // Use a specific exception for "not found" scenarios.
                    throw new InvalidOperationException($"User with ID {userId} not found for update.");
                }

                // --- Cache Invalidation ---
                // Invalidate cache BEFORE saving changes to prevent race conditions.
                string cacheKey = $"user:telegram_id:{user.TelegramId}";
                _ = await _cacheService.RemoveAsync(cacheKey);
                _logger.LogInformation("Invalidated cache for user {TelegramId} due to update.", user.TelegramId);

                // --- Business Validation: Check for email uniqueness if email is being changed ---
                if (!string.IsNullOrWhiteSpace(updateDto.Email) &&
                    !user.Email.Equals(updateDto.Email, StringComparison.OrdinalIgnoreCase))
                {
                    if (await _userRepository.ExistsByEmailAsync(updateDto.Email, cancellationToken))
                    {
                        throw new InvalidOperationException($"Another user with email '{updateDto.Email}' already exists.");
                    }
                }

                // Apply updates from DTO to the User entity.
                _ = _mapper.Map(updateDto, user);
                user.UpdatedAt = DateTime.UtcNow; // Ensure UpdatedAt is always updated on modification.

                // Save the updated user entity.
                await _userRepository.UpdateAsync(user, cancellationToken);
                _ = await _context.SaveChangesAsync(cancellationToken); // Save changes to the DB.

                _logger.LogInformation("User with ID {UserId} updated successfully.", userId);
            }
            catch (InvalidOperationException) { throw; } // Re-throw specific business rule exceptions.
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during user update for UserID {UserId}.", userId);
                throw new ApplicationException($"An error occurred during user update.", ex);
            }
        }

        /// <summary>
        /// Changes a user's access level. Admin-only, called from PATCH /api/users/{id}/level.
        /// </summary>
        public async Task<UserDto> SetLevelAsync(Guid userId, Domain.Enums.UserLevel level, CancellationToken cancellationToken = default)
        {
            if (!Enum.IsDefined(typeof(Domain.Enums.UserLevel), level))
            {
                throw new ArgumentException($"Unknown user level: {(int)level}.", nameof(level));
            }

            User? user = await _userRepository.GetByIdAsync(userId, cancellationToken);
            if (user == null)
            {
                throw new InvalidOperationException($"User with ID {userId} not found.");
            }

            user.Level = level;
            user.UpdatedAt = DateTime.UtcNow;

            await _userRepository.UpdateAsync(user, cancellationToken);
            _ = await _context.SaveChangesAsync(cancellationToken);

            _ = await _cacheService.RemoveAsync($"user:telegram_id:{user.TelegramId}");
            _ = await _cacheService.RemoveAsync($"user:id:{user.Id}");

            _logger.LogInformation("User {UserId} level changed to {Level}.", userId, level);
            return MapToUserDto(user);
        }


        /// <summary>
        /// Asynchronously deletes a user by their unique internal ID.
        /// Also invalidates the user's cache entry upon successful deletion.
        /// </summary>
        /// <param name="id">The ID of the user to delete.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <exception cref="ApplicationException">Thrown on critical technical errors during deletion.</exception>
        public async Task DeleteUserAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Attempting to delete user with ID: {UserId}", id);

            try
            {
                User? user = await _userRepository.GetByIdAsync(id, cancellationToken);
                if (user == null)
                {
                    _logger.LogWarning("User with ID {UserId} not found for deletion. Operation considered successful.", id);
                    return; // User not found, no action needed.
                }

                // --- Cache Invalidation ---
                // Invalidate cache before deletion.
                string cacheKey = $"user:telegram_id:{user.TelegramId}";
                _ = await _cacheService.RemoveAsync(cacheKey);
                _logger.LogInformation("Invalidated cache for user {TelegramId} due to deletion.", user.TelegramId);

                // Delete the user from the repository.
                await _userRepository.DeleteAsync(user, cancellationToken);
                _ = await _context.SaveChangesAsync(cancellationToken); // Save changes to the DB.

                _logger.LogInformation("User with ID {UserId} deleted successfully.", id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during user deletion for UserID {UserId}.", id);
                throw new ApplicationException($"An error occurred during user deletion.", ex);
            }
        }

        // --- Mapping Methods ---
        // These methods are private helpers for mapping between Domain Entities and DTOs.

        private UserDto MapToUserDto(User user)
        {
            // Ensure that even if user.TokenWallet is null, TokenBalance is 0.0m.
            UserDto userDto = new()
            {
                Id = user.Id,
                Username = user.Username,
                TelegramId = user.TelegramId,
                Email = user.Email,
                Level = user.Level,
                CreatedAt = user.CreatedAt,
                TokenBalance = user.TokenWallet?.Balance ?? 0.0m,
                TokenWallet = user.TokenWallet != null ? _mapper.Map<TokenWalletDto>(user.TokenWallet) : null,

                // --- THIS IS THE CORRECTED LINE ---
                // Use AutoMapper to map the Subscription entity to a SubscriptionDto.
                // This is consistent with how TokenWallet is mapped.
                ActiveSubscription = _mapper.Map<SubscriptionDto>(user.Subscriptions?.OrderByDescending(s => s.StartDate).FirstOrDefault())
            };
            return userDto;
        }

    }
}