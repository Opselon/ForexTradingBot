using Infrastructure.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Tests.Application;

public sealed class InfrastructureConfigurationRegressionTests
{
    [Fact]
    public void Unknown_connection_dialect_does_not_silently_become_sqlite()
    {
        var configuration = BuildConfiguration(
            connectionString: "this-is-not-a-supported-connection-string",
            provider: null);

        var services = new ServiceCollection();

        var exception = Assert.Throws<NotSupportedException>(
            () => services.AddInfrastructureServices(configuration, isSmokeTest: false));

        Assert.Contains("Database provider could not be inferred safely", exception.Message);
    }

    [Theory]
    [InlineData("mysql")]
    [InlineData("oracle")]
    [InlineData("unsupported")]
    public void Explicitly_unsupported_provider_fails_closed(string provider)
    {
        var configuration = BuildConfiguration(
            connectionString: "Host=localhost;Database=forexbotdb;Username=forexbot;Password=test",
            provider);

        var services = new ServiceCollection();

        Assert.Throws<NotSupportedException>(
            () => services.AddInfrastructureServices(configuration, isSmokeTest: false));
    }

    [Fact]
    public void Smoke_mode_is_the_only_place_allowed_to_register_in_memory_database_behavior()
    {
        var configuration = BuildConfiguration(
            connectionString: null,
            provider: null);

        var services = new ServiceCollection();

        services.AddInfrastructureServices(configuration, isSmokeTest: true);

        var contextRegistration = services.Single(
            descriptor => descriptor.ServiceType == typeof(Microsoft.EntityFrameworkCore.DbContextOptions<AppDbContext>));

        Assert.NotNull(contextRegistration);
    }

    private static IConfiguration BuildConfiguration(
        string? connectionString,
        string? provider)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connectionString,
            ["DatabaseSettings:DatabaseProvider"] = provider,
            ["ConnectionStrings:Redis"] = null
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
