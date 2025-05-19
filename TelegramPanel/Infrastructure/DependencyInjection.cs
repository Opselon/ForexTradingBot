using Microsoft.Extensions.DependencyInjection;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Infrastructure.Services;

namespace TelegramPanel.Infrastructure
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddInfrastructure(this IServiceCollection services)
        {
            services.AddHttpClient();
            services.AddScoped<IMarketDataService, MarketDataService>();
            return services;
        }
    }
} 