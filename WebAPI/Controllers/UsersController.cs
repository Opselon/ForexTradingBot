using Application.DTOs;
using Application.DTOs.Admin;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WebAPI.Controllers;

[ApiController]
[Route("api/users")]
[Authorize]
public sealed class UsersController : ControllerBase
{
    private readonly IUserService _userService;
    private readonly IAdminService _adminService;
    private readonly ILogger<UsersController> _logger;

    public UsersController(
        IUserService userService,
        IAdminService adminService,
        ILogger<UsersController> logger)
    {
        _userService = userService;
        _adminService = adminService;
        _logger = logger;
    }

    /// <summary>
    /// Gets all registered users.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<UserDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<UserDto>>> GetAll(CancellationToken cancellationToken)
    {
        try
        {
            List<UserDto> users = await _userService.GetAllUsersAsync(cancellationToken);
            return Ok(users);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve users.");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to retrieve users.");
        }
    }

    /// <summary>
    /// Gets a user by internal Guid ID.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        UserDto? user = await _userService.GetUserByIdAsync(id, cancellationToken);
        if (user == null)
        {
            return NotFound($"User with ID {id} not found.");
        }
        return Ok(user);
    }

    /// <summary>
    /// Gets a user by Telegram ID, optionally returning rich admin detail (subscriptions, wallet, transactions).
    /// </summary>
    [HttpGet("telegram/{telegramId}")]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AdminUserDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByTelegramId(
        string telegramId,
        [FromQuery] bool detail = false,
        CancellationToken cancellationToken = default)
    {
        if (detail && long.TryParse(telegramId, out long tgIdLong))
        {
            AdminUserDetailDto? adminDetail = await _adminService.GetUserDetailByTelegramIdAsync(tgIdLong, cancellationToken);
            if (adminDetail != null)
            {
                return Ok(adminDetail);
            }
        }

        UserDto? user = await _userService.GetUserByTelegramIdAsync(telegramId, cancellationToken);
        if (user == null)
        {
            return NotFound($"User with Telegram ID {telegramId} not found.");
        }
        return Ok(user);
    }

    /// <summary>
    /// Updates user details.
    /// </summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateUserDto updateDto,
        CancellationToken cancellationToken)
    {
        if (updateDto == null)
        {
            return BadRequest("Update data is required.");
        }

        try
        {
            await _userService.UpdateUserAsync(id, updateDto, cancellationToken);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update user {UserId}", id);
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Deletes a user.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await _userService.DeleteUserAsync(id, cancellationToken);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete user {UserId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to delete user.");
        }
    }

    /// <summary>
    /// Marks a user as unreachable (e.g. blocked the bot).
    /// </summary>
    [HttpPost("unreachable")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> MarkUnreachable(
        [FromQuery] string telegramId,
        [FromQuery] string reason = "Marked by admin",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(telegramId))
        {
            return BadRequest("telegramId is required.");
        }

        await _userService.MarkUserAsUnreachableAsync(telegramId, reason, cancellationToken);
        return Ok(new { message = $"User {telegramId} marked as unreachable.", reason });
    }
}
