using Application.Features.Forwarding.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Features.Forwarding.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddForwardingServices(this IServiceCollection services)
        {
            // Register services

            _ = services.AddScoped<IForwardingService, ForwardingService>();
            _ = services.AddScoped<MessageProcessingService>();
            return services;
        }
    }
}