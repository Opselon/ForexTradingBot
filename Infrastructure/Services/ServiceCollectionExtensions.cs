using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Services
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddForwardingOrchestratorServices(this IServiceCollection services)
        {
            services.AddSingleton<UserApiForwardingOrchestrator>();
            return services;
        }
    }
}