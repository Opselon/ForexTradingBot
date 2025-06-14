// File: Infrastructure/Persistence/Repositories/UserRepository.cs

#region Usings
// Standard .NET & NuGet
// Project specific
using Application.Common.Interfaces; // For IUserRepository
using Dapper; // Dapper for micro-ORM operations
using Domain.Entities;             // For User, Subscription, TokenWallet, UserSignalPreference entities
using Domain.Enums; // For UserLevel enum (stored as string in DB)
using Microsoft.Data.SqlClient; // SQL Server specific connection
using Microsoft.Extensions.Configuration; // To access connection strings
using Microsoft.Extensions.Logging; // For logging
using Polly; // For resilience policies
using Polly.Retry; // For retry policies
using Polly.Timeout; // For custom RepositoryException
using Shared.Extensions;
using System.Data; // Common Ado.Net interfaces like IDbConnection, IDbTransaction
using System.Data.Common; // For DbException (base class for database exceptions)
using System.Linq.Expressions; // Still included, but will throw NotSupportedException
#endregion

namespace Infrastructure.Repositories
{
    /// <summary>
    /// Implements the INewsItemRepository for data operations related to NewsItem entities
    /// using Dapper.
    /// </summary>
    public class UserRepository : IUserRepository
    {
        private readonly string _connectionString;
        private readonly ILogger<UserRepository> _logger;
        private readonly AsyncRetryPolicy _retryPolicy; // Polly policy for DB operations

        // --- Internal DTOs for Dapper Mapping ---
        private class UserDbDto
        {
            public Guid Id { get; set; }
            public string Username { get; set; } = default!;
            public string TelegramId { get; set; } = default!;
            public string Email { get; set; } = default!;
            public string Level { get; set; } = default!; // Mapped from DB string
            public DateTime CreatedAt { get; set; }
            public DateTime? UpdatedAt { get; set; }
            public bool EnableGeneralNotifications { get; set; }
            public bool EnableVipSignalNotifications { get; set; }
            public bool EnableRssNewsNotifications { get; set; }
            public string PreferredLanguage { get; set; } = default!;

            public User ToDomainEntity()
            {
                return new User
                {
                    Id = Id,
                    Username = Username,
                    TelegramId = TelegramId,
                    Email = Email,
                    Level = Enum.Parse<UserLevel>(Level), // Convert string back to enum
                    CreatedAt = CreatedAt,
                    UpdatedAt = UpdatedAt,
                    EnableGeneralNotifications = EnableGeneralNotifications,
                    EnableVipSignalNotifications = EnableVipSignalNotifications,
                    EnableRssNewsNotifications = EnableRssNewsNotifications,
                    PreferredLanguage = PreferredLanguage
                };
            }
        }

        private class TokenWalletDbDto
        {
            public Guid Id { get; set; }
            public Guid UserId { get; set; }
            public decimal Balance { get; set; }
            public bool IsActive { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime UpdatedAt { get; set; }

            public TokenWallet ToDomainEntity()
            {
                // Assuming TokenWallet now has a constructor that accepts all these values.
                return new TokenWallet(Id, UserId, Balance, IsActive, CreatedAt, UpdatedAt);
            }
        }

        private class SubscriptionDbDto
        {
            public Guid Id { get; set; }
            public Guid UserId { get; set; }
            public DateTime StartDate { get; set; }
            public DateTime EndDate { get; set; }
            public string Status { get; set; } = default!;
            public Guid? ActivatingTransactionId { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime? UpdatedAt { get; set; }

            public Subscription ToDomainEntity()
            {
                return new Subscription
                {
                    Id = Id,
                    UserId = UserId,
                    StartDate = StartDate,
                    EndDate = EndDate,
                    Status = Status,
                    ActivatingTransactionId = ActivatingTransactionId,
                    CreatedAt = CreatedAt,
                    UpdatedAt = UpdatedAt
                };
            }
        }

        private class UserSignalPreferenceDbDto
        {
            public Guid Id { get; set; } // Matches PK on UserSignalPreferences table
            public Guid UserId { get; set; }
            public Guid CategoryId { get; set; }
            public DateTime CreatedAt { get; set; }

            public UserSignalPreference ToDomainEntity()
            {
                return new UserSignalPreference
                {
                    Id = Id,
                    UserId = UserId,
                    CategoryId = CategoryId,
                    CreatedAt = CreatedAt
                };
            }
        }
        private readonly AsyncTimeoutPolicy _timeoutPolicy;
        // --- Constructor ---
        public UserRepository(IConfiguration configuration, ILogger<UserRepository> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _connectionString = configuration.GetConnectionString("DefaultConnection")
                                ?? throw new ArgumentNullException("DefaultConnection", "DefaultConnection string not found.");

            _timeoutPolicy = Policy.TimeoutAsync(TimeSpan.FromMinutes(5), TimeoutStrategy.Pessimistic);
            // Polly configuration for transient errors (e.g., network issues, temporary DB unavailability)
            // Excludes primary key violation errors (e.g., trying to add a user with an existing ID/email/telegramId)
            _retryPolicy = Policy
                .Handle<DbException>(ex => !(ex is SqlException sqlEx && (sqlEx.Number == 2627 || sqlEx.Number == 2601))) // SQL Server PK/Unique constraint violation
                .WaitAndRetryAsync(
                    retryCount: 3, // Max 3 retries
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // Exponential backoff: 2s, 4s, 8s
                    onRetry: (exception, timeSpan, retryAttempt, context) =>
                    {
                        _logger.LogWarning(exception,
                            "UserRepository: Transient database error encountered. Retrying in {TimeSpan} for attempt {RetryAttempt}. Error: {Message}",
                            timeSpan, retryAttempt, exception.Message);
                    });
        }

