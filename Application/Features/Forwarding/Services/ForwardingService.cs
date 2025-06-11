// File: Application\Features\Forwarding\Services\ForwardingService.cs

using Application.Common.Interfaces;
using Application.Features.Forwarding.Interfaces;
using Domain.Features.Forwarding.Entities; // For ForwardingRule
using Domain.Features.Forwarding.Repositories; // For IForwardingRuleRepository
using Hangfire; // For [AutomaticRetry], IBackgroundJobClient
// Specific usings for caching and shared models
using Microsoft.Extensions.Caching.Memory; // For IMemoryCache, MemoryCacheEntryOptions
using Microsoft.Extensions.Logging;
// MODIFICATION START: Add Polly namespaces for resilience policies
using Polly;
using Polly.Retry;
using TL; // For Peer, MessageEntity
// MODIFICATION END

namespace Application.Features.Forwarding.Services
{
    public class ForwardingService : IForwardingService
    {

        private readonly IForwardingRuleRepository _ruleRepository;
        private readonly ILogger<ForwardingService> _logger; // Logger type specific to this service
        private readonly IBackgroundJobClient _backgroundJobClient; // Kept for consistency if other enqueueing is needed
        private readonly MessageProcessingService _messageProcessingService; // ADDED: Dependency for MessageProcessingService
        private readonly IAppDbContext _context; // یا نام انتزاعی که استفاده می کنید
        // Caching fields for performance
        private readonly IMemoryCache _memoryCache;
        private readonly TimeSpan _rulesCacheExpiration = TimeSpan.FromMinutes(5); // Cache rules for 5 minutes

        // MODIFICATION START: Declare Polly retry policy for database operations (rule retrieval)
        private readonly AsyncRetryPolicy<IEnumerable<ForwardingRule>> _ruleRetrievalRetryPolicy;
        // MODIFICATION END

