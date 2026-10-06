using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Npgsql;

namespace Infrastructure.ExternalServices;

public interface IExternalDependencyManager
{
    Task<bool> EnsureDependenciesReadyAsync(IConfigurationBuilder config);
}

public class ExternalDependencyManager : IExternalDependencyManager, IAsyncDisposable
{
    private readonly ILogger<ExternalDependencyManager> _logger;
    private readonly HttpClient _httpClient;

    private readonly string _baseDir;
    private readonly string _pgDir;
    private readonly string _redisDir;
    private readonly string _pgDataDir;

    private readonly string _pgDbName = "forex_local_db";
    private readonly string _pgUser = "forex_user";
    private readonly string _pgPassword = Guid.NewGuid().ToString("N");
    private readonly int _pgPort = 5433;
    private readonly int _redisPort = 6380;

    private Process? _postgresProcess;
    private Process? _redisProcess;

    public ExternalDependencyManager(ILogger<ExternalDependencyManager> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();
        _baseDir = Path.Combine(AppContext.BaseDirectory, "external_services");
        _pgDir = Path.Combine(_baseDir, "pgsql");
        _redisDir = Path.Combine(_baseDir, "redis");
        _pgDataDir = Path.Combine(_pgDir, "data");
    }


    public async Task<bool> EnsureDependenciesReadyAsync(IConfigurationBuilder config)
    {
        _logger.LogInformation("--- Initializing Self-Contained Dependencies ---");
        _ = Directory.CreateDirectory(_baseDir);

        string? postgresConnectionString = await EnsurePostgresAsync();
        if (string.IsNullOrEmpty(postgresConnectionString))
        {
            return false;
        }

        string? redisConnectionString = await EnsureRedisAsync();
        if (string.IsNullOrEmpty(redisConnectionString))
        {
            return false;
        }

        _ = config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatabaseSettings:DatabaseProvider"] = "postgres",
            ["ConnectionStrings:DefaultConnection"] = postgresConnectionString,
            ["ConnectionStrings:Redis"] = redisConnectionString
        });

        _logger.LogInformation("✅ All self-contained dependencies are configured and running.");
        return true;
    }

    #region PostgreSQL Management

    private async Task<string?> EnsurePostgresAsync()
    {
        _logger.LogInformation("--- Checking PostgreSQL Status ---");
        string pgCtlPath = GetPostgresToolPath("pg_ctl");

        if (!File.Exists(pgCtlPath))
        {
            if (!await DownloadAndExtractPostgresAsync())
            {
                return null;
            }
        }
        else
        {
            _logger.LogInformation("Portable PostgreSQL found. Skipping download.");
        }

        pgCtlPath = GetPostgresToolPath("pg_ctl");
        if (!File.Exists(pgCtlPath))
        {
            _logger.LogCritical("pg_ctl.exe not found at expected path: {Path}", pgCtlPath);
            return null;
        }

        if (!Directory.Exists(_pgDataDir) || !File.Exists(Path.Combine(_pgDataDir, "PG_VERSION")))
        {
            _logger.LogInformation("Initializing new PostgreSQL data cluster at {DataDir}...", _pgDataDir);
            // Initialize with the system user as the superuser for bootstrapping
            string bootstrapUser = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Environment.UserName : "postgres";
            (bool success, string _) = await RunProcessAsync(pgCtlPath, $"initdb -D \"{_pgDataDir}\" -U {bootstrapUser}");
            if (!success)
            {
                return null;
            }

            await File.AppendAllTextAsync(Path.Combine(_pgDataDir, "postgresql.conf"), $"\nport = {_pgPort}\n");
            // Allow local connections via password
            await File.AppendAllTextAsync(Path.Combine(_pgDataDir, "pg_hba.conf"), $"\nhost    all             all             127.0.0.1/32            scram-sha-256\nhost    all             all             ::1/128                 scram-sha-256\n");
        }

        (bool statusSuccess, string? statusOutput) = await RunProcessAsync(pgCtlPath, $"status -D \"{_pgDataDir}\"");
        if (!statusSuccess || !statusOutput.Contains("server is running", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Starting PostgreSQL server...");
            _postgresProcess = StartPersistentProcess(GetPostgresToolPath("postgres"), $"-D \"{_pgDataDir}\"");
            if (_postgresProcess == null)
            {
                return null;
            }

            await Task.Delay(4000);
        }

        if (!await EnsureDatabaseAndUserExistAsync())
        {
            return null;
        }

        // Assemble the connection string through the builder so the password is
        // never embedded into an interpolated string (keeps it out of logs).
        var csb = new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Port = _pgPort,
            Database = _pgDbName,
            Username = _pgUser,
            Password = _pgPassword,
        };
        return csb.ConnectionString;
    }

    private async Task<bool> EnsureDatabaseAndUserExistAsync()
    {
        string psqlPath = GetPostgresToolPath("psql");
        string bootstrapUser = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Environment.UserName : "postgres";
        string adminConnStr = $"--host=localhost --port={_pgPort} --username={bootstrapUser} --dbname=postgres";

        // 1. Check if our app user exists
        (bool userExists, string? userOutput) = await RunProcessAsync(psqlPath, $"{adminConnStr} -tAc \"SELECT 1 FROM pg_roles WHERE rolname='{_pgUser}'\"");
        // Check if the command was successful AND the output is "1"
        if (!userExists || !userOutput.Trim().Equals("1"))
        {
            _logger.LogInformation("Application user '{User}' not found. Creating it...", _pgUser);
            // The generated password is handed to psql through the PGPASSWORD
            // environment variable so it never appears in the argument list
            // (which can be logged or read from the process list).
            (bool createUserSuccess, string _) = await RunProcessAsync(
                psqlPath,
                $"{adminConnStr} -c \"CREATE ROLE {_pgUser} WITH LOGIN PASSWORD '{_pgPassword}';\"",
                new Dictionary<string, string> { ["PGPASSWORD"] = _pgPassword });
            if (!createUserSuccess)
            {
                return false;
            }
        }
        else
        {
            _logger.LogInformation("✅ Application user '{User}' already exists.", _pgUser);
        }

        // 2. Check if our app database exists
        (bool dbExists, string? dbOutput) = await RunProcessAsync(psqlPath, $"{adminConnStr} -tAc \"SELECT 1 FROM pg_database WHERE datname='{_pgDbName}'\"");
        if (!dbExists || !dbOutput.Trim().Equals("1"))
        {
            _logger.LogInformation("Database '{DbName}' not found. Creating it...", _pgDbName);
            if (!(await RunProcessAsync(psqlPath, $"{adminConnStr} -c \"CREATE DATABASE {_pgDbName} OWNER {_pgUser}\"")).Success)
            {
                return false;
            }

            _logger.LogInformation("✅ Database created.");
        }
        else
        {
            _logger.LogInformation("✅ Database '{DbName}' already exists.", _pgDbName);
        }

        _logger.LogInformation("✅ Database and user are configured.");
        return true;
    }

    // --- The rest of the file is unchanged ---

    private async Task<bool> DownloadAndExtractPostgresAsync()
    {
        string url;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            url = "https://get.enterprisedb.com/postgresql/postgresql-16.1-1-windows-x64-binaries.zip";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            url = "https://get.enterprisedb.com/postgresql/postgresql-16.1-1-linux-x64-binaries.tar.gz";
        }
        else { return false; }

        string archivePath = Path.Combine(_baseDir, "postgres_archive" + Path.GetExtension(url));
        if (!await DownloadFileAsync("PostgreSQL", url, archivePath))
        {
            return false;
        }

        Console.WriteLine("Extracting PostgreSQL...");

        if (archivePath.EndsWith(".zip"))
        {
            string tempExtractDir = Path.Combine(_baseDir, "temp_pg_extract");
            if (Directory.Exists(tempExtractDir))
            {
                Directory.Delete(tempExtractDir, true);
            }

            _ = Directory.CreateDirectory(tempExtractDir);

            ZipFile.ExtractToDirectory(archivePath, tempExtractDir);

            string sourceDir = Path.Combine(tempExtractDir, "pgsql");
            if (!Directory.Exists(sourceDir))
            {
                return false;
            }

            if (Directory.Exists(_pgDir))
            {
                Directory.Delete(_pgDir, true);
            }

            Directory.Move(sourceDir, _pgDir);
            Directory.Delete(tempExtractDir, true);
        }
        else
        {
            if (Directory.Exists(_pgDir))
            {
                Directory.Delete(_pgDir, true);
            }

            _ = Directory.CreateDirectory(_pgDir);
            (bool success, string _) = await RunProcessAsync("tar", $"-xzf \"{archivePath}\" -C \"{_pgDir}\" --strip-components=1");
            if (!success)
            {
                return false;
            }
        }

        File.Delete(archivePath);
        _logger.LogInformation("✅ Portable PostgreSQL is ready.");
        return true;
    }

    private string GetPostgresToolPath(string toolName)
    {
        string exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"{toolName}.exe" : toolName;
        return Path.Combine(_pgDir, "bin", exe);
    }

    #endregion

    #region Redis Management

    private async Task<string?> EnsureRedisAsync()
    {
        _logger.LogInformation("--- Checking Redis Status ---");
        string redisServerPath = GetRedisToolPath("redis-server");

        if (!File.Exists(redisServerPath))
        {
            if (!await DownloadAndExtractRedisAsync())
            {
                return null;
            }
        }

        (bool pingSuccess, string _) = await RunProcessAsync(GetRedisToolPath("redis-cli"), $"-p {_redisPort} ping");
        if (!pingSuccess)
        {
            _logger.LogInformation("Starting Redis server...");
            _redisProcess = StartPersistentProcess(redisServerPath, $"--port {_redisPort}");
            if (_redisProcess == null)
            {
                return null;
            }

            await Task.Delay(2000);
        }

        return $"localhost:{_redisPort}";
    }

    private async Task<bool> DownloadAndExtractRedisAsync()
    {
        string url;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            url = "https://github.com/microsoftarchive/redis/releases/download/win-3.0.504/Redis-x64-3.0.504.zip";
            string zipPath = Path.Combine(_baseDir, "redis.zip");
            if (!await DownloadFileAsync("Redis", url, zipPath))
            {
                return false;
            }

            if (Directory.Exists(_redisDir))
            {
                Directory.Delete(_redisDir, true);
            }

            Console.WriteLine("Extracting Redis...");
            ZipFile.ExtractToDirectory(zipPath, _redisDir);
            File.Delete(zipPath);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            _logger.LogInformation("Attempting to install Redis via apt-get...");
            (bool success, string _) = await RunProcessAsync("sudo", "apt-get update && sudo apt-get install -y redis-server");
            if (success)
            {
                _ = await RunProcessAsync("sudo", "systemctl enable --now redis-server");
            }

            return success;
        }
        else
        {
            _logger.LogCritical("Automatic download/install of Redis is not supported on this OS.");
            return false;
        }

        _logger.LogInformation("✅ Portable Redis is ready.");
        return true;
    }

    private string GetRedisToolPath(string toolName)
    {
        string exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"{toolName}.exe" : toolName;
        return RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? toolName : Path.Combine(_redisDir, exe);
    }

    #endregion

    #region Generic Utilities

    private async Task<bool> DownloadFileAsync(string name, string url, string destinationPath)
    {
        _logger.LogInformation("Downloading {Name} from {Url}...", name, url);
        Console.WriteLine($"Downloading {name} (this may take a few minutes)...");
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/108.0.0.0 Safari/537.36");

            using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            _ = response.EnsureSuccessStatusCode();

            using Stream stream = await response.Content.ReadAsStreamAsync();
            using FileStream fileStream = new(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await stream.CopyToAsync(fileStream);

            _logger.LogInformation("Download of {Name} complete.", name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to download {Name}.", name);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n--- Automatic Download Failed ---");
            Console.WriteLine($"Error downloading {name}. This can happen due to network issues or if the download link has changed.");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\nMANUAL ACTION REQUIRED:");
            Console.WriteLine($"1. Please download the file from this URL: {url}");
            Console.WriteLine($"2. Extract the contents.");
            Console.WriteLine($"3. Place the extracted folder into: {_baseDir}");
            Console.WriteLine($"4. Restart the application.");
            Console.ResetColor();
            return false;
        }
    }

    private Process? StartPersistentProcess(string command, string args)
    {
        Process process = new()
        {
            StartInfo = new ProcessStartInfo(command, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };
        try
        {
            _ = process.Start();
            _logger.LogInformation("Started persistent process {ProcessId}: {Command} {Args}", process.Id, command, args);
            return process;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to start persistent process: {Command}", command);
            return null;
        }
    }

    private async Task<(bool Success, string Output)> RunProcessAsync(string command, string args, Dictionary<string, string>? environment = null)
    {
        Process process = new()
        {
            StartInfo = new ProcessStartInfo(command, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                process.StartInfo.Environment[key] = value;
            }
        }

        _ = process.Start();
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        string fullOutput = (output + "\n" + error).Trim();

        if (process.ExitCode != 0)
        {
            _logger.LogError("Process '{Command} {Args}' failed with code {ExitCode}. Output: {Output}", command, args, process.ExitCode, fullOutput);
            return (false, fullOutput);
        }

        _logger.LogInformation("Process '{Command} {Args}' completed successfully.", command, args);
        return (true, fullOutput);
    }

    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("Disposing ExternalDependencyManager and shutting down services...");
        if (_postgresProcess != null && !_postgresProcess.HasExited)
        {
            string pgCtlPath = GetPostgresToolPath("pg_ctl");
            _logger.LogInformation("Stopping PostgreSQL server...");
            _ = await RunProcessAsync(pgCtlPath, $"stop -D \"{_pgDataDir}\" -m fast");
            if (!_postgresProcess.HasExited)
            {
                _postgresProcess.Kill(true);
            }
        }
        if (_redisProcess != null && !_redisProcess.HasExited)
        {
            _logger.LogInformation("Stopping Redis server...");
            _redisProcess.Kill(true);
        }
    }

    #endregion
}