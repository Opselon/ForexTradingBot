// File: Infrastructure/Persistence/Repositories/UserSqlProvider.cs

using Infrastructure.Data;

namespace Infrastructure.Persistence.Configurations
{
    /// <summary>
    /// Provides a comprehensive and database-specific set of SQL queries for all user-related operations.
    /// This class acts as a centralized repository for SQL logic, encapsulating the dialect differences
    /// between supported database systems (SQLite, PostgreSQL, and SQL Server) for a wide range of
    /// data retrieval, modification, batch processing, and analytical tasks related to users.
    /// The SQL Server implementation is heavily optimized for performance, security, and scalability.
    /// </summary>
    public class UserSqlProvider
    {
        #region C# Property Definitions (SQL Queries)

        #region Core User & Related Data Retrieval
        /// <summary>
        /// Gets the base SQL fragment for selecting a single user's data along with aggregated JSON fragments
        /// for their related entities. This is highly optimized for single-record fetching.
        /// <remarks>
        /// SQL Server: Uses subqueries with `FOR JSON PATH` and `ISNULL` to efficiently create nested JSON.
        /// PostgreSQL: Uses `jsonb_agg` for superior performance.
        /// SQLite: Uses `json_group_array` and `json_object`.
        /// </remarks>
        /// </summary>
        public string GetUserWithRelatedDataFragment { get; }

        /// <summary>
        /// Gets the full SQL query to retrieve a complete user entity graph by their primary key (Id).
        /// </summary>
        public string GetByIdSql { get; } = string.Empty;

        /// <summary>
        /// Gets the full SQL query to retrieve a complete user entity graph by their unique TelegramId.
        /// An index on the TelegramId column is highly recommended for performance.
        /// </summary>
        public string GetByTelegramIdSql { get; } = string.Empty;

        /// <summary>
        /// Gets the full SQL query to retrieve a complete user entity graph by their unique, case-insensitive Email.
        /// An index on a computed, persisted, lowercased Email column is highly recommended for performance.
        /// </summary>
        public string GetByEmailSql { get; } = string.Empty;

        /// <summary>
        /// Gets a lightweight SQL query to retrieve only a user's Id by their case-insensitive Email.
        /// This is optimized to avoid fetching the full user object when only the ID is needed.
        /// </summary>
        public string GetUserIdByEmailSql { get; } = string.Empty;
        #endregion

        #region User Creation, Modification, and Deletion
        /// <summary>
        /// Gets the SQL query to insert a new user record into the Users table.
        /// </summary>
        public string AddUserSql { get; } = string.Empty;

        /// <summary>
        /// Gets the SQL query to update an existing user record in the Users table.
        /// </summary>
        public string UpdateUserSql { get; } = string.Empty;

        /// <summary>
        /// Gets the SQL query to delete a user record from the Users table.
        /// Assumes `ON DELETE CASCADE` is configured for foreign keys to handle related data.
        /// </summary>
        public string DeleteUserSql { get; } = string.Empty;
        #endregion

        #region Existence Checks
        /// <summary>
        /// Gets a highly optimized SQL query to check for the existence of a user by their Id.
        /// </summary>
        public string CheckUserExistsSql { get; } = string.Empty;

        /// <summary>
        /// Gets a highly optimized SQL query to check for the existence of a user by their Email.
        /// </summary>
        public string CheckExistsByEmailSql { get; } = string.Empty;

        /// <summary>
        /// Gets a highly optimized SQL query to check for the existence of a user by their TelegramId.
        /// </summary>
        public string CheckExistsByTelegramIdSql { get; } = string.Empty;
        #endregion

        #region Wallet Management
        /// <summary>
        /// Gets the SQL query to insert a new token wallet record.
        /// </summary>
        public string AddWalletSql { get; } = string.Empty;

        /// <summary>
        /// Gets a highly performant SQL query to insert a new token wallet or update it if it already exists for a given UserId.
        /// <remarks>
        /// SQL Server: Implemented using the atomic `MERGE` statement.
        /// PostgreSQL: Implemented using `ON CONFLICT DO UPDATE`.
        /// SQLite: Implemented using `ON CONFLICT DO UPDATE`.
        /// </remarks>
        /// </summary>
        public string UpsertWalletSql { get; } = string.Empty;

        /// <summary>
        /// Gets the SQL query to safely adjust a user's wallet balance by a given amount.
        /// This performs a relative update (`Balance = Balance + @Adjustment`) to prevent race conditions.
        /// </summary>
        public string AdjustUserWalletBalanceSql { get; } = string.Empty;

        /// <summary>
        /// Gets the SQL query to retrieve all token wallets with a balance below a specified threshold.
        /// Useful for administrative alerts or reports.
        /// </summary>
        public string GetWalletsWithLowBalanceSql { get; } = string.Empty;
        #endregion

        #region Subscription Management
        /// <summary>
        /// Gets the SQL query to retrieve all active subscriptions for a specific user.
        /// </summary>
        public string GetActiveSubscriptionsForUserSql { get; } = string.Empty;

        /// <summary>
        /// Gets the SQL query to find all users whose subscriptions will expire within a given number of days.
        /// Useful for sending renewal reminders.
        /// </summary>
        public string GetUsersWithExpiringSubscriptionsSql { get; } = string.Empty;