        // --- Helper to create a new SqlConnection ---
        private SqlConnection CreateConnection()
        {
            return new SqlConnection(_connectionString);
        }

        // --- Read Operations ---

        /// <inheritdoc />
        public async Task<IEnumerable<User>> GetUsersForNewsNotificationAsync(
           Guid? newsItemSignalCategoryId,
           bool isNewsItemVipOnly,
           CancellationToken cancellationToken = default)
        {
            _logger.LogInformation(
                "UserRepository: Fetching users for news notification. CategoryId: {CategoryId}, IsVipOnly: {IsVip}",
                newsItemSignalCategoryId, isNewsItemVipOnly);

            try
            {
                return await _retryPolicy.ExecuteAsync(async () =>
                {
                    using var connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    var parameters = new DynamicParameters();
                    if (newsItemSignalCategoryId.HasValue)
                    {
                        parameters.Add("NewsItemSignalCategoryId", newsItemSignalCategoryId.Value);
                    }

                    // SQL Server specific query. Columns should match DTO properties for auto-mapping.
                    // SELECT u.*, tw.*, s.*, usp.* is used for Dapper's multi-mapping to grab all fields.
                    var sql = @"
                        SELECT
                            u.Id, u.Username, u.TelegramId, u.Email, u.Level, u.CreatedAt, u.UpdatedAt,
                            u.EnableGeneralNotifications, u.EnableVipSignalNotifications, u.EnableRssNewsNotifications, u.PreferredLanguage,
                            tw.Id, tw.UserId, tw.Balance, tw.IsActive, tw.CreatedAt, tw.UpdatedAt,
                            s.Id, s.UserId, s.StartDate, s.EndDate, s.Status, s.ActivatingTransactionId, s.CreatedAt, s.UpdatedAt,
                            usp.Id, usp.UserId, usp.CategoryId, usp.CreatedAt
                        FROM Users u
                        LEFT JOIN TokenWallets tw ON u.Id = tw.UserId
                        LEFT JOIN Subscriptions s ON u.Id = s.UserId
                        LEFT JOIN UserSignalPreferences usp ON u.Id = usp.UserId
                        WHERE u.EnableRssNewsNotifications = 1"; // SQL Server boolean true

                    if (isNewsItemVipOnly)
                    {
                        // Assuming VIP access is tied to User.Level being 'Premium' or 'Vip' AND having an active subscription.
                        // Adjust 'Premium', 'Vip' if your UserLevel enum/DB values are different.
                        sql += " AND u.Level IN ('Premium', 'Vip') AND EXISTS (SELECT 1 FROM Subscriptions s_sub WHERE s_sub.UserId = u.Id AND s_sub.StartDate <= GETUTCDATE() AND s_sub.EndDate >= GETUTCDATE() AND s_sub.Status = 'Active')";
                    }

                    if (newsItemSignalCategoryId.HasValue)
                    {
                        // Users with NO preferences OR users who prefer this specific category
                        sql += " AND (NOT EXISTS (SELECT 1 FROM UserSignalPreferences usp_pref WHERE usp_pref.UserId = u.Id) OR EXISTS (SELECT 1 FROM UserSignalPreferences usp_pref WHERE usp_pref.UserId = u.Id AND usp_pref.CategoryId = @NewsItemSignalCategoryId))";
                    }

                    // Use a Dictionary to maintain unique User objects and aggregate their collections
                    var userMap = new Dictionary<Guid, User>();

                    // Dapper's QueryAsync with multi-mapping
                    // The splitOn parameter needs to match the column names where new entities begin
                    // Ensure the order of column names in the SELECT clause matches the order of DTOs here.
                    var result = await connection.QueryAsync<UserDbDto, TokenWalletDbDto, SubscriptionDbDto, UserSignalPreferenceDbDto, User>(
                        sql,
                        (userDto, tokenWalletDto, subscriptionDto, userSignalPreferenceDto) =>
                        {
                            // Get or create the User entity
                            if (!userMap.TryGetValue(userDto.Id, out var user))
                            {
                                user = userDto.ToDomainEntity();
                                user.Subscriptions = [];
                                user.Preferences = [];
                                user.Transactions = []; // Transactions are not included in this query
                                userMap.Add(user.Id, user);
                            }

                            // Assign TokenWallet (if not already assigned and DTO is not null)
                            if (tokenWalletDto != null && user.TokenWallet == null)
                            {
                                user.TokenWallet = tokenWalletDto.ToDomainEntity();
                            }
                            // Ensure every user has a TokenWallet instance (even if default/empty)
                            user.TokenWallet ??= TokenWallet.Create(user.Id); // Default wallet


                            // Add Subscription to the user's collection if not already added
                            if (subscriptionDto != null && !user.Subscriptions.Any(s => s.Id == subscriptionDto.Id))
                            {
                                user.Subscriptions.Add(subscriptionDto.ToDomainEntity());
                            }

                            // Add UserSignalPreference to the user's collection if not already added
                            if (userSignalPreferenceDto != null && !user.Preferences.Any(usp => usp.Id == userSignalPreferenceDto.Id))
                            {
                                user.Preferences.Add(userSignalPreferenceDto.ToDomainEntity());
                            }

                            return user; // Dapper expects to return the parent entity for multi-mapping
                        },
                        param: parameters,
                        splitOn: "Id,Id,Id" // Split on the 'Id' column of TokenWallet, Subscription, UserSignalPreference respectively
                    );

                    // The 'result' IEnumerable will contain duplicate User objects (one for each related row).
                    // userMap.Values already contains the unique, fully hydrated User objects.
                    var eligibleUsers = userMap.Values.ToList();

                    _logger.LogInformation("UserRepository: Found {UserCount} eligible users for news notification.", eligibleUsers.Count);
                    return eligibleUsers;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserRepository: Error fetching users for news notification.");
                throw new RepositoryException("Failed to fetch users for news notification.", ex); // Wrap in custom exception
            }
        }


        /// <inheritdoc />
        public async Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _logger.LogTrace("UserRepository: Fetching user by ID: {UserId}.", id);
            return await _retryPolicy.ExecuteAsync(async () =>
            {
                using var connection = CreateConnection();
                await connection.OpenAsync(cancellationToken);

                // Use QueryMultiple to fetch main user and related entities in one roundtrip
                var sql = @"
                    SELECT Id, Username, TelegramId, Email, Level, CreatedAt, UpdatedAt, EnableGeneralNotifications, EnableVipSignalNotifications, EnableRssNewsNotifications, PreferredLanguage
                    FROM Users WHERE Id = @Id;

                    SELECT Id, UserId, Balance, IsActive, CreatedAt, UpdatedAt
                    FROM TokenWallets WHERE UserId = @Id;

                    SELECT Id, UserId, StartDate, EndDate, Status, ActivatingTransactionId, CreatedAt, UpdatedAt
                    FROM Subscriptions WHERE UserId = @Id;

                    SELECT Id, UserId, CategoryId, CreatedAt
                    FROM UserSignalPreferences WHERE UserId = @Id;";

                using var multi = await connection.QueryMultipleAsync(sql, new { Id = id });

                var userDto = await multi.ReadFirstOrDefaultAsync<UserDbDto>();
                if (userDto == null)
                {
                    return null;
                }

                var user = userDto.ToDomainEntity();
                user.TokenWallet = (await multi.ReadFirstOrDefaultAsync<TokenWalletDbDto>())?.ToDomainEntity() ?? TokenWallet.Create(user.Id);
                user.Subscriptions = (await multi.ReadAsync<SubscriptionDbDto>()).Select(s => s.ToDomainEntity()).ToList();
                user.Preferences = (await multi.ReadAsync<UserSignalPreferenceDbDto>()).Select(usp => usp.ToDomainEntity()).ToList();
                user.Transactions = []; // Not fetched by default in this query

                return user;
            });
        }

        /// <inheritdoc />
        /// <summary>
        /// Fetches a user and their complete related entity graph by their Telegram ID in a single,
        /// highly optimized database round-trip.
        /// </summary>
        /// <param name="telegramId">The user's unique Telegram ID.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>The complete User entity, or null if not found.</returns>
        public async Task<User?> GetByTelegramIdAsync(string telegramId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(telegramId))
            {
                _logger.LogWarning("UserRepository: GetByTelegramIdAsync called with null or empty telegramId.");
                return null;
            }

            _logger.LogTrace("UserRepository: Fetching user and related entities by TelegramID: {TelegramId} in a single trip.", telegramId);

            // ✅✅ --- OPTIMIZATION 1: COMBINED SQL FOR A SINGLE ROUND-TRIP --- ✅✅
            // All queries are now in one command, executed by QueryMultiple.
            const string combinedSql = @"
        -- 1st Result: The main user
        SELECT Id, Username, TelegramId, Email, Level, CreatedAt, UpdatedAt, EnableGeneralNotifications, EnableVipSignalNotifications, EnableRssNewsNotifications, PreferredLanguage
        FROM Users 
        WHERE TelegramId = @TelegramId;

        -- 2nd Result: The user's wallet (if exists)
        SELECT w.Id, w.UserId, w.Balance, w.IsActive, w.CreatedAt, w.UpdatedAt
        FROM TokenWallets w
        INNER JOIN Users u ON w.UserId = u.Id
        WHERE u.TelegramId = @TelegramId;

        -- 3rd Result: The user's subscriptions
        SELECT s.Id, s.UserId, s.StartDate, s.EndDate, s.Status, s.ActivatingTransactionId, s.CreatedAt, s.UpdatedAt
        FROM Subscriptions s
        INNER JOIN Users u ON s.UserId = u.Id
        WHERE u.TelegramId = @TelegramId;

        -- 4th Result: The user's signal preferences
        SELECT p.Id, p.UserId, p.CategoryId, p.CreatedAt
        FROM UserSignalPreferences p
        INNER JOIN Users u ON p.UserId = u.Id
        WHERE u.TelegramId = @TelegramId;";

            try
            {
                // --- THIS IS THE CORE CHANGE ---
                // We create a combined policy on the fly. The timeout policy wraps the retry policy.
                // This means the 5-minute timer starts, and WITHIN that time, Polly can perform its retries.
                var combinedPolicy = _timeoutPolicy.WrapAsync(_retryPolicy);

                return await combinedPolicy.ExecuteAsync(async (ct) =>
                {
                    // The 'ct' CancellationToken passed here is now managed by the Pessimistic Timeout policy.
                    // If the timeout is reached, this token will be cancelled.

                    using var connection = CreateConnection();
                    // Pass the policy-managed token to OpenAsync
                    await connection.OpenAsync(ct);

                    // Dapper's CommandDefinition will respect the cancellation token.
                    using var multi = await connection.QueryMultipleAsync(
                        new CommandDefinition(combinedSql, new { TelegramId = telegramId }, cancellationToken: ct)
                    );

                    var userDto = await multi.ReadFirstOrDefaultAsync<UserDbDto>();
                    if (userDto == null)
                    {
                        _logger.LogTrace("User with TelegramID {TelegramId} not found.", telegramId);
                        return null;
                    }

                    var user = userDto.ToDomainEntity();

                    var walletDto = await multi.ReadFirstOrDefaultAsync<TokenWalletDbDto>();
                    user.TokenWallet = walletDto?.ToDomainEntity() ?? TokenWallet.Create(user.Id);

                    var subscriptionsDto = await multi.ReadAsync<SubscriptionDbDto>();
                    user.Subscriptions = subscriptionsDto.Select(s => s.ToDomainEntity()).ToList();

                    var preferencesDto = await multi.ReadAsync<UserSignalPreferenceDbDto>();
                    user.Preferences = preferencesDto.Select(usp => usp.ToDomainEntity()).ToList();

                    user.Transactions = [];

                    _logger.LogDebug("Successfully fetched user {UserId} from TelegramID {TelegramId}.", user.Id, telegramId);
                    return user;

                }, cancellationToken); // Pass the original cancellationToken here
            }
            // --- CATCHING THE TIMEOUT EXCEPTION ---
            catch (TimeoutRejectedException ex)
            {
                _logger.LogError(ex, "UserRepository: Operation timed out after 5 minutes while fetching user by TelegramID {TelegramId}.", telegramId);
                // Rethrow as a more specific domain exception
                throw new RepositoryException($"The operation to fetch user by TelegramID {telegramId} timed out.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get user by TelegramID {TelegramId} after retries and within timeout.", telegramId);
                throw new RepositoryException($"An error occurred while fetching user by TelegramID: {telegramId}", ex);
            }
        }

