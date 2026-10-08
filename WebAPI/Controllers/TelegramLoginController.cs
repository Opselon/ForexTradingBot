using Application.Common.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebAPI.Models.Telegram;
using WebAPI.Setup;

namespace WebAPI.Controllers;

/// <summary>
/// Telegram user-account login from the panel. Replaces the console prompt:
/// the user enters their phone number, receives a code from Telegram, types it
/// in the panel, and (when the account has 2FA) the password — no appsettings
/// editing, no process restart.
/// </summary>
[ApiController]
[Route("api/telegram/login")]
[Authorize]
public sealed class TelegramLoginController : ControllerBase
{
    private readonly TelegramLoginService _loginService;
    private readonly ILogger<TelegramLoginController> _logger;

    public TelegramLoginController(TelegramLoginService loginService, ILogger<TelegramLoginController> logger)
    {
        _loginService = loginService;
        _logger = logger;
    }

    /// <summary>
    /// Step 1 — submit API credentials + phone number. Telegram sends a login code.
    /// </summary>
    [HttpPost("start")]
    [ProducesResponseType(typeof(StartLoginResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<StartLoginResult>> Start(
        [FromBody] StartLoginRequest request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Panel: starting Telegram user login for phone ending in {Suffix}",
            request.PhoneNumber.Length >= 4 ? request.PhoneNumber[^4..] : "****");

        StartLoginResult result = await _loginService.StartLoginAsync(request, cancellationToken);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Step 2 — the login code, and the 2FA password when the account requires one.
    /// Safe to call repeatedly: the flow reports which input it still needs.
    /// </summary>
    [HttpPost("verify")]
    [ProducesResponseType(typeof(VerifyLoginResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<VerifyLoginResult>> Verify(
        [FromBody] VerifyLoginRequest request,
        CancellationToken cancellationToken)
    {
        VerifyLoginResult result = await _loginService.VerifyLoginAsync(request, cancellationToken);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Reports whether a user session is already active.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(VerifyLoginResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<VerifyLoginResult>> Status(
        [FromServices] ITelegramUserApiClient client,
        CancellationToken cancellationToken)
    {
        return Ok(await _loginService.GetStatusAsync(client, cancellationToken));
    }
}
