// NEW FILE: Application/Interfaces/IAdminStatsService.cs
// Or in another suitable "Interfaces" folder

using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

public interface IAdminStatsService
{
    Task<(int UserCount, int NewsItemCount)> GetDashboardStatsAsync(CancellationToken cancellationToken = default);
}

namespace TelegramPanel.Application.Interfaces
{
    public class AdminStatsService : IAdminStatsService
    {
        private readonly IConfiguration _configuration;

        public AdminStatsService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public async Task<(int UserCount, int NewsItemCount)> GetDashboardStatsAsync(CancellationToken cancellationToken = default)
        {
            await using SqlConnection connection = new(_configuration.GetConnectionString("DefaultConnection"));

            // A multi-result query that is highly efficient.
            string sql = @"
                SELECT COUNT(1) FROM dbo.Users;
                SELECT COUNT(1) FROM dbo.NewsItems;
            ";

            using SqlMapper.GridReader multi = await connection.QueryMultipleAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));

            int userCount = await multi.ReadSingleAsync<int>();
            int newsItemCount = await multi.ReadSingleAsync<int>();

            return (userCount, newsItemCount);
        }
    }
}