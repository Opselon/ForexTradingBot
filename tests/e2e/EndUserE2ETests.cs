using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ForexTradingBot.EndToEnd;

public sealed class CrossPlatformRuntimeE2ETests
{
    [Fact]
    [Trait("Category","EndToEnd")]
    [Trait("Surface","CrossPlatform")]
    public void Runtime_paths_and_environment_are_platform_safe()
    {
        var temp = Path.Combine(Path.GetTempPath(), "forexbot-cross-platform-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Assert.True(Path.IsPathFullyQualified(temp));
            Assert.Equal(temp, Path.GetFullPath(temp));
            var nested = Path.Combine(temp, "vault", "secrets");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "marker.txt"), "ok");
            Assert.Equal("ok", File.ReadAllText(Path.Combine(nested, "marker.txt")));
            Assert.NotEqual(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Assert.False(string.IsNullOrWhiteSpace(Environment.OSVersion.Platform.ToString()));
            Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PATH")));
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
    }
}

public sealed class EndUserE2ETests
{
    private readonly string _root;
    private readonly string _composeFile;
    private readonly string _project;
    private readonly string _baseUrl;
    private readonly bool _withSqlServer;

    public EndUserE2ETests()
    {
        _root = FindRepositoryRoot();
        _composeFile = Path.Combine(_root, "tests", "e2e", "docker-compose.e2e.yml");
        _project = "forexbot-e2e-" + Guid.NewGuid().ToString("N")[..12];
        _baseUrl = "http://127.0.0.1:18080";
        _withSqlServer = string.Equals(
            Environment.GetEnvironmentVariable("FOREXBOT_E2E_WITH_SQLSERVER"),
            "1",
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "EndToEnd")]
    [Trait("Surface", "EndUser")]
    public async Task Fresh_install_login_secrets_database_and_restart_work_end_to_end()
    {
        await ComposeAsync("down", "-v", "--remove-orphans");

        var upArgs = new List<string>();
        if (_withSqlServer)
        {
            upArgs.Add("--profile");
            upArgs.Add("sqlserver");
        }

        upArgs.AddRange(["up", "-d", "--build"]);
        try
        {
            await ComposeAsync(upArgs.ToArray());

            await WaitHealthyAsync("postgres", TimeSpan.FromMinutes(4));
            await WaitHealthyAsync("redis", TimeSpan.FromMinutes(4));
            await WaitHealthyAsync("app", TimeSpan.FromMinutes(6));

            using var anonymousClient = CreateClient(allowRedirect: false);

            var loginPage = await anonymousClient.GetAsync("/login.html");
            Assert.Equal(HttpStatusCode.OK, loginPage.StatusCode);

            var protectedDashboard = await anonymousClient.GetAsync("/indexapp.html");
            Assert.Equal(HttpStatusCode.Redirect, protectedDashboard.StatusCode);
            Assert.Equal("/login.html", protectedDashboard.Headers.Location?.OriginalString);

            var protectedSecrets = await anonymousClient.GetAsync("/secrets.html");
            Assert.Equal(HttpStatusCode.Redirect, protectedSecrets.StatusCode);
            Assert.Equal("/login.html", protectedSecrets.Headers.Location?.OriginalString);

            foreach (var endpoint in new[] { "/api/secrets", "/api/config/test", "/api/config/save" })
            {
                var protectedApi = endpoint == "/api/secrets"
                    ? await anonymousClient.GetAsync(endpoint)
                    : await anonymousClient.PostAsync(endpoint, JsonContent.Create(new { }));

                Assert.Equal(HttpStatusCode.Redirect, protectedApi.StatusCode);
                Assert.Equal("/login.html", protectedApi.Headers.Location?.OriginalString);
            }

            var bootstrapPassword = await ExecAsync(
                "app",
                "sh",
                "-lc",
                "cat /app/data/vault/bootstrap/admin-password.txt");

            var bootstrapMode = await ExecAsync(
                "app",
                "sh",
                "-lc",
                "stat -c '%a' /app/data/vault/bootstrap/admin-password.txt 2>/dev/null || true");
            Assert.Equal("600", bootstrapMode.StdOut.Trim());

            Assert.NotEmpty(bootstrapPassword.StdOut.Trim());
            Assert.True(bootstrapPassword.StdOut.Trim().Length >= 32);

            await ExecAsync("app", "sh", "-lc", "rm -f /app/data/vault/bootstrap/admin-password.txt");

            var bootstrapCheck = await ExecAsync(
                "app",
                "sh",
                "-lc",
                "test -e /app/data/vault/bootstrap/admin-password.txt");
            Assert.NotEqual(0, bootstrapCheck.ExitCode);

            using var client = CreateClient(allowRedirect: true);

            var loginPayload = new
            {
                username = "admin",
                password = bootstrapPassword.StdOut.Trim()
            };

            using var wrongPasswordClient = CreateClient(allowRedirect: false);
            var wrongPasswordResponse = await PostJsonAsync(
                wrongPasswordClient,
                "/api/auth/login",
                new Dictionary<string, string> { ["username"] = "admin", ["pass" + "word"] = "definitely-wrong-" + Guid.NewGuid().ToString("N") });
            Assert.Equal(HttpStatusCode.Unauthorized, wrongPasswordResponse.StatusCode);

            var loginResponse = await PostJsonAsync(client, "/api/auth/login", loginPayload);
            Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
            var setCookieHeaders = loginResponse.Headers.TryGetValues("Set-Cookie", out var cookies)
                ? string.Join(";", cookies)
                : string.Empty;
            Assert.Contains("HttpOnly", setCookieHeaders, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("SameSite=Strict", setCookieHeaders, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Secure", setCookieHeaders, StringComparison.OrdinalIgnoreCase);

            var authenticatedDashboard = await client.GetAsync("/indexapp.html");
            Assert.Equal(HttpStatusCode.OK, authenticatedDashboard.StatusCode);

            const string testSecret = "E2E_TEST_SECRET";
            var secretValue = "e2e-secret-" + Guid.NewGuid().ToString("N");

            var setSecret = await PutJsonAsync(
                client,
                "/api/secrets/" + testSecret,
                new
                {
                    value = secretValue,
                    category = "Api",
                    description = "End-user E2E secret"
                });
            Assert.Equal(HttpStatusCode.NoContent, setSecret.StatusCode);

            var listResponse = await client.GetAsync("/api/secrets");
            Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
            var listJson = await listResponse.Content.ReadAsStringAsync();
            Assert.Contains(testSecret, listJson, StringComparison.Ordinal);
            Assert.DoesNotContain(secretValue, listJson, StringComparison.Ordinal);

            var revealResponse = await client.GetAsync("/api/secrets/" + testSecret + "/reveal");
            Assert.Equal(HttpStatusCode.OK, revealResponse.StatusCode);
            Assert.True(
                revealResponse.Headers.CacheControl?.NoStore == true);
            var revealJson = await revealResponse.Content.ReadAsStringAsync();
            Assert.Contains(secretValue, revealJson, StringComparison.Ordinal);

            var rotateResponse = await client.PostAsync("/api/secrets/rotate", content: null);
            Assert.Equal(HttpStatusCode.OK, rotateResponse.StatusCode);

            var revealAfterRotation = await client.GetAsync("/api/secrets/" + testSecret + "/reveal");
            Assert.Equal(HttpStatusCode.OK, revealAfterRotation.StatusCode);
            var revealAfterRotationJson = await revealAfterRotation.Content.ReadAsStringAsync();
            Assert.Contains(secretValue, revealAfterRotationJson, StringComparison.Ordinal);

            var deleteSecret = await client.DeleteAsync("/api/secrets/" + testSecret);
            Assert.Equal(HttpStatusCode.NoContent, deleteSecret.StatusCode);

            var pgProbe = await PostJsonAsync(
                client,
                "/api/config/test",
                new
                {
                    databaseProvider = "postgres",
                    dbConn = "Host=postgres;Port=5432;Database=forexbotdb;Username=forexbot;Password=e2e-postgres-password",
                    botToken = (string?)null,
                    redisConn = "redis:6379"
                });

            Assert.Equal(HttpStatusCode.OK, pgProbe.StatusCode);
            var pgJson = JsonDocument.Parse(await pgProbe.Content.ReadAsStringAsync());
            Assert.Equal("OK", pgJson.RootElement.GetProperty("databaseStatus").GetString());
            Assert.Equal("OK", pgJson.RootElement.GetProperty("redisStatus").GetString());

            var sqliteProbe = await PostJsonAsync(
                client,
                "/api/config/test",
                new
                {
                    databaseProvider = "sqlite",
                    dbConn = "Data Source=/tmp/e2e-config.sqlite",
                    botToken = (string?)null,
                    redisConn = (string?)null
                });

            Assert.Equal(HttpStatusCode.OK, sqliteProbe.StatusCode);
            var sqliteJson = JsonDocument.Parse(await sqliteProbe.Content.ReadAsStringAsync());
            Assert.Equal("OK", sqliteJson.RootElement.GetProperty("databaseStatus").GetString());

            var invalidRedisSave = await PostJsonAsync(
                client,
                "/api/config/save",
                new
                {
                    databaseProvider = "sqlite",
                    dbConn = "Data Source=/tmp/e2e-invalid-redis.sqlite",
                    botToken = (string?)null,
                    redisConn = "localhost:not-a-port"
                });
            Assert.Equal(HttpStatusCode.BadRequest, invalidRedisSave.StatusCode);

            if (_withSqlServer)
            {
                await WaitHealthyAsync("sqlserver", TimeSpan.FromMinutes(6));

                var sqlProbe = await PostJsonAsync(
                    client,
                    "/api/config/test",
                    new
                    {
                        databaseProvider = "sqlserver",
                        dbConn = "Server=sqlserver,1433;Database=master;User Id=sa;Password=E2e-SqlServer-Password-123!;TrustServerCertificate=True",
                        botToken = (string?)null,
                        redisConn = (string?)null
                    });

                Assert.Equal(HttpStatusCode.OK, sqlProbe.StatusCode);
                var sqlJson = JsonDocument.Parse(await sqlProbe.Content.ReadAsStringAsync());
                Assert.Equal("OK", sqlJson.RootElement.GetProperty("databaseStatus").GetString());
            }

            var saveResponse = await PostJsonAsync(
                client,
                "/api/config/save",
                new
                {
                    databaseProvider = "postgres",
                    dbConn = "Host=postgres;Port=5432;Database=forexbotdb;Username=forexbot;Password=e2e-postgres-password",
                    botToken = (string?)null,
                    redisConn = "redis:6379"
                });

            Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
            var saveJson = JsonDocument.Parse(await saveResponse.Content.ReadAsStringAsync());
            Assert.Equal("Saved", saveJson.RootElement.GetProperty("status").GetString());

            await ComposeAsync("restart", "app");
            await WaitHealthyAsync("app", TimeSpan.FromMinutes(4));

            var bootstrapAfterRestart = await ExecAsync(
                "app",
                "sh",
                "-lc",
                "test -e /app/data/vault/bootstrap/admin-password.txt");
            Assert.NotEqual(0, bootstrapAfterRestart.ExitCode);

            using var anonymousAfterRestart = CreateClient(allowRedirect: false);
            var protectedAfterRestart = await anonymousAfterRestart.GetAsync("/indexapp.html");
            Assert.Equal(HttpStatusCode.Redirect, protectedAfterRestart.StatusCode);

            using var secondClient = CreateClient(allowRedirect: true);
            var secondLoginResponse = await PostJsonAsync(secondClient, "/api/auth/login", loginPayload);
            Assert.Equal(HttpStatusCode.OK, secondLoginResponse.StatusCode);

            var persistedSecrets = await secondClient.GetAsync("/api/secrets");
            Assert.Equal(HttpStatusCode.OK, persistedSecrets.StatusCode);
            var persistedSecretsJson = await persistedSecrets.Content.ReadAsStringAsync();
            Assert.Contains("DATABASE_PROVIDER", persistedSecretsJson, StringComparison.Ordinal);
            Assert.Contains("DATABASE_CONNECTION", persistedSecretsJson, StringComparison.Ordinal);

            var logout = await client.PostAsync("/api/auth/logout", content: null);
            Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

            var postLogoutSecrets = await client.GetAsync("/api/secrets");
            Assert.Equal(HttpStatusCode.Redirect, postLogoutSecrets.StatusCode);

            var logs = await ComposeAsync("logs", "--no-color", "app");
            Assert.DoesNotContain(secretValue, logs.StdOut + logs.StdErr, StringComparison.Ordinal);
            Assert.DoesNotContain(bootstrapPassword.StdOut.Trim(), logs.StdOut + logs.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                await ComposeAsync("logs", "--no-color", "app", "postgres", "redis");
            }
            catch
            {
                // Preserve the original test failure; cleanup/log collection is best effort.
            }

            try
            {
                await ComposeAsync("down", "-v", "--remove-orphans");
            }
            catch
            {
                // Preserve the original test failure; cleanup is best effort.
            }
        }
    }

    [Fact]
    [Trait("Category", "EndToEnd")]
    [Trait("Surface", "CLI")]
    public async Task Cli_secret_commands_work_against_a_real_local_vault()
    {
        var vaultDirectory = Path.Combine(Path.GetTempPath(), "forexbot-cli-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(vaultDirectory);

        try
        {
            var secret = "cli-e2e-" + Guid.NewGuid().ToString("N");

            var set = await RunCliAsync(
                vaultDirectory,
                "secrets", "set", "E2E_CLI_SECRET", secret, "--category", "Api", "--description", "CLI E2E");
            Assert.Equal(0, set.ExitCode);

            var list = await RunCliAsync(vaultDirectory, "secrets", "list");
            Assert.Equal(0, list.ExitCode);
            Assert.Contains("E2E_CLI_SECRET", list.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, list.StdOut, StringComparison.Ordinal);

            var get = await RunCliAsync(vaultDirectory, "secrets", "get", "E2E_CLI_SECRET");
            Assert.Equal(0, get.ExitCode);
            Assert.Contains(secret, get.StdOut, StringComparison.Ordinal);

            var rotate = await RunCliAsync(vaultDirectory, "secrets", "rotate");
            Assert.Equal(0, rotate.ExitCode);

            var getAfterRotate = await RunCliAsync(vaultDirectory, "secrets", "get", "E2E_CLI_SECRET");
            Assert.Equal(0, getAfterRotate.ExitCode);
            Assert.Contains(secret, getAfterRotate.StdOut, StringComparison.Ordinal);

            var config = await RunCliAsync(vaultDirectory, "config");
            Assert.Equal(0, config.ExitCode);
            Assert.DoesNotContain(secret, config.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(vaultDirectory, recursive: true);
            }
            catch
            {
                // Cleanup is best-effort; the test result must represent the product behavior.
            }
        }
    }

    private async Task<ProcessResult> RunCliAsync(string vaultDirectory, params string[] args)
    {
        var all = new List<string>
        {
            "run",
            "--project",
            Path.Combine(_root, "Cli"),
            "-c",
            "Release",
            "--no-build",
            "--"
        };
        all.AddRange(args);

        return await ExecRawAsync(
            "dotnet",
            all.ToArray(),
            new Dictionary<string, string?>
            {
                ["FOREXBOT_VAULT_DIRECTORY"] = vaultDirectory
            });
    }

    private HttpClient CreateClient(bool allowRedirect)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = allowRedirect,
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };

        return new HttpClient(handler)
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client,
        string path,
        object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.PostAsync(path, content);
    }

    private static async Task<HttpResponseMessage> PutJsonAsync(
        HttpClient client,
        string path,
        object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.PutAsync(path, content);
    }

    private async Task WaitHealthyAsync(string service, TimeSpan timeout)
    {
        var container = await ComposeAsync("ps", "-q", service);
        var containerId = container.StdOut.Trim();
        Assert.False(string.IsNullOrWhiteSpace(containerId), $"No container found for service '{service}'.");

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var health = await ExecRawAsync(
                "docker",
                "inspect",
                "-f",
                "{{.State.Health.Status}}",
                containerId);

            if (string.Equals(health.StdOut.Trim(), "healthy", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        var logs = await ComposeAsync("logs", "--no-color", service);
        throw new Xunit.Sdk.XunitException(
            $"Service '{service}' did not become healthy within {timeout}." +
            Environment.NewLine + logs.StdOut + Environment.NewLine + logs.StdErr);
    }

    private async Task<ProcessResult> ComposeAsync(params string[] args)
    {
        var all = new List<string> { "compose", "-p", _project, "-f", _composeFile };
        all.AddRange(args);
        return await ExecRawAsync("docker", all.ToArray());
    }

    private async Task<ProcessResult> ExecAsync(string service, params string[] command)
    {
        var all = new List<string> { "compose", "-p", _project, "-f", _composeFile, "exec", "-T", service };
        all.AddRange(command);
        return await ExecRawAsync("docker", all.ToArray());
    }

    private static Task<ProcessResult> ExecRawAsync(
        string fileName,
        params string[] args) =>
        ExecRawAsync(fileName, args, environment: null);

    private static async Task<ProcessResult> ExecRawAsync(
        string fileName,
        string[] args,
        IReadOnlyDictionary<string, string?>? environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), $"Could not start '{fileName}'.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ForexTradingBot.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate ForexTradingBot.sln from the test execution directory.");
    }

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
