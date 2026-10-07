using System.Reflection;
using System.Security.Claims;
using ForexTradingBot.Cli.Secrets;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using WebAPI.Controllers;

namespace Tests.Application;

public sealed class WebApiSecurityRegressionTests
{
    [Fact]
    public void SecretsController_is_admin_only()
    {
        var authorize = typeof(SecretsController)
            .GetCustomAttributes<AuthorizeAttribute>()
            .SingleOrDefault();

        Assert.NotNull(authorize);
        Assert.Equal("Admin", authorize!.Roles);
    }

    [Fact]
    public void ConfigController_is_admin_only()
    {
        var authorize = typeof(ConfigController)
            .GetCustomAttributes<AuthorizeAttribute>()
            .SingleOrDefault();

        Assert.NotNull(authorize);
        Assert.Equal("Admin", authorize!.Roles);
    }

    [Fact]
    public void Secret_reveal_is_never_cacheable()
    {
        var method = typeof(SecretsController).GetMethod(nameof(SecretsController.Reveal));
        var responseCache = method?.GetCustomAttribute<ResponseCacheAttribute>();

        Assert.NotNull(responseCache);
        Assert.Equal(ResponseCacheLocation.None, responseCache!.Location);
        Assert.True(responseCache.NoStore);
    }

    [Fact]
    public void Secret_list_always_redacts_backend_plaintext()
    {
        var vault = new Mock<ISecretVault>();
        vault.Setup(x => x.List()).Returns(new[]
        {
            new SecretRecord
            {
                Key = "BOT",
                Value = "real-plaintext-secret",
                Category = SecretCategory.Telegram
            }
        });

        var controller = new SecretsController(
            vault.Object,
            Mock.Of<Microsoft.Extensions.Logging.ILogger<SecretsController>>());

        var result = Assert.IsType<OkObjectResult>(controller.List());
        var payload = result.Value!;
        var items = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            payload.GetType().GetProperty("items")!.GetValue(payload));
        var item = items.Cast<object>().Single();

