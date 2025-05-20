using Microsoft.Extensions.Hosting;
using TelegramPanel.Infrastructure.Extensions;

namespace TelegramPanel;

public class Program
{
    public static void Main(string[] args)
    {
        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureServices((hostContext, services) =>
            {
                // Add market data services
                services.AddMarketDataServices(hostContext.Configuration);
            });
}