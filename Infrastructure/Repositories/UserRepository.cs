// File: Infrastructure/Persistence/Repositories/UserRepository.cs

#region Usings
// Standard .NET & NuGet
// Project specific
using Application.Common.Interfaces; // For IUserRepository
using Dapper; // Dapper for micro-ORM operations
using Domain.Entities;             // For User, Subscription, TokenWallet, UserSignalPreference entities
using Domain.Enums; // For UserLevel enum (stored as string in DB)
using Infrastructure.Data;
using Infrastructure.Persistence.Configurations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration; // To access connection strings
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging; // For logging
using Polly; // For resilience policies
using Polly.Retry; // For retry policies
using Polly.Timeout; // For custom RepositoryException
using System.Data; // Common Ado.Net interfaces like IDbConnection, IDbTransaction
using System.Data.Common; // For DbException (base class for database exceptions)
using System.Linq.Expressions;
using System.Text;
using System.Text.Json; // Still included, but will throw NotSupportedException
using System.Text.Json.Serialization; // Added for IntToBoolJsonConverter
#endregion

// --- Add IntToBoolJsonConverter for local use ---
public class IntToBoolJsonConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType == JsonTokenType.Number
            ? reader.GetInt32() != 0
            : reader.TokenType == JsonTokenType.True || (reader.TokenType == JsonTokenType.False ? false : throw new JsonException());
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value ? 1 : 0);
    }
}

// --- Add FlexibleDateTimeJsonConverter for local use ---
public class FlexibleDateTimeJsonConverter : System.Text.Json.Serialization.JsonConverter<DateTime>
{
    private static readonly string[] Formats = new[]
    {
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ssZ",
        "yyyy-MM-ddTHH:mm:ss.fffZ"
    };

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? str = reader.GetString();
            return DateTime.TryParseExact(str, Formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime dt)
                ? dt
                : DateTime.TryParse(str, out dt) ? dt : throw new JsonException($"Could not parse DateTime: {str}");
        }
        return reader.GetDateTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString("O")); // ISO 8601
    }
}

namespace Infrastructure.Repositories
{
    /// <summary>
    /// Implements the INewsItemRepository for data operations related to NewsItem entities
    /// using Dapper.
    /// </summary>
    public class UserRepository : IUserRepository
    {
        private readonly ILogger<UserRepository> _logger;
        private readonly AsyncRetryPolicy _retryPolicy; // Polly policy for DB operations
        private readonly UserSqlProvider _sql;
        private readonly DbProviderService _dbProvider;
        private readonly IDbConnectionFactory _dbConnectionFactory;
        private readonly IServiceProvider _serviceProvider;
        private class UserWithRelatedDataDto
        {
            // --- User Properties (from Users table) ---
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

            // --- JSON Properties for Related Data ---
            // These properties will hold the JSON strings returned by PostgreSQL's json_agg.
            // We expect them to be in the format '[{"Id": "...", ...}]' or '[]' if no records exist.
            public string TokenWalletJson { get; set; } = default!;
            public string SubscriptionsJson { get; set; } = default!;
            public string PreferencesJson { get; set; } = default!;

            /// <summary>
            /// Converts this DTO into the User domain entity, deserializing JSON data
            /// and utilizing the existing internal DTOs' ToDomainEntity methods.
            /// </summary>
            /// <param name="logger">The logger to use for reporting deserialization errors.</param>
            /// <returns>The constructed User domain entity.</returns>
            public User ToDomainEntity(ILogger logger)
            {
                // Create the main user entity using the properties from this DTO.
                // This is similar to what UserDbDto.ToDomainEntity() does, but here we map directly.
                User user = new()
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

                // --- Deserialize Token Wallet ---
                TokenWalletDbDto? singleWalletDto = null;
                // Check for null, empty string, or the specific "[]" string that json_agg can return for zero rows.
                if (!string.IsNullOrWhiteSpace(TokenWalletJson) && TokenWalletJson != "[]")
                {
                    try
                    {
                        // Deserialize the JSON array string into a list of TokenWalletDbDto.
                        List<TokenWalletDbDto>? walletDtos = System.Text.Json.JsonSerializer.Deserialize<List<TokenWalletDbDto>>(TokenWalletJson);
                        if (walletDtos != null && walletDtos.Any())
                        {
                            singleWalletDto = walletDtos.First(); // Get the first (and likely only) wallet
                        }
                    }
                    catch (JsonException jsonEx)
                    {
                        // Log any errors during JSON deserialization. For read operations, it's often best to continue with default values.
                        logger.LogError(jsonEx, "UserRepository: Failed to deserialize TokenWalletJson for User {UserId}. JSON: {TokenWalletJson}", user.Id, TokenWalletJson);
                    }
                }
                // Assign the domain entity, providing a default if not found or if deserialization failed.
                user.TokenWallet = singleWalletDto?.ToDomainEntity() ?? TokenWallet.Create(user.Id); // Assuming TokenWallet.Create() provides a default instance.

                // --- Deserialize Subscriptions ---
                List<SubscriptionDbDto>? subscriptionDtos = null;
                if (!string.IsNullOrWhiteSpace(SubscriptionsJson) && SubscriptionsJson != "[]")
                {
                    try
                    {
                        subscriptionDtos = System.Text.Json.JsonSerializer.Deserialize<List<SubscriptionDbDto>>(SubscriptionsJson);
                    }
                    catch (JsonException jsonEx)
                    {
                        logger.LogError(jsonEx, "UserRepository: Failed to deserialize SubscriptionsJson for User {UserId}. JSON: {SubscriptionsJson}", user.Id, SubscriptionsJson);
                    }
                }
                // Assign the domain entity list, providing an empty list if not found or if deserialization failed.
                user.Subscriptions = subscriptionDtos?.Select(dto => dto.ToDomainEntity()).ToList() ?? [];

                // --- Deserialize User Signal Preferences ---
                List<UserSignalPreferenceDbDto>? preferenceDtos = null;
                if (!string.IsNullOrWhiteSpace(PreferencesJson) && PreferencesJson != "[]")
                {
                    try
                    {
                        preferenceDtos = System.Text.Json.JsonSerializer.Deserialize<List<UserSignalPreferenceDbDto>>(PreferencesJson);
                    }
                    catch (JsonException jsonEx)
                    {
                        logger.LogError(jsonEx, "UserRepository: Failed to deserialize PreferencesJson for User {UserId}. JSON: {PreferencesJson}", user.Id, PreferencesJson);
                    }
                }
                // Assign the domain entity list, providing an empty list if not found or if deserialization failed.
                user.Preferences = preferenceDtos?.Select(dto => dto.ToDomainEntity()).ToList() ?? [];

                // Transactions are not fetched in this query. Ensure the list is initialized.
                user.Transactions = [];

                return user;
            }
        }
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
            [System.Text.Json.Serialization.JsonConverter(typeof(IntToBoolJsonConverter))]
            public bool IsActive { get; set; }
            [System.Text.Json.Serialization.JsonConverter(typeof(FlexibleDateTimeJsonConverter))]
            public DateTime CreatedAt { get; set; }
            [System.Text.Json.Serialization.JsonConverter(typeof(FlexibleDateTimeJsonConverter))]
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
        private readonly ILoggingSanitizer _logSanitizer;
        // --- Constructor ---
        public UserRepository(IDbConnectionFactory dbConnectionFactory,
        IConfiguration configuration,
        ILogger<UserRepository> logger,
        ILoggingSanitizer logSanitizer,
        DbProviderService dbProviderService, // <<< INJECTED: Provides the database provider context.
        UserSqlProvider userSqlProvider,     // <<< INJECTED: Provides SQL statements tailored to the database.
        IServiceProvider serviceProvider) // Inject IServiceProvider
        {
            #region Constructor Initialization
            _dbConnectionFactory = dbConnectionFactory;
            // --- Assign Injected Dependencies ---
            _logger = logger;
            _logSanitizer = logSanitizer;
            _serviceProvider = serviceProvider;

            // Assign the injected database provider service and SQL provider to internal fields.
            _dbProvider = dbProviderService;
            _sql = userSqlProvider;

            #endregion

            #region Resilience Policies Configuration

            // --- Timeout Policy ---
            // Configures a pessimistic timeout for asynchronous operations.
            // Operations exceeding 5 minutes will be cancelled.
            _timeoutPolicy = Policy.TimeoutAsync(TimeSpan.FromMinutes(5), TimeoutStrategy.Pessimistic);

            // --- Dynamic Retry Policy ---
            // Configures a retry policy that adapts based on the database provider and exception type.
            // This policy defines specific retry conditions and wait strategies for different database exceptions.
            _retryPolicy = Policy
                .Handle<DbException>(ex => // Target generic database exceptions.
                {
                    // --- Provider-Specific Retry Logic ---
                    // Customize retry behavior based on the database provider to avoid retrying
                    // non-transient errors like constraint violations that are unlikely to succeed on retry.

                    // SQLite specific handling:
                    if (_dbProvider.Provider == DatabaseProvider.SQLite && ex is SqliteException sqliteEx)
                    {
                        // SQLITE_CONSTRAINT error code (19) typically indicates a constraint violation (e.g., unique key, foreign key).
                        // We choose NOT to retry on these specific constraint violations as they are usually data-related and won't resolve on retry.
                        // All other SqliteExceptions will be retried.
                        return sqliteEx.SqliteErrorCode != 19;
                    }

                    // PostgreSQL specific handling:
                    if (_dbProvider.Provider == DatabaseProvider.Postgres && ex is Npgsql.NpgsqlException pgEx)
                    {
                        // PostgreSQL error state '23505' indicates a unique_violation.
                        // Similar to SQLite, we choose NOT to retry on unique constraint violations.
                        // All other NpgsqlExceptions will be retried.
                        return pgEx.SqlState != "23505";
                    }

                    // For any other database provider or DbException subclass not specifically handled above,
                    // we default to retrying the operation, assuming these might be transient network issues or similar.
                    return true;
                })
             // --- Wait and Retry Strategy ---
             .WaitAndRetryAsync(
                retryCount: 3, // Max 3 retries
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // Exponential backoff: 2s, 4s, 8s
                onRetry: (exception, timeSpan, retryAttempt, context) =>
                {
                    _logger.LogWarning(exception,
                        "UserRepository: Transient database error encountered. Retrying in {TimeSpan} for attempt {RetryAttempt}. Error: {Message}",
                        timeSpan, retryAttempt, exception.Message);
                });

            #endregion
        }