        Assert.Equal("••••••••", item.GetType().GetProperty("value")!.GetValue(item));
        Assert.DoesNotContain(
            "real-plaintext-secret",
            item.GetType().GetProperty("value")!.GetValue(item)?.ToString() ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_secret_key_is_rejected_before_vault_write()
    {
        var vault = new Mock<ISecretVault>();
        vault.Setup(x => x.Set(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<SecretCategory>(),
                It.IsAny<string?>()))
            .Throws<ArgumentException>();

        var controller = new SecretsController(
            vault.Object,
            Mock.Of<Microsoft.Extensions.Logging.ILogger<SecretsController>>());

        var result = Assert.IsType<BadRequestObjectResult>(
            controller.Set("   ", new SecretsController.SetSecretRequest("value")));

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        vault.Verify(
            x => x.Set(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SecretCategory>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public void Empty_secret_value_is_rejected_before_vault_write()
    {
        var vault = new Mock<ISecretVault>();
        var controller = new SecretsController(
            vault.Object,
            Mock.Of<Microsoft.Extensions.Logging.ILogger<SecretsController>>());

        var result = Assert.IsType<BadRequestObjectResult>(
            controller.Set("BOT", new SecretsController.SetSecretRequest("   ")));

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        vault.Verify(
            x => x.Set(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SecretCategory>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public void Missing_secret_reveal_returns_not_found()
    {
        var vault = new Mock<ISecretVault>();
        vault.Setup(x => x.Get("missing")).Returns((string?)null);

        var controller = new SecretsController(
            vault.Object,
            Mock.Of<Microsoft.Extensions.Logging.ILogger<SecretsController>>());

        var result = Assert.IsType<NotFoundObjectResult>(controller.Reveal("missing"));

        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
    }

    [Fact]
    public void Successful_secret_delete_returns_no_content_and_calls_vault_once()
    {
        var vault = new Mock<ISecretVault>();
        vault.Setup(x => x.Delete("BOT")).Returns(true);

        var controller = new SecretsController(
            vault.Object,
            Mock.Of<Microsoft.Extensions.Logging.ILogger<SecretsController>>());

        var result = Assert.IsType<NoContentResult>(controller.Delete("BOT"));

        Assert.Equal(StatusCodes.Status204NoContent, result.StatusCode);
        vault.Verify(x => x.Delete("BOT"), Times.Once);
    }

    [Fact]
    public void Missing_secret_delete_returns_not_found()
    {
        var vault = new Mock<ISecretVault>();
        vault.Setup(x => x.Delete("BOT")).Returns(false);

        var controller = new SecretsController(
            vault.Object,
            Mock.Of<Microsoft.Extensions.Logging.ILogger<SecretsController>>());

        var result = Assert.IsType<NotFoundObjectResult>(controller.Delete("BOT"));

        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
    }

    [Fact]
    public async Task Auth_login_rejects_wrong_password_without_signing_in()
    {
        var auth = CreateAuthenticationMock().Mock;
        var controller = CreateAuthController("admin", "correct-password", auth);

        var result = await controller.Login(new AuthController.LoginModel
        {
            Username = "admin",
            Password = "wrong-password"
        });

        Assert.IsType<UnauthorizedObjectResult>(result);
        auth.Verify(
            x => x.SignInAsync(
                It.IsAny<HttpContext>(),
                CookieAuthenticationDefaults.AuthenticationScheme,
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<AuthenticationProperties>()),
            Times.Never);
    }

    [Fact]
    public async Task Auth_login_signs_in_correct_admin_and_assigns_admin_role()
    {
        var authentication = CreateAuthenticationMock();
        var auth = authentication.Mock;
        var capture = authentication.Capture;
        var controller = CreateAuthController("admin", "correct-password", auth);

        var result = await controller.Login(new AuthController.LoginModel
        {
            Username = "admin",
            Password = "correct-password"
        });

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(capture.Principal);
        Assert.Equal("admin", capture.Principal!.Identity?.Name);
        Assert.True(capture.Principal.IsInRole("Admin"));
    }

    [Fact]
    public async Task Auth_login_returns_server_error_when_credentials_are_not_configured()
    {
        var auth = CreateAuthenticationMock().Mock;
        var controller = CreateAuthController(null, null, auth);

        var result = await controller.Login(new AuthController.LoginModel
        {
            Username = "admin",
            Password = "anything"
        });

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
    }

    [Fact]
    public async Task Auth_login_rejects_invalid_model_state()
    {
        var auth = CreateAuthenticationMock().Mock;
        var controller = CreateAuthController("admin", "correct-password", auth);
        controller.ModelState.AddModelError("Password", "required");

        var result = await controller.Login(new AuthController.LoginModel
        {
            Username = "admin",
            Password = null
        });

        Assert.IsType<BadRequestObjectResult>(result);
        auth.Verify(
            x => x.SignInAsync(
                It.IsAny<HttpContext>(),
                CookieAuthenticationDefaults.AuthenticationScheme,
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<AuthenticationProperties>()),
            Times.Never);
    }

    [Fact]
    public void Auth_login_is_explicitly_anonymous_but_logout_requires_authentication()
    {
        var login = typeof(AuthController).GetMethod(nameof(AuthController.Login));
        var logout = typeof(AuthController).GetMethod(nameof(AuthController.Logout));

        Assert.NotNull(login);
        Assert.NotNull(logout);
        Assert.NotNull(login!.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.NotNull(logout!.GetCustomAttribute<AuthorizeAttribute>());
    }

    private sealed class AuthenticationCapture
    {
        public ClaimsPrincipal? Principal { get; set; }
    }

    private static (Mock<IAuthenticationService> Mock, AuthenticationCapture Capture) CreateAuthenticationMock()
    {
        var capture = new AuthenticationCapture();
        var mock = new Mock<IAuthenticationService>();
        mock.Setup(x => x.SignInAsync(
                It.IsAny<HttpContext>(),
                CookieAuthenticationDefaults.AuthenticationScheme,
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<AuthenticationProperties>()))
            .Callback<HttpContext, string?, ClaimsPrincipal, AuthenticationProperties?>(
                (_, _, principal, _) => capture.Principal = principal)
            .Returns(Task.CompletedTask);
        return (mock, capture);
    }

    private static AuthController CreateAuthController(
        string? username,
        string? password,
        Mock<IAuthenticationService> authentication)
    {
        var configuration = new ConfigurationManager();
        configuration["Admin:Username"] = username;
        configuration["Admin:Password"] = password;

        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authentication.Object)
            .BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = services
        };

        return new AuthController(configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }
}

public sealed class ConfigControllerRegressionTests : IDisposable
{
    private readonly string _vaultDirectory;
    private readonly string? _previousVaultDirectory;

    public ConfigControllerRegressionTests()
    {
        _vaultDirectory = Path.Combine(
            Path.GetTempPath(),
            "forexbot-config-regression-" + Guid.NewGuid().ToString("N"));
        _previousVaultDirectory = Environment.GetEnvironmentVariable("FOREXBOT_VAULT_DIRECTORY");
        Environment.SetEnvironmentVariable("FOREXBOT_VAULT_DIRECTORY", _vaultDirectory);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("FOREXBOT_VAULT_DIRECTORY", _previousVaultDirectory);

        if (Directory.Exists(_vaultDirectory))
            Directory.Delete(_vaultDirectory, recursive: true);
    }

    [Fact]
    public async Task Config_test_endpoint_can_probe_real_sqlite_without_network_dependencies()
    {
        var controller = CreateController();
        var result = await controller.TestConfiguration(new ConfigController.TestConfigRequestModel
        {
            DbConn = "Data Source=:memory:",
            DatabaseProvider = "sqlite",
            BotToken = null,
            RedisConn = null
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = ok.Value!;
        Assert.Equal("OK", Get(response, "DatabaseStatus"));
        Assert.Equal("Not Provided", Get(response, "RedisStatus"));
        Assert.Equal("Error", Get(response, "TelegramStatus"));
    }

    [Fact]
    public async Task Config_test_endpoint_rejects_unsupported_provider_without_throwing()
    {
        var controller = CreateController();
        var result = await controller.TestConfiguration(new ConfigController.TestConfigRequestModel
        {
            DbConn = "anything",
            DatabaseProvider = "mysql",
            BotToken = null,
            RedisConn = null
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Error", Get(ok.Value!, "DatabaseStatus"));
    }

    [Fact]
    public void Config_save_rejects_unsupported_provider_before_touching_vault()
    {
        var controller = CreateController();

        var result = controller.SaveConfiguration(new ConfigController.SaveConfigRequestModel
        {
            DbConn = "ignored",
            DatabaseProvider = "mysql",
            RedisConn = null,
            BotToken = null
        });

        var badRequest = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
        Assert.False(Directory.Exists(_vaultDirectory));
    }

    [Fact]
    public void Config_save_rejects_malformed_bot_token_before_persisting_it()
    {
        var controller = CreateController();

        var result = controller.SaveConfiguration(new ConfigController.SaveConfigRequestModel
        {
            DbConn = "Data Source=:memory:",
            DatabaseProvider = "sqlite",
            RedisConn = null,
            BotToken = "malformed-token-without-colon"
        });

        var badRequest = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
        Assert.False(Directory.Exists(_vaultDirectory));
    }

    [Fact]
    public void Config_save_persists_provider_and_connection_in_the_encrypted_vault()
    {
        var controller = CreateController();

        var result = controller.SaveConfiguration(new ConfigController.SaveConfigRequestModel
        {
            DbConn = "Data Source=:memory:",
            DatabaseProvider = "sqlite",
            RedisConn = null,
            BotToken = null
        });

        Assert.Equal(StatusCodes.Status200OK, Assert.IsType<OkObjectResult>(result).StatusCode);

        var key = SecretKeyStore.LoadOrCreateKey(_vaultDirectory);
        using var vault = new SqliteSecretVault(new SecretCipher(key), _vaultDirectory);

        Assert.Equal("sqlite", vault.Get("DATABASE_PROVIDER"));
        Assert.Equal("Data Source=:memory:", vault.Get("DATABASE_CONNECTION"));
    }

    private ConfigController CreateController()
    {
        var configuration = new ConfigurationManager();
        var logger = Mock.Of<Microsoft.Extensions.Logging.ILogger<ConfigController>>();
        var httpClientFactory = Mock.Of<IHttpClientFactory>();

        return new ConfigController(configuration, logger, httpClientFactory);
    }

    private static string? Get(object response, string property)
        => response.GetType().GetProperty(property)?.GetValue(response)?.ToString();
}

public sealed class RepositorySecurityRegressionTests
{
    [Fact]
    public void Shipped_appsettings_must_not_contain_a_default_admin_password()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "WebAPI", "appsettings.json");
        var json = File.ReadAllText(path);

        Assert.DoesNotContain("\"Password\": \"admin\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Admin\": {\"Password\"", json.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public void Production_Redis_failure_degrades_to_in_memory_without_crashing()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "WebAPI", "Program.cs"));

        // A configured Redis that cannot be reached must NOT crash startup. Crashing left
        // SQLite users unable to run the app at all when no Redis was installed; failing
        // open to the in-memory queue is the correct, recoverable behaviour.
        Assert.DoesNotContain(
            "Startup will fail closed",
            source,
            StringComparison.OrdinalIgnoreCase);

        var fallbackRegistrations = source
            .Split("new Infrastructure.Services.FallbackRedisService", StringSplitOptions.None)
            .Length - 1;

        // 1) the explicit smoke-test branch, 2) the connection failure branch.
        Assert.Equal(2, fallbackRegistrations);
    }

    [Fact]
    public void Redis_connection_failures_do_not_terminate_the_application()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "Infrastructure", "Data", "ServiceCollectionExtensions.cs"));

        // Previously this threw ("FATAL ERROR ..."), which made the whole app unstartable
        // whenever Redis was not running.
        Assert.DoesNotContain(
            "FATAL ERROR: Could not connect to Redis",
            source,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_infrastructure_must_not_silently_fallback_Hangfire_to_memory_storage()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "Infrastructure", "Data", "ServiceCollectionExtensions.cs"));

        Assert.DoesNotContain(
            "FALLING BACK TO IN-MEMORY STORAGE",
            source,
            StringComparison.OrdinalIgnoreCase);

        var memoryStorageRegistrations = source
            .Split("UseMemoryStorage()", StringSplitOptions.None)
            .Length - 1;

        // The sole in-memory registration is the explicit smoke-test branch.
        Assert.Equal(1, memoryStorageRegistrations);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ForexTradingBot.sln")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root could not be located from the test output directory.");
    }
}
