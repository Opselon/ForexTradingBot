using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Tests.Application;

public sealed class DatabaseProviderWiringRegressionTests
{
    [Theory]
    [InlineData("postgres", "Npgsql.EntityFrameworkCore.PostgreSQL")]
    [InlineData("sqlite", "Microsoft.EntityFrameworkCore.Sqlite")]
    [InlineData("sqlserver", "Microsoft.EntityFrameworkCore.SqlServer")]
    public void Explicit_provider_wires_the_matching_EF_core_driver(
        string provider,
        string expectedProviderAssembly)
    {
        var connection = provider switch
        {
            "postgres" => "Host=localhost;Port=5432;Database=forexbotdb;Username=forexbot;Password=test",
            "sqlite" => "Data Source=:memory:",
            "sqlserver" => "Server=localhost,1433;Database=forexbotdb;User Id=sa;Password=StrongTestPassword123!;TrustServerCertificate=True",
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseSettings:DatabaseProvider"] = provider,
                ["ConnectionStrings:DefaultConnection"] = connection
            })
            .Build();

        var services = new ServiceCollection();
        services.AddInfrastructureServices(configuration, isSmokeTest: false);

        using var serviceProvider = services.BuildServiceProvider();
        using var db = serviceProvider.GetRequiredService<AppDbContext>();

        Assert.Equal(
            expectedProviderAssembly,
            db.Database.ProviderName?.Split(',')[0]);
    }

    [Fact]
    public void Provider_selection_is_not_overridden_by_connection_string_shape()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseSettings:DatabaseProvider"] = "sqlite",
                ["ConnectionStrings:DefaultConnection"] =
                    "Data Source=:memory:;Cache=Shared;Mode=Memory"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddInfrastructureServices(configuration, isSmokeTest: false);

        using var provider = services.BuildServiceProvider();
        using var db = provider.GetRequiredService<AppDbContext>();

        Assert.StartsWith(
            "Microsoft.EntityFrameworkCore.Sqlite",
            db.Database.ProviderName,
            StringComparison.Ordinal);
    }
}
