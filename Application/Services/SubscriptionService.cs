using Application.Common.Interfaces;
using Application.DTOs;
using Application.Interfaces;
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

        /// <summary>
        /// Asynchronously creates a new subscription for a user.
        /// Handles user existence check, mapping, data access, and potential errors.
        /// </summary>
        /// <param name="createSubscriptionDto">The data transfer object containing information for the new subscription.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The DTO of the newly created subscription.</returns>
        /// <exception cref="NotFoundException">Thrown if the user with the given ID is not found.</exception> // Assuming you have a NotFoundException
        /// <exception cref="ApplicationException">Thrown on critical technical errors during the creation process.</exception>
        public async Task<SubscriptionDto> CreateSubscriptionAsync(CreateSubscriptionDto createSubscriptionDto, CancellationToken cancellationToken = default)
        {
            // Input validation (basic check)
            if (createSubscriptionDto == null)
            {
                throw new ArgumentNullException(nameof(createSubscriptionDto));
            }
            // Add more validation for properties in createSubscriptionDto if needed

            _logger.LogInformation("Attempting to create subscription for UserID {UserId}", createSubscriptionDto.UserId);

            try
            {
                // Retrieve the user to ensure they exist. Potential database interaction.
                var user = await _userRepository.GetByIdAsync(createSubscriptionDto.UserId, cancellationToken);
                if (user == null)
                {
                    _logger.LogWarning("User with ID {UserId} not found for creating subscription.", createSubscriptionDto.UserId);
                    // Throw specific exception for 'Not Found'.
                    // Using a custom NotFoundException is better practice than a generic Exception.
                    throw new InvalidOperationException($"User with ID {createSubscriptionDto.UserId} not found."); // Using InvalidOperationException for consistency with your current code
                }

                // --- Add more business logic here ---
                // - Check for overlapping active subscriptions:
                // var existingActiveSubscription = await _subscriptionRepository.GetActiveSubscriptionByUserIdAsync(user.Id, cancellationToken);
                // if (existingActiveSubscription != null && SubscriptionsOverlap(existingActiveSubscription, createSubscriptionDto))
                // {
                //      _logger.LogWarning("User {UserId} already has an overlapping active subscription.", user.Id);
                //      throw new InvalidOperationException($"User {user.Id} already has an overlapping active subscription.");
                // }
                // - Validate PlanId if applicable:
                // var plan = await _planRepository.GetByIdAsync(createSubscriptionDto.PlanId, cancellationToken);
                // if (plan == null) { throw new InvalidOperationException("Invalid plan ID."); }
                // - Payment processing (if applicable)
                // -------------------------------------


                // Map DTO to Subscription entity. Potential mapping error point.
                var subscription = _mapper.Map<Subscription>(createSubscriptionDto);
                subscription.Id = Guid.NewGuid(); // Ensure ID is set
                subscription.CreatedAt = DateTime.UtcNow; // Set creation timestamp

                // Add the new subscription entity to the context.
                await _subscriptionRepository.AddAsync(subscription, cancellationToken);

                // --- Optional: Update User level based on new subscription ---
                // user.Level = DetermineUserLevel(user, subscription); // Business logic
                // // If updating user, explicitly mark for update or ensure EF tracks it.
                // // _userRepository.UpdateAsync(user, cancellationToken); // Often not needed if entity is tracked
                // -------------------------------------------------------------


                // Save all changes to the database (Subscription and potentially User). **CRITICAL point of failure.**
                _ = await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Subscription with ID {SubscriptionId} created successfully for UserID {UserId}", subscription.Id, subscription.UserId);

                // Map the created Subscription entity back to DTO for return. Potential mapping error point.
                return _mapper.Map<SubscriptionDto>(subscription);
            }
            catch (InvalidOperationException) // Catch specific business rule exceptions (User not found, Overlapping subscription etc.)
            {
                // Re-throw the business exception.
                throw;
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically.
                _logger.LogInformation(ex, "Subscription creation for UserID {UserId} was cancelled.", createSubscriptionDto.UserId);
                throw; // Re-throw cancellation.
            }
            // Catch specific database/ORM exceptions if you want more granular logging or handling.
            // catch (DbUpdateException dbEx) // Example: Foreign Key violation if UserID doesn't exist (though GetByIdAsync handles this earlier) or other DB constraints.
            // {
            //     _logger.LogError(dbEx, "Database update error during subscription creation for UserID {UserId}.", createSubscriptionDto.UserId);
            //     throw new ApplicationException($"Database error occurred while creating subscription for user {createSubscriptionDto.UserId}.", dbEx);
            // }
            // catch (AutoMapperMappingException mapEx) // Example: Mapping errors
            // {
            //      _logger.LogError(mapEx, "Mapping error during subscription creation for UserID {UserId}.", createSubscriptionDto.UserId);
            //      throw new ApplicationException($"Error processing subscription data for user {createSubscriptionDto.UserId}.", mapEx);
            // }
            catch (Exception ex)
            {
                // Catch any other unexpected technical exceptions.
                // Log the critical technical error.
                _logger.LogError(ex, "An unexpected error occurred during subscription creation process for UserID {UserId}.", createSubscriptionDto.UserId);

                // Throw a generic application exception indicating a critical failure.
                throw new ApplicationException("An unexpected error occurred during subscription creation. Please try again later.", ex);
            }
        }

        /// <summary>
        /// Asynchronously retrieves the active subscription for a specific user.
        /// Handles potential data access and mapping errors.
        /// </summary>
        /// <param name="userId">The ID of the user.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The DTO of the active subscription if found, otherwise null. Throws an exception on critical failure.</returns>
        // NOTE: The return type is SubscriptionDto?, indicating that null means "no active subscription found",
        // while an exception means a critical error occurred during retrieval/processing.
        public async Task<SubscriptionDto?> GetActiveSubscriptionByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            // Basic validation for Guid.Empty if needed.
            // if (userId == Guid.Empty) { ... log warning and return null ... }

            _logger.LogDebug("Fetching active subscription for UserID {UserId}", userId);

            try
            {
                // Fetch active subscription from the repository. Potential database interaction.
                var subscription = await _subscriptionRepository.GetActiveSubscriptionByUserIdAsync(userId, cancellationToken);

                // Handle case where no active subscription is found (normal outcome).
                if (subscription == null)
                {
                    _logger.LogDebug("No active subscription found for UserID {UserId}", userId);
                    return null; // Return null as no active subscription was found.
                }

                // Map Subscription entity to SubscriptionDto. Potential mapping error point.
                return _mapper.Map<SubscriptionDto>(subscription);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically.
                _logger.LogInformation(ex, "Fetching active subscription for UserID {UserId} was cancelled.", userId);
                throw; // Re-throw the cancellation exception.
            }
            // Catch specific database or mapping exceptions if desired.
            // catch (DbException dbEx)
            // {
            //     _logger.LogError(dbEx, "Database error while fetching active subscription for UserID {UserId}.", userId);
            //     throw new ApplicationException($"Database error occurred while retrieving active subscription for user {userId}.", dbEx);
            // }
            // catch (AutoMapperMappingException mapEx)
            // {
            //     _logger.LogError(mapEx, "Mapping error while processing subscription data for UserID {UserId}.", userId);
            //     throw new ApplicationException($"Error processing subscription data for user {userId}.", mapEx);
            // }
            catch (Exception ex)
            {
                // Catch any other unexpected technical exceptions.
                // Log the general error with context.
                _logger.LogError(ex, "An unexpected error occurred while fetching or processing active subscription for UserID {UserId}.", userId);

                // Throw a generic application exception indicating a critical failure.
                // This distinguishes a technical failure from a "subscription not found" case.
                throw new ApplicationException($"An error occurred while retrieving active subscription for user {userId}.", ex);
            }
        }



        /// <summary>
        /// Asynchronously retrieves all subscriptions for a specific user.
        /// Handles potential data access and mapping errors.
        /// </summary>
        /// <param name="userId">The ID of the user.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>An enumerable collection of Subscription DTOs. Returns an empty collection if no subscriptions are found or on certain errors if designed to return empty list on error. Throws an exception on critical failure.</returns>
        // NOTE: The return type is IEnumerable<SubscriptionDto>, which implies success means returning
        // a collection (potentially empty). A critical technical error should be handled by throwing an exception.
        public async Task<IEnumerable<SubscriptionDto>> GetUserSubscriptionsAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            // Basic validation for Guid.Empty if needed.
            // if (userId == Guid.Empty) { ... log warning and return Enumerable.Empty<SubscriptionDto>() ... }


            _logger.LogDebug("Fetching all subscriptions for UserID {UserId}", userId);

            try
            {
                // Fetch subscriptions from the repository. Potential database interaction.
                var subscriptions = await _subscriptionRepository.GetSubscriptionsByUserIdAsync(userId, cancellationToken);

                // If no subscriptions are found, the repository should return an empty collection or null.
                // Mapping a null collection to IEnumerable usually results in an empty collection.
                // So, checking for null here is mainly if repository *might* return null.
                // If repository returns an empty list for no results, this check isn't strictly needed for null,
                // but checking 'Any()' might be useful for logging "no subscriptions found".

                _logger.LogDebug("Found {SubscriptionCount} subscriptions for UserID {UserId}", subscriptions?.Count() ?? 0, userId);


                // Map the collection of Subscription entities to a collection of Subscription DTOs. Potential mapping error point.
                // If 'subscriptions' is null, AutoMapper's Map<IEnumerable<T>> should handle it gracefully, often returning an empty IEnumerable.
                return _mapper.Map<IEnumerable<SubscriptionDto>>(subscriptions);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically.
                _logger.LogInformation(ex, "Fetching subscriptions for UserID {UserId} was cancelled.", userId);
                throw; // Re-throw the cancellation exception.
            }
            // Catch specific database or mapping exceptions if desired.
            // catch (DbException dbEx)
            // {
            //     _logger.LogError(dbEx, "Database error while fetching subscriptions for UserID {UserId}.", userId);
            //     // Throw a higher-level exception.
            //     throw new ApplicationException($"Database error occurred while retrieving subscriptions for user {userId}.", dbEx);
            // }
            // catch (AutoMapperMappingException mapEx)
            // {
            //     _logger.LogError(mapEx, "Mapping error while processing subscriptions for UserID {UserId}.", userId);
            //     throw new ApplicationException($"Error processing subscription data for user {userId}.", mapEx);
            // }
            catch (Exception ex)
            {
                // Catch any other unexpected technical exceptions.
                // Log the general error with context.
                _logger.LogError(ex, "An unexpected error occurred while fetching or processing subscriptions for UserID {UserId}.", userId);

                // Throw a generic application exception indicating a critical failure.
                // Since the return type is IEnumerable<T>, throwing an exception is the standard way
                // to signal a technical failure that prevents returning the expected collection.
                throw new ApplicationException($"An error occurred while retrieving subscriptions for user {userId}. Please try again.", ex);
                // Alternatively, *if* your design allows returning an empty collection on *any* error,
                // you could return Enumerable.Empty<SubscriptionDto>(); but this is less common
                // for unrecoverable technical errors like database connection issues.
                // return Enumerable.Empty<SubscriptionDto>(); // Generally AVOID for technical errors.
            }
        }

        /// <summary>
        /// Asynchronously retrieves a subscription by its unique internal ID.
        /// Handles cases where the subscription is not found and potential data access/mapping errors.
        /// </summary>
        /// <param name="subscriptionId">The ID of the subscription to retrieve.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The DTO of the subscription if found, otherwise null. Throws an exception on critical failure.</returns>
        // NOTE: The return type is SubscriptionDto?, indicating that null means "subscription not found",
        // while an exception means a critical error occurred during retrieval/processing.
        public async Task<SubscriptionDto?> GetSubscriptionByIdAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
        {
            // Basic validation for Guid.Empty if needed.
            // if (subscriptionId == Guid.Empty) { ... log warning and return null ... }

            _logger.LogDebug("Fetching subscription with ID {SubscriptionId}", subscriptionId);

            try
            {
                // Fetch subscription from the repository by internal ID. Potential database interaction.
                var subscription = await _subscriptionRepository.GetByIdAsync(subscriptionId, cancellationToken);

                // Handle case where subscription is not found (normal outcome).
                if (subscription == null)
                {
                    _logger.LogWarning("Subscription with ID {SubscriptionId} not found.", subscriptionId);
                    return null; // Return null as the subscription was not found.
                }

                // Map Subscription entity to SubscriptionDto. Potential mapping error point.
                return _mapper.Map<SubscriptionDto>(subscription);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically.
                _logger.LogInformation(ex, "Fetching subscription with ID {SubscriptionId} was cancelled.", subscriptionId);
                throw; // Re-throw the cancellation exception.
            }
            // Catch specific database or mapping exceptions if desired.
            // catch (DbException dbEx)
            // {
            //     _logger.LogError(dbEx, "Database error while fetching subscription with ID {SubscriptionId}.", subscriptionId);
            //     throw new ApplicationException($"Database error occurred while retrieving subscription {subscriptionId}.", dbEx);
            // }
            // catch (AutoMapperMappingException mapEx)
            // {
            //     _logger.LogError(mapEx, "Mapping error while processing subscription data for ID {SubscriptionId}.", subscriptionId);
            //     throw new ApplicationException($"Error processing subscription data for subscription {subscriptionId}.", mapEx);
            // }
            catch (Exception ex)
            {
                // Catch any other unexpected technical exceptions.
                // Log the general error with context.
                _logger.LogError(ex, "An unexpected error occurred while fetching or processing subscription with ID {SubscriptionId}.", subscriptionId);

                // Throw a generic application exception indicating a critical failure.
                // This distinguishes a technical failure from a "subscription not found" case.
                throw new ApplicationException($"An error occurred while retrieving subscription {subscriptionId}.", ex);
            }
        }
    }
}