        // --- Helper to create a new SqlConnection ---
        private IDbConnection CreateConnection()
        {
            // The repository now asks the factory for a connection, it doesn't build it itself.
            return _dbConnectionFactory.CreateConnection();
        }

        private const string UserWithRelatedDataSqlFragment = @"
            SELECT
                u.""Id"", u.""Username"", u.""TelegramId"", u.""Email"", u.""Level"", u.""CreatedAt"", u.""UpdatedAt"",
                u.""EnableGeneralNotifications"", u.""EnableVipSignalNotifications"", u.""EnableRssNewsNotifications"", u.""PreferredLanguage"",
                (
                    SELECT COALESCE(json_agg(json_build_object(
                        'Id', tw.""Id"", 'UserId', tw.""UserId"", 'Balance', tw.""Balance"", 'IsActive', tw.""IsActive"", 'CreatedAt', tw.""CreatedAt"", 'UpdatedAt', tw.""UpdatedAt""
                    )), '[]'::json)
                    FROM public.""TokenWallets"" tw WHERE tw.""UserId"" = u.""Id""
                ) AS TokenWalletJson,
                (
                    SELECT COALESCE(json_agg(json_build_object(
                        'Id', s.""Id"", 'UserId', s.""UserId"", 'StartDate', s.""StartDate"", 'EndDate', s.""EndDate"", 'Status', s.""Status"",
                        'ActivatingTransactionId', s.""ActivatingTransactionId"", 'CreatedAt', s.""CreatedAt"", 'UpdatedAt', s.""UpdatedAt""
                    )), '[]'::json)
                    FROM public.""Subscriptions"" s WHERE s.""UserId"" = u.""Id""
                ) AS SubscriptionsJson,
                (
                    SELECT COALESCE(json_agg(json_build_object(
                        'Id', usp.""Id"", 'UserId', usp.""UserId"", 'CategoryId', usp.""CategoryId"", 'CreatedAt', usp.""CreatedAt""
                    )), '[]'::json)
                    FROM public.""UserSignalPreferences"" usp WHERE usp.""UserId"" = u.""Id""
                ) AS PreferencesJson
            FROM public.""Users"" u";



        // In Infrastructure/Persistence/Repositories/UserRepository.cs

