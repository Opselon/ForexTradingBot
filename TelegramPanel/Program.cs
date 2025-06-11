using Microsoft.Extensions.Hosting;
using TelegramPanel.Infrastructure.Extensions;

namespace TelegramPanel;

public class Program
{
    public static void Main(string[] args)
    {
        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args)
    {
        return Host.CreateDefaultBuilder(args)
            .ConfigureServices((hostContext, services) =>
            {
                // Add market data services
                _ = services.AddMarketDataServices(hostContext.Configuration);

            });
    }
}