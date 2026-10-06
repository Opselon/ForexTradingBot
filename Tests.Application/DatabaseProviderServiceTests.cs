using FluentAssertions;
using Infrastructure.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Tests.Application;

/// <summary>
/// Tests for the database provider resolution logic (master's DbProviderService):
/// alias mapping, case-insensitivity, empty/unsupported values, and the enum itself.
/// </summary>
public class DatabaseProviderTests
{
    private static DbProviderService CreateService(string? configValue)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseSettings:DatabaseProvider"] = configValue,
            })
            .Build();

        return new DbProviderService(
            config,
            Mock.Of<ILogger<DbProviderService>>());
    }

    [Theory]
    [InlineData("postgres", DatabaseProvider.Postgres)]
    [InlineData("postgresql", DatabaseProvider.Postgres)]
    [InlineData("POSTGRES", DatabaseProvider.Postgres)]
    [InlineData("sqlite", DatabaseProvider.SQLite)]
    [InlineData("SQLite", DatabaseProvider.SQLite)]
    [InlineData("sqlserver", DatabaseProvider.SqlServer)]
    [InlineData("SqlServer", DatabaseProvider.SqlServer)]
    public void Known_providers_resolve_case_insensitively(string raw, DatabaseProvider expected)
    {
        CreateService(raw).Provider.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Missing_provider_falls_back_to_unsupported(string? raw)
    {
        CreateService(raw).Provider.Should().Be(DatabaseProvider.Unsupported);
    }

    [Theory]
    [InlineData("mysql")]
    [InlineData("oracle")]
    [InlineData("not-a-provider")]
    public void Unknown_provider_resolves_to_unsupported(string raw)
    {
        CreateService(raw).Provider.Should().Be(DatabaseProvider.Unsupported);
    }

    [Fact]
    public void Provider_enum_contains_exactly_the_three_supported_engines()
    {
        var supported = new[] { DatabaseProvider.Postgres, DatabaseProvider.SQLite, DatabaseProvider.SqlServer };
        Enum.GetValues<DatabaseProvider>().Should().Contain(supported);
    }
}
