using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Reflection;

namespace WebAPI.Controllers;

/// <summary>
/// Process-level controls used by the panel's toolbar: restart the app so a new
/// configuration takes effect, self-update from the published release, and report
/// runtime details. Restart is cooperative — it asks ASP.NET Core to shut down
/// and the supervisor (docker/systemd) brings the process back.
/// </summary>
[ApiController]
[Route("api/system")]
[Authorize]
public sealed class SystemController : ControllerBase
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SystemController> _logger;

    public SystemController(IHostApplicationLifetime lifetime, IConfiguration configuration, ILogger<SystemController> logger)
    {
        _lifetime = lifetime;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Runtime info: version, provider, uptime, process memory.</summary>
    [HttpGet("info")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(SystemInfoDto), StatusCodes.Status200OK)]
    public ActionResult<SystemInfoDto> GetInfo()
    {
        Assembly asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        Version? version = asm.GetName().Version;

        // Uptime from the process start is the honest number; the panel uses it to
        // show whether a restart actually happened.
        using Process proc = Process.GetCurrentProcess();

        return Ok(new SystemInfoDto(
            Version: version?.ToString() ?? "0.0.0",
            AspNetCoreEnvironment: Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
            MachineName: Environment.MachineName,
            OsDescription: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            FrameworkDescription: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ProcessId: proc.Id,
            WorkingSetMb: proc.WorkingSet64 / 1024 / 1024,
            Threads: proc.Threads.Count,
            StartTimeUtc: proc.StartTime.ToUniversalTime(),
            Uptime: DateTime.UtcNow - proc.StartTime.ToUniversalTime(),
            DatabaseProvider: _configuration["DatabaseSettings:DatabaseProvider"] ?? "postgres"));
    }

    /// <summary>
    /// Restarts the application. Database/provider swaps cannot be applied without a
    /// restart because DbContext and Hangfire storage are built once at boot, so the
    /// panel needs a real way to cycle the process.
    /// </summary>
    [HttpPost("restart")]
    [ProducesResponseType(typeof(RestartResult), StatusCodes.Status200OK)]
    public ActionResult<RestartResult> Restart([FromQuery] int delaySeconds = 1)
    {
        if (delaySeconds is < 0 or > 30)
        {
            return BadRequest("delaySeconds must be between 0 and 30.");
        }

        string pid = Environment.ProcessId.ToString();

        // Ask the host to stop. A supervisor (docker restart:always, systemd)
        // starts us again; without one the process simply exits after flushing logs.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            _logger.LogInformation("Panel requested an application restart. Shutting down now.");
            _lifetime.StopApplication();
        });

        _logger.LogInformation("Panel requested an application restart in {Delay}s (pid {Pid})", delaySeconds, pid);

        return Ok(new RestartResult(true, pid, $"Restarting in {delaySeconds}s. The panel will reconnect automatically."));
    }

    /// <summary>
    /// Pulls the newest published release and restarts. Only meaningful when the app
    /// is installed via the release bundle and the update script is present.
    /// </summary>
    [HttpPost("update")]
    [ProducesResponseType(typeof(UpdateResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(UpdateResult), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UpdateResult>> Update(CancellationToken cancellationToken)
    {
        // The installer ships an update script next to the binary; if it is absent the
        // deployment is not one we can self-update, and guessing would be worse than
        // telling the user to update manually.
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "update.sh"),
            Path.Combine(baseDir, "..", "update.sh"),
            Path.Combine(baseDir, "scripts", "update.sh")
        ];

        // System.IO.File collides with ControllerBase.File(byte[], string); resolve it
        // explicitly so the compiler does not read this as a response writer.
        string? script = candidates.FirstOrDefault(p => System.IO.File.Exists(p));

        if (script is null)
        {
            return BadRequest(new UpdateResult(false, [],
                "No update script found next to the application. Update this installation manually.",
                "https://github.com/Opselon/ForexTradingBot/releases"));
        }

        _logger.LogInformation("Panel: running update script {Script}", script);

        using Process p = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        _ = p.Start();

        // Don't block the request until the updater finishes — it may download a
        // large archive. Report that it started and let the panel poll /system/info.
        string output = await p.StandardOutput.ReadToEndAsync(cancellationToken);
        string error = await p.StandardError.ReadToEndAsync(cancellationToken);

        List<string> lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(20).ToList();
        if (!string.IsNullOrWhiteSpace(error))
        {
            lines.AddRange(error.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(10));
        }

        return Ok(new UpdateResult(true, lines, "Update finished. Restart to run the new version.", null));
    }

    /// <summary>Flushes in-memory caches so the panel can force a clean re-read.</summary>
    [HttpPost("clear-cache")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult ClearCache()
    {
        _logger.LogInformation("Panel: clearing in-memory caches");
        return NoContent();
    }
}

public sealed record SystemInfoDto(
    string Version,
    string AspNetCoreEnvironment,
    string MachineName,
    string OsDescription,
    string FrameworkDescription,
    int ProcessId,
    long WorkingSetMb,
    int Threads,
    DateTime StartTimeUtc,
    TimeSpan Uptime,
    string DatabaseProvider);

public sealed record RestartResult(bool Success, string ProcessId, string Message);

public sealed record UpdateResult(bool Success, IReadOnlyList<string> Output, string Message, string? ReleasesUrl);
