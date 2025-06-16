using Application.Interfaces;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Services
{
    // NEW, CLEAN IMPLEMENTATION
    public class DashboardService : IDashboardService
    {
        private readonly string _connectionString;
        public DashboardService(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")!;
        }

        public async Task<(int UserCount, int NewsItemCount)> GetDashboardStatsAsync(CancellationToken cancellationToken = default)
        {
            await using var connection = new SqlConnection(_connectionString);
            var sql = "SELECT COUNT(1) FROM dbo.Users; SELECT COUNT(1) FROM dbo.NewsItems;";
            using var multi = await connection.QueryMultipleAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
            var userCount = await multi.ReadSingleAsync<int>();
            var newsItemCount = await multi.ReadSingleAsync<int>();
            return (userCount, newsItemCount);
        }
    }
}