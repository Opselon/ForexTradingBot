// --- START OF FILE: Infrastructure/Services/AdminService.cs ---

using Application.DTOs.Admin;
using Application.Interfaces;
using Dapper;
using Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Infrastructure.Services.Admin
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
            await using SqlConnection connection = new(_connectionString);
            string sql = "SELECT COUNT(1) FROM dbo.Users; SELECT COUNT(1) FROM dbo.NewsItems;";
            using SqlMapper.GridReader multi = await connection.QueryMultipleAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
            return (await multi.ReadSingleAsync<int>(), await multi.ReadSingleAsync<int>());
        }

        public async Task<List<long>> GetAllActiveUserChatIdsAsync(CancellationToken cancellationToken = default)
        {
            await using SqlConnection connection = new(_connectionString);
            string sql = "SELECT TelegramId FROM dbo.Users WHERE TelegramId IS NOT NULL AND TelegramId <> '';";
            IEnumerable<string> idsAsString = await connection.QueryAsync<string>(new CommandDefinition(sql, cancellationToken: cancellationToken));

            List<long> userChatIds = new();
            foreach (string idStr in idsAsString)
            {
                if (long.TryParse(idStr, out long id))
                {
                    userChatIds.Add(id);
                }
                else
                {
                    _logger.LogWarning("Could not parse TelegramId '{IdString}' to long.", idStr);
                }
            }
            return userChatIds;
        }




        // In AdminService.cs
        public async Task<string> ExecuteRawSqlQueryAsync(string sqlQuery, CancellationToken cancellationToken = default)
        {
            _logger.LogWarning("Admin is executing a raw SQL query: {Query}", sqlQuery);
            await using SqlConnection connection = new(_connectionString);
            StringBuilder response = new();

            try
            {
                CommandDefinition command = new(sqlQuery, commandTimeout: 60, cancellationToken: cancellationToken);

                // Use QueryMultiple for flexibility, as the query could be anything.
                using SqlMapper.GridReader multi = await connection.QueryMultipleAsync(command);

                int resultSetIndex = 1;
                while (!multi.IsConsumed)
                {
                    IEnumerable<dynamic> grid = await multi.ReadAsync();
                    List<dynamic> data = grid.ToList();

                    if (!data.Any())
                    {
                        _ = response.AppendLine($"-- Result Set {resultSetIndex} (No Rows) --\n");
                        resultSetIndex++;
                        continue;
                    }

                    _ = response.AppendLine($"-- Result Set {resultSetIndex} ({data.Count} Rows) --");
                    // Get headers from the first row (which is an IDictionary<string, object>)
                    ICollection<string> headers = ((IDictionary<string, object>)data.First()).Keys;
                    _ = response.AppendLine("`" + string.Join(" | ", headers) + "`");

                    foreach (dynamic? row in data)
                    {
                        IDictionary<string, object> rowDict = (IDictionary<string, object>)row;
                        IEnumerable<string> values = rowDict.Values.Select(v => v?.ToString() ?? "NULL");
                        _ = response.AppendLine("`" + string.Join(" | ", values) + "`");
                    }
                    _ = response.AppendLine();
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
            await using SqlConnection connection = new(_connectionString);

            string sql = @"
                SELECT * FROM dbo.Users WHERE TelegramId = @TelegramIdStr;
                SELECT Balance, UpdatedAt AS WalletLastUpdated FROM dbo.TokenWallets WHERE UserId = (SELECT Id FROM dbo.Users WHERE TelegramId = @TelegramIdStr);
                SELECT Id AS SubscriptionId, StartDate, EndDate, Status FROM dbo.Subscriptions WHERE UserId = (SELECT Id FROM dbo.Users WHERE TelegramId = @TelegramIdStr) ORDER BY StartDate DESC;
                SELECT TOP 10 Id AS TransactionId, Amount, Type, Status, Timestamp FROM dbo.Transactions WHERE UserId = (SELECT Id FROM dbo.Users WHERE TelegramId = @TelegramIdStr) ORDER BY Timestamp DESC;
            ";

            using SqlMapper.GridReader multi = await connection.QueryMultipleAsync(sql, new { TelegramIdStr = telegramId.ToString() });

            User? user = await multi.ReadSingleOrDefaultAsync<User>();
            if (user == null)
            {
                return null;
            }

            AdminUserDetailDto userDetail = new()
            {
                UserId = user.Id,
                Username = user.Username,
                TelegramId = long.Parse(user.TelegramId)
            };
            // ... etc.

            dynamic? walletInfo = await multi.ReadSingleOrDefaultAsync();
            if (walletInfo != null)
            {
                userDetail.TokenBalance = walletInfo.Balance;
                userDetail.WalletLastUpdated = walletInfo.WalletLastUpdated;
            }

            List<SubscriptionSummaryDto> subscriptions = (await multi.ReadAsync<SubscriptionSummaryDto>()).ToList();
            if (subscriptions.Any())
            {
                userDetail.Subscriptions = subscriptions;
                SubscriptionSummaryDto? activeSub = subscriptions.FirstOrDefault(s => s.Status == "Active" && DateTime.UtcNow >= s.StartDate && DateTime.UtcNow <= s.EndDate);
                if (activeSub != null)
                {
                    userDetail.ActiveSubscription = new ActiveSubscriptionDto { EndDate = activeSub.EndDate };
                    // ...
                }
            }

            List<TransactionSummaryDto> transactions = (await multi.ReadAsync<TransactionSummaryDto>()).ToList();
            if (transactions.Any())
            {
                userDetail.RecentTransactions = transactions;
                // ... calculate total spent etc. ...
            }

            return userDetail;
        }
    }
}