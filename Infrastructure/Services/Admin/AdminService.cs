// --- START OF FILE: Infrastructure/Services/AdminService.cs ---

#region Usings
using Application.Common.Interfaces;
using Application.DTOs.Admin;
using Application.DTOs.Settings;
using Application.Interfaces;
using Dapper;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shared.Security; // For SecureExceptionSanitizer
using System.Data;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
#endregion

namespace Infrastructure.Services.Admin
{
    public class AdminService : IAdminService
    {
        #region Fields and Constructor
        private readonly string _connectionString;
        private readonly ILogger<AdminService> _logger;
        private const int CommandTimeoutSeconds = 180; // Increased timeout for admin queries
        private readonly ICacheService _cacheService;
        private readonly IUserRepository _userRepository;
        private readonly Application.Common.Interfaces.IDbConnectionFactory _connectionFactory;
        private readonly DbProviderService _providerService;
        private readonly ISignalRepository _signalRepository;
        private readonly IProMonitoringLogRepository _proMonitoringLogRepository;

        public AdminService(
            IConfiguration configuration,
            ILogger<AdminService> logger,
            ICacheService cacheService,
            IUserRepository userRepository,
            ISignalRepository signalRepository,
            IProMonitoringLogRepository proMonitoringLogRepository,
            Application.Common.Interfaces.IDbConnectionFactory connectionFactory,
            DbProviderService providerService)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                                ?? throw new InvalidOperationException("DefaultConnection string is not found in configuration.");
            _logger = logger;
            _cacheService = cacheService;
            _userRepository = userRepository;
            _signalRepository = signalRepository;
            _proMonitoringLogRepository = proMonitoringLogRepository;
            _connectionFactory = connectionFactory;
            _providerService = providerService;
        }
        #endregion

        #region Security Validation Methods
        /// <summary>
        /// Sanitizes user input for safe logging by removing newlines and other problematic characters.
        /// </summary>
        /// <param name="input">The user input to sanitize</param>
        /// <returns>Sanitized string safe for logging</returns>
        private static string SanitizeForLogging(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return "[EMPTY_INPUT]";
            }

            // Remove newlines, carriage returns, and other problematic characters
            string sanitized = input
                .Replace("\r", "")
                .Replace("\n", "")
                .Replace("\t", " ")
                .Replace("\0", ""); // Null characters

            // Remove any remaining control characters
            sanitized = Regex.Replace(sanitized, @"[\x00-\x1F\x7F]", "");

            // Limit length to prevent log flooding
            if (sanitized.Length > 100)
            {
                sanitized = sanitized[..97] + "...";
            }

