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
    private readonly IConfiguration _configuration;
    private readonly ILogger<SetupController> _logger;

    public SetupController(
        SetupService setupService,
        IConfiguration configuration,
        ILogger<SetupController> logger)
    {
        _setupService = setupService;
        _configuration = configuration;
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
        string safeProvider = (request.DatabaseProvider ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
        _logger.LogInformation("Panel requested database setup for provider {Provider}", safeProvider);

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

    /// <summary>
    /// Changes the admin panel password. Persists it to the encrypted vault and
    /// rotates the server-side security stamp so cookies issued under the old
    /// password stop authenticating immediately.
    /// </summary>
    [HttpPost("admin/password")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public IActionResult ChangeAdminPassword([FromBody] ChangeAdminPasswordRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid request",
                Detail = "NewPassword is required."
            });
        }

        if (request.NewPassword.Length < 12)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Password too short",
                Detail = "The admin password must be at least 12 characters."
            });
        }

        if (!string.Equals(request.NewPassword, request.ConfirmPassword, StringComparison.Ordinal))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Passwords do not match",
                Detail = "NewPassword and ConfirmPassword must be identical."
            });
        }

        try
        {
            // The vault is the authoritative store; AuthController reads it when
            // the config value is empty, so this takes effect on the next login.
            ForexTradingBot.Cli.Secrets.SecretVaultBootstrap.Set("ADMIN_PASSWORD", request.NewPassword);
            _configuration["Admin:Password"] = request.NewPassword;

            // Invalidate every session issued under the old credential.
            WebAPI.Security.AuthSecurityStamp.Rotate();

            _logger.LogInformation("Admin password changed via the setup wizard.");
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to change the admin password.");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to change the admin password.");
        }
    }

    /// <summary>
    /// Saves the Telegram bot token to the encrypted vault. The running process
    /// picks it up on the next start; the response says so instead of claiming a
    /// hot reload the bot client cannot do.
    /// </summary>
    [HttpPost("telegram/token")]
    [Authorize]
    [ProducesResponseType(typeof(SaveTokenResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public IActionResult SaveBotToken([FromBody] SaveBotTokenRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.BotToken))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid request",
                Detail = "BotToken is required."
            });
        }

        // A Telegram bot token always contains a colon; catch a paste error here
        // rather than letting the bot client fail at startup.
        if (!request.BotToken.Contains(':'))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid token format",
                Detail = "A bot token looks like 123456789:ABC-DEF… and always contains a colon."
            });
        }

        try
        {
            ForexTradingBot.Cli.Secrets.SecretVaultBootstrap.Set("TELEGRAM_BOT_TOKEN", request.BotToken);
            _configuration["TelegramPanel:BotToken"] = request.BotToken;

            _logger.LogInformation("Telegram bot token saved via the setup wizard.");
            return Ok(new SaveTokenResult(
                Success: true,
                Message: "Token saved to the encrypted vault. Restart the core so the bot client reconnects with it.",
                RequiresRestart: true));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save the Telegram bot token.");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to save the bot token.");
        }
    }
}

/// <summary>Body for POST /api/setup/admin/password.</summary>
public sealed record ChangeAdminPasswordRequest(string NewPassword, string ConfirmPassword);

/// <summary>Body for POST /api/setup/telegram/token.</summary>
public sealed record SaveBotTokenRequest(string BotToken);

/// <summary>Result of POST /api/setup/telegram/token.</summary>
public sealed record SaveTokenResult(bool Success, string Message, bool RequiresRestart);
