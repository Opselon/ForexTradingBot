// --- START OF FILE: Infrastructure/Services/AdminService.cs ---

using Application.DTOs.Admin;
using Application.Interfaces;
using Dapper;
using Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Infrastructure.Services
{
    public class AdminService : IAdminService
    {
        private readonly string _connectionString;
        private readonly ILogger<AdminService> _logger;

        public AdminService(IConfiguration configuration, ILogger<AdminService> logger)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")!;
            _logger = logger;
        }

        public async Task<(int UserCount, int NewsItemCount)> GetDashboardStatsAsync(CancellationToken cancellationToken = default)
        {
            await using var connection = new SqlConnection(_connectionString);
            var sql = "SELECT COUNT(1) FROM dbo.Users; SELECT COUNT(1) FROM dbo.NewsItems;";
            using var multi = await connection.QueryMultipleAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
            return (await multi.ReadSingleAsync<int>(), await multi.ReadSingleAsync<int>());
        }

        public async Task<List<long>> GetAllActiveUserChatIdsAsync(CancellationToken cancellationToken = default)
        {
            await using var connection = new SqlConnection(_connectionString);
            var sql = "SELECT TelegramId FROM dbo.Users WHERE TelegramId IS NOT NULL AND TelegramId <> '';";
            var idsAsString = await connection.QueryAsync<string>(new CommandDefinition(sql, cancellationToken: cancellationToken));

            var userChatIds = new List<long>();
            foreach (var idStr in idsAsString)
            {
                if (long.TryParse(idStr, out var id)) userChatIds.Add(id);
                else _logger.LogWarning("Could not parse TelegramId '{IdString}' to long.", idStr);
            }
            return userChatIds;
        }




        // In AdminService.cs
        public async Task<string> ExecuteRawSqlQueryAsync(string sqlQuery, CancellationToken cancellationToken = default)
        {
            _logger.LogWarning("Admin is executing a raw SQL query: {Query}", sqlQuery);
            await using var connection = new SqlConnection(_connectionString);
            var response = new StringBuilder();

            try
            {
                var command = new CommandDefinition(sqlQuery, commandTimeout: 60, cancellationToken: cancellationToken);

                // Use QueryMultiple for flexibility, as the query could be anything.
                using var multi = await connection.QueryMultipleAsync(command);

                int resultSetIndex = 1;
                while (!multi.IsConsumed)
                {
                    var grid = await multi.ReadAsync();
                    var data = grid.ToList();

                    if (!data.Any())
                    {
                        response.AppendLine($"-- Result Set {resultSetIndex} (No Rows) --\n");
                        resultSetIndex++;
                        continue;
                    }

                    response.AppendLine($"-- Result Set {resultSetIndex} ({data.Count} Rows) --");
                    // Get headers from the first row (which is an IDictionary<string, object>)
                    var headers = ((IDictionary<string, object>)data.First()).Keys;
                    response.AppendLine("`" + string.Join(" | ", headers) + "`");

                    foreach (var row in data)
                    {
                        var rowDict = (IDictionary<string, object>)row;
                        var values = rowDict.Values.Select(v => v?.ToString() ?? "NULL");
                        response.AppendLine("`" + string.Join(" | ", values) + "`");
                    }
                    response.AppendLine();
                    resultSetIndex++;
                }
                return response.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing raw SQL query.");
                return $"❌ **SQL Execution Error:**\n`{ex.Message}`";
            }
        }



        // ✅ This is the single, correct implementation for the detailed user lookup.
        public async Task<AdminUserDetailDto?> GetUserDetailByTelegramIdAsync(long telegramId, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Fetching detailed profile for Telegram ID: {TelegramId}", telegramId);
            await using var connection = new SqlConnection(_connectionString);

            var sql = @"
                SELECT * FROM dbo.Users WHERE TelegramId = @TelegramIdStr;
                SELECT Balance, UpdatedAt AS WalletLastUpdated FROM dbo.TokenWallets WHERE UserId = (SELECT Id FROM dbo.Users WHERE TelegramId = @TelegramIdStr);
                SELECT Id AS SubscriptionId, StartDate, EndDate, Status FROM dbo.Subscriptions WHERE UserId = (SELECT Id FROM dbo.Users WHERE TelegramId = @TelegramIdStr) ORDER BY StartDate DESC;
                SELECT TOP 10 Id AS TransactionId, Amount, Type, Status, Timestamp FROM dbo.Transactions WHERE UserId = (SELECT Id FROM dbo.Users WHERE TelegramId = @TelegramIdStr) ORDER BY Timestamp DESC;
            ";

            using var multi = await connection.QueryMultipleAsync(sql, new { TelegramIdStr = telegramId.ToString() });

            var user = await multi.ReadSingleOrDefaultAsync<User>();
            if (user == null) return null;

            var userDetail = new AdminUserDetailDto { /* Map all user properties... */ };
            userDetail.UserId = user.Id;
            userDetail.Username = user.Username;
            userDetail.TelegramId = long.Parse(user.TelegramId);
            // ... etc.

            var walletInfo = await multi.ReadSingleOrDefaultAsync();
            if (walletInfo != null)
            {
                userDetail.TokenBalance = walletInfo.Balance;
                userDetail.WalletLastUpdated = walletInfo.WalletLastUpdated;
            }

            var subscriptions = (await multi.ReadAsync<SubscriptionSummaryDto>()).ToList();
            if (subscriptions.Any())
            {
                userDetail.Subscriptions = subscriptions;
                var activeSub = subscriptions.FirstOrDefault(s => s.Status == "Active" && DateTime.UtcNow >= s.StartDate && DateTime.UtcNow <= s.EndDate);
                if (activeSub != null)
                {
                    userDetail.ActiveSubscription = new ActiveSubscriptionDto { EndDate = activeSub.EndDate };
                    // ...
                }
            }

            var transactions = (await multi.ReadAsync<TransactionSummaryDto>()).ToList();
            if (transactions.Any())
            {
                userDetail.RecentTransactions = transactions;
                // ... calculate total spent etc. ...
            }

            return userDetail;
        }
    }
}