        // Constructor - All dependencies injected, cleaned up duplicates
        public ForwardingService(
              // Injecting the repository for rule management
              IAppDbContext context,
             IForwardingRuleRepository ruleRepository,
             ILogger<ForwardingService> logger,
             IBackgroundJobClient backgroundJobClient,
             IMemoryCache memoryCache,
             MessageProcessingService messageProcessingService) // ADDED: Inject MessageProcessingService
        {
            _context = context ?? throw new ArgumentNullException(nameof(context)); // Ensure context is not null
            _ruleRepository = ruleRepository ?? throw new ArgumentNullException(nameof(ruleRepository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _backgroundJobClient = backgroundJobClient ?? throw new ArgumentNullException(nameof(backgroundJobClient));
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
            _messageProcessingService = messageProcessingService ?? throw new ArgumentNullException(nameof(messageProcessingService)); // Initialize messageProcessingService

            // MODIFICATION START: Initialize the Polly retry policy for rule retrieval using Policy<IEnumerable<ForwardingRule>>
            _ruleRetrievalRetryPolicy = Policy<IEnumerable<ForwardingRule>> // <--- Changed from 'Policy' to 'Policy<IEnumerable<ForwardingRule>>'
                .Handle<Exception>(ex =>
                {
                    // Filter exceptions that are transient for database operations.
                    // Customize these exception types based on your database and ORM.
                    // Examples for PostgreSQL with Npgsql: Npgsql.NpgsqlException
                    // Examples for SQL Server: System.Data.SqlClient.SqlException, System.TimeoutException
                    if (ex is TimeoutException) return true; // Common for transient network/DB issues
                    // if (ex is Npgsql.NpgsqlException pgEx && pgEx.IsTransient) return true; // Check for transient PostgreSQL errors
                    // if (ex is System.Data.SqlClient.SqlException sqlEx && IsSqlTransientError(sqlEx)) return true; // Custom check for SQL Server transient errors

                    _logger.LogWarning(ex, "Polly: Transient error occurred while retrieving forwarding rules from the repository. Retrying...");
                    return true; // Catch-all for other exceptions for now; refine this to be more specific if possible.
                })
                .WaitAndRetryAsync(new[] // Async policy with increasing delays
                {
                    TimeSpan.FromSeconds(1), // First retry after 1 second
                    TimeSpan.FromSeconds(3), // Second retry after 3 seconds
                    TimeSpan.FromSeconds(10) // Third (final) retry after 10 seconds
                }, (exception, timeSpan, retryCount, context) =>
                {
                });
            // MODIFICATION END
        }

        // --- Standard CRUD/Read methods (minimal logging kept if any) ---

        // NOTE: For other CRUD methods (GetRuleAsync, CreateRuleAsync, UpdateRuleAsync, DeleteRuleAsync),
        // you might also consider applying similar Polly policies if they interact with the database
        // and require resilience against transient failures. For this request, we are focusing on
        // ProcessMessageAsync as it's the core forwarding logic.
        /// <summary>
        /// Asynchronously retrieves a forwarding rule by its unique name.
        /// Handles cases where the rule is not found and potential data access errors.
        /// </summary>
        /// <param name="ruleName">The name of the forwarding rule.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The ForwardingRule entity if found, otherwise null. Throws an exception on critical failure.</returns>
        // NOTE: The return type is ForwardingRule?, indicating that null means "rule not found",
        // while an exception means a critical error occurred during retrieval.
        public async Task<ForwardingRule?> GetRuleAsync(string ruleName, CancellationToken cancellationToken = default)
        {
            // Basic validation
            if (string.IsNullOrWhiteSpace(ruleName))
            {
                // Log warning or throw ArgumentException depending on strictness
                _logger.LogWarning("Attempted to get rule with null or empty name.");
                return null; // Treat invalid input as not found
            }

            _logger.LogDebug("Fetching forwarding rule by name: {RuleName}", ruleName);

            try
            {
                // Fetch the rule from the repository. Potential database interaction.
                var rule = await _ruleRepository.GetByIdAsync(ruleName, cancellationToken);

                // Handle case where rule is not found (normal outcome).
                if (rule == null)
                {
                    _logger.LogDebug("Forwarding rule with name {RuleName} not found.", ruleName);
                    return null; // Return null as the rule was not found.
                }

                _logger.LogDebug("Forwarding rule with name {RuleName} found.", ruleName);
                return rule; // Return the found entity.
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically.
                _logger.LogInformation(ex, "Fetching forwarding rule '{RuleName}' was cancelled.", ruleName);
                throw; // Re-throw the cancellation exception.
            }
            // Catch specific database exceptions if desired.
            // catch (DbException dbEx)
            // {
            //     _logger.LogError(dbEx, "Database error while fetching forwarding rule '{RuleName}'.", ruleName);
            //     throw new ApplicationException($"Database error occurred while retrieving rule '{ruleName}'.", dbEx);
            // }
            catch (Exception ex)
            {
                // Catch any other unexpected technical exceptions.
                // Log the general error.
                _logger.LogError(ex, "An unexpected error occurred while fetching forwarding rule '{RuleName}'.", ruleName);

                // Throw a generic application exception indicating a critical failure.
                throw new ApplicationException($"An error occurred while retrieving rule '{ruleName}'. Please try again.", ex);
            }
        }

        /// <summary>
        /// Asynchronously retrieves all forwarding rules.
        /// Handles potential data access errors.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>An enumerable collection of ForwardingRule entities. Returns an empty collection if no rules are found. Throws an exception on critical failure.</returns>
        // NOTE: The return type is IEnumerable<ForwardingRule>, implying success means returning
        // a collection (potentially empty). A critical technical error should be handled by throwing an exception.
        public async Task<IEnumerable<ForwardingRule>> GetAllRulesAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Fetching all forwarding rules.");

            try
            {
                // Fetch all rules from the repository. Potential database interaction.
                var rules = await _ruleRepository.GetAllAsync(cancellationToken);

                // The repository should return an empty collection or null if no rules exist.
                // Returning the result directly is usually fine.

                _logger.LogDebug("Fetched {RuleCount} forwarding rules.", rules?.Count() ?? 0);
                return rules ?? Enumerable.Empty<ForwardingRule>(); // Ensure returning an empty collection if repository returned null
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically.
                _logger.LogInformation(ex, "Fetching all forwarding rules was cancelled.");
                throw; // Re-throw the cancellation exception.
            }
            // Catch specific database exceptions if desired.
            // catch (DbException dbEx)
            // {
            //     _logger.LogError(dbEx, "Database error while fetching all forwarding rules.");
            //     throw new ApplicationException("Database error occurred while retrieving all rules.", dbEx);
            // }
            catch (Exception ex)
            {
                // Catch any other unexpected technical exceptions.
                // Log the general error.
                _logger.LogError(ex, "An unexpected error occurred while fetching all forwarding rules.");

                // Throw a generic application exception indicating a critical failure.
                // Since the return type is IEnumerable<T>, throwing an exception is the standard way
                // to signal a technical failure that prevents returning the expected collection.
                throw new ApplicationException("An unexpected error occurred while retrieving all rules. Please try again.", ex);
                // Alternatively, *if* your design allows returning an empty collection on *any* error,
                // you could return Enumerable.Empty<ForwardingRule>(); but throwing is more common for critical errors.
            }
        }


        /// <summary>
        /// Asynchronously retrieves forwarding rules associated with a specific source channel.
        /// Uses a Polly retry policy for resilience in data access operations.
        /// Handles potential data access errors not fully managed by the retry policy.
        /// </summary>
        /// <param name="sourceChannelId">The ID of the source channel.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>An enumerable collection of ForwardingRule entities. Returns an empty collection if no rules are found. Throws an exception on critical failure.</returns>
        // NOTE: The return type is IEnumerable<ForwardingRule>, implying success means returning
        // a collection (potentially empty). A critical technical error should be handled by throwing an exception.
        public async Task<IEnumerable<ForwardingRule>> GetRulesBySourceChannelAsync(long sourceChannelId, CancellationToken cancellationToken = default)
        {
            // Basic validation for sourceChannelId if needed (e.g., if 0 or negative is invalid)
            // if (sourceChannelId <= 0) { ... log warning and return Enumerable.Empty<ForwardingRule>() ... }

            _logger.LogDebug("ForwardingService: Attempting to retrieve rules for source channel {SourceChannelId}.", sourceChannelId);

            try
            {
                // Execute the repository call within the Polly retry policy.
                // This handles transient database errors and retries.
                var rules = await _ruleRetrievalRetryPolicy.ExecuteAsync(async () =>
                {
                    // This lambda is the actual work being retried by Polly.
                    _logger.LogTrace("Polly Execute: Retrieving rules for source channel {SourceChannelId}.", sourceChannelId);
                    var repoRules = await _ruleRepository.GetBySourceChannelAsync(sourceChannelId, cancellationToken);
                    _logger.LogTrace("Polly Execute: Successfully retrieved {RuleCount} rules from repository for source channel {SourceChannelId}.", repoRules.Count(), sourceChannelId);
                    return repoRules;
                }).ConfigureAwait(false); // Use ConfigureAwait(false) if not strictly needing context

                // If Polly's execution was successful (either on first try or after retries),
                // the result is returned here. It might be an empty collection if no rules exist.
                _logger.LogDebug("ForwardingService: Finished rule retrieval (including retries) for source channel {SourceChannelId}. Found {RuleCount} rules.", sourceChannelId, rules?.Count() ?? 0);

                // Ensure returning an empty collection if the repository method returned null (less common but safe).
                return rules ?? Enumerable.Empty<ForwardingRule>();
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically. This exception is often thrown by awaited operations
                // when the linked CancellationToken is cancelled. Polly should ideally propagate this.
                _logger.LogInformation(ex, "Forwarding rule retrieval for source channel {SourceChannelId} was cancelled.", sourceChannelId);
                throw; // Re-throw the cancellation exception.
            }
            // Catch specific repository or ORM exceptions if you want to log them differently before the general catch.
            // Example: Catch a specific RepositoryException if your repository throws them.
            // catch (RepositoryException repEx)
            // {
            //     _logger.LogError(repEx, "Repository error (after Polly retries) while fetching rules for source channel {SourceChannelId}.", sourceChannelId);
            //     throw new ApplicationException($"Data access error while retrieving rules for channel {sourceChannelId}.", repEx);
            // }
            catch (Exception ex)
            {
                // Catch any other unexpected technical exceptions that might occur:
                // - Exceptions that Polly didn't handle/retry (e.g., configuration errors, NullReferenceExceptions not from transient issues).
                // - The final exception after Polly has exhausted all retries.
                // Log the critical technical error.
                _logger.LogError(ex, "An unexpected error occurred while fetching forwarding rules for source channel {SourceChannelId} (after Polly retries).", sourceChannelId);

                // Throw a generic application exception indicating a critical failure.
                // Since the return type is IEnumerable<T>, throwing is the standard way
                // to signal a technical failure that prevents returning the expected collection.
                throw new ApplicationException($"An unexpected error occurred while retrieving rules for channel {sourceChannelId}. Please try again.", ex);
                // Alternatively, *if* your design allows returning an empty collection on *any* error (including technical),
                // you could return Enumerable.Empty<ForwardingRule>(); but throwing provides a clearer signal of failure.
            }
        }


        /// <summary>
        /// Asynchronously creates a new forwarding rule.
        /// Handles business validation (unique rule name) and potential data access errors.
        /// </summary>
        /// <param name="rule">The ForwardingRule entity to create.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the rule object is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown if a rule with the same name already exists.</exception>
        /// <exception cref="ApplicationException">Thrown on critical technical errors during the creation process.</exception>
        public async Task CreateRuleAsync(ForwardingRule rule, CancellationToken cancellationToken = default)
        {
            // Input validation
            if (rule == null)
            {
                throw new ArgumentNullException(nameof(rule));
            }
            if (string.IsNullOrWhiteSpace(rule.RuleName))
            {
                _logger.LogWarning("Attempted to create rule with null or empty name.");
                throw new ArgumentException("Rule name cannot be null or empty.", nameof(rule.RuleName));
            }


            _logger.LogInformation("Attempting to create forwarding rule: {RuleName}", rule.RuleName);

            try
            {
                // Business validation: Check if a rule with this name already exists. Potential database interaction.
                var existingRule = await _ruleRepository.GetByIdAsync(rule.RuleName, cancellationToken);
                if (existingRule != null)
                {
                    _logger.LogWarning("Rule creation failed: A rule with name '{RuleName}' already exists.", rule.RuleName);
                    // Throw specific business exception
                    throw new InvalidOperationException($"A rule with name '{rule.RuleName}' already exists.");
                }

                // Add the new rule entity to the repository context. Potential database interaction preparation.
                await _ruleRepository.AddAsync(rule, cancellationToken);

                // Assuming AddAsync triggers SaveChangesAsync internally or you have a separate Unit of Work SaveChangesAsync call.
                // If SaveChangesAsync is called here or within the Repository, this is the critical point of failure.
                // If AddAsync just adds to context and SaveChanges is elsewhere (e.g., in calling service/handler),
                // the try-catch should ideally wrap the SaveChanges call in the caller.
                // Assuming SaveChangesAsync happens within or immediately after this AddAsync call for simplicity here.
                // If SaveChanges is separate, consider moving the catch blocks there.
                await _context.SaveChangesAsync(cancellationToken); // Explicitly adding SaveChangesAsync if it's not in Repository.AddAsync

                _logger.LogInformation("Forwarding rule '{RuleName}' created successfully.", rule.RuleName);
            }
            catch (InvalidOperationException) // Catch specific business rule exception (Rule already exists)
            {
                // Re-throw the business exception.
                throw;
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically.
                _logger.LogInformation(ex, "Rule creation for '{RuleName}' was cancelled.", rule.RuleName);
                throw; // Re-throw cancellation.
            }
            // Catch specific database/ORM exceptions if desired (e.g., DbUpdateException for constraint violations during SaveChanges).
            // catch (DbUpdateException dbEx) // Example: For unique constraint violation during SaveChanges
            // {
            //     _logger.LogError(dbEx, "Database update error during rule creation for '{RuleName}'.", rule.RuleName);
            //     // You might inspect dbEx for specific error codes (like SQL unique violation) if needed.
            //     throw new ApplicationException($"Database error occurred while creating rule '{rule.RuleName}'.", dbEx);
            // }
            // catch (RepositoryException repEx) // If your repository wraps DB errors
            // {
            //      _logger.LogError(repEx, "Repository error during rule creation for '{RuleName}'.", rule.RuleName);
            //      throw new ApplicationException($"Data access error while creating rule '{rule.RuleName}'.", repEx);
            // }
            catch (Exception ex)
            {
                // Catch any other unexpected technical exceptions.
                // Log the critical technical error.
                _logger.LogError(ex, "An unexpected error occurred during rule creation process for '{RuleName}'.", rule.RuleName);

                // Throw a generic application exception indicating a critical failure.
                // The caller should catch this and handle it.
                throw new ApplicationException("An unexpected error occurred during rule creation. Please try again later.", ex);
            }
        }


        /// <summary>
        /// Asynchronously updates an existing forwarding rule.
        /// Handles cases where the rule is not found and potential data access errors.
        /// </summary>
        /// <param name="rule">The ForwardingRule entity with updated information.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the rule object is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the rule with the given name is not found.</exception>
        /// <exception cref="ApplicationException">Thrown on critical technical errors during the update process.</exception>
        public async Task UpdateRuleAsync(ForwardingRule rule, CancellationToken cancellationToken = default)
        {
            // Input validation
            if (rule == null)
            {
                throw new ArgumentNullException(nameof(rule));
            }
            if (string.IsNullOrWhiteSpace(rule.RuleName))
            {
                _logger.LogWarning("Attempted to update rule with null or empty name.");
                throw new ArgumentException("Rule name cannot be null or empty.", nameof(rule.RuleName));
            }

            _logger.LogInformation("Attempting to update forwarding rule: {RuleName}", rule.RuleName);

            try
            {
                // Retrieve the existing rule to ensure it exists. Potential database interaction.
                var existingRule = await _ruleRepository.GetByIdAsync(rule.RuleName, cancellationToken);
                if (existingRule == null)
                {
                    _logger.LogWarning("Update failed: Rule with name '{RuleName}' not found.", rule.RuleName);
                    // Throw specific business exception for 'Not Found'.
                    throw new InvalidOperationException($"Rule with name '{rule.RuleName}' not found.");
                }

                // Assuming _ruleRepository.UpdateAsync (or SaveChangesAsync after tracking) handles applying changes.
                // This is the point of potential database failure (concurrency, constraints, etc.).
                await _ruleRepository.UpdateAsync(rule, cancellationToken); // This might add/update the entity in context

                // If SaveChangesAsync is not inside Repository.UpdateAsync, call it here.
                // This is the CRITICAL point of failure.
                await _context.SaveChangesAsync(cancellationToken); // Explicitly add if needed

                _logger.LogInformation("Forwarding rule '{RuleName}' updated successfully.", rule.RuleName);
            }
            catch (InvalidOperationException) // Catch specific business rule exception (Rule not found)
            {
                // Re-throw the business exception.
                throw;
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically.
                _logger.LogInformation(ex, "Rule update for '{RuleName}' was cancelled.", rule.RuleName);
                throw; // Re-throw cancellation.
            }
            // Catch specific database/ORM exceptions if desired (e.g., DbUpdateConcurrencyException, DbUpdateException).
            // catch (DbUpdateConcurrencyException dbConcEx) // Example: Concurrency conflict
            // {
            //     _logger.LogError(dbConcEx, "Concurrency conflict during rule update for '{RuleName}'.", rule.RuleName);
            //     throw new ApplicationException($"Concurrency conflict detected while updating rule '{rule.RuleName}'.", dbConcEx);
            // }
            // catch (DbUpdateException dbEx) // Example: Other database update errors
            // {
            //      _logger.LogError(dbEx, "Database update error during rule update for '{RuleName}'.", rule.RuleName);
            //      throw new ApplicationException($"Database error occurred while updating rule '{rule.RuleName}'.", dbEx);
            // }
            // catch (RepositoryException repEx) // If your repository wraps DB errors
            // {
            //      _logger.LogError(repEx, "Repository error during rule update for '{RuleName}'.", rule.RuleName);
            //      throw new ApplicationException($"Data access error while updating rule '{rule.RuleName}'.", repEx);
            // }
            catch (Exception ex)
            {
                // Catch any other unexpected technical exceptions.
                // Log the critical technical error.
                _logger.LogError(ex, "An unexpected error occurred during rule update process for '{RuleName}'.", rule.RuleName);

                // Throw a generic application exception indicating a critical failure.
                throw new ApplicationException("An unexpected error occurred during rule update. Please try again later.", ex);
            }
        }


        /// <summary>
        /// Asynchronously deletes a forwarding rule by its name.
        /// Handles cases where the rule is not found and potential data access errors.
        /// </summary>
        /// <param name="ruleName">The name of the rule to delete.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the rule name is null or empty.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the rule with the given name is not found.</exception>
        /// <exception cref="ApplicationException">Thrown on critical technical errors during the deletion process.</exception>
        public async Task DeleteRuleAsync(string ruleName, CancellationToken cancellationToken = default)
        {
            // Input validation
            if (string.IsNullOrWhiteSpace(ruleName))
            {
                _logger.LogWarning("Attempted to delete rule with null or empty name.");
                throw new ArgumentException("Rule name cannot be null or empty.", nameof(ruleName));
            }

            _logger.LogInformation("Attempting to delete forwarding rule: {RuleName}", ruleName);

            try
            {
                // Retrieve the existing rule to ensure it exists before attempting deletion. Potential database interaction.
                var existingRule = await _ruleRepository.GetByIdAsync(ruleName, cancellationToken);
                if (existingRule == null)
                {
                    _logger.LogWarning("Deletion failed: Rule with name '{RuleName}' not found.", ruleName);
                    // Throw specific business exception for 'Not Found'.
                    throw new InvalidOperationException($"Rule with name '{ruleName}' not found.");
                }

                // Delete the rule entity (or mark for deletion). Potential database interaction/preparation.
                // Assuming _ruleRepository.DeleteAsync(ruleName, cancellationToken) handles the deletion and/or context tracking.
                await _ruleRepository.DeleteAsync(ruleName, cancellationToken);

                // If SaveChangesAsync is not inside Repository.DeleteAsync, call it here.
                // This is the CRITICAL point of failure.
                await _context.SaveChangesAsync(cancellationToken); // Explicitly add if needed

                _logger.LogInformation("Forwarding rule '{RuleName}' deleted successfully.", ruleName);
            }
            catch (InvalidOperationException) // Catch specific business rule exception (Rule not found)
            {
                // Re-throw the business exception.
                throw;
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically.
                _logger.LogInformation(ex, "Rule deletion for '{RuleName}' was cancelled.", ruleName);
                throw; // Re-throw cancellation.
            }
            // Catch specific database/ORM exceptions if desired (e.g., DbUpdateException for constraint violations during SaveChanges).
            // catch (DbUpdateException dbEx) // Example: For Foreign Key constraint violation during SaveChanges
            // {
            //     _logger.LogError(dbEx, "Database update error during rule deletion for '{RuleName}'.", ruleName);
            //     // You might inspect dbEx for specific error codes (like SQL FK violation) if needed.
            //     throw new ApplicationException($"Database error occurred while deleting rule '{ruleName}'.", dbEx);
            // }
            // catch (RepositoryException repEx) // If your repository wraps DB errors
            // {
            //      _logger.LogError(repEx, "Repository error during rule deletion for '{RuleName}'.", ruleName);
            //      throw new ApplicationException($"Data access error while deleting rule '{ruleName}'.", repEx);
            // }
            catch (Exception ex)
            {
                // Catch any other unexpected technical exceptions.
                // Log the critical technical error.
                _logger.LogError(ex, "An unexpected error occurred during rule deletion process for '{RuleName}'.", ruleName);

                // Throw a generic application exception indicating a critical failure.
                throw new ApplicationException("An unexpected error occurred during rule deletion. Please try again later.", ex);
            }
        }

        // --- Core Message Processing Method ---

        [AutomaticRetry(Attempts = 10)] // Configure Hangfire to retry job 10 times if it fails
        public async Task ProcessMessageAsync(
         long sourceChannelIdForMatching,
         long originalMessageId,
         long rawSourcePeerIdForApi,
         string messageContent,
         MessageEntity[]? messageEntities,
         Peer? senderPeerForFilter,
         List<InputMediaWithCaption>? mediaGroupItems,
         CancellationToken cancellationToken = default)
        {
            // Logging stripped down for speed (only error logs kept)

            // Caching Logic: Attempt to retrieve rules from in-memory cache first
            string cacheKey = $"Rules_SourceChannel_{sourceChannelIdForMatching}";
            IEnumerable<ForwardingRule> rules;

            // This part already calls GetRulesBySourceChannelAsync, which now uses Polly
            if (!_memoryCache.TryGetValue(cacheKey, out rules))
            {
                rules = await GetRulesBySourceChannelAsync(sourceChannelIdForMatching, cancellationToken); // This call is now protected by Polly
                _memoryCache.Set(cacheKey, rules, new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(_rulesCacheExpiration)
                    .SetSlidingExpiration(_rulesCacheExpiration));
                _logger.LogInformation("ForwardingService: Rules for source {SourceId} loaded from DB and cached.", sourceChannelIdForMatching);
            }
            else
            {
                _logger.LogInformation("ForwardingService: Rules for source {SourceId} loaded from cache.", sourceChannelIdForMatching);
            }


            var activeRules = rules.Where(r => r.IsEnabled).ToList();

            if (!activeRules.Any())
            {
                _logger.LogInformation("ForwardingService: No active rules found for source {SourceId}. Skipping message {MessageId}.", sourceChannelIdForMatching, originalMessageId);
                return;
            }

            foreach (var rule in activeRules)
            {
                try
                {
                    if (rule.TargetChannelIds == null || !rule.TargetChannelIds.Any())
                    {
                        _logger.LogWarning("ForwardingService: Rule '{RuleName}' for source {SourceId} has no target channels defined. Skipping this rule for message {MessageId}.", rule.RuleName, sourceChannelIdForMatching, originalMessageId);
                        continue;
                    }

                    // This delegation also benefits from Polly because MessageProcessingService.EnqueueAndRelayMessageAsync
                    // is also internally protected by Polly for its enqueue operations.
                    await _messageProcessingService.EnqueueAndRelayMessageAsync(
                        sourceChannelIdForMatching,
                        originalMessageId,
                        rawSourcePeerIdForApi,
                        messageContent,
                        messageEntities,
                        senderPeerForFilter,
                        mediaGroupItems,
                        rule, // Pass the rule directly to the MessageProcessingService
                        cancellationToken
                    );
                    _logger.LogDebug("ForwardingService: Enqueued job for rule '{RuleName}' (Msg {MessageId}, Source {SourceId}).", rule.RuleName, originalMessageId, sourceChannelIdForMatching);
                }
                catch (Exception ex)
                {
                    // CRITICAL LOG: Always keep error logs for job failures
                    // This catch block handles exceptions *during the enqueueing process* within the loop.
                    // The job itself (ProcessMessageAsync) will retry due to its [AutomaticRetry] attribute
                    // if any of these inner operations throw a persistent error.
                    _logger.LogError(ex, "FORWARDING_SERVICE_ERROR: Error processing message {MessageId} from SourceForMatching {SourceForMatching} for rule '{RuleName}'. This will cause the main job to retry.",
                        originalMessageId, sourceChannelIdForMatching, rule.RuleName);
                    // Throwing here ensures Hangfire retries the entire ProcessMessageAsync job,
                    // which will re-attempt all rules for this message.
                    throw;
                }
            }
        }
    }
}