        /// <inheritdoc />
        public async Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                _logger.LogWarning("UserRepository: GetByEmailAsync called with null or empty email.");
                return null;
            }
            string lowerEmail = email.ToLowerInvariant();
            _logger.LogTrace("UserRepository: Fetching user by Email (case-insensitive): {Email}.", lowerEmail);
            return await _retryPolicy.ExecuteAsync(async () =>
            {
                using var connection = CreateConnection();
                await connection.OpenAsync(cancellationToken);

                // Fetch user ID first
                var userIdQuery = "SELECT Id FROM Users WHERE LOWER(Email) = LOWER(@Email);";
                var userId = await connection.ExecuteScalarAsync<Guid?>(userIdQuery, new { Email = lowerEmail });
                if (!userId.HasValue)
                {
                    return null;
                }

                var sql = @"
                    SELECT Id, Username, TelegramId, Email, Level, CreatedAt, UpdatedAt, EnableGeneralNotifications, EnableVipSignalNotifications, EnableRssNewsNotifications, PreferredLanguage
                    FROM Users WHERE Id = @UserId;

                    SELECT Id, UserId, Balance, IsActive, CreatedAt, UpdatedAt
                    FROM TokenWallets WHERE UserId = @UserId;

                    SELECT Id, UserId, StartDate, EndDate, Status, ActivatingTransactionId, CreatedAt, UpdatedAt
                    FROM Subscriptions WHERE UserId = @UserId;

                    SELECT Id, UserId, CategoryId, CreatedAt
                    FROM UserSignalPreferences WHERE UserId = @UserId;";

                using var multi = await connection.QueryMultipleAsync(sql, new { UserId = userId.Value, Email = lowerEmail }); // Pass email for the main user select, UserId for related

                var userDto = await multi.ReadFirstOrDefaultAsync<UserDbDto>();
                if (userDto == null)
                {
                    return null;
                }

                var user = userDto.ToDomainEntity();
                user.TokenWallet = (await multi.ReadFirstOrDefaultAsync<TokenWalletDbDto>())?.ToDomainEntity() ?? TokenWallet.Create(user.Id);
                user.Subscriptions = (await multi.ReadAsync<SubscriptionDbDto>()).Select(s => s.ToDomainEntity()).ToList();
                user.Preferences = (await multi.ReadAsync<UserSignalPreferenceDbDto>()).Select(usp => usp.ToDomainEntity()).ToList();
                user.Transactions = []; // Not fetched

                return user;
            });
        }

