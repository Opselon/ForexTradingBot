// In Infrastructure/Services/BroadcastService.cs
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

public class BroadcastService : IBroadcastService
{
    private readonly IConfiguration _configuration;

    public BroadcastService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<List<long>> GetAllActiveUserChatIdsAsync(CancellationToken cancellationToken = default)
    {
        await using SqlConnection connection = new(_configuration.GetConnectionString("DefaultConnection"));
        string sql = "SELECT TelegramId FROM dbo.Users WHERE IsActive = 1 AND TelegramId IS NOT NULL;";

        IEnumerable<string> idsAsString = await connection.QueryAsync<string>(new CommandDefinition(sql, cancellationToken: cancellationToken));

        // Convert string IDs to long
        return idsAsString.Select(long.Parse).ToList();
    }
}