        /// <summary>
        /// Asynchronously retrieves users who are eligible to receive news notifications.
        /// The eligibility is determined by their notification preferences and subscription status.
        /// </summary>
        /// <param name="newsItemSignalCategoryId">Optional. The ID of the signal category for the news item. If provided, only users interested in this category (or no specific category) will be included.</param>
        /// <param name="isNewsItemVipOnly">A flag indicating whether the news item is restricted to VIP users.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>An enumerable collection of eligible <see cref="User"/> domain entities.</returns>
        /// <exception cref="RepositoryException">Thrown if any error occurs during the data retrieval process.</exception>
        public async Task<IEnumerable<User>> GetUsersForNewsNotificationAsync(
            Guid? newsItemSignalCategoryId,
            bool isNewsItemVipOnly,
            CancellationToken cancellationToken = default)
        {
            // --- 1. Log the request details for diagnostic purposes ---
            _logger.LogInformation(
                "UserRepository: Fetching users for news notification. CategoryId: {CategoryId}, IsVipOnly: {IsVip}",
                newsItemSignalCategoryId, isNewsItemVipOnly);

            #region SQL Query Construction

            // --- Build the SQL Query Dynamically ---
            // CORRECTED: Use the new 'GetUsersForNewsNotificationBaseSql' property from the upgraded UserSqlProvider.
            // This property provides a complete and syntactically correct base query, including the initial WHERE clause.
            StringBuilder sqlBuilder = new(_sql.GetUsersForNewsNotificationBaseSql);

            // Initialize Dapper parameters. This object will hold any dynamic values needed for the query.
            DynamicParameters parameters = new();

            // Conditionally append the clause for VIP-only news notifications.
            // This clause safely adds an 'AND' condition to the existing WHERE clause.
            if (isNewsItemVipOnly)
            {
                _ = sqlBuilder.Append(_sql.VipSubscriptionCheckClause);
            }

            // Conditionally append the clause for filtering by specific news category preferences.
            if (newsItemSignalCategoryId.HasValue)
            {
                _ = sqlBuilder.Append(_sql.CategoryPreferenceCheckClause);
                // Add the category ID to the parameters, ensuring it's correctly passed to the query.
                parameters.Add("NewsItemSignalCategoryId", newsItemSignalCategoryId.Value);
            }

            // Finalize the SQL query string.
            string finalSql = sqlBuilder.ToString();
            // Log the constructed SQL query at a trace level for debugging purposes.
            _logger.LogTrace("Executing SQL for GetUsersForNewsNotificationAsync: {Sql}", finalSql);

            #endregion

            try
            {
                // --- 2. Execute the Query with Resilience Policies ---
                Polly.Wrap.AsyncPolicyWrap combinedPolicy = _timeoutPolicy.WrapAsync(_retryPolicy);

                List<User> eligibleUsers = await combinedPolicy.ExecuteAsync(async (ct) =>
                {
                    using IDbConnection connection = CreateConnection();
                    await (connection as System.Data.Common.DbConnection)!.OpenAsync(ct);

                    // --- UPGRADE: Use a dictionary for robust multi-mapping ---
                    // This prevents issues if a user has multiple subscriptions or preferences, ensuring each user appears only once.
                    Dictionary<Guid, User> userMap = [];

                    // The DTOs are defined in the repository, this mapping assumes their existence.
                    _ = await connection.QueryAsync<UserDbDto, TokenWalletDbDto, SubscriptionDbDto, UserSignalPreferenceDbDto, User>(
                        new CommandDefinition(finalSql, parameters, cancellationToken: ct),
                        (userDto, walletDto, subscriptionDto, preferenceDto) =>
                        {
                            // Find or create the main User entity
                            if (!userMap.TryGetValue(userDto.Id, out User? user))
                            {
                                user = userDto.ToDomainEntity(); // This creates the User with empty collections
                                userMap.Add(user.Id, user);
                            }

                            // Hydrate the related entities.
                            // The ToDomainEntity() methods in your DTOs should handle the mapping.
                            if (walletDto != null && user.TokenWallet == null)
                            {
                                user.TokenWallet = walletDto.ToDomainEntity();
                            }

                            if (subscriptionDto != null && !user.Subscriptions.Any(s => s.Id == subscriptionDto.Id))
                            {
                                user.Subscriptions.Add(subscriptionDto.ToDomainEntity());
                            }

                            if (preferenceDto != null && !user.Preferences.Any(p => p.Id == preferenceDto.Id))

                            {
                                user.Preferences.Add(preferenceDto.ToDomainEntity());
                            }

                            return user; // Return the user for Dapper's internal processing
                        },
                        // The splitOn parameter tells Dapper where to split the data for each new object type.
                        splitOn: "TokenWallet_Id,Subscription_Id,Preference_Id"
                    );

                    // Ensure every user has a non-null wallet, even if it's a default/empty one.
                    foreach (User user in userMap.Values)
                    {
                        user.TokenWallet ??= TokenWallet.Create(user.Id);
                    }

                    List<User> eligibleUsersList = userMap.Values.ToList();

                    _logger.LogInformation("UserRepository: Found {UserCount} eligible users for news notification.", eligibleUsersList.Count);
                    return eligibleUsersList;

                }, cancellationToken);

                return eligibleUsers;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserRepository: Error fetching users for news notification.");
                // Background error log
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new Domain.Entities.ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "UserRepository",
                        EventType = "FetchUsersForNewsNotification.Error",
                        Message = ex.Message,
                        Details = ex.StackTrace,
                        Exception = ex.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                throw new RepositoryException("Failed to fetch users for news notification.", ex);
            }
        }
        /// <inheritdoc />
        // Make sure the UserWithRelatedDataDto class is defined within your UserRepository class
        // as shown in the previous example for GetByTelegramIdAsync.

        // If you haven't added it yet, here's the DTO again for context:
        /*
        private class UserWithRelatedDataDto
        {
            public Guid Id { get; set; }
            public string Username { get; set; } = default!;
            public string TelegramId { get; set; } = default!;
            public string Email { get; set; } = default!;
            public string Level { get; set; } = default!;
            public DateTime CreatedAt { get; set; }
            public DateTime? UpdatedAt { get; set; }
            public bool EnableGeneralNotifications { get; set; }
            public bool EnableVipSignalNotifications { get; set; }
            public bool EnableRssNewsNotifications { get; set; }
            public string PreferredLanguage { get; set; } = default!;

            public string TokenWalletJson { get; set; } = default!;
            public string SubscriptionsJson { get; set; } = default!;
            public string PreferencesJson { get; set; } = default!;

            public User ToDomainEntity(ILogger logger)
            {
                var user = new User
                {
                    Id = Id,
                    Username = Username,
                    TelegramId = TelegramId,
                    Email = Email,
                    Level = Enum.Parse<UserLevel>(Level),
                    CreatedAt = CreatedAt,
                    UpdatedAt = UpdatedAt,
                    EnableGeneralNotifications = EnableGeneralNotifications,
                    EnableVipSignalNotifications = EnableVipSignalNotifications,
                    EnableRssNewsNotifications = EnableRssNewsNotifications,
                    PreferredLanguage = PreferredLanguage
                };

                TokenWalletDbDto? singleWalletDto = null;
                if (!string.IsNullOrWhiteSpace(TokenWalletJson) && TokenWalletJson != "[]")
                {
                    try {
                        var walletDtos = System.Text.Json.JsonSerializer.Deserialize<List<TokenWalletDbDto>>(TokenWalletJson);
                        if (walletDtos != null && walletDtos.Any()) {
                            singleWalletDto = walletDtos.First();
                        }
                    } catch (JsonException jsonEx) {
                        logger.LogError(jsonEx, "UserRepository: Failed to deserialize TokenWalletJson for User {UserId}. JSON: {TokenWalletJson}", user.Id, TokenWalletJson);
                    }
                }
                user.TokenWallet = singleWalletDto?.ToDomainEntity() ?? TokenWallet.Create(user.Id);

                List<SubscriptionDbDto>? subscriptionDtos = null;
                if (!string.IsNullOrWhiteSpace(SubscriptionsJson) && SubscriptionsJson != "[]")
                {
                    try {
                        subscriptionDtos = System.Text.Json.JsonSerializer.Deserialize<List<SubscriptionDbDto>>(SubscriptionsJson);
                    } catch (JsonException jsonEx) {
                        logger.LogError(jsonEx, "UserRepository: Failed to deserialize SubscriptionsJson for User {UserId}. JSON: {SubscriptionsJson}", user.Id, SubscriptionsJson);
                    }
                }
                user.Subscriptions = subscriptionDtos?.Select(dto => dto.ToDomainEntity()).ToList() ?? new List<Subscription>();

                List<UserSignalPreferenceDbDto>? preferenceDtos = null;
                if (!string.IsNullOrWhiteSpace(PreferencesJson) && PreferencesJson != "[]")
                {
                    try {
                        preferenceDtos = System.Text.Json.JsonSerializer.Deserialize<List<UserSignalPreferenceDbDto>>(PreferencesJson);
                    } catch (JsonException jsonEx) {
                        logger.LogError(jsonEx, "UserRepository: Failed to deserialize PreferencesJson for User {UserId}. JSON: {PreferencesJson}", user.Id, PreferencesJson);
                    }
                }
                user.Preferences = preferenceDtos?.Select(dto => dto.ToDomainEntity()).ToList() ?? new List<UserSignalPreference>();

                user.Transactions = new List<Transaction>(); // Not fetched in this query

                return user;
            }
        }
        */


        // ... (Rest of your UserRepository class) ...

        /// <inheritdoc />
        public async Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            // The logging message is now more generic and accurate.
            _logger.LogTrace("UserRepository: Fetching user by ID: {UserId} using provider-specific JSON aggregation.", id);

            // --- REMOVED THE LARGE, HARDCODED 'const string sql' ---
            // Instead, we get the correct, provider-specific SQL from our SQL provider.
            string sql = _sql.GetByIdSql;