        /// <inheritdoc />
        public async Task<IEnumerable<User>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogTrace("UserRepository: Fetching all users.");
            return await _retryPolicy.ExecuteAsync(async () =>
            {
                using var connection = CreateConnection();
                await connection.OpenAsync(cancellationToken);

                // Fetch all users first
                var users = (await connection.QueryAsync<UserDbDto>("SELECT Id, Username, TelegramId, Email, Level, CreatedAt, UpdatedAt, EnableGeneralNotifications, EnableVipSignalNotifications, EnableRssNewsNotifications, PreferredLanguage FROM Users ORDER BY Username;")).Select(dto => dto.ToDomainEntity()).ToList();

                if (users.Any())
                {
                    var userIds = users.Select(u => u.Id).ToList();

                    // Batch fetch all related data for all users in one go
                    // This is more efficient for N-many users than individual QueryMultiple calls
                    var wallets = (await connection.QueryAsync<TokenWalletDbDto>("SELECT Id, UserId, Balance, IsActive, CreatedAt, UpdatedAt FROM TokenWallets WHERE UserId IN @UserIds;", new { UserIds = userIds })).ToList();
                    var subscriptions = (await connection.QueryAsync<SubscriptionDbDto>("SELECT Id, UserId, StartDate, EndDate, Status, ActivatingTransactionId, CreatedAt, UpdatedAt FROM Subscriptions WHERE UserId IN @UserIds;", new { UserIds = userIds })).ToList();
                    var preferences = (await connection.QueryAsync<UserSignalPreferenceDbDto>("SELECT Id, UserId, CategoryId, CreatedAt FROM UserSignalPreferences WHERE UserId IN @UserIds;", new { UserIds = userIds })).ToList();

                    // Manually map collections back to the parent users
                    foreach (var user in users)
                    {
                        user.TokenWallet = wallets.FirstOrDefault(tw => tw.UserId == user.Id)?.ToDomainEntity() ?? TokenWallet.Create(user.Id);
                        user.Subscriptions = subscriptions.Where(s => s.UserId == user.Id).Select(s => s.ToDomainEntity()).ToList();
                        user.Preferences = preferences.Where(usp => usp.UserId == user.Id).Select(usp => usp.ToDomainEntity()).ToList();
                        user.Transactions = []; // Not fetched in this method
                    }
                }

                return users;
            });
        }

