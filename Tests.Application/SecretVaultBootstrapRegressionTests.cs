using ForexTradingBot.Cli.Secrets;
using Microsoft.Extensions.Configuration;

namespace Tests.Application;

public sealed class SecretVaultBootstrapRegressionTests : IDisposable
{
    private readonly string _directory;
    private readonly string? _previousVaultDirectory;

    public SecretVaultBootstrapRegressionTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "forexbot-bootstrap-regression-" + Guid.NewGuid().ToString("N"));
        _previousVaultDirectory = Environment.GetEnvironmentVariable("FOREXBOT_VAULT_DIRECTORY");
        Environment.SetEnvironmentVariable("FOREXBOT_VAULT_DIRECTORY", _directory);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("FOREXBOT_VAULT_DIRECTORY", _previousVaultDirectory);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Set_then_Apply_loads_persisted_values_into_the_expected_configuration_keys()
    {
        SecretVaultBootstrap.Set("ADMIN_PASSWORD", "administrator-password");
        SecretVaultBootstrap.Set("DATABASE_PROVIDER", "postgres");
        SecretVaultBootstrap.Set("DATABASE_CONNECTION", "Host=localhost;Database=forexbotdb;Username=forexbot;Password=test");
        SecretVaultBootstrap.Set("REDIS_CONNECTION", "localhost:6379");

        var config = new ConfigurationManager();
        SecretVaultBootstrap.Apply(config);

        Assert.Equal("administrator-password", config["Admin:Password"]);
        Assert.Equal("postgres", config["DatabaseSettings:DatabaseProvider"]);
        Assert.Equal("Host=localhost;Database=forexbotdb;Username=forexbot;Password=test", config["ConnectionStrings:DefaultConnection"]);
        Assert.Equal("localhost:6379", config["ConnectionStrings:Redis"]);
    }


    [Theory]
    [InlineData("ADMIN_PASSWORD", "Admin:Password")]
    [InlineData("TELEGRAM_BOT_TOKEN", "TelegramPanel:BotToken")]
    [InlineData("TELEGRAM_WEBHOOK_ADDRESS", "TelegramPanel:WebhookAddress")]
    [InlineData("TELEGRAM_WEBHOOK_SECRET", "TelegramPanel:WebhookSecretToken")]
    [InlineData("TELEGRAM_API_ID", "TelegramUserApi:ApiId")]
    [InlineData("TELEGRAM_API_HASH", "TelegramUserApi:ApiHash")]
    [InlineData("TELEGRAM_PHONE_NUMBER", "TelegramUserApi:PhoneNumber")]
    [InlineData("TELEGRAM_FORWARDER_BOT_TOKEN", "TelegramUserApi:BotToken")]
    [InlineData("CRYPTO_PAY_API_TOKEN", "CryptoPay:ApiToken")]
    [InlineData("CRYPTO_PAY_WEBHOOK_SECRET", "CryptoPay:WebhookSecretForCryptoPay")]
    [InlineData("REDIS_CONNECTION", "ConnectionStrings:Redis")]
    [InlineData("DATABASE_PROVIDER", "DatabaseSettings:DatabaseProvider")]
    [InlineData("DATABASE_CONNECTION", "ConnectionStrings:DefaultConnection")]
    public void Every_supported_vault_key_maps_to_exactly_one_runtime_configuration_key(
        string vaultKey,
        string configurationKey)
    {
        var value = "test-" + Guid.NewGuid().ToString("N");
        SecretVaultBootstrap.Set(vaultKey, value);

        var config = new ConfigurationManager();
        SecretVaultBootstrap.Apply(config);

        Assert.Equal(value, config[configurationKey]);
    }

    [Fact]
    public void Apply_does_not_materialize_secret_values_for_unknown_vault_keys()
    {
        SecretVaultBootstrap.Set("NOT_A_CONFIGURATION_KEY", "private-value");

        var config = new ConfigurationManager();
        SecretVaultBootstrap.Apply(config);

        Assert.Null(config["NOT_A_CONFIGURATION_KEY"]);
        Assert.Null(config["Admin:Password"]);
        Assert.DoesNotContain("private-value", string.Join("|", config.AsEnumerable().Select(x => $"{x.Key}={x.Value}")));
    }
}
