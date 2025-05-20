using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Infrastructure.Services;
using TelegramPanel.Infrastructure.Settings;

namespace TelegramPanel.Infrastructure.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddMarketDataServices(this IServiceCollection services, IConfiguration configuration)
        {
            // Register settings
            services.Configure<MarketDataSettings>(
                configuration.GetSection(MarketDataSettings.SectionName));
            services.Configure<CurrencyInfoSettings>(
                configuration.GetSection(CurrencyInfoSettings.SectionName));

            // Get market data settings
            var marketDataSettings = configuration
                .GetSection(MarketDataSettings.SectionName)
                .Get<MarketDataSettings>();

            if (marketDataSettings?.Providers == null)
            {
                throw new InvalidOperationException("Market data providers configuration is missing");
            }

            // Register named HTTP clients for each provider
            foreach (var provider in marketDataSettings.Providers)
            {
                if (!provider.Value.IsEnabled)
                    continue;

                services.AddHttpClient(provider.Key, client =>
                    {
                        if (!string.IsNullOrWhiteSpace(provider.Value.BaseUrl))
                        {
                            client.BaseAddress = new Uri(provider.Value.BaseUrl);
                        }

                        client.DefaultRequestHeaders.Accept.Add(
                            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                        if (!string.IsNullOrWhiteSpace(provider.Value.ApiKey))
                        {
                            client.DefaultRequestHeaders.Add("X-API-Key", provider.Value.ApiKey);
                        }

                        client.Timeout = TimeSpan.FromSeconds(provider.Value.TimeoutSeconds);
                    })
                    .SetHandlerLifetime(TimeSpan.FromMinutes(5));

            }

            // Register the market data service
            services.AddScoped<IMarketDataService, MarketDataService>();

            return services;
        }
    }
}