            try
            {
                // This entire block of C# logic remains UNCHANGED. It's already database-agnostic.
                Polly.Wrap.AsyncPolicyWrap combinedPolicy = _timeoutPolicy.WrapAsync(_retryPolicy);

                return await combinedPolicy.ExecuteAsync(async (ct) =>
                {
                    // CreateConnection() already handles switching between database connections.
                    using IDbConnection connection = CreateConnection();
                    await (connection as System.Data.Common.DbConnection)!.OpenAsync(ct);

                    UserWithRelatedDataDto? userWithRelatedDataDto = await connection.QueryFirstOrDefaultAsync<UserWithRelatedDataDto>(
                        new CommandDefinition(sql, new { Id = id }, cancellationToken: ct)
                    );

                    if (userWithRelatedDataDto == null)
                    {
                        _logger.LogTrace("User with ID {UserId} not found.", id);
                        return null;
                    }

                    User user = userWithRelatedDataDto.ToDomainEntity(_logger);

                    _logger.LogDebug("Successfully fetched user {UserId} with all related entities.", id);
                    return user;

                }, cancellationToken);
            }
            catch (TimeoutRejectedException ex)
            {
                _logger.LogError(ex, "UserRepository: Operation timed out while fetching user by ID {UserId}.", id);
                // Background error log
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new Domain.Entities.ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "UserRepository",
                        EventType = "GetUserById.Timeout",
                        Message = ex.Message,
                        Details = ex.StackTrace,
                        Exception = ex.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                throw new RepositoryException($"The operation to fetch user by ID {id} timed out.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get user by ID {UserId} after retries and within timeout.", id);
                // Background error log
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new Domain.Entities.ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "UserRepository",
                        EventType = "GetUserById.Error",
                        Message = ex.Message,
                        Details = ex.StackTrace,
                        Exception = ex.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                throw new RepositoryException($"An error occurred while fetching user by ID: {id}", ex);
            }
        }


