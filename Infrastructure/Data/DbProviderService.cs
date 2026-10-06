// File: Infrastructure/Data/DbProviderService.cs
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Data
{

    public class DbProviderService
    {
        public DatabaseProvider Provider { get; }

        public DbProviderService(IConfiguration configuration, ILogger<DbProviderService> logger)
        {
            string? providerName = configuration.GetValue<string>("DatabaseSettings:DatabaseProvider")?.Trim().ToLowerInvariant();

            // This service now ONLY reads the configuration. It does not try to auto-detect or throw.
            // The logic for that is now centralized in AddInfrastructureServices.
            if (string.IsNullOrEmpty(providerName))
            {
                // If not specified, we'll treat it as unsupported here. 
                // The main DI extension will handle the logic.
                Provider = DatabaseProvider.Unsupported;
                logger.LogWarning("DatabaseSettings:DatabaseProvider is not specified in configuration.");
            }
            else
            {
                switch (providerName)
                {
                    case "postgres":
                    case "postgresql":
                    case "npgsql":
                        Provider = DatabaseProvider.Postgres;
                        break;
                    case "sqlite":
                    case "sqlite3":
                        Provider = DatabaseProvider.SQLite;
                        break;
                    case "sqlserver":
                    case "mssql":
                    case "sql":
                        Provider = DatabaseProvider.SqlServer;
                        break;
                    default:
                        Provider = DatabaseProvider.Unsupported;
                        logger.LogError("Unsupported DatabaseProvider '{ProviderName}' configured.", providerName);
                        break;
                }
            }
            logger.LogInformation("DbProviderService initialized. Provider determined as: {Provider}", Provider);
        }
    }
}