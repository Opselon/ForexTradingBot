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
            IAppDbContext context,
            ILogger<UserService> logger)
        {
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
            // Validate input early
            if (string.IsNullOrWhiteSpace(telegramId))
            {
                _logger.LogWarning("Attempted to get user with null or empty Telegram ID.");
                // Depending on requirements, you might throw an ArgumentException here,
                // but returning null is also acceptable if treated as "not found due to invalid input".
                return null;
            }

            _logger.LogInformation("Fetching user by Telegram ID: {TelegramId}", telegramId);

            try
            {
                // Fetch user from the repository by Telegram ID. Potential database interaction.
                // Assumed: This method includes TokenWallet information.
                var user = await _userRepository.GetByTelegramIdAsync(telegramId, cancellationToken);

                // Handle case where user is not found (this is a normal outcome, not an error)
                if (user == null)
                {
                    _logger.LogWarning("User with Telegram ID {TelegramId} not found.", telegramId);
                    return null; // Return null as the user was not found.
                }

                _logger.LogInformation("User with Telegram ID {TelegramId} found: {Username}", telegramId, user.Username);

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
                _logger.LogInformation(ex, "Fetching user by Telegram ID {TelegramId} was cancelled.", telegramId);
                throw; // Re-throw the cancellation exception.
            }
            // Catch specific database or mapping exceptions if desired.
            // catch (DbException dbEx)
            // {
            //     _logger.LogError(dbEx, "Database error while fetching user with Telegram ID {TelegramId}.", telegramId);
            //     throw new ApplicationException($"Database error occurred while retrieving user {telegramId}.", dbEx);
            // }
            // catch (AutoMapperMappingException mapEx)
            // {
            //     _logger.LogError(mapEx, "Mapping error while processing user data for Telegram ID {TelegramId}.", telegramId);
            //     throw new ApplicationException($"Error processing user data for user {telegramId}.", mapEx);
            // }
            catch (Exception ex)
            {
                // Catch any other unexpected technical exceptions (general database errors, mapping errors, etc.)
                // Log the general error with context.
                _logger.LogError(ex, "An unexpected error occurred while fetching or processing user with Telegram ID {TelegramId}.", telegramId);

                // Depending on your error handling strategy and caller's expectations:
                // Option 1 (Standard for methods returning T? on success/not found): Wrap and throw a higher-level exception.
                // This distinguishes a technical failure from a "user not found" case.
                throw new ApplicationException($"An error occurred while retrieving user {telegramId}.", ex);

                // Option 2 (Less common with T? return, but possible): Return null.
                // This would treat technical errors the same way as "user not found", which is usually NOT desired.
                // return null; // Generally AVOID returning null for technical errors.
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
                await _context.SaveChangesAsync(cancellationToken);
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
            // Input validation (check updateDto itself)
            if (updateDto == null)
            {
                throw new ArgumentNullException(nameof(updateDto));
            }
            // Add validation for properties within updateDto if needed (e.g., empty strings if not allowed)

            _logger.LogInformation("Attempting to update user with ID: {UserId}", userId);

            try
            {
                // Fetch the user entity to update. Potential database interaction.
                var user = await _userRepository.GetByIdAsync(userId, cancellationToken);
                if (user == null)
                {
                    _logger.LogWarning("User with ID {UserId} not found for update.", userId);
                    // Throw a specific exception for 'Not Found' scenario.
                    // Assuming NotFoundException is a custom exception you've defined.
                    // If not, you might use InvalidOperationException but it's less clear.
                    throw new InvalidOperationException($"User with ID {userId} not found for update."); // Using InvalidOperationException for consistency with your current code
                }

                // Check for duplicate email if email is being updated and is different from current. Potential database interaction.
                // Added null check for user.Email defensively
                if (!string.IsNullOrWhiteSpace(updateDto.Email) &&
                    user.Email != null &&
                    !user.Email.Equals(updateDto.Email, StringComparison.OrdinalIgnoreCase))
                {
                    if (await _userRepository.ExistsByEmailAsync(updateDto.Email, cancellationToken))
                    {
                        _logger.LogWarning("Update failed for UserID {UserId}: New email {Email} already exists.", userId, updateDto.Email);
                        // Throw specific business rule violation exception.
                        throw new InvalidOperationException($"Another user with email {updateDto.Email} already exists.");
                    }
                }

                // Apply updates from DTO to entity using AutoMapper. Potential mapping error point.
                // Assuming AutoMapper configuration handles mapping only non-null/non-default values from DTO.
                _mapper.Map(updateDto, user);

                // Update 'UpdatedAt' timestamp.
                user.UpdatedAt = DateTime.UtcNow;

                // Save changes to the database. **CRITICAL point of failure (Database/Concurrency/Constraints).**
                // EF Core tracks the changes to the 'user' entity automatically.
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("User with ID {UserId} updated successfully.", userId);
            }
            catch (InvalidOperationException) // Catch specific business rule exceptions (User not found, Duplicate email)
            {
                // Re-throw the business exception as it's a known condition handled by caller.
                throw;
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically.
                _logger.LogInformation(ex, "User update operation for UserID {UserId} was cancelled.", userId);
                throw; // Re-throw cancellation.
            }
            // Catch specific database/ORM exceptions if you want to differentiate them or handle specific error types.
            // catch (DbUpdateConcurrencyException ex) // Example: Concurrency conflicts
            // {
            //     _logger.LogError(ex, "Concurrency conflict during user update for UserID {UserId}.", userId);
            //     // Handle concurrency: inform user, retry, etc. Re-throw specific exception.
            //     throw new ApplicationException($"Concurrency conflict detected while updating user {userId}.", ex);
            // }
            // catch (DbUpdateException ex) // Example: Other database update errors (constraints, etc.)
            // {
            //      _logger.LogError(ex, "Database update error during user update for UserID {UserId}.", userId);
            //      throw new ApplicationException($"Database error occurred while updating user {userId}.", ex);
            // }
            // catch (AutoMapperMappingException mapEx) // Example: Mapping errors
            // {
            //      _logger.LogError(mapEx, "Mapping error during user update for UserID {UserId}.", userId);
            //      throw new ApplicationException($"Error processing user data for user {userId}.", mapEx);
            // }
            catch (Exception ex)
            {
                // Catch any other unexpected technical exceptions.
                // Log the critical technical error.
                _logger.LogError(ex, "An unexpected error occurred during user update process for UserID {UserId}.", userId);

                // Throw a generic application exception indicating a critical failure.
                // The caller should catch this and inform the user.
                throw new ApplicationException("An unexpected error occurred during user update. Please try again later.", ex);
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
            // Basic validation for Guid.Empty if needed.
            // if (id == Guid.Empty) { ... log warning and return ... }

            _logger.LogInformation("Attempting to delete user with ID: {UserId}", id);

            try
            {
                // Retrieve the user entity. Potential database interaction.
                var user = await _userRepository.GetByIdAsync(id, cancellationToken);

                // If user is not found, treat it as successful deletion (idempotency).
                if (user == null)
                {
                    _logger.LogWarning("User with ID {UserId} not found for deletion. Operation considered successful.", id);
                    return; // Exit gracefully as the user doesn't exist to be deleted.
                }

                // --- Optional: Add business logic before deletion here ---
                // E.g., Logic to handle related entities if not using cascade delete in DB
                // await _subscriptionService.CancelUserSubscriptionsAsync(user.Id, cancellationToken);
                // await _signalsService.RemoveUserSignalsAsync(user.Id, cancellationToken);
                // ----------------------------------------------------------


                // Delete the user entity (or mark for deletion). Potential database interaction/preparation.
                await _userRepository.DeleteAsync(user, cancellationToken); // Assuming this marks the entity as Deleted

                // Save changes to the database. **CRITICAL point of failure (Database/Constraints).**
                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("User with ID {UserId} deleted successfully.", id);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically.
                _logger.LogInformation(ex, "User deletion operation for UserID {UserId} was cancelled.", id);
                throw; // Re-throw cancellation.
            }
            // Catch specific database/ORM exceptions if you want to handle them differently or log more detail.
            // catch (DbUpdateException dbEx) // Example: Foreign Key or other DB constraint violations
            // {
            //     _logger.LogError(dbEx, "Database update error during user deletion for UserID {UserId}.", id);
            //     // Check dbEx details for specific constraint violations if needed.
            //     throw new ApplicationException($"Database error occurred while deleting user {id}.", dbEx);
            // }
            // catch (RepositoryException repEx) // If your repository throws specific exceptions
            // {
            //      _logger.LogError(repEx, "Repository error during user deletion for UserID {UserId}.", id);
            //      throw new ApplicationException($"Error in data access layer while deleting user {id}.", repEx);
            // }
            catch (Exception ex)
            {
                // Catch any other unexpected technical exceptions.
                // Log the critical technical error.
                _logger.LogError(ex, "An unexpected error occurred during user deletion process for UserID {UserId}.", id);

                // Throw a generic application exception indicating a critical failure.
                // The caller should catch this and handle it (e.g., inform admin, log, perhaps inform user if appropriate).
                throw new ApplicationException("An unexpected error occurred during user deletion. Please contact support.", ex);
            }
        }
    }
}