        // File: Infrastructure/Persistence/Repositories/UserRepository.cs

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
                return null;
            }

            _logger.LogTrace("UserRepository: Fetching user by TelegramID: {TelegramId}", telegramId);

            // --- MODIFIED LINE ---
            // The SQL is now fetched from the provider, which returns the correct dialect.
            string sql = _sql.GetByTelegramIdSql;

            try
            {
                // The rest of this method's logic is already database-agnostic and requires no changes.
                Polly.Wrap.AsyncPolicyWrap combinedPolicy = _timeoutPolicy.WrapAsync(_retryPolicy);
                return await combinedPolicy.ExecuteAsync(async (ct) =>
                {
                    using IDbConnection connection = CreateConnection();
                    await (connection as System.Data.Common.DbConnection)!.OpenAsync(ct);
                    UserWithRelatedDataDto? userDto = await connection.QueryFirstOrDefaultAsync<UserWithRelatedDataDto>(
                        new CommandDefinition(sql, new { TelegramId = telegramId }, cancellationToken: ct));

                    return userDto?.ToDomainEntity(_logger);
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get user by TelegramID {TelegramId}", telegramId);
                // Background error log
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new Domain.Entities.ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "UserRepository",
                        EventType = "GetUserByTelegramId.Error",
                        Message = ex.Message,
                        Details = ex.StackTrace,
                        Exception = ex.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                throw new RepositoryException($"An error occurred while fetching user by TelegramID: {telegramId}", ex);
            }
        }


        /// <inheritdoc />
        public async Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            #region Input Validation and Preparation

            // --- Validate Email Input ---
            // Check if the provided email is null, empty, or consists only of whitespace characters.
            if (string.IsNullOrWhiteSpace(email))
            {
                // Log a warning for invalid input, as an empty/null email cannot match any user.
                _logger.LogWarning("UserRepository: GetByEmailAsync called with null or empty email.");
                // Return null immediately since no user can exist with invalid criteria.
                return null;
            }

            // --- Sanitize Email for Logging ---
            // SECURITY: Sanitize email before logging to prevent exposure of private information
            string sanitizedEmail = _logSanitizer.Sanitize(email);

            // --- Log Operation Details ---
            // Log the sanitized email being searched at a trace level for detailed operational diagnostics.
            _logger.LogTrace("UserRepository: Fetching user by email: [REDACTED_EMAIL].");

            #endregion

            try
            {
                // --- Apply Resilience Policy ---
                // Execute the core database operation within the retry policy.
                // This makes the operation resilient to transient issues like temporary network disruptions or database load.
                return await _retryPolicy.ExecuteAsync(async () =>
                {
                    // --- Database Connection and Query Execution ---
                    // Obtain a database connection instance using the helper method.
                    using IDbConnection connection = CreateConnection();
                    // Asynchronously open the database connection to the data source.
                    // Using System.Data.Common.DbConnection for generality.
                    await (connection as System.Data.Common.DbConnection)!.OpenAsync(cancellationToken);

                    // --- MODIFIED LINE ---
                    // The query is now fetched from the provider, ensuring correct syntax.
                    string userIdQuery = _sql.GetUserIdByEmailSql;
                    Guid? userId = await connection.ExecuteScalarAsync<Guid?>(userIdQuery, new { Email = email });

                    if (!userId.HasValue)
                    {
                        _logger.LogDebug("User not found.");
                        return null;
                    }

                    // This part is perfect and requires no changes as GetByIdAsync is already refactored.
                    return await GetByIdAsync(userId.Value, cancellationToken);
                });
            }
            catch (Exception ex)
            {
                // SECURITY: Log the error with sanitized email and exception message
                _logger.LogError(ex, "UserRepository: Error fetching user by email [REDACTED_EMAIL]. Exception (Sanitized): {SanitizedException}",
                    _logSanitizer.Sanitize(ex.Message));
                // Background error log
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new Domain.Entities.ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "UserRepository",
                        EventType = "GetByEmail.Error",
                        Message = ex.Message,
                        Details = ex.StackTrace,
                        Exception = ex.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                throw new RepositoryException($"Failed to fetch user by email '[REDACTED_EMAIL]'.", ex);
            }
        }

        /// <inheritdoc />
        public async Task<IEnumerable<User>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogTrace("UserRepository: Fetching all users.");
            return await _retryPolicy.ExecuteAsync(async () =>
            {
                using IDbConnection connection = CreateConnection();
                await (connection as System.Data.Common.DbConnection)!.OpenAsync(cancellationToken);

                // --- MODIFIED: Fetch all users first using the provider's SQL ---
                List<User> users = (await connection.QueryAsync<UserDbDto>(_sql.GetAllUsersSql))
                    .Select(dto => dto.ToDomainEntity()).ToList();

                if (users.Any())
                {
                    List<Guid> userIds = users.Select(u => u.Id).ToList();

                    // --- MODIFIED: Batch fetch all related data using the provider's SQL ---
                    List<TokenWalletDbDto> wallets = (await connection.QueryAsync<TokenWalletDbDto>(_sql.GetWalletsForUsersSql, new { UserIds = userIds })).ToList();
                    List<SubscriptionDbDto> subscriptions = (await connection.QueryAsync<SubscriptionDbDto>(_sql.GetSubscriptionsForUsersSql, new { UserIds = userIds })).ToList();
                    List<UserSignalPreferenceDbDto> preferences = (await connection.QueryAsync<UserSignalPreferenceDbDto>(_sql.GetPreferencesForUsersSql, new { UserIds = userIds })).ToList();

                    // This mapping logic is unchanged as it's pure C#
                    foreach (User? user in users)
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
            // Background error log
            _ = Task.Run(async () =>
            {
                using IServiceScope scope = _serviceProvider.CreateScope();
                IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                await repo.AddAsync(new Domain.Entities.ProMonitoringLog
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Source = "UserRepository",
                    EventType = "FindAsync.NotSupported",
                    Message = "FindAsync with Expression<Func<User, bool>> is not directly supported by Dapper.",
                    Details = null,
                    Exception = null,
                    Status = "Failed",
                    CreatedAt = DateTime.UtcNow
                });
            });
            throw new NotSupportedException("Arbitrary LINQ Expression predicates are not supported by this Dapper repository. " +
                                            "Please use specific query methods (e.g., GetByTelegramIdAsync, GetByEmailAsync) " +
                                            "or extend the repository with methods that accept SQL query parts and parameters.");
        }
        private string SanitizeSensitiveData(string? input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return string.Empty;
            }
            // Simple regex to find and redact email-like patterns.
            // This prevents leaking PII like emails into logs.
            return System.Text.RegularExpressions.Regex.Replace(input, @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", "[REDACTED_EMAIL]");
        }

        // In Infrastructure/Persistence/Repositories/UserRepository.cs

        public async Task AddAsync(User user, CancellationToken cancellationToken = default)
        {
            #region Pre-operation Validation and Logging

            // --- Input Validation ---
            // Ensure the user object provided is not null.
            if (user == null)
            {
                throw new ArgumentNullException(nameof(user));
            }
            // Further validation for user and tokenWallet properties could be added here.

            // --- Prepare for Logging ---
            // Sanitize sensitive information like usernames before logging.
            string sanitizedUsername = _logSanitizer.Sanitize(user.Username);
            // Log the intent to add a new user, including the sanitized username for traceability.
            _logger.LogInformation("UserRepository: Preparing to add new user '{SanitizedUsername}'.", sanitizedUsername);

            #endregion

            #region SQL Construction for Atomic Operation

            // --- Construct the Combined SQL Statement ---
            // Concatenate the SQL statements for adding a user and their wallet.
            // The _sql provider ensures that AddUserSql and AddWalletSql use the correct syntax for the target database.
            string insertSql = $"{_sql.AddUserSql} {_sql.AddWalletSql}";

            #endregion

            try
            {
                // --- Apply Resilience Policies ---
                // Combine the timeout and retry policies for execution.
                Polly.Wrap.AsyncPolicyWrap combinedPolicy = _timeoutPolicy.WrapAsync(_retryPolicy);

                // Execute the core logic within the resilience policies.
                await combinedPolicy.ExecuteAsync(async (ct) =>
                {
                    // --- Database Connection and Transaction Management ---
                    // Obtain a database connection using the factory method, which is provider-aware.
                    // Use synchronous 'using' for IDbConnection as it's only IDisposable, not IAsyncDisposable.
                    using IDbConnection connection = CreateConnection();
                    // Asynchronously open the connection. Cast is safe if CreateConnection returns DbConnection.
                    await (connection as System.Data.Common.DbConnection)!.OpenAsync(ct);

                    // Begin a database transaction.
                    // Use 'await using' for the transaction because DbTransaction implements IAsyncDisposable.
                    // This ensures the transaction is properly managed (committed or rolled back).
                    await using DbTransaction transaction = await (connection as System.Data.Common.DbConnection)!.BeginTransactionAsync(ct);

                    try
                    {
                        // --- Prepare Dapper Parameters ---
                        // Create an anonymous object to hold all necessary parameters for the SQL execution.
                        // This maps domain object properties to SQL parameter names, ensuring correct data types and values are passed.
                        var parameters = new
                        {
                            UserId = user.Id,
                            user.Username,
                            user.TelegramId,
                            user.Email,
                            Level = user.Level.ToString(), // Ensure Enum is converted to string if necessary for DB
                            user.CreatedAt,
                            user.UpdatedAt,
                            user.EnableGeneralNotifications,
                            user.EnableVipSignalNotifications,
                            user.EnableRssNewsNotifications,
                            user.PreferredLanguage,
                            // Parameters for the associated TokenWallet
                            TokenWalletId = user.TokenWallet.Id,
                            user.TokenWallet.Balance,
                            user.TokenWallet.IsActive,
                            TokenWalletCreatedAt = user.TokenWallet.CreatedAt,
                            TokenWalletUpdatedAt = user.TokenWallet.UpdatedAt
                        };

                        // --- Execute SQL within the Transaction ---
                        // Execute the combined SQL statement (AddUserSql + AddWalletSql) using Dapper.
                        // The parameters are passed, and the transaction is specified to ensure atomicity.
                        _ = await connection.ExecuteAsync(insertSql, parameters, transaction: transaction);

                        // --- Commit the Transaction ---
                        // If ExecuteAsync completes without exceptions, commit the transaction to make changes permanent.
                        await transaction.CommitAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        // --- Rollback on Failure ---
                        // If any part of the transaction fails (e.g., constraint violation, data error),
                        // roll back the entire transaction to maintain data integrity.
                        await transaction.RollbackAsync(ct);
                        // Log the failure and the rollback action, including the sanitized username.
                        _logger.LogError(ex, "Transaction failed for user '{SanitizedUsername}'. Rolling back.", sanitizedUsername);
                        // Re-throw a specific RepositoryException to indicate the transaction failure.
                        throw new RepositoryException($"Dapper transaction failed for user '{sanitizedUsername}'.", ex);
                    }
                }, cancellationToken); // Pass the cancellation token to the execution policy.

                #region Post-Operation Logging

                // --- Success Logging ---
                // Log a success message if the entire operation, including resilience policies, completes successfully.
                _logger.LogInformation("UserRepository: Successfully added user '{SanitizedUsername}' and their wallet.", sanitizedUsername);

                #endregion
            }
            catch (Exception ex)
            {
                // --- Global Exception Handling ---
                // Catch any exceptions that might have propagated from the combined policy or the initial validation.
                // Log the error, providing context about the user that failed to be added.
                _logger.LogError(ex, "An error occurred while adding user '{SanitizedUsername}'.", sanitizedUsername);
                // Background error log
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new Domain.Entities.ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "UserRepository",
                        EventType = "AddUser.Error",
                        Message = ex.Message,
                        Details = ex.StackTrace,
                        Exception = ex.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                throw;
            }
        }

        // In Infrastructure/Repositories/UserRepository.cs

        /// <summary>
        /// Asynchronously updates an existing user and their associated token wallet in the database.
        /// This operation is performed atomically within a transaction and uses resilience policies
        /// for handling transient errors. It also includes logic to detect potential concurrency issues
        /// or missing records during the update.
        /// </summary>
        /// <param name="user">The <see cref="User"/> entity with updated information.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the provided <paramref name="user"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the user record specified by <paramref name="user.Id"/> is not found in the database, indicating a potential concurrency issue or that the record no longer exists.</exception>
        /// <exception cref="RepositoryException">Thrown if any other database-related error occurs during the update process.</exception>
        public async Task UpdateAsync(User user, CancellationToken cancellationToken = default)
        {
            #region Input Validation and Initial Logging

            // --- Validate User Input ---
            // Ensure that the user object provided for update is not null.
            if (user == null)
            {
                // Log an error message detailing the invalid input before throwing.
                _logger.LogError("UserRepository: Attempted to update with a null User object.");
                throw new ArgumentNullException(nameof(user));
            }

            // --- Log Update Operation Start ---
            // Log the user ID and username for traceability. The username is logged directly as it's not sensitive in this context for a public method.
            _logger.LogInformation("UserRepository: Updating user. UserID: {UserId}, Username: {Username}.", user.Id, user.Username);

            #endregion

            try
            {
                // --- Apply Resilience Policies ---
                // Combine timeout and retry policies to execute the database operation robustly.
                Polly.Wrap.AsyncPolicyWrap combinedPolicy = _timeoutPolicy.WrapAsync(_retryPolicy);

                // Execute the core database update logic within the resilience policies.
                await combinedPolicy.ExecuteAsync(async (ct) =>
                {
                    // --- Database Connection and Transaction Setup ---
                    // Obtain a database connection using the factory method (_CreateConnection), ensuring the correct provider is used.
                    // Use synchronous 'using' for IDbConnection as it is only IDisposable.
                    using IDbConnection connection = CreateConnection();
                    // Asynchronously open the database connection.
                    await (connection as System.Data.Common.DbConnection)!.OpenAsync(ct);

                    // Begin a new database transaction to ensure atomicity of the update operations.
                    // Use 'await using' for DbTransaction as it implements IAsyncDisposable.
                    await using DbTransaction transaction = await (connection as System.Data.Common.DbConnection)!.BeginTransactionAsync(ct);

                    try
                    {
                        // --- Retrieve Provider-Specific SQL Statements ---
                        // Get the appropriate SQL strings for updating a user and upserting a wallet
                        // from the injected UserSqlProvider, which accounts for database dialect differences.
                        string updateUserSql = _sql.UpdateUserSql;
                        string upsertWalletSql = _sql.UpsertWalletSql;

                        #region Prepare User Update Parameters

                        // --- Prepare Parameters for User Update ---
                        // Create an anonymous object containing the user's data mapped to the SQL parameters.
                        // Note: DateTime.UtcNow is used for the 'UpdatedAt' timestamp to ensure consistency.
                        var userParams = new
                        {
                            user.Id,
                            user.Username,
                            user.TelegramId,
                            user.Email,
                            Level = user.Level.ToString(), // Convert enum to string for database storage.
                            UpdatedAt = DateTime.UtcNow,
                            user.EnableGeneralNotifications,
                            user.EnableVipSignalNotifications,
                            user.EnableRssNewsNotifications,
                            user.PreferredLanguage
                        };

                        #endregion

                        #region Execute User Update

                        // --- Execute User Update SQL ---
                        // Execute the user update command against the database within the current transaction.
                        int rowsAffected = await connection.ExecuteAsync(updateUserSql, userParams, transaction: transaction);

                        // --- Check if User Was Found and Updated ---
                        // If zero rows were affected, it means the user ID did not exist in the database.
                        // This could indicate a concurrency issue (e.g., user deleted concurrently) or a logic error.
                        // Throw an InvalidOperationException to signal this critical state.
                        if (rowsAffected == 0)
                        {
                            throw new InvalidOperationException($"User with ID '{user.Id}' not found for update. Concurrency conflict or record does not exist.");
                        }

                        #endregion

                        #region Execute Token Wallet Upsert (Conditional)

                        // --- Update Token Wallet if Present ---
                        // Check if the user object includes associated token wallet data.
                        if (user.TokenWallet != null)
                        {
                            // Prepare parameters for the wallet upsert operation.
                            var walletParams = new
                            {
                                user.TokenWallet.Id,
                                user.TokenWallet.UserId, // This might be redundant if Id implies UserId relationship, but included as per original code.
                                user.TokenWallet.Balance,
                                user.TokenWallet.IsActive,
                                user.TokenWallet.CreatedAt,
                                UpdatedAt = DateTime.UtcNow // Update the timestamp for the wallet record.
                            };
                            // Execute the upsert command for the token wallet.
                            _ = await connection.ExecuteAsync(upsertWalletSql, walletParams, transaction: transaction);
                        }
                        else
                        {
                            // Log a warning if no token wallet data is provided for the user, as this update will be skipped.
                            _logger.LogWarning("UserRepository: User {UserId} has no TokenWallet object for update. Skipping wallet update.", user.Id);
                        }

                        #endregion

                        #region Commit Transaction

                        // --- Commit the Transaction ---
                        // If all operations within the try block were successful, commit the transaction.
                        await transaction.CommitAsync(ct);

                        #endregion
                    }
                    catch (Exception)
                    {
                        // --- Rollback Transaction on Error ---
                        // If any exception occurred during the SQL execution or parameter preparation,
                        // roll back the transaction to ensure data consistency.
                        await transaction.RollbackAsync();
                        // Re-throw the exception to be caught by the outer handler for further processing.
                        throw;
                    }
                }, cancellationToken); // Pass the cancellation token to the executed action.

                #region Success Logging

                // --- Log Successful Update ---
                // Log a success message after the operation, including the username.
                _logger.LogInformation("UserRepository: Successfully updated user: {Username}", user.Username);

                #endregion
            }
            #region Exception Handling

            // --- Handle Specific Concurrency/Not Found Exception ---
            catch (InvalidOperationException concEx)
            {
                _logger.LogError(concEx, "UserRepository: Concurrency conflict or user not found while updating user {Username}.", user.Username);
                // Background error log
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new Domain.Entities.ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "UserRepository",
                        EventType = "UpdateUser.ConcurrencyConflict",
                        Message = concEx.Message,
                        Details = concEx.StackTrace,
                        Exception = concEx.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                throw;
            }
            // --- Handle General Exceptions ---
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserRepository: An error occurred while updating user {Username}.", user.Username);
                // Background error log
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new Domain.Entities.ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "UserRepository",
                        EventType = "UpdateUser.Error",
                        Message = ex.Message,
                        Details = ex.StackTrace,
                        Exception = ex.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                if (ex is not RepositoryException)
                {
                    throw new RepositoryException($"Failed to update user '{user.Username}'.", ex);
                }
                throw;
            }

            #endregion
        }


        /// <summary>
        /// Asynchronously retrieves a user by their Telegram ID, including related notification data (token wallets, subscriptions, preferences).
        /// This method uses provider-specific SQL for JSON aggregation and applies resilience policies (timeout and retry)
        /// to database operations.
        /// </summary>
        /// <param name="telegramId">The Telegram ID of the user to retrieve.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A <see cref="Task{User?}" /> representing the asynchronous operation. Returns the found <see cref="User"/> domain entity, or <c>null</c> if no user is found or if the input is invalid.</returns>
        public async Task<User?> GetByTelegramIdWithNotificationsAsync(string telegramId, CancellationToken cancellationToken = default)
        {
            #region Input Validation and Initial Logging

            // --- Validate Input Parameter ---
            // Check if the provided Telegram ID is null, empty, or whitespace.
            if (string.IsNullOrWhiteSpace(telegramId))
            {
                // Log a warning for invalid input, as it prevents a meaningful database query.
                _logger.LogWarning("UserRepository: GetByTelegramIdWithNotificationsAsync called with null or empty telegramId.");
                // Return null as no user can be found with invalid criteria.
                return null;
            }

            // --- Log Operation Details ---
            // Log the Telegram ID being queried and mention that JSON aggregation is used,
            // which implies fetching related data in a single query.
            _logger.LogTrace("UserRepository: Fetching user with notifications by TelegramID: {TelegramId} using JSON aggregation.", telegramId);

            #endregion

            #region SQL Query Retrieval

            // --- Retrieve Provider-Specific SQL ---
            // Fetch the SQL statement for retrieving user data by Telegram ID.
            // The UserSqlProvider ensures that the correct SQL dialect (e.g., syntax for JSON functions, quoting) is used.
            string sql = _sql.GetByTelegramIdSql;

            #endregion

            try
            {
                // --- Apply Resilience Policies ---
                // Combine timeout and retry policies to manage potential transient issues during database access.
                Polly.Wrap.AsyncPolicyWrap combinedPolicy = _timeoutPolicy.WrapAsync(_retryPolicy);

                // Execute the database retrieval logic within the defined resilience policies.
                User? user = await combinedPolicy.ExecuteAsync(async (ct) =>
                {
                    // --- Database Connection and Query Execution ---
                    // Obtain a database connection using the factory method (_CreateConnection), ensuring the correct provider is used.
                    // Use synchronous 'using' for IDbConnection as it is only IDisposable.
                    using IDbConnection connection = CreateConnection();
                    // Asynchronously open the database connection.
                    await (connection as System.Data.Common.DbConnection)!.OpenAsync(ct);

                    // Execute the query to fetch the user data, including related entities aggregated as JSON.
                    // Dapper's QueryFirstOrDefaultAsync returns the first matching record or null if none is found.
                    UserWithRelatedDataDto? userDto = await connection.QueryFirstOrDefaultAsync<UserWithRelatedDataDto>(
                        // CommandDefinition encapsulates the SQL, parameters, and cancellation token for Dapper.
                        new CommandDefinition(sql, new { TelegramId = telegramId }, cancellationToken: ct)
                    );

                    // --- Handle Query Results ---
                    // If no user DTO was returned, log and return null.
                    if (userDto == null)
                    {
                        _logger.LogTrace("User with TelegramID {TelegramId} not found.", telegramId);
                        return null;
                    }

                    // Convert the fetched DTO into a domain entity, potentially parsing JSON fields.
                    User userDomainEntity = userDto.ToDomainEntity(_logger);

                    // Log successful retrieval of the user and their related data.
                    _logger.LogDebug("Successfully fetched user {UserId} (TelegramID: {TelegramId}) with all related entities.", userDomainEntity.Id, telegramId);
                    return userDomainEntity;

                }, cancellationToken); // Pass the cancellation token to the executed action.

                return user; // Return the fetched user domain entity or null.
            }
            #region Exception Handling

            // --- Handle Timeout Specific Exception ---
            catch (TimeoutRejectedException ex)
            {
                _logger.LogError(ex, "UserRepository: Operation timed out while fetching user by TelegramID {TelegramId}.", telegramId);
                // Background error log
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new Domain.Entities.ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "UserRepository",
                        EventType = "GetUserByTelegramIdWithNotifications.Timeout",
                        Message = ex.Message,
                        Details = ex.StackTrace,
                        Exception = ex.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                throw new RepositoryException($"The operation to fetch user by TelegramID {telegramId} timed out.", ex);
            }
            // --- Handle General Exceptions ---
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get user by TelegramID {TelegramId} after retries.", telegramId);
                // Background error log
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new Domain.Entities.ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "UserRepository",
                        EventType = "GetUserByTelegramIdWithNotifications.Error",
                        Message = ex.Message,
                        Details = ex.StackTrace,
                        Exception = ex.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                throw new RepositoryException($"An error occurred while fetching user by TelegramID: {telegramId}", ex);
            }

            #endregion
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
        // In Infrastructure/Repositories/UserRepository.cs

        /// <summary>
        /// Asynchronously checks if a user with the specified email address exists in the database.
        /// The email comparison is performed case-insensitively. The operation is executed with a retry policy
        /// to handle transient database errors.
        /// </summary>
        /// <param name="email">The email address to check for existence.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A <see cref="Task{bool}"/> representing the asynchronous operation. Returns <c>true</c> if a user with the email exists, <c>false</c> otherwise.</returns>
        /// <exception cref="RepositoryException">Thrown if any database error occurs during the check, after retries.</exception>
        public async Task<bool> ExistsByEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            #region Input Validation and Preparation

            // --- Validate Email Input ---
            // Check if the provided email is null, empty, or consists only of whitespace characters.
            if (string.IsNullOrWhiteSpace(email))
            {
                // Log a warning for invalid input, as an empty/null email cannot match any user.
                _logger.LogWarning("UserRepository: Checking existence by email: Input is null or empty.");
                // Return false immediately since no user can exist with invalid criteria.
                return false;
            }

            // --- Sanitize Email for Logging ---
            // SECURITY: Sanitize email before logging to prevent exposure of private information
            string sanitizedEmail = _logSanitizer.Sanitize(email);

            // --- Log Operation Details ---
            // Log a generic message indicating the operation without exposing sensitive data.
            _logger.LogTrace("UserRepository: Checking existence by email.");

            #endregion

            try
            {
                // --- Apply Resilience Policy ---
                // Execute the core database operation within the retry policy.
                // This makes the operation resilient to transient issues like temporary network disruptions or database load.
                return await _retryPolicy.ExecuteAsync(async () =>
                {
                    // --- Database Connection and Query Execution ---
                    // Obtain a database connection instance using the helper method.
                    using IDbConnection connection = CreateConnection();
                    // Asynchronously open the database connection to the data source.
                    // Using System.Data.Common.DbConnection for generality.
                    await (connection as System.Data.Common.DbConnection)!.OpenAsync(cancellationToken);

                    // --- MODIFIED LINE: Retrieve SQL from Provider ---
                    // Fetch the SQL statement for checking user existence by email from the UserSqlProvider.
                    // This ensures that the query uses the correct syntax and parameter naming conventions for the target database.
                    string sql = _sql.CheckExistsByEmailSql;

                    // --- Execute Scalar Query ---
                    // Execute the SQL query which is expected to return a single scalar value (the count of matching emails).
                    // Dapper's ExecuteScalarAsync is used for this purpose.
                    // The lowercased email is passed as a parameter to the query.
                    int count = await connection.ExecuteScalarAsync<int>(sql, new { Email = email });

                    // --- Determine Existence ---
                    // The user exists if the count of matching emails returned from the database is greater than zero.
                    return count > 0;
                });
            }
            #region Exception Handling

            // --- Handle General Exceptions During Operation ---
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserRepository: Error checking existence for email {SanitizedEmail}. Exception (Sanitized): {SanitizedException}",
                    sanitizedEmail, _logSanitizer.Sanitize(ex.Message));
                // Background error log (external monitoring) - redact sensitive data
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new Domain.Entities.ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "UserRepository",
                        EventType = "CheckExistsByEmail.Error",
                        // Do NOT log sanitizedEmail or any sensitive data externally
                        Message = "Sensitive data redacted", // Placeholder for compliance
                        Details = ex.StackTrace,
                        Exception = ex.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                throw new RepositoryException($"Failed to check existence for email '{sanitizedEmail}'.", ex);
            }

            #endregion
        }

        /// <inheritdoc />
        public async Task<bool> ExistsByTelegramIdAsync(string telegramId, CancellationToken cancellationToken = default)
        {
            #region Input Validation and Preparation

            // --- Validate Telegram ID Input ---
            // Check if the provided Telegram ID is null, empty, or consists only of whitespace characters.
            if (string.IsNullOrWhiteSpace(telegramId))
            {
                // Log a warning for invalid input, as an empty/null ID cannot match any user.
                _logger.LogWarning("UserRepository: Checking existence by TelegramID: Input is null or empty.");
                // Return false immediately since no user can exist with invalid criteria.
                return false;
            }

            // --- Sanitize Telegram ID for Logging ---
            // SECURITY: Sanitize Telegram ID before logging to prevent exposure of private information
            string sanitizedTelegramId = _logSanitizer.Sanitize(telegramId);

            // --- Log Operation Details ---
            // Log the sanitized Telegram ID being checked at a trace level for detailed operational diagnostics.
            _logger.LogTrace("UserRepository: Checking existence by TelegramID: {SanitizedTelegramId}.", sanitizedTelegramId);

            #endregion

            try
            {
                // --- Apply Resilience Policy ---
                // Execute the core database operation within the retry policy.
                // This makes the operation resilient to transient issues like temporary network disruptions or database load.
                return await _retryPolicy.ExecuteAsync(async () =>
                {
                    // --- Database Connection and Query Execution ---
                    // Obtain a database connection instance using the helper method.
                    using IDbConnection connection = CreateConnection();
                    // Asynchronously open the database connection to the data source.
                    // Using System.Data.Common.DbConnection for generality.
                    await (connection as System.Data.Common.DbConnection)!.OpenAsync(cancellationToken);

                    // --- MODIFIED LINE: Retrieve SQL from Provider ---
                    // Fetch the SQL statement for checking user existence by Telegram ID from the UserSqlProvider.
                    // This ensures that the query uses the correct syntax and parameter naming conventions for the target database.
                    string sql = _sql.CheckExistsByTelegramIdSql;

                    // --- Execute Scalar Query ---
                    // Use Dapper's ExecuteScalarAsync to run the SQL query and retrieve a single scalar value (the count).
                    // The TelegramId parameter is passed to the query, ensuring safe parameterization.
                    int count = await connection.ExecuteScalarAsync<int>(sql, new { TelegramId = telegramId });

                    // --- Determine Existence Based on Count ---
                    // The user is considered to exist if the count returned from the database is greater than zero.
                    return count > 0;
                });
            }
            #region Exception Handling

            // --- Handle General Exceptions ---
            // Catch any exceptions that occur during the execution of the retry policy or the database operation itself.
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserRepository: Error checking existence by TelegramID {SanitizedTelegramId}. Exception (Sanitized): {SanitizedException}",
                    sanitizedTelegramId, _logSanitizer.Sanitize(ex.Message));
                // Background error log
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new Domain.Entities.ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "UserRepository",
                        EventType = "CheckExistsByTelegramId.Error",
                        Message = ex.Message,
                        Details = ex.StackTrace,
                        Exception = ex.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                throw new RepositoryException($"Failed to check existence by TelegramID '{sanitizedTelegramId}'.", ex);
            }

            #endregion
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
        /// <summary>
        /// Asynchronously deletes a user and their related data from the database.
        /// This operation is performed atomically within a transaction, uses database-specific SQL queries
        /// fetched from <see cref="UserSqlProvider"/>, and employs resilience patterns like retries.
        /// It includes checks for user existence and proper transaction management (commit/rollback).
        /// </summary>
        /// <param name="userId">The unique identifier of the user to delete.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the user is not found for deletion, indicating a potential concurrency issue.</exception>
        /// <exception cref="RepositoryException">Thrown if any database error occurs during the deletion process, after applying retry attempts.</exception>
        private async Task DeleteUserAndRelatedData(Guid userId, CancellationToken cancellationToken)
        {
            try
            {
                // --- Apply Resilience Policy for Database Operations ---
                // Execute the entire deletion logic within the retry policy. This handles transient database errors.
                await _retryPolicy.ExecuteAsync(async (ct) =>
                {
                    // --- Establish Database Connection and Transaction ---
                    // Obtain a database connection instance using the helper method.
                    using IDbConnection connection = CreateConnection();
                    // Asynchronously open the database connection. Cast to DbConnection is for accessing async methods.
                    await (connection as System.Data.Common.DbConnection)!.OpenAsync(ct);

                    // Start an asynchronous database transaction. This ensures that all operations within the transaction
                    // are treated as a single atomic unit – either all succeed, or all are rolled back.
                    // Using 'await using' ensures the transaction is properly disposed of (committed or rolled back).
                    await using DbTransaction transaction = await (connection as System.Data.Common.DbConnection)!.BeginTransactionAsync(ct);
                    try
                    {
                        // --- MODIFIED: Fetch Provider-Specific SQL for Existence Check ---
                        // Retrieve the SQL query to check if the user exists from the UserSqlProvider.
                        // This ensures the query uses the correct syntax for the target database.
                        int userExistsCount = await connection.ExecuteScalarAsync<int>(_sql.CheckUserExistsSql, new { Id = userId }, transaction: transaction);

                        // --- Check User Existence ---
                        // If the count returned is 0, the user does not exist in the database.
                        if (userExistsCount == 0)
                        {
                            // Log a warning indicating that the user was not found for deletion.
                            _logger.LogWarning("UserRepository: User with ID {UserId} not found for deletion in DeleteUserAndRelatedData.", userId);
                            // Roll back the transaction to ensure no partial state is left, although no operations have been performed yet.
                            await transaction.RollbackAsync(ct); // Ensure rollback is awaited.
                            // Exit the current execution scope within the retry policy.
                            return;
                        }

                        // --- Data Deletion Logic ---
                        // The comment about ON DELETE CASCADE remains valid for both DBs, assuming schemas are consistent.
                        // This implies that related data (like wallets, subscriptions) might be automatically deleted
                        // by the database if foreign key constraints with CASCADE ON DELETE are set up.
                        // However, this repository explicitly handles user deletion via its own SQL.

                        // --- MODIFIED: Fetch Provider-Specific SQL for User Deletion ---
                        // Retrieve the SQL query for deleting the user record from the UserSqlProvider.
                        int rowsAffected = await connection.ExecuteAsync(_sql.DeleteUserSql, new { Id = userId }, transaction: transaction);

                        // --- Verify Deletion Success ---
                        // Check if the delete operation affected any rows. If not, it indicates a concurrency issue
                        // or that the user was deleted by another process between the existence check and the delete command.
                        if (rowsAffected == 0)
                        {
                            // Throw an InvalidOperationException to signal this specific error condition.
                            throw new InvalidOperationException($"User with ID '{userId}' was not found for deletion or was deleted by another process. Concurrency conflict suspected.");
                        }

                        // --- Commit Transaction ---
                        // If all operations within the try block were successful (user existed, delete succeeded), commit the transaction.
                        await transaction.CommitAsync(ct); // Ensure commit is awaited.
                    }
                    catch (InvalidOperationException) // Catch specific concurrency/not-found exceptions.
                    {
                        // If an InvalidOperationException occurred (e.g., user not found for deletion), rollback the transaction.
                        await transaction.RollbackAsync(ct);
                        // Re-throw the exception to propagate it upwards.
                        throw;
                    }
                    catch (Exception ex) // Catch any other exceptions during the transaction.
                    {
                        // Roll back the transaction if any other error occurs.
                        await transaction.RollbackAsync(ct);
                        // Wrap the exception in a RepositoryException for standardized error handling.
                        throw new RepositoryException($"Failed to delete user with ID '{userId}' with Dapper transaction.", ex);
                    }
                }, cancellationToken); // Pass the cancellation token to the ExecuteAsync method of the retry policy.

                // --- Log Successful Deletion ---
                // Log information that the user deletion process completed successfully.
                _logger.LogInformation("UserRepository: Successfully deleted user with ID: {UserId}", userId);
            }
            // --- Outer Exception Handling ---
            // Catch specific exceptions that might be thrown by the retry policy or the operation itself.

            // Handle concurrency issues or user-not-found scenarios specifically.
            catch (InvalidOperationException concEx)
            {
                _logger.LogError(concEx, "UserRepository: Concurrency conflict or user not found while deleting user with ID {UserId}.", userId);
                // Background error log
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new Domain.Entities.ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "UserRepository",
                        EventType = "DeleteUser.ConcurrencyConflict",
                        Message = concEx.Message,
                        Details = concEx.StackTrace,
                        Exception = concEx.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                throw;
            }
            catch (RepositoryException dbEx)
            {
                _logger.LogError(dbEx, "UserRepository: Error deleting user with ID {UserId} from the database after retries.", userId);
                // Background error log
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new Domain.Entities.ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "UserRepository",
                        EventType = "DeleteUser.RepositoryException",
                        Message = dbEx.Message,
                        Details = dbEx.StackTrace,
                        Exception = dbEx.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserRepository: An unexpected error occurred while deleting user with ID {UserId}.", userId);
                // Background error log
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _serviceProvider.CreateScope();
                    IProMonitoringLogRepository repo = scope.ServiceProvider.GetRequiredService<IProMonitoringLogRepository>();
                    await repo.AddAsync(new Domain.Entities.ProMonitoringLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Source = "UserRepository",
                        EventType = "DeleteUser.Error",
                        Message = ex.Message,
                        Details = ex.StackTrace,
                        Exception = ex.ToString(),
                        Status = "Failed",
                        CreatedAt = DateTime.UtcNow
                    });
                });
                throw;
            }
        }
    }
}