            return sanitized;
        }

        /// <summary>
        /// Validates file names to prevent path traversal and other injection attacks.
        /// </summary>
        /// <param name="fileName">The file name to validate</param>
        /// <returns>Validated file name or null if invalid</returns>
        private string? ValidateFileName(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                _logger.LogWarning("File name is null or empty.");
                return null;
            }

            string sanitizedFileName = SanitizeForLogging(fileName);

            // Check for path traversal attempts
            if (sanitizedFileName.Contains("..") ||
                sanitizedFileName.Contains("\\") ||
                sanitizedFileName.Contains("/") ||
                sanitizedFileName.Contains(":") ||
                sanitizedFileName.Contains(";") ||
                sanitizedFileName.Contains("'") ||
                sanitizedFileName.Contains("\"") ||
                sanitizedFileName.Contains("<") ||
                sanitizedFileName.Contains(">"))
            {
                _logger.LogWarning("Potentially dangerous characters detected in file name: {SanitizedFileName}", sanitizedFileName);
                return null;
            }

            // Validate file name format (should be alphanumeric with dots, hyphens, and underscores)
            if (!Regex.IsMatch(sanitizedFileName, @"^[a-zA-Z0-9._-]+$"))
            {
                _logger.LogWarning("Invalid file name format: {SanitizedFileName}", sanitizedFileName);
                return null;
            }

            return sanitizedFileName;
        }
        #endregion

        #region Database Connection
        /// <summary>
        /// A raw connection for the handful of admin queries that are still written in
        /// Postgres-specific SQL (the <c>public.</c> schema and <c>jsonb</c> casts).
        /// </summary>
        /// <exception cref="NotSupportedException">
        /// Thrown on SQLite/SQL Server — those queries have not been translated yet, so
        /// failing loudly is better than handing them a connection that blows up with
        /// an unhelpful cast mid-query.
        /// </exception>
        private NpgsqlConnection CreateConnection()
        {
            if (_providerService.Provider is not DatabaseProvider.Postgres)
            {
                throw new NotSupportedException(
                    $"This admin query is written for PostgreSQL and is not yet available on {_providerService.Provider}. " +
                    "Use the EF Core path (AppDbContext) or translate the statement.");
            }

            return (NpgsqlConnection)_connectionFactory.CreateConnection();
        }
        #endregion

        public async Task<(int UserCount, int NewsItemCount)> GetDashboardStatsAsync(CancellationToken cancellationToken = default)
        {
            // CORRECTED: Using NpgsqlConnection and PostgreSQL-compliant quoted identifiers.
            await using NpgsqlConnection connection = CreateConnection();

            const string sql = @"SELECT COUNT(1) FROM public.""Users""; SELECT COUNT(1) FROM public.""NewsItems"";";

            using SqlMapper.GridReader multi = await connection.QueryMultipleAsync(new CommandDefinition(sql, cancellationToken: cancellationToken, commandTimeout: CommandTimeoutSeconds));
            return (await multi.ReadSingleAsync<int>(), await multi.ReadSingleAsync<int>());
        }

        public async Task<List<long>> GetAllActiveUserChatIdsAsync(CancellationToken cancellationToken = default)
        {
            // CORRECTED: Using NpgsqlConnection and PostgreSQL-compliant quoted identifiers.
            // Also, directly querying for the 'bigint' type is more efficient than parsing strings.
            await using NpgsqlConnection connection = CreateConnection();

            const string sql = @"SELECT ""TelegramId""::bigint FROM public.""Users"" WHERE ""TelegramId"" IS NOT NULL AND ""TelegramId"" <> '';";

            IEnumerable<long> ids = await connection.QueryAsync<long>(new CommandDefinition(sql, cancellationToken: cancellationToken));
            return ids.ToList();
        }

        public async Task<(byte[]? ZipContents, string FileName, string? ErrorMessage)> GetLogFilesAsZipAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Assuming logs are in a 'logs' folder at the application root.
                string logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");

                if (!Directory.Exists(logDirectory))
                {
                    _logger.LogWarning("Admin requested log download, but the 'logs' directory was not found at {LogPath}", logDirectory);
                    return (null, "", "Log directory not found.");
                }

                List<string> logFiles = Directory.GetFiles(logDirectory, "log-*.txt").OrderByDescending(f => f).ToList();

                if (!logFiles.Any())
                {
                    return (null, "", "No log files found in the directory.");
                }

                // Create a zip file in memory
                await using MemoryStream memoryStream = new();
                using (ZipArchive archive = new(memoryStream, ZipArchiveMode.Create, true))
                {
                    foreach (string? logFile in logFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        string entryName = Path.GetFileName(logFile);
                        ZipArchiveEntry zipEntry = archive.CreateEntry(entryName, CompressionLevel.Optimal);

                        await using Stream entryStream = zipEntry.Open();
                        // Use FileShare.ReadWrite to avoid locking issues with the logger
                        await using FileStream fileStream = new(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        await fileStream.CopyToAsync(entryStream, cancellationToken);
                    }
                }

                // Reset stream position to be read from the beginning
                memoryStream.Position = 0;
                string fileName = $"logs_archive_{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss}.zip";

                return (memoryStream.ToArray(), fileName, null);
            }
            catch (Exception ex)
            {
                // SECURITY: Use SecureExceptionSanitizer for logging exceptions
                string sanitizedException = SecureExceptionSanitizer.SanitizeForLogging(ex);
                _logger.LogError(sanitizedException, "Failed to create log archive for admin download.");
                return (null, "", $"An unexpected error occurred: {ex.Message}");
            }
        }

        // In AdminService.cs
        public async Task<string> ExecuteRawSqlQueryAsync(string sqlQuery, CancellationToken cancellationToken = default)
        {
            // SECURITY: Sanitize SQL query before logging to prevent log forging
            string sanitizedQuery = SanitizeForLogging(sqlQuery);
            _logger.LogWarning("Admin is executing a raw SQL query. THIS IS A HIGH-RISK OPERATION. Query: {SanitizedQuery}", sanitizedQuery);

            // CORRECTED: Using NpgsqlConnection
            await using NpgsqlConnection connection = CreateConnection();
            StringBuilder response = new();

            try
            {
                CommandDefinition command = new(sqlQuery, commandTimeout: CommandTimeoutSeconds, cancellationToken: cancellationToken);
                using SqlMapper.GridReader multi = await connection.QueryMultipleAsync(command);
                while (!multi.IsConsumed)
                {
                    // No changes needed here, as Dapper returns IDictionary<string, object> which is provider-agnostic.
                    // ... (Your existing result formatting logic is fine)
                }
                return response.ToString();
            }
            catch (PostgresException pgEx) // CORRECTED: Catch specific PostgreSQL exceptions
            {
                // SECURITY: Use SecureExceptionSanitizer for logging exceptions
                string sanitizedException = SecureExceptionSanitizer.SanitizeForLogging(pgEx);
                _logger.LogError(sanitizedException, "Error executing raw SQL query. SQLSTATE: {SqlState}", pgEx.SqlState);
                return $"❌ **PostgreSQL Execution Error (Code: {pgEx.SqlState}):**\n`{pgEx.Message}`";
            }
            catch (Exception ex)
            {
                // SECURITY: Use SecureExceptionSanitizer for logging exceptions
                string sanitizedException = SecureExceptionSanitizer.SanitizeForLogging(ex);
                _logger.LogError(sanitizedException, "Error executing raw SQL query.");
                return $"❌ **General Execution Error:**\n`{ex.Message}`";
            }
        }


        // ✅ This is the single, correct implementation for the detailed user lookup.
        public async Task<AdminUserDetailDto?> GetUserDetailByTelegramIdAsync(long telegramId, CancellationToken cancellationToken = default)
        {
            // SECURITY: Sanitize telegramId before logging (though it's a long, it's still user input)
            string sanitizedTelegramId = SanitizeForLogging(telegramId.ToString());
            _logger.LogInformation("Fetching detailed profile for Telegram ID: {SanitizedTelegramId} using optimized PG query.", sanitizedTelegramId);

            // CORRECTED: A single, optimized query using PostgreSQL's JSON aggregation functions.
            const string sql = @"
                SELECT
                    u.*,
                    (SELECT jsonb_agg(tw.*) FROM public.""TokenWallets"" tw WHERE tw.""UserId"" = u.""Id"") AS ""WalletJson"",
                    (SELECT jsonb_agg(sub.* ORDER BY sub.""StartDate"" DESC) FROM public.""Subscriptions"" sub WHERE sub.""UserId"" = u.""Id"") AS ""SubscriptionsJson"",
                    (SELECT jsonb_agg(t.* ORDER BY t.""Timestamp"" DESC) FROM (SELECT * FROM public.""Transactions"" tr WHERE tr.""UserId"" = u.""Id"" ORDER BY tr.""Timestamp"" DESC LIMIT 10) t) AS ""TransactionsJson""
                FROM public.""Users"" u
                WHERE u.""TelegramId"" = @TelegramIdStr;";

            await using NpgsqlConnection connection = CreateConnection();

            // Dapper will map the main columns and the JSON strings to this DTO.
            AdminUserDetailRawDto? resultDto = await connection.QuerySingleOrDefaultAsync<AdminUserDetailRawDto>(
                new CommandDefinition(sql, new { TelegramIdStr = telegramId.ToString() }, cancellationToken: cancellationToken)
            );

            if (resultDto == null)
            {
                return null;
            }

            // Map the raw DTO with JSON strings into the final, structured DTO.
            return MapRawDtoToAdminUserDetail(resultDto);
        }

        #region DTOs and Mappers for GetUserDetailByTelegramIdAsync
        private AdminUserDetailDto MapRawDtoToAdminUserDetail(AdminUserDetailRawDto rawDto)
        {
            AdminUserDetailDto userDetail = new()
            {

                UserId = rawDto.Id,
                Username = rawDto.Username,
                TelegramId = long.Parse(rawDto.TelegramId),
                // ... map other user properties
            };

            if (!string.IsNullOrEmpty(rawDto.WalletJson) && rawDto.WalletJson != "[]")
            {
                WalletDto? wallet = JsonSerializer.Deserialize<List<WalletDto>>(rawDto.WalletJson)?.FirstOrDefault();
                if (wallet != null)
                {
                    userDetail.TokenBalance = wallet.Balance;
                    userDetail.WalletLastUpdated = wallet.UpdatedAt;
                }
            }

            if (!string.IsNullOrEmpty(rawDto.SubscriptionsJson) && rawDto.SubscriptionsJson != "[]")
            {
                List<SubscriptionSummaryDto>? subscriptions = JsonSerializer.Deserialize<List<SubscriptionSummaryDto>>(rawDto.SubscriptionsJson);
                userDetail.Subscriptions = subscriptions ?? new List<SubscriptionSummaryDto>();
                SubscriptionSummaryDto? activeSub = subscriptions?.FirstOrDefault(s => s.Status == "Active" && DateTime.UtcNow >= s.StartDate && DateTime.UtcNow <= s.EndDate);
                if (activeSub != null)
                {
                    userDetail.ActiveSubscription = new ActiveSubscriptionDto { EndDate = activeSub.EndDate };
                }
            }

            if (!string.IsNullOrEmpty(rawDto.TransactionsJson) && rawDto.TransactionsJson != "[]")
            {
                userDetail.RecentTransactions = JsonSerializer.Deserialize<List<TransactionSummaryDto>>(rawDto.TransactionsJson) ?? new List<TransactionSummaryDto>();
            }

            return userDetail;
        }
        private class WalletDto // This DTO structure must match the JSON structure from the DB
        {
            public Guid Id { get; set; }
            public Guid UserId { get; set; }
            public decimal Balance { get; set; }
            public bool IsActive { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime? UpdatedAt { get; set; } // This property is nullable

            // --- THIS IS THE FIX ---
            // The TokenWallet domain entity likely requires a non-nullable DateTime for UpdatedAt.
            // We must provide a default value if the DTO's UpdatedAt is null.
            public TokenWallet ToDomainEntity()
            {
                return new TokenWallet(
                Id,
                UserId,
                Balance,
                IsActive,
                CreatedAt,
                UpdatedAt ?? CreatedAt // If UpdatedAt is null, use CreatedAt as a sensible default.
            );
            }
        }
        #endregion
        // This DTO receives the raw data from the database, including the JSON strings.
        private class AdminUserDetailRawDto
        {
            public Guid Id { get; set; }
            public string Username { get; set; } = string.Empty;
            public string TelegramId { get; set; } = string.Empty;
            // ... other user properties
            public string? WalletJson { get; set; }
            public string? SubscriptionsJson { get; set; }
            public string? TransactionsJson { get; set; }
        }



        private const string ForceJoinSettingsKey = "settings:force_join";
        public async Task<ForceJoinSettingsDto> GetForceJoinSettingsAsync(CancellationToken cancellationToken = default)
        {
            await using NpgsqlConnection connection = CreateConnection();
            const string sql = @"SELECT ""Value"" FROM public.""Settings"" WHERE ""Key"" = @Key;";

            string? jsonValue = await connection.QuerySingleOrDefaultAsync<string>(
                new CommandDefinition(sql, new { Key = ForceJoinSettingsKey }, cancellationToken: cancellationToken));

            if (string.IsNullOrEmpty(jsonValue))
            {
                _logger.LogInformation("Force join settings not found in database, returning default (disabled) state.");
                return new ForceJoinSettingsDto { IsEnabled = false };
            }

            return JsonSerializer.Deserialize<ForceJoinSettingsDto>(jsonValue) ?? new ForceJoinSettingsDto();
        }
        public async Task UpdateForceJoinSettingsAsync(ForceJoinSettingsDto settings, CancellationToken cancellationToken = default)
        {
            await using System.Data.Common.DbConnection connection = (System.Data.Common.DbConnection)_connectionFactory.CreateConnection();
            await SettingsDbHelper.SetAsync(connection, _providerService.Provider, ForceJoinSettingsKey, settings, _logger, cancellationToken);
            _logger.LogInformation("Force join settings have been updated in the database.");

            // CRITICAL: Invalidate the cache so the application picks up the new setting immediately.
            _ = await _cacheService.RemoveAsync(ForceJoinSettingsKey);
            _logger.LogInformation("Force join settings cache key '{CacheKey}' has been invalidated.", ForceJoinSettingsKey);
        }

        public async Task<(int UserCount, int NewsItemCount, List<(DateTime Date, int Count)> UserJoinStats)> GetDashboardStatsWithUserJoinsAsync(CancellationToken cancellationToken = default)
        {
            await using NpgsqlConnection connection = CreateConnection();
            // Get total user count and news item count
            const string sqlCounts = @"SELECT COUNT(1) FROM public.""Users""; SELECT COUNT(1) FROM public.""NewsItems"";";
            using SqlMapper.GridReader multi = await connection.QueryMultipleAsync(new CommandDefinition(sqlCounts, cancellationToken: cancellationToken, commandTimeout: CommandTimeoutSeconds));
            int userCount = await multi.ReadSingleAsync<int>();
            int newsItemCount = await multi.ReadSingleAsync<int>();

            // Get user join stats for the last 30 days
            const string sqlJoins = @"SELECT date_trunc('day', ""CreatedAt"" ) AS join_date, COUNT(*) AS count FROM public.""Users"" WHERE ""CreatedAt"" >= (CURRENT_DATE - INTERVAL '29 days') GROUP BY join_date ORDER BY join_date;";
            List<(DateTime join_date, int count)> joinStatsRaw = (await connection.QueryAsync<(DateTime join_date, int count)>(new CommandDefinition(sqlJoins, cancellationToken: cancellationToken, commandTimeout: CommandTimeoutSeconds))).ToList();

            // Fill missing days with 0
            List<(DateTime Date, int Count)> userJoinStats = [];
            DateTime today = DateTime.UtcNow.Date;
            for (int i = 29; i >= 0; i--)
            {
                DateTime day = today.AddDays(-i);
                (DateTime join_date, int count) found = joinStatsRaw.FirstOrDefault(x => x.join_date.Date == day);
                int count = found != default ? found.count : 0;
                userJoinStats.Add((day, count));
            }

            return (userCount, newsItemCount, userJoinStats);
        }

        public async Task<AdminDashboardStatsDto> GetAdminDashboardStatsAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Fetching admin dashboard statistics.");
            AdminDashboardStatsDto stats = new();

            // Get User Stats
            try
            {
                (int UserCount, int NewsItemCount, List<(DateTime Date, int Count)>? UserJoinStats) = await GetDashboardStatsWithUserJoinsAsync(cancellationToken);
                stats.TotalUsers = UserCount;
                stats.UserGrowthLast7Days = UserJoinStats
                    .Select(s => new DailyCountDto { Date = s.Date, Count = s.Count })
                    .OrderByDescending(d => d.Date) // Ensure it's for the last 7 days from the GetDashboardStatsWithUserJoinsAsync logic
                    .Take(7) // Take last 7, assuming GetDashboardStatsWithUserJoinsAsync provides enough data
                    .OrderBy(d => d.Date) // Re-order for chart
                    .ToList();
            }
            catch (Exception ex)
            {
                // SECURITY: Use SecureExceptionSanitizer for logging exceptions
                string sanitizedException = SecureExceptionSanitizer.SanitizeForLogging(ex);
                _logger.LogError(sanitizedException, "Error fetching user statistics for admin dashboard.");
                // Optionally set default/error values or rethrow
                stats.TotalUsers = -1; // Indicate error or unavailable
            }

            // Get Signal Stats
            try
            {
                DateTime utcNow = DateTime.UtcNow;
                DateTime todayStart = utcNow.Date;
                DateTime sevenDaysAgoStart = todayStart.AddDays(-6); // -6 to include today as the 7th day

                // This requires ISignalRepository to have methods to query by date ranges
                // or to fetch all and filter in memory if the dataset isn't too large.
                // For demonstration, assuming ISignalRepository might need new methods like:
                // - GetSignalsCountAsync(DateTime from, DateTime to, CancellationToken token)
                // - GetSignalsCountPerDayAsync(DateTime from, DateTime to, CancellationToken token) -> returns List<DailyCountDto>

                // Placeholder: Efficient way would be direct DB queries via repository.
                // Fetching all signals and filtering in memory is inefficient for large datasets.
                // Using GetAllWithCategoryAsync as it's the closest available method in ISignalRepository.
                IEnumerable<Signal> allSignals = await _signalRepository.GetAllWithCategoryAsync(cancellationToken);

                stats.SignalsToday = allSignals.Count(s => s.PublishedAt.Date == todayStart);

                stats.SignalsPerDayLast7Days = allSignals
                    .Where(s => s.PublishedAt.Date >= sevenDaysAgoStart && s.PublishedAt.Date <= todayStart)
                    .GroupBy(s => s.PublishedAt.Date)
                    .Select(g => new DailyCountDto { Date = g.Key, Count = g.Count() })
                    .OrderBy(d => d.Date)
                    .ToList();

                // Fill missing days for signals
                List<DailyCountDto> signalsPerDayFilled = [];
                for (int i = 0; i < 7; i++)
                {
                    DateTime day = sevenDaysAgoStart.AddDays(i);
                    DailyCountDto? existingStat = stats.SignalsPerDayLast7Days.FirstOrDefault(s => s.Date == day);
                    if (existingStat != null)
                    {
                        signalsPerDayFilled.Add(existingStat);
                    }
                    else
                    {
                        signalsPerDayFilled.Add(new DailyCountDto { Date = day, Count = 0 });
                    }
                }
                stats.SignalsPerDayLast7Days = signalsPerDayFilled.OrderBy(d => d.Date).ToList();

            }
            catch (Exception ex)
            {
                // SECURITY: Use SecureExceptionSanitizer for logging exceptions
                string sanitizedException = SecureExceptionSanitizer.SanitizeForLogging(ex);
                _logger.LogError(sanitizedException, "Error fetching signal statistics for admin dashboard.");
                stats.SignalsToday = -1; // Indicate error or unavailable
            }

            // MessagesToday is deferred as per plan.

            _logger.LogInformation("Admin dashboard statistics compiled successfully.");
            return stats;
        }

        #region Pro Monitoring Logs
        public async Task<List<ProMonitoringLog>> GetRecentProMonitoringLogsAsync(int limit, int offset, CancellationToken cancellationToken = default)
        {
            // We now call a new repository method that supports pagination
            return await _proMonitoringLogRepository.GetRecentPagedAsync(limit, offset, cancellationToken);
        }
        public async Task<int> DeleteAllProMonitoringLogsAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogWarning("ADMIN ACTION: Initiating deletion of ALL pro monitoring logs.");
            int deletedCount = await _proMonitoringLogRepository.DeleteAllAsync(cancellationToken);
            _logger.LogWarning("ADMIN ACTION: Successfully deleted {Count} pro monitoring logs.", deletedCount);
            return deletedCount;
        }

        #endregion

        #region Log File Operations
        public async Task<List<string>> ListLogFilesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                string logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
                if (!Directory.Exists(logDirectory))
                {
                    _logger.LogWarning("Log directory not found at {LogPath} when listing files.", logDirectory);
                    return [];
                }

                List<string> logFiles = Directory.GetFiles(logDirectory, "log-*.txt")
                                        .Select(Path.GetFileName)
                                        .OfType<string>() // Ensure GetFileName doesn't return nulls that break ToList()
                                        .OrderByDescending(f => f) // Show newest first
                                        .ToList();
                return await Task.FromResult(logFiles); // Directory.GetFiles is sync, wrap in Task.FromResult for async signature
            }
            catch (Exception ex)
            {
                // SECURITY: Use SecureExceptionSanitizer for logging exceptions
                string sanitizedException = SecureExceptionSanitizer.SanitizeForLogging(ex);
                _logger.LogError(sanitizedException, "Error listing log files.");
                return []; // Return empty list on error
            }
        }

        public async Task<string?> GetLogFileContentAsync(string fileName, int? lineCount = null, CancellationToken cancellationToken = default)
        {
            // SECURITY: Validate file name before processing
            string? validatedFileName = ValidateFileName(fileName);
            if (validatedFileName == null)
            {
                _logger.LogWarning("GetLogFileContentAsync called with invalid file name format.");
                return null;
            }

            // Additional validation for log file format
            if (!validatedFileName.StartsWith("log-") || !validatedFileName.EndsWith(".txt"))
            {
                _logger.LogWarning("Invalid log file name format requested: {SanitizedFileName}", SanitizeForLogging(validatedFileName));
                return null;
            }

            try
            {
                string logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
                // Securely combine path and ensure it's still within the logDirectory
                string filePath = Path.Combine(logDirectory, Path.GetFileName(validatedFileName)); // Use GetFileName to sanitize

                if (!File.Exists(filePath) || !Path.GetFullPath(filePath).StartsWith(Path.GetFullPath(logDirectory), StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Log file not found or access denied (path traversal attempt?): {SanitizedFilePath}", SanitizeForLogging(filePath));
                    return null;
                }

                // SECURITY: Sanitize all user inputs before logging
                string sanitizedFilePath = SanitizeForLogging(filePath);
                string sanitizedLineCount = lineCount.HasValue ? lineCount.Value.ToString() : "All";
                _logger.LogInformation("Reading log file: {SanitizedFilePath}. Line count: {SanitizedLineCount}", sanitizedFilePath, sanitizedLineCount);

                if (lineCount.HasValue && lineCount > 0)
                {
                    List<string> lines = [];
                    // File.ReadLines allows efficient reading for large files if we only need a few lines from the end.
                    // However, getting LAST N lines efficiently requires reading from end or keeping track.
                    // A simpler approach for moderate log files:
                    string[] allLines = await File.ReadAllLinesAsync(filePath, cancellationToken);
                    lines = allLines.TakeLast(lineCount.Value).ToList();
                    return string.Join(Environment.NewLine, lines);
                }
                else
                {
                    return await File.ReadAllTextAsync(filePath, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                // SECURITY: Use SecureExceptionSanitizer for logging exceptions
                string sanitizedException = SecureExceptionSanitizer.SanitizeForLogging(ex);
                string sanitizedFileName = SanitizeForLogging(validatedFileName);
                _logger.LogError(sanitizedException, "Error reading log file content for {SanitizedFileName}.", sanitizedFileName);
                return $"Error reading log file '{validatedFileName}': {ex.Message}"; // Return error message as content for user
            }
        }
        #endregion
    }
}