        /// <summary>
        /// This method cannot directly translate an arbitrary LINQ Expression to SQL with Dapper.
        /// It's a fundamental difference between an ORM like EF Core and a micro-ORM like Dapper.
        /// You should refactor callers to provide SQL 'WHERE' clauses and parameters directly,
        /// or consider building a more sophisticated expression parser, which is non-trivial.
        /// </summary>
        /// <exception cref="NotSupportedException">Thrown to indicate that arbitrary LINQ Expression predicates are not supported by this Dapper repository.</exception>
        public Task<IEnumerable<User>> FindAsync(Expression<Func<User, bool>> predicate, CancellationToken cancellationToken = default)
        {
            _logger.LogError("UserRepository: FindAsync with Expression<Func<User, bool>> is not directly supported by Dapper. " +
                             "Please refactor to use specific Get methods or pass raw SQL conditions.");
            throw new NotSupportedException("Arbitrary LINQ Expression predicates are not supported by this Dapper repository. " +
                                            "Please use specific query methods (e.g., GetByTelegramIdAsync, GetByEmailAsync) " +
                                            "or extend the repository with methods that accept SQL query parts and parameters.");
        }

        /// <inheritdoc />
        public async Task AddAsync(User user, CancellationToken cancellationToken = default)
        {
            if (user == null)
            {
                _logger.LogError("UserRepository: Attempted to add a null User object.");
                throw new ArgumentNullException(nameof(user));
            }
            _logger.LogInformation("UserRepository: Adding new user. Username: {Username}, Email: {Email}.", user.Username, user.Email);
            try
            {
                await _retryPolicy.ExecuteAsync(async () =>
                {
                    using var connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    using var transaction = connection.BeginTransaction();
                    try
                    {
                        // Insert User
                        var userParams = new
                        {
                            user.Id,
                            user.Username,
                            user.TelegramId,
                            user.Email,
                            Level = user.Level.ToString(), // Convert enum to string
                            user.CreatedAt,
                            UpdatedAt = user.UpdatedAt ?? user.CreatedAt, // Ensure UpdatedAt is set
                            user.EnableGeneralNotifications,
                            user.EnableVipSignalNotifications,
                            user.EnableRssNewsNotifications,
                            user.PreferredLanguage
                        };
                        _ = await connection.ExecuteAsync(@"
                            INSERT INTO Users (Id, Username, TelegramId, Email, Level, CreatedAt, UpdatedAt, EnableGeneralNotifications, EnableVipSignalNotifications, EnableRssNewsNotifications, PreferredLanguage)
                            VALUES (@Id, @Username, @TelegramId, @Email, @Level, @CreatedAt, @UpdatedAt, @EnableGeneralNotifications, @EnableVipSignalNotifications, @EnableRssNewsNotifications, @PreferredLanguage);",
                            userParams, transaction: transaction);

                        // Insert TokenWallet (assuming it's always created with the user)
                        // Note: If TokenWallet is optional or created separately, adjust this logic.
                        if (user.TokenWallet != null)
                        {
                            var walletParams = new
                            {
                                user.TokenWallet.Id,
                                user.TokenWallet.UserId,
                                user.TokenWallet.Balance,
                                user.TokenWallet.IsActive,
                                user.TokenWallet.CreatedAt,
                                user.TokenWallet.UpdatedAt
                            };
                            _ = await connection.ExecuteAsync(@"
                                INSERT INTO TokenWallets (Id, UserId, Balance, IsActive, CreatedAt, UpdatedAt)
                                VALUES (@Id, @UserId, @Balance, @IsActive, @CreatedAt, @UpdatedAt);",
                                walletParams, transaction: transaction);
                        }
                        else
                        {
                            _logger.LogWarning("UserRepository: User {UserId} is being added without an associated TokenWallet. A default wallet will be created by the domain service if missing.", user.Id);
                        }

                        // Subscriptions and Preferences are typically managed by their own repositories or dedicated services.
                        // If they are part of the initial user creation flow, add them here.

                        transaction.Commit();
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        // Wrap the exception in a RepositoryException for consistent error handling patterns
                        throw new RepositoryException($"Failed to add user '{user.Username}' and associated entities with Dapper transaction.", ex);
                    }
                });
                _logger.LogInformation("UserRepository: Successfully added user: {Username}", user.Username);
            }
            catch (RepositoryException dbEx) // Catch the wrapped DB errors
            {
                _logger.LogError(dbEx, "UserRepository: Error adding user {Username} to the database after retries.", user.Username);
                throw;
            }
            catch (Exception ex) // Catch any other unexpected errors
            {
                _logger.LogError(ex, "UserRepository: An unexpected error occurred while adding user {Username}.", user.Username);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task UpdateAsync(User user, CancellationToken cancellationToken = default)
        {
            if (user == null)
            {
                _logger.LogError("UserRepository: Attempted to update with a null User object.");
                throw new ArgumentNullException(nameof(user));
            }
            _logger.LogInformation("UserRepository: Updating user. UserID: {UserId}, Username: {Username}.", user.Id, user.Username);
            try
            {
                await _retryPolicy.ExecuteAsync(async () =>
                {
                    using var connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    using var transaction = connection.BeginTransaction();
                    try
                    {
                        // Update User
                        var userParams = new
                        {
                            user.Id,
                            user.Username,
                            user.TelegramId,
                            user.Email,
                            Level = user.Level.ToString(),
                            UpdatedAt = DateTime.UtcNow, // Always update UpdatedAt on modification
                            user.EnableGeneralNotifications,
                            user.EnableVipSignalNotifications,
                            user.EnableRssNewsNotifications,
                            user.PreferredLanguage
                        };
                        var rowsAffected = await connection.ExecuteAsync(@"
                            UPDATE Users SET
                                Username = @Username,
                                TelegramId = @TelegramId,
                                Email = @Email,
                                Level = @Level,
                                UpdatedAt = @UpdatedAt,
                                EnableGeneralNotifications = @EnableGeneralNotifications,
                                EnableVipSignalNotifications = @EnableVipSignalNotifications,
                                EnableRssNewsNotifications = @EnableRssNewsNotifications,
                                PreferredLanguage = @PreferredLanguage
                            WHERE Id = @Id;",
                            userParams, transaction: transaction);

                        if (rowsAffected == 0)
                        {
                            // If no rows affected, it means user wasn't found or was concurrently deleted/modified.
                            throw new InvalidOperationException($"User with ID '{user.Id}' not found or modified by another process. Concurrency conflict suspected.");
                        }

                        // Update or Insert TokenWallet (UPSERT logic for SQL Server)
                        if (user.TokenWallet != null)
                        {
                            var walletParams = new
                            {
                                user.TokenWallet.Id, // Needed for INSERT part of UPSERT
                                user.TokenWallet.UserId,
                                user.TokenWallet.Balance,
                                user.TokenWallet.IsActive,
                                user.TokenWallet.CreatedAt,
                                UpdatedAt = DateTime.UtcNow // Set UpdatedAt here for wallet
                            };

                            // SQL Server UPSERT (MERGE is an option, but IF EXISTS / INSERT-UPDATE is simpler for this case)
                            _ = await connection.ExecuteAsync(@"
                                IF EXISTS (SELECT 1 FROM TokenWallets WHERE UserId = @UserId)
                                    UPDATE TokenWallets SET Balance = @Balance, IsActive = @IsActive, UpdatedAt = @UpdatedAt WHERE UserId = @UserId;
                                ELSE
                                    INSERT INTO TokenWallets (Id, UserId, Balance, IsActive, CreatedAt, UpdatedAt)
                                    VALUES (@Id, @UserId, @Balance, @IsActive, @CreatedAt, @UpdatedAt);",
                                walletParams, transaction: transaction);
                        }
                        else
                        {
                            _logger.LogWarning("UserRepository: User {UserId} has no TokenWallet object for update. Ensure wallet is initialized and passed.", user.Id);
                        }

                        // Subscriptions and Preferences should generally be updated by their own dedicated repository methods
                        // if their changes are independent of the User's core properties. If changes to User entity
                        // automatically imply changes to these collections, you would need explicit DELETE/INSERT or MERGE statements here.

                        transaction.Commit();
                    }
                    catch (InvalidOperationException) // Re-throw the concurrency specific error
                    {
                        transaction.Rollback();
                        throw;
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        throw new RepositoryException($"Failed to update user '{user.Username}' with Dapper transaction.", ex); // Wrap in custom exception
                    }
                });
                _logger.LogInformation("UserRepository: Successfully updated user: {Username}", user.Username);
            }
            catch (InvalidOperationException concEx) // Catch the custom concurrency exception
            {
                _logger.LogError(concEx, "UserRepository: Concurrency conflict or user not found while updating user {Username}.", user.Username);
                throw;
            }
            catch (RepositoryException dbEx) // Catch the wrapped DB errors
            {
                _logger.LogError(dbEx, "UserRepository: Error updating user {Username} in the database after retries.", user.Username);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserRepository: An unexpected error occurred while updating user {Username}.", user.Username);
                throw;
            }
        }
        public async Task<User?> GetByTelegramIdWithNotificationsAsync(string telegramId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(telegramId))
            {
                _logger.LogWarning("UserRepository: GetByTelegramIdWithNotificationsAsync called with null or empty telegramId.");
                return null;
            }

            _logger.LogTrace("UserRepository: Fetching user with notifications and related entities by TelegramID: {TelegramId}", telegramId);

            // This single query fetches the user, their notification settings, and all related entities in one go.
            const string combinedSql = @"
                -- 1. Main user with all notification flags
                SELECT Id, Username, TelegramId, Email, Level, CreatedAt, UpdatedAt, 
                       EnableGeneralNotifications, EnableVipSignalNotifications, EnableRssNewsNotifications, PreferredLanguage
                FROM Users 
                WHERE TelegramId = @TelegramId;

                -- 2. User's wallet
                SELECT w.Id, w.UserId, w.Balance, w.IsActive, w.CreatedAt, w.UpdatedAt
                FROM TokenWallets w
                INNER JOIN Users u ON w.UserId = u.Id
                WHERE u.TelegramId = @TelegramId;

                -- 3. User's subscriptions
                SELECT s.Id, s.UserId, s.StartDate, s.EndDate, s.Status, s.ActivatingTransactionId, s.CreatedAt, s.UpdatedAt
                FROM Subscriptions s
                INNER JOIN Users u ON s.UserId = u.Id
                WHERE u.TelegramId = @TelegramId;

                -- 4. User's signal preferences
                SELECT p.Id, p.UserId, p.CategoryId, p.CreatedAt
                FROM UserSignalPreferences p
                INNER JOIN Users u ON p.UserId = u.Id
                WHERE u.TelegramId = @TelegramId;";

            try
            {
                var combinedPolicy = _timeoutPolicy.WrapAsync(_retryPolicy);

                return await combinedPolicy.ExecuteAsync(async (ct) =>
                {
                    using var connection = CreateConnection();
                    await connection.OpenAsync(ct);

                    using var multi = await connection.QueryMultipleAsync(new CommandDefinition(combinedSql, new { TelegramId = telegramId }, cancellationToken: ct));

                    var userDto = await multi.ReadFirstOrDefaultAsync<UserDbDto>();
                    if (userDto == null)
                    {
                        _logger.LogTrace("User with TelegramID {TelegramId} not found.", telegramId);
                        return null;
                    }

                    var user = userDto.ToDomainEntity();
                    var walletDto = await multi.ReadFirstOrDefaultAsync<TokenWalletDbDto>();
                    user.TokenWallet = walletDto?.ToDomainEntity() ?? TokenWallet.Create(user.Id);

                    user.Subscriptions = (await multi.ReadAsync<SubscriptionDbDto>()).Select(s => s.ToDomainEntity()).ToList();
                    user.Preferences = (await multi.ReadAsync<UserSignalPreferenceDbDto>()).Select(usp => usp.ToDomainEntity()).ToList();
                    user.Transactions = []; // Not fetched in this query

                    _logger.LogDebug("Successfully fetched user {UserId} (TelegramID: {TelegramId}) with all related entities.", user.Id, telegramId);
                    return user;

                }, cancellationToken);
            }
            catch (TimeoutRejectedException ex)
            {
                _logger.LogError(ex, "UserRepository: Operation timed out while fetching user by TelegramID {TelegramId}.", telegramId);
                throw new RepositoryException($"The operation to fetch user by TelegramID {telegramId} timed out.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get user by TelegramID {TelegramId} after retries.", telegramId);
                throw new RepositoryException($"An error occurred while fetching user by TelegramID: {telegramId}", ex);
            }
        }

        /// <inheritdoc />
        public async Task DeleteAsync(User user, CancellationToken cancellationToken = default)
        {
            if (user == null)
            {
                _logger.LogError("UserRepository: Attempted to delete a null User object.");
                throw new ArgumentNullException(nameof(user));
            }
            _logger.LogInformation("UserRepository: Deleting user by object. UserID: {UserId}, Username: {Username}.", user.Id, user.Username);
            await DeleteUserAndRelatedData(user.Id, cancellationToken);
        }

        /// <inheritdoc />
        public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("UserRepository: Attempting to delete user by ID: {UserId}.", id);
            await DeleteUserAndRelatedData(id, cancellationToken);
        }

        /// <inheritdoc />
        public async Task<bool> ExistsByEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return false;
            }

            string lowerEmail = email.ToLowerInvariant();
            _logger.LogTrace("UserRepository: Checking existence by Email (case-insensitive): {Email}.", lowerEmail);
            try
            {
                return await _retryPolicy.ExecuteAsync(async () =>
                {
                    using var connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);
                    var count = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE LOWER(Email) = LOWER(@Email);", new { Email = lowerEmail });
                    return count > 0;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserRepository: Error checking existence by email {Email}.", lowerEmail);
                throw new RepositoryException($"Failed to check existence by email '{lowerEmail}'.", ex);
            }
        }

        /// <inheritdoc />
        public async Task<bool> ExistsByTelegramIdAsync(string telegramId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(telegramId))
            {
                return false;
            }

            _logger.LogTrace("UserRepository: Checking existence by TelegramID: {TelegramId}.", telegramId);
            try
            {
                return await _retryPolicy.ExecuteAsync(async () =>
                {
                    using var connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);
                    var count = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE TelegramId = @TelegramId;", new { TelegramId = telegramId });
                    return count > 0;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserRepository: Error checking existence by TelegramID {TelegramId}.", telegramId);
                throw new RepositoryException($"Failed to check existence by TelegramID '{telegramId}'.", ex);
            }
        }

        /// <inheritdoc />
        public async Task DeleteAndSaveAsync(User user, CancellationToken cancellationToken)
        {
            if (user == null)
            {
                _logger.LogError("UserRepository: Attempted to delete and save a null User object.");
                throw new ArgumentNullException(nameof(user));
            }
            _logger.LogInformation("UserRepository: Marking user for deletion and immediate save. UserID: {UserId}, Username: {Username}.", user.Id, user.Username);
            // This method effectively just calls the same underlying delete logic.
            await DeleteUserAndRelatedData(user.Id, cancellationToken);
            _logger.LogInformation("UserRepository: Successfully deleted user (ID: {UserId}) and saved changes.", user.Id);
        }

        /// <inheritdoc />
        public async Task<IEnumerable<User>> GetUsersWithNotificationSettingAsync(
    Expression<Func<User, bool>> notificationPredicate,
    CancellationToken cancellationToken = default)
        {
            _logger.LogError("UserRepository: GetUsersWithNotificationSettingAsync with Expression<Func<User, bool>> is NOT SUPPORTED by Dapper directly. " +
                             "This method will throw a NotSupportedException.");
            return await Task.FromException<IEnumerable<User>>(
                new NotSupportedException("Arbitrary LINQ Expression predicates are not supported by this Dapper repository for notification settings. " +
                                           "Please replace calls to this method with specific SQL queries or dedicated methods like GetUsersForNewsNotificationAsync, " +
                                           "or pass raw SQL conditions from the calling layer.")
            );
        }

        // --- Private Helper Method for Deletion (to encapsulate transaction and retry logic) ---
        private async Task DeleteUserAndRelatedData(Guid userId, CancellationToken cancellationToken)
        {
            try
            {
                await _retryPolicy.ExecuteAsync(async () =>
                {
                    using var connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);

                    using var transaction = connection.BeginTransaction();
                    try
                    {
                        // Check if the user exists before attempting to delete
                        var userExists = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE Id = @Id;", new { Id = userId }, transaction: transaction);
                        if (userExists == 0)
                        {
                            _logger.LogWarning("UserRepository: User with ID {UserId} not found for deletion in DeleteUserAndRelatedData.", userId);
                            transaction.Rollback(); // Rollback empty transaction
                            return; // Return silently if not found
                        }

                        // Due to ON DELETE CASCADE foreign key constraints configured in your EF Core model:
                        // Deleting the parent 'Users' record will automatically delete related records
                        // in 'TokenWallets', 'Subscriptions', 'Transactions', and 'UserSignalPreferences'.
                        // If you did NOT have ON DELETE CASCADE, you would need explicit DELETE statements
                        // for child tables BEFORE deleting from the Users table, respecting foreign key order.
                        var rowsAffected = await connection.ExecuteAsync("DELETE FROM Users WHERE Id = @Id;", new { Id = userId }, transaction: transaction);

                        if (rowsAffected == 0)
                        {
                            // This might indicate a concurrency issue where the user was deleted by another process
                            // between the initial existence check and this actual delete.
                            throw new InvalidOperationException($"User with ID '{userId}' was not found for deletion or was deleted by another process. Concurrency conflict suspected.");
                        }

                        transaction.Commit();
                    }
                    catch (InvalidOperationException) // Catch the custom concurrency exception
                    {
                        transaction.Rollback();
                        throw;
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        // Wrap the exception in a RepositoryException for consistency
                        throw new RepositoryException($"Failed to delete user with ID '{userId}' with Dapper transaction.", ex);
                    }
                });
                _logger.LogInformation("UserRepository: Successfully deleted user with ID: {UserId}", userId);
            }
            catch (InvalidOperationException concEx)
            {
                _logger.LogError(concEx, "UserRepository: Concurrency conflict or user not found while deleting user with ID {UserId}.", userId);
                throw;
            }
            catch (RepositoryException dbEx)
            {
                _logger.LogError(dbEx, "UserRepository: Error deleting user with ID {UserId} from the database after retries.", userId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserRepository: An unexpected error occurred while deleting user with ID {UserId}.", ex);
                throw;
            }
        }
    }
}