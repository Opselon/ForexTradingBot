using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Scripts
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile("appsettings.Development.json", optional: true)
                .AddEnvironmentVariables()
                .Build();

            var services = new ServiceCollection()
                .AddLogging(builder =>
                {
                    builder.AddConsole();
                    builder.SetMinimumLevel(LogLevel.Information);
                })
                .AddSingleton<IConfiguration>(configuration)
                .BuildServiceProvider();

            var logger = services.GetRequiredService<ILogger<Program>>();

            try
            {
                logger.LogInformation("Script execution completed successfully.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error occurred during script execution");
                Environment.Exit(1);
            }
        }
    }
} 