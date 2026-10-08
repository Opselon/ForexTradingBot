using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebAPI.Models.Setup;
using WebAPI.Setup;

namespace WebAPI.Controllers;

/// <summary>
/// The "Easy Setup" wizard API used by the admin panel. Everything the panel
/// needs to install, configure and verify the app lives here so a fresh server
/// can be brought up from the UI without editing appsettings.json.
/// </summary>
[ApiController]
[Route("api/setup")]
public sealed class SetupController : ControllerBase
{
    private readonly SetupService _setupService;
    private readonly ILogger<SetupController> _logger;

    public SetupController(SetupService setupService, ILogger<SetupController> logger)
    {
        _setupService = setupService;
        _logger = logger;
    }

    /// <summary>
    /// Live status of every dependency (database, Redis, Hangfire, Telegram).
    /// The panel renders the setup wizard from this. Anonymous: this is what
    /// the first-run screen needs before any admin session exists.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(SetupStatusDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SetupStatusDto>> GetStatus(CancellationToken cancellationToken)
    {
        return Ok(await _setupService.GetStatusAsync(cancellationToken));
    }

    /// <summary>
    /// Applies EF migrations and optionally seeds reference data.
    /// </summary>
    [HttpPost("database")]
    [Authorize]
    [ProducesResponseType(typeof(ApplyDatabaseResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApplyDatabaseResult>> ApplyDatabase(
        [FromBody] ApplyDatabaseRequest request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Panel requested database setup for provider {Provider}", request.DatabaseProvider);

        ApplyDatabaseResult result = await _setupService.ApplyDatabaseAsync(request, cancellationToken);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Pings Redis and reports latency and server version.
    /// </summary>
    [HttpPost("redis/test")]
    [Authorize]
    [ProducesResponseType(typeof(RedisTestResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<RedisTestResult>> TestRedis(CancellationToken cancellationToken)
    {
        return Ok(await _setupService.TestRedisAsync(cancellationToken));
    }

    /// <summary>
    /// Verifies the Telegram bot token against api.telegram.org.
    /// </summary>
    [HttpPost("telegram/test")]
    [Authorize]
    [ProducesResponseType(typeof(TelegramTestResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<TelegramTestResult>> TestTelegram(CancellationToken cancellationToken)
    {
        return Ok(await _setupService.TestTelegramAsync(cancellationToken));
    }
}