        /// <summary>
        /// Gets the SQL to update the status of expired subscriptions from 'Active' to 'Expired'.
        /// This is an efficient batch operation.
        /// </summary>
        public string BulkUpdateExpiredSubscriptionsSql { get; } = string.Empty;
        #endregion

        #region Preference Management
        /// <summary>
        /// Gets the SQL query to completely replace a user's signal preferences with a new set.
        /// This is done atomically by first deleting all existing preferences for the user, then inserting the new ones.
        /// <remarks>Requires a Table-Valued Parameter (TVP) in SQL Server for performance.</remarks>
        /// </summary>
        public string SetUserPreferencesSql { get; } = string.Empty;
        #endregion

        #region News Notification Queries
        /// <summary>
        /// Gets the initial WHERE clause for news notification queries, checking the user's notification setting.
        /// </summary>
        public string NewsNotificationInitialWhereClause { get; }

        /// <summary>
        /// Gets the SQL clause to filter users who have a currently active VIP or Premium subscription.
        /// </summary>
        public string VipSubscriptionCheckClause { get; }

        /// <summary>
        /// Gets the SQL clause to filter users based on their category preferences.
        /// This logic includes users who have no preferences set, treating them as subscribed to all.
        /// </summary>
        public string CategoryPreferenceCheckClause { get; }

        /// <summary>
        /// Gets a base query for fetching users for news notifications, intended for simple, non-performant scenarios or backward compatibility.
        /// </summary>
        public string GetUsersForNewsNotificationBase { get; }

        /// <summary>
        /// Gets a base SQL query for fetching users for notifications.
        /// This property might be a duplicate or an evolution of `GetUsersForNewsNotificationBase`.
        /// </summary>
        public string GetUsersForNewsNotificationBaseSql { get; } = string.Empty;
        #endregion

        #region Batch Operations
        /// <summary>
        /// Gets the SQL query to fetch all base user records for list views, ordered by username.
        /// </summary>
        public string GetAllUsersSql { get; } = string.Empty;

        /// <summary>
        /// Gets the SQL query to fetch all token wallets for a given list of user IDs.
        /// Optimized for batch-loading scenarios to prevent N+1 query problems.
        /// </summary>
        public string GetWalletsForUsersSql { get; } = string.Empty;

        /// <summary>
        /// Gets the SQL query to fetch all subscriptions for a given list of user IDs.
        /// Optimized for batch-loading scenarios.
        /// </summary>
        public string GetSubscriptionsForUsersSql { get; } = string.Empty;

        /// <summary>
        /// Gets the SQL query to fetch all user signal preferences for a given list of user IDs.
        /// Optimized for batch-loading scenarios.
        /// </summary>
        public string GetPreferencesForUsersSql { get; } = string.Empty;

        /// <summary>
        /// Gets the SQL for performing a bulk update of user levels based on a list of User IDs.
        /// <remarks>Requires a Table-Valued Parameter (TVP) in SQL Server for performance.</remarks>
        /// </summary>
        public string BulkUpdateUserLevelsSql { get; } = string.Empty;
        #endregion

        #region Auditing & Logging
        /// <summary>
        /// Gets the SQL to insert a record into a `UserAuditLogs` table after a user's properties have been changed.
        /// Assumes a table `UserAuditLogs` exists.
        /// </summary>
        public string LogUserUpdateAuditSql { get; } = string.Empty;
        #endregion

        #region Reporting & Analytics
        /// <summary>
        /// Gets the SQL to count users grouped by their `Level`. Useful for dashboard analytics.
        /// </summary>
        public string GetUserCountByLevelSql { get; } = string.Empty;

        /// <summary>
        /// Gets the SQL to count new user signups grouped by a specified time period (e.g., 'day', 'month', 'year').
        /// Expects @Period (varchar) and @StartDate (datetime2) parameters.
        /// </summary>
        public string GetNewUserSignupsByPeriodSql { get; } = string.Empty;

        /// <summary>
        /// Gets a detailed summary of user engagement, including last login, subscription status, and total transactions.
        /// Uses Common Table Expressions (CTEs) for clarity and performance.
        /// </summary>
        public string GetUserEngagementSummarySql { get; } = string.Empty;
        #endregion

        #endregion

        /// <summary>
        /// Initializes a new instance of the <see cref="UserSqlProvider"/> class.
        /// It determines the appropriate SQL queries and clauses based on the database provider
        /// configured via the <paramref name="dbProviderService"/>.
        /// </summary>
        /// <param name="dbProviderService">The service that provides information about the configured database provider.</param>
        /// <exception cref="NotSupportedException">Thrown if the database provider configured in <paramref name="dbProviderService"/> is not supported by this provider.</exception>
        public UserSqlProvider(DbProviderService dbProviderService)
        {
            switch (dbProviderService.Provider)
            {
                case DatabaseProvider.SQLite:
                    #region SQLite SQL Definitions (Untouched)
                    GetAllUsersSql = "SELECT Id, Username, TelegramId, Email, Level, CreatedAt, UpdatedAt, EnableGeneralNotifications, EnableVipSignalNotifications, EnableRssNewsNotifications, PreferredLanguage FROM Users ORDER BY Username;";
                    GetWalletsForUsersSql = "SELECT Id, UserId, Balance, IsActive, CreatedAt, UpdatedAt FROM TokenWallets WHERE UserId IN @UserIds;";
                    GetSubscriptionsForUsersSql = "SELECT Id, UserId, StartDate, EndDate, Status, ActivatingTransactionId, CreatedAt, UpdatedAt FROM Subscriptions WHERE UserId IN @UserIds;";
                    GetPreferencesForUsersSql = "SELECT Id, UserId, CategoryId, CreatedAt FROM UserSignalPreferences WHERE UserId IN @UserIds;";
                    GetUserWithRelatedDataFragment = @"
                        SELECT
                            u.Id, u.Username, u.TelegramId, u.Email, u.Level, u.CreatedAt, u.UpdatedAt,
                            u.EnableGeneralNotifications, u.EnableVipSignalNotifications, u.EnableRssNewsNotifications, u.PreferredLanguage,
                            IFNULL((
                                SELECT json_group_array(json_object(
                                    'Id', tw.Id, 'UserId', tw.UserId, 'Balance', tw.Balance, 'IsActive', tw.IsActive, 'CreatedAt', tw.CreatedAt, 'UpdatedAt', tw.UpdatedAt
                                )) FROM TokenWallets tw WHERE tw.UserId = u.Id
                            ), '[]') AS TokenWalletJson,
                            IFNULL((
                                SELECT json_group_array(json_object(
                                    'Id', s.Id, 'UserId', s.UserId, 'StartDate', s.StartDate, 'EndDate', s.EndDate, 'Status', s.Status,
                                    'ActivatingTransactionId', s.ActivatingTransactionId, 'CreatedAt', s.CreatedAt, 'UpdatedAt', s.UpdatedAt
                                )) FROM Subscriptions s WHERE s.UserId = u.Id
                            ), '[]') AS SubscriptionsJson,
                            IFNULL((
                                SELECT json_group_array(json_object(
                                    'Id', usp.Id, 'UserId', usp.UserId, 'CategoryId', usp.CategoryId, 'CreatedAt', usp.CreatedAt
                                )) FROM UserSignalPreferences usp WHERE usp.UserId = u.Id
                            ), '[]') AS PreferencesJson
                        FROM Users u";
                    CheckExistsByEmailSql = "SELECT COUNT(1) FROM Users WHERE LOWER(Email) = LOWER(@Email);";
                    GetUserIdByEmailSql = "SELECT Id FROM Users WHERE LOWER(Email) = LOWER(@Email);";
                    GetByIdSql = $"{GetUserWithRelatedDataFragment} WHERE u.Id = @Id;";
                    GetByTelegramIdSql = $"{GetUserWithRelatedDataFragment} WHERE u.TelegramId = @TelegramId;";
                    GetByEmailSql = $"{GetUserWithRelatedDataFragment} WHERE LOWER(u.Email) = LOWER(@Email);";
                    CheckExistsByTelegramIdSql = "SELECT COUNT(1) FROM Users WHERE TelegramId = @TelegramId;";
                    AddUserSql = "INSERT INTO Users (Id, Username, TelegramId, Email, Level, CreatedAt, UpdatedAt, EnableGeneralNotifications, EnableVipSignalNotifications, EnableRssNewsNotifications, PreferredLanguage) VALUES (@UserId, @Username, @TelegramId, @Email, @Level, @CreatedAt, @UpdatedAt, @EnableGeneralNotifications, @EnableVipSignalNotifications, @EnableRssNewsNotifications, @PreferredLanguage);";
                    AddWalletSql = "INSERT INTO TokenWallets (Id, UserId, Balance, IsActive, CreatedAt, UpdatedAt) VALUES (@TokenWalletId, @UserId, @Balance, @IsActive, @TokenWalletCreatedAt, @TokenWalletUpdatedAt);";
                    UpdateUserSql = "UPDATE Users SET Username = @Username, TelegramId = @TelegramId, Email = @Email, Level = @Level, UpdatedAt = @UpdatedAt, EnableGeneralNotifications = @EnableGeneralNotifications, EnableVipSignalNotifications = @EnableVipSignalNotifications, EnableRssNewsNotifications = @EnableRssNewsNotifications, PreferredLanguage = @PreferredLanguage WHERE Id = @Id;";
                    UpsertWalletSql = "INSERT INTO TokenWallets (Id, UserId, Balance, IsActive, CreatedAt, UpdatedAt) VALUES (@Id, @UserId, @Balance, @IsActive, @CreatedAt, @UpdatedAt) ON CONFLICT(UserId) DO UPDATE SET Balance = excluded.Balance, IsActive = excluded.IsActive, UpdatedAt = excluded.UpdatedAt;";
                    DeleteUserSql = "DELETE FROM Users WHERE Id = @Id;";
                    CheckUserExistsSql = "SELECT COUNT(1) FROM Users WHERE Id = @Id;";
                    NewsNotificationInitialWhereClause = "WHERE u.EnableRssNewsNotifications = 1";
                    VipSubscriptionCheckClause = @"
                        AND u.Level IN ('Premium', 'Vip') 
                        AND EXISTS (
                            SELECT 1 FROM Subscriptions s_sub 
                            WHERE s_sub.UserId = u.Id 
                              AND s_sub.StartDate <= CURRENT_TIMESTAMP 
                              AND s_sub.EndDate >= CURRENT_TIMESTAMP 
                              AND s_sub.Status = 'Active'
                        )";
                    CategoryPreferenceCheckClause = @"
                        AND (
                            NOT EXISTS (SELECT 1 FROM UserSignalPreferences usp_pref WHERE usp_pref.UserId = u.Id) 
                            OR EXISTS (
                                SELECT 1 FROM UserSignalPreferences usp_pref 
                                WHERE usp_pref.UserId = u.Id AND usp_pref.CategoryId = @NewsItemSignalCategoryId
                            )
                        )";
                    GetUsersForNewsNotificationBase = "SELECT u.*, tw.*, s.*, usp.* FROM Users u LEFT JOIN TokenWallets tw ON u.Id = tw.UserId LEFT JOIN Subscriptions s ON u.Id = s.UserId LEFT JOIN UserSignalPreferences usp ON u.Id = usp.UserId " + NewsNotificationInitialWhereClause;
                    GetUsersForNewsNotificationBaseSql = GetUsersForNewsNotificationBase; // Match definition
                    break;
                #endregion

                case DatabaseProvider.Postgres:
                    #region PostgreSQL SQL Definitions (Untouched)
                    CheckExistsByTelegramIdSql = @"SELECT 1 FROM public.""Users"" WHERE ""TelegramId"" = @TelegramId LIMIT 1;";
                    GetAllUsersSql = @"SELECT ""Id"", ""Username"", ""TelegramId"", ""Email"", ""Level"", ""CreatedAt"", ""UpdatedAt"", ""EnableGeneralNotifications"", ""EnableVipSignalNotifications"", ""EnableRssNewsNotifications"", ""PreferredLanguage"" FROM public.""Users"" ORDER BY ""Username"";";
                    GetWalletsForUsersSql = @"SELECT ""Id"", ""UserId"", ""Balance"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"" FROM public.""TokenWallets"" WHERE ""UserId"" = ANY(@UserIds);";
                    GetSubscriptionsForUsersSql = @"SELECT ""Id"", ""UserId"", ""StartDate"", ""EndDate"", ""Status"", ""ActivatingTransactionId"", ""CreatedAt"", ""UpdatedAt"" FROM public.""Subscriptions"" WHERE ""UserId"" = ANY(@UserIds);";
                    GetPreferencesForUsersSql = @"SELECT ""Id"", ""UserId"", ""CategoryId"", ""CreatedAt"" FROM public.""UserSignalPreferences"" WHERE ""UserId"" = ANY(@UserIds);";
                    GetUserWithRelatedDataFragment = @"
                        SELECT
                            u.""Id"", u.""Username"", u.""TelegramId"", u.""Email"", u.""Level"", u.""CreatedAt"", u.""UpdatedAt"",
                            u.""EnableGeneralNotifications"", u.""EnableVipSignalNotifications"", u.""EnableRssNewsNotifications"", u.""PreferredLanguage"",
                            (SELECT COALESCE(jsonb_agg(tw), '[]'::jsonb) FROM public.""TokenWallets"" tw WHERE tw.""UserId"" = u.""Id"") AS ""TokenWalletJson"",
                            (SELECT COALESCE(jsonb_agg(s), '[]'::jsonb) FROM public.""Subscriptions"" s WHERE s.""UserId"" = u.""Id"") AS ""SubscriptionsJson"",
                            (SELECT COALESCE(jsonb_agg(usp), '[]'::jsonb) FROM public.""UserSignalPreferences"" usp WHERE usp.""UserId"" = u.""Id"") AS ""PreferencesJson""
                        FROM public.""Users"" u";

                    GetByIdSql = $"{GetUserWithRelatedDataFragment} WHERE u.\"Id\" = @Id;";
                    GetByTelegramIdSql = $"{GetUserWithRelatedDataFragment} WHERE u.\"TelegramId\" = @TelegramId;";
                    GetByEmailSql = $"{GetUserWithRelatedDataFragment} WHERE LOWER(u.\"Email\") = LOWER(@Email);";
                    GetUserIdByEmailSql = @"SELECT ""Id"" FROM public.""Users"" WHERE LOWER(""Email"") = LOWER(@Email) LIMIT 1;";
                    AddUserSql = @"INSERT INTO public.""Users"" (""Id"", ""Username"", ""TelegramId"", ""Email"", ""Level"", ""CreatedAt"", ""UpdatedAt"", ""EnableGeneralNotifications"", ""EnableVipSignalNotifications"", ""EnableRssNewsNotifications"", ""PreferredLanguage"") VALUES (@UserId, @Username, @TelegramId, @Email, @Level, @CreatedAt, @UpdatedAt, @EnableGeneralNotifications, @EnableVipSignalNotifications, @EnableRssNewsNotifications, @PreferredLanguage);";
                    AddWalletSql = @"INSERT INTO public.""TokenWallets"" (""Id"", ""UserId"", ""Balance"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"") VALUES (@TokenWalletId, @UserId, @Balance, @IsActive, @TokenWalletCreatedAt, @TokenWalletUpdatedAt);";
                    CheckExistsByEmailSql = @"SELECT 1 FROM public.""Users"" WHERE LOWER(""Email"") = LOWER(@Email) LIMIT 1;";
                    UpdateUserSql = @"UPDATE public.""Users"" SET ""Username"" = @Username, ""TelegramId"" = @TelegramId, ""Email"" = @Email, ""Level"" = @Level, ""UpdatedAt"" = @UpdatedAt, ""EnableGeneralNotifications"" = @EnableGeneralNotifications, ""EnableVipSignalNotifications"" = @EnableVipSignalNotifications, ""EnableRssNewsNotifications"" = @EnableRssNewsNotifications, ""PreferredLanguage"" = @PreferredLanguage WHERE ""Id"" = @Id;";
                    UpsertWalletSql = @"INSERT INTO public.""TokenWallets"" (""Id"", ""UserId"", ""Balance"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"") VALUES (@Id, @UserId, @Balance, @IsActive, @CreatedAt, @UpdatedAt) ON CONFLICT (""UserId"") DO UPDATE SET ""Balance"" = EXCLUDED.""Balance"", ""IsActive"" = EXCLUDED.""IsActive"", ""UpdatedAt"" = EXCLUDED.""UpdatedAt"";";
                    DeleteUserSql = @"DELETE FROM public.""Users"" WHERE ""Id"" = @Id;";
                    CheckUserExistsSql = @"SELECT 1 FROM public.""Users"" WHERE ""Id"" = @Id LIMIT 1;";
                    NewsNotificationInitialWhereClause = @"WHERE u.""EnableRssNewsNotifications"" = true";
                    GetUsersForNewsNotificationBase = @"
                        SELECT
                            u.""Id"", u.""Username"", u.""TelegramId"", u.""Email"", u.""Level"", u.""CreatedAt"", u.""UpdatedAt"",
                            u.""EnableGeneralNotifications"", u.""EnableVipSignalNotifications"", u.""EnableRssNewsNotifications"", u.""PreferredLanguage"",
                            tw.""Id"" AS ""TokenWallet_Id"", tw.""Balance"" AS ""TokenWallet_Balance"", tw.""IsActive"" AS ""TokenWallet_IsActive"",
                            s.""Id"" AS ""Subscription_Id"", s.""StartDate"" AS ""Subscription_StartDate"", s.""EndDate"" AS ""Subscription_EndDate"", s.""Status"" AS ""Subscription_Status"",
                            usp.""Id"" AS ""Preference_Id"", usp.""CategoryId"" AS ""Preference_CategoryId""
                        FROM public.""Users"" u
                        LEFT JOIN public.""TokenWallets"" tw ON u.""Id"" = tw.""UserId""
                        LEFT JOIN public.""Subscriptions"" s ON u.""Id"" = s.""UserId""
                        LEFT JOIN public.""UserSignalPreferences"" usp ON u.""Id"" = usp.""UserId""
                        WHERE u.""EnableRssNewsNotifications"" = true";
                    GetUsersForNewsNotificationBaseSql = GetUsersForNewsNotificationBase;
                    VipSubscriptionCheckClause = @"
                        AND u.""Level"" IN ('Premium', 'Vip') 
                        AND EXISTS (
                            SELECT 1 FROM public.""Subscriptions"" s_sub 
                            WHERE s_sub.""UserId"" = u.""Id"" 
                              AND s_sub.""StartDate"" <= NOW() 
                              AND s_sub.""EndDate"" >= NOW() 
                              AND s_sub.""Status"" = 'Active'
                        )";
                    CategoryPreferenceCheckClause = @"
                        AND (
                            NOT EXISTS (SELECT 1 FROM public.""UserSignalPreferences"" usp_pref WHERE usp_pref.""UserId"" = u.""Id"") 
                            OR EXISTS (
                                SELECT 1 FROM public.""UserSignalPreferences"" usp_pref 
                                WHERE usp_pref.""UserId"" = u.""Id"" AND usp_pref.""CategoryId"" = @NewsItemSignalCategoryId
                            )
                        )";
                    break;
                #endregion

                // ####################################################################################
                // ###                  NEW SQL SERVER IMPLEMENTATION AND EXPANSION                 ###
                // ####################################################################################
                case DatabaseProvider.SqlServer:
                    #region SQL Server: Core User & Related Data Retrieval
                    // --- SQL Server Optimization ---
                    // Using subqueries with `FOR JSON PATH` is the most efficient way to create nested JSON in SQL Server.
                    // `WITHOUT_ARRAY_WRAPPER` is used on the outer subquery to get a single JSON object instead of a single-element array for the one-to-one wallet relationship.
                    // `ISNULL(..., '[]')` ensures that if a user has no related entities, an empty JSON array is returned, which is crucial for client-side deserialization.
                    GetUserWithRelatedDataFragment = @"
                        SELECT
                            u.[Id], u.[Username], u.[TelegramId], u.[Email], u.[Level], u.[CreatedAt], u.[UpdatedAt],
                            u.[EnableGeneralNotifications], u.[EnableVipSignalNotifications], u.[EnableRssNewsNotifications], u.[PreferredLanguage],
                            ISNULL((SELECT tw.* FROM [TokenWallets] AS tw WHERE tw.[UserId] = u.[Id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER), '{}') AS TokenWalletJson,
                            ISNULL((SELECT s.* FROM [Subscriptions] AS s WHERE s.[UserId] = u.[Id] FOR JSON PATH), '[]') AS SubscriptionsJson,
                            ISNULL((SELECT usp.* FROM [UserSignalPreferences] AS usp WHERE usp.[UserId] = u.[Id] FOR JSON PATH), '[]') AS PreferencesJson
                        FROM [Users] AS u";

                    GetByIdSql = $"{GetUserWithRelatedDataFragment} WHERE u.[Id] = @Id;";
                    GetByTelegramIdSql = $"{GetUserWithRelatedDataFragment} WHERE u.[TelegramId] = @TelegramId;";
                    GetByEmailSql = $"{GetUserWithRelatedDataFragment} WHERE LOWER(u.[Email]) = LOWER(@Email);";
                    GetUserIdByEmailSql = "SELECT TOP (1) [Id] FROM [Users] WHERE LOWER([Email]) = LOWER(@Email);";
                    #endregion

                    #region SQL Server: User Creation, Modification, and Deletion
                    AddUserSql = "INSERT INTO [Users] ([Id], [Username], [TelegramId], [Email], [Level], [CreatedAt], [UpdatedAt], [EnableGeneralNotifications], [EnableVipSignalNotifications], [EnableRssNewsNotifications], [PreferredLanguage]) OUTPUT INSERTED.[Id] VALUES (@UserId, @Username, @TelegramId, @Email, @Level, GETUTCDATE(), GETUTCDATE(), @EnableGeneralNotifications, @EnableVipSignalNotifications, @EnableRssNewsNotifications, @PreferredLanguage);";
                    UpdateUserSql = "UPDATE [Users] SET [Username] = @Username, [TelegramId] = @TelegramId, [Email] = @Email, [Level] = @Level, [UpdatedAt] = GETUTCDATE(), [EnableGeneralNotifications] = @EnableGeneralNotifications, [EnableVipSignalNotifications] = @EnableVipSignalNotifications, [EnableRssNewsNotifications] = @EnableRssNewsNotifications, [PreferredLanguage] = @PreferredLanguage WHERE [Id] = @Id;";
                    DeleteUserSql = "DELETE FROM [Users] WHERE [Id] = @Id;";
                    #endregion

                    #region SQL Server: Existence Checks
                    // --- SQL Server Optimization ---
                    // Using `COUNT_BIG(1)` is standard and safe for Dapper scalar mapping.
                    // `WITH (NOLOCK)` hint is used for read-only checks on high-traffic tables to reduce locking, at the cost of potentially reading uncommitted data (dirty reads), which is acceptable for a simple existence check.
                    // An index on the checked column (Id, Email, TelegramId) is critical for performance.
                    CheckUserExistsSql = "SELECT COUNT_BIG(1) FROM [Users] WITH (NOLOCK) WHERE [Id] = @Id;";
                    CheckExistsByEmailSql = "SELECT COUNT_BIG(1) FROM [Users] WITH (NOLOCK) WHERE LOWER([Email]) = LOWER(@Email);";
                    CheckExistsByTelegramIdSql = "SELECT COUNT_BIG(1) FROM [Users] WITH (NOLOCK) WHERE [TelegramId] = @TelegramId;";
                    #endregion

                    #region SQL Server: Wallet Management
                    AddWalletSql = "INSERT INTO [TokenWallets] ([Id], [UserId], [Balance], [IsActive], [CreatedAt], [UpdatedAt]) VALUES (@TokenWalletId, @UserId, @Balance, @IsActive, GETUTCDATE(), GETUTCDATE());";

                    // --- SQL Server Optimization ---
                    // The `MERGE` statement is the atomic and performant way to handle UPSERT logic in T-SQL.
                    // It avoids race conditions of separate `IF EXISTS... UPDATE ELSE INSERT` logic.
                    UpsertWalletSql = @"
                        MERGE INTO [TokenWallets] AS T
                        USING (SELECT @Id AS Id, @UserId AS UserId, @Balance AS Balance, @IsActive AS IsActive, @CreatedAt AS CreatedAt, GETUTCDATE() AS UpdatedAt) AS S
                        ON T.[UserId] = S.[UserId]
                        WHEN MATCHED THEN
                            UPDATE SET
                                T.[Balance] = S.[Balance],
                                T.[IsActive] = S.[IsActive],
                                T.[UpdatedAt] = S.[UpdatedAt]
                        WHEN NOT MATCHED BY TARGET THEN
                            INSERT ([Id], [UserId], [Balance], [IsActive], [CreatedAt], [UpdatedAt])
                            VALUES (S.[Id], S.[UserId], S.[Balance], S.[IsActive], S.[CreatedAt], S.[UpdatedAt]);";

                    AdjustUserWalletBalanceSql = "UPDATE [TokenWallets] SET [Balance] = [Balance] + @AdjustmentAmount, [UpdatedAt] = GETUTCDATE() WHERE [UserId] = @UserId AND [Balance] + @AdjustmentAmount >= 0;";
                    GetWalletsWithLowBalanceSql = "SELECT * FROM [TokenWallets] WITH (NOLOCK) WHERE [IsActive] = 1 AND [Balance] < @Threshold;";
                    #endregion

                    #region SQL Server: Subscription Management
                    GetActiveSubscriptionsForUserSql = "SELECT * FROM [Subscriptions] WITH (NOLOCK) WHERE [UserId] = @UserId AND [Status] = 'Active' AND [EndDate] >= GETUTCDATE();";

                    GetUsersWithExpiringSubscriptionsSql = @"
                        SELECT u.*
                        FROM [Users] AS u
                        INNER JOIN [Subscriptions] AS s ON u.[Id] = s.[UserId]
                        WHERE s.[Status] = 'Active'
                          AND s.[EndDate] BETWEEN GETUTCDATE() AND DATEADD(day, @DaysUntilExpiry, GETUTCDATE());";

                    BulkUpdateExpiredSubscriptionsSql = "UPDATE [Subscriptions] SET [Status] = 'Expired', [UpdatedAt] = GETUTCDATE() WHERE [Status] = 'Active' AND [EndDate] < GETUTCDATE();";
                    #endregion

                    #region SQL Server: Preference Management
                    // --- SQL Server Optimization ---
                    // This uses a transaction with DELETE followed by a bulk INSERT from a Table-Valued Parameter (TVP).
                    // This is vastly more performant than inserting rows one by one in a loop.
                    // The C# code must define a TVP type `dbo.UserPreferenceType` as `(CategoryId UNIQUEIDENTIFIER)`.
                    SetUserPreferencesSql = @"
                        -- This should be executed within a transaction from the C# repository layer.
                        DELETE FROM [UserSignalPreferences] WHERE [UserId] = @UserId;
                        INSERT INTO [UserSignalPreferences] ([Id], [UserId], [CategoryId], [CreatedAt])
                        SELECT NEWID(), @UserId, tvp.CategoryId, GETUTCDATE()
                        FROM @UserPreferencesTVP AS tvp;";
                    #endregion

                    #region SQL Server: News Notification Queries
                    NewsNotificationInitialWhereClause = "WHERE u.[EnableRssNewsNotifications] = 1";
                    VipSubscriptionCheckClause = @"AND u.[Level] IN ('Premium', 'Vip') AND EXISTS (SELECT 1 FROM [Subscriptions] s_sub WITH (NOLOCK) WHERE s_sub.[UserId] = u.[Id] AND s_sub.[StartDate] <= GETUTCDATE() AND s_sub.[EndDate] >= GETUTCDATE() AND s_sub.[Status] = 'Active')";
                    CategoryPreferenceCheckClause = @"AND (NOT EXISTS (SELECT 1 FROM [UserSignalPreferences] usp_pref WITH (NOLOCK) WHERE usp_pref.[UserId] = u.[Id]) OR EXISTS (SELECT 1 FROM [UserSignalPreferences] usp_pref WITH (NOLOCK) WHERE usp_pref.[UserId] = u.[Id] AND usp_pref.[CategoryId] = @NewsItemSignalCategoryId))";

                    // The T-SQL equivalent for the old, less performant base query properties.
                    // Kept for backward compatibility as requested.
                    GetUsersForNewsNotificationBase = @"
                        SELECT
                            u.[Id], u.[Username], u.[TelegramId], u.[Email], u.[Level], u.[CreatedAt], u.[UpdatedAt],
                            u.[EnableGeneralNotifications], u.[EnableVipSignalNotifications], u.[EnableRssNewsNotifications], u.[PreferredLanguage],
                            tw.[Id] AS [TokenWallet_Id], tw.[Balance] AS [TokenWallet_Balance], tw.[IsActive] AS [TokenWallet_IsActive],
                            s.[Id] AS [Subscription_Id], s.[StartDate] AS [Subscription_StartDate], s.[EndDate] AS [Subscription_EndDate], s.[Status] AS [Subscription_Status],
                            usp.[Id] AS [Preference_Id], usp.[CategoryId] AS [Preference_CategoryId]
                        FROM [Users] u WITH (NOLOCK)
                        LEFT JOIN [TokenWallets] tw WITH (NOLOCK) ON u.[Id] = tw.[UserId]
                        LEFT JOIN [Subscriptions] s WITH (NOLOCK) ON u.[Id] = s.[UserId]
                        LEFT JOIN [UserSignalPreferences] usp WITH (NOLOCK) ON u.[Id] = usp.[UserId]
                        WHERE u.[EnableRssNewsNotifications] = 1";
                    GetUsersForNewsNotificationBaseSql = GetUsersForNewsNotificationBase;
                    #endregion

                    #region SQL Server: Batch Operations
                    GetAllUsersSql = "SELECT [Id], [Username], [TelegramId], [Email], [Level], [CreatedAt], [UpdatedAt], [EnableGeneralNotifications], [EnableVipSignalNotifications], [EnableRssNewsNotifications], [PreferredLanguage] FROM [Users] WITH (NOLOCK) ORDER BY [Username];";
                    GetWalletsForUsersSql = "SELECT * FROM [TokenWallets] WITH (NOLOCK) WHERE [UserId] IN @UserIds;";
                    GetSubscriptionsForUsersSql = "SELECT * FROM [Subscriptions] WITH (NOLOCK) WHERE [UserId] IN @UserIds;";
                    GetPreferencesForUsersSql = "SELECT * FROM [UserSignalPreferences] WITH (NOLOCK) WHERE [UserId] IN @UserIds;";

                    // --- SQL Server Optimization ---
                    // Using a MERGE statement with a Table-Valued Parameter is the most efficient way to perform bulk updates.
                    // The C# code must define a TVP type `dbo.UserLevelUpdateType` as `(Id UNIQUEIDENTIFIER, Level NVARCHAR(50))`.
                    BulkUpdateUserLevelsSql = @"
                        MERGE INTO [Users] AS T
                        USING @UserLevelUpdatesTVP AS S
                        ON T.[Id] = S.[Id]
                        WHEN MATCHED THEN
                            UPDATE SET
                                T.[Level] = S.[Level],
                                T.[UpdatedAt] = GETUTCDATE();";
                    #endregion

                    #region SQL Server: Auditing & Logging
                    // Assumes an audit table: CREATE TABLE UserAuditLogs (LogId BIGINT IDENTITY PRIMARY KEY, UserId UNIQUEIDENTIFIER, ChangedByUserId UNIQUEIDENTIFIER, ChangeTimestamp DATETIME2(7) DEFAULT(GETUTCDATE()), OldValuesJson NVARCHAR(MAX), NewValuesJson NVARCHAR(MAX));
                    LogUserUpdateAuditSql = "INSERT INTO [UserAuditLogs] ([UserId], [ChangedByUserId], [OldValuesJson], [NewValuesJson]) VALUES (@UserId, @ChangedByUserId, @OldValuesJson, @NewValuesJson);";
                    #endregion

                    #region SQL Server: Reporting & Analytics
                    GetUserCountByLevelSql = "SELECT [Level], COUNT_BIG(*) AS UserCount FROM [Users] WITH (NOLOCK) GROUP BY [Level] ORDER BY UserCount DESC;";

                    // This query uses DATETRUNC (SQL Server 2022+) for modern, clean date grouping.
                    // A comment suggests a fallback for older versions.
                    // Recommended Index: CREATE NONCLUSTERED INDEX IX_Users_CreatedAt ON [Users]([CreatedAt]);
                    GetNewUserSignupsByPeriodSql = @"
                        -- For SQL Server 2017 and older, replace DATETRUNC(period, [CreatedAt]) with appropriate DATEADD/DATEDIFF or FORMAT functions.
                        SELECT
                            DATETRUNC(@Period, [CreatedAt]) AS PeriodStart,
                            COUNT_BIG(*) AS NewUserCount
                        FROM [Users]
                        WHERE [CreatedAt] >= @StartDate AND [CreatedAt] < @EndDate
                        GROUP BY DATETRUNC(@Period, [CreatedAt])
                        ORDER BY PeriodStart;";

                    // --- SQL Server Optimization ---
                    // This advanced query uses Common Table Expressions (CTEs) to pre-aggregate data before joining.
                    // This is much more efficient than joining large tables first.
                    // It uses window functions like `ROW_NUMBER()` to get the latest subscription without a self-join.
                    GetUserEngagementSummarySql = @"
                        WITH LatestSubscription AS (
                            SELECT
                                s.[UserId],
                                s.[Status] AS LastSubscriptionStatus,
                                s.[EndDate] AS LastSubscriptionEndDate,
                                ROW_NUMBER() OVER(PARTITION BY s.[UserId] ORDER BY s.[EndDate] DESC, s.[CreatedAt] DESC) AS rn
                            FROM [Subscriptions] s WITH (NOLOCK)
                        ),
                        TransactionCounts AS (
                            SELECT
                                t.[UserId],
                                COUNT_BIG(*) AS TotalTransactionCount
                            FROM [Transactions] t WITH (NOLOCK)
                            GROUP BY t.[UserId]
                        )
                        SELECT
                            u.[Id],
                            u.[Username],
                            u.[Email],
                            u.[Level],
                            u.[CreatedAt],
                            ls.LastSubscriptionStatus,
                            ls.LastSubscriptionEndDate,
                            ISNULL(tc.TotalTransactionCount, 0) AS TotalTransactionCount
                        FROM [Users] AS u WITH (NOLOCK)
                        LEFT JOIN LatestSubscription AS ls ON u.[Id] = ls.[UserId] AND ls.rn = 1
                        LEFT JOIN TransactionCounts AS tc ON u.[Id] = tc.[UserId]
                        WHERE u.[Id] = @UserId;";
                    #endregion

                    break;

                default:
                    throw new NotSupportedException($"The configured database provider is not supported by the UserSqlProvider.");
            }
        }
    }
}