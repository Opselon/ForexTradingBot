using Domain.Features.Forwarding.Repositories;
using Infrastructure.Features.Forwarding.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Features.Forwarding.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddForwardingInfrastructure(this IServiceCollection services)
        {
            _ = services.AddScoped<IForwardingRuleRepository, ForwardingRuleRepository>();
            return services;
        }
    }
}