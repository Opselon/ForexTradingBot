using Application.DTOs;
using Application.DTOs.Admin;
using Application.Interfaces;
using Domain.Enums;
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
    private readonly ISubscriptionService _subscriptionService;
    private readonly ILogger<UsersController> _logger;

    public UsersController(
        IUserService userService,
        IAdminService adminService,
        ISubscriptionService subscriptionService,
        ILogger<UsersController> logger)
    {
        _userService = userService;
        _adminService = adminService;
        _subscriptionService = subscriptionService;
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
    /// Registers a new user (panel path — builds the entity + wallet itself).
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UserDto>> Register(
        [FromBody] RegisterUserDto registerDto,
        CancellationToken cancellationToken)
    {
        if (registerDto == null)
        {
            return BadRequest("Registration data is required.");
        }

        try
        {
            UserDto created = await _userService.RegisterUserAsync(registerDto, cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register user.");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to register user.");
        }
    }

    /// <summary>
    /// Changes a user's access level (Free/Bronze/Silver/Gold/Platinum/Admin).
    /// </summary>
    [HttpPatch("{id:guid}/level")]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserDto>> SetLevel(
        Guid id,
        [FromBody] SetLevelRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Level))
        {
            return BadRequest("Level is required (Free, Bronze, Silver, Gold, Platinum, Admin).");
        }

        if (!Enum.TryParse<UserLevel>(request.Level, ignoreCase: true, out UserLevel level)
            || !Enum.IsDefined(typeof(UserLevel), level))
        {
            return BadRequest($"Unknown level '{request.Level}'. Valid: Free, Bronze, Silver, Gold, Platinum, Admin.");
        }

        try
        {
            UserDto updated = await _userService.SetLevelAsync(id, level, cancellationToken);
            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set level for user {UserId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to set user level.");
        }
    }

    /// <summary>
    /// Lists all subscriptions of a user.
    /// </summary>
    [HttpGet("{id:guid}/subscriptions")]
    [ProducesResponseType(typeof(IEnumerable<SubscriptionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<SubscriptionDto>>> GetSubscriptions(
        Guid id, CancellationToken cancellationToken)
    {
        UserDto? user = await _userService.GetUserByIdAsync(id, cancellationToken);
        if (user == null)
        {
            return NotFound($"User with ID {id} not found.");
        }

        IEnumerable<SubscriptionDto> subs =
            await _subscriptionService.GetUserSubscriptionsAsync(id, cancellationToken);
        return Ok(subs);
    }

    /// <summary>
    /// Creates a subscription for a user.
    /// </summary>
    [HttpPost("{id:guid}/subscriptions")]
    [ProducesResponseType(typeof(SubscriptionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SubscriptionDto>> CreateSubscription(
        Guid id,
        [FromBody] CreateUserSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        UserDto? user = await _userService.GetUserByIdAsync(id, cancellationToken);
        if (user == null)
        {
            return NotFound($"User with ID {id} not found.");
        }

        if (request == null)
        {
            return BadRequest("Subscription dates are required.");
        }

        if (request.EndDate <= request.StartDate)
        {
            return BadRequest("EndDate must be later than StartDate.");
        }

        try
        {
            SubscriptionDto created = await _subscriptionService.CreateSubscriptionAsync(
                new CreateSubscriptionDto
                {
                    UserId = id,
                    StartDate = request.StartDate,
                    EndDate = request.EndDate,
                },
                cancellationToken);
            return CreatedAtAction(nameof(GetSubscriptions), new { id }, created);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create subscription for user {UserId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to create subscription.");
        }
    }

    /// <summary>
    /// Deletes a subscription by ID.
    /// </summary>
    [HttpDelete("subscriptions/{subscriptionId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteSubscription(
        Guid subscriptionId, CancellationToken cancellationToken)
    {
        bool deleted =
            await _subscriptionService.DeleteSubscriptionAsync(subscriptionId, cancellationToken);
        if (!deleted)
        {
            return NotFound($"Subscription {subscriptionId} not found.");
        }

        return NoContent();
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

/// <summary>Body for PATCH /api/users/{id}/level.</summary>
public sealed class SetLevelRequest
{
    public string Level { get; set; } = string.Empty;
}

/// <summary>Body for POST /api/users/{id}/subscriptions.</summary>
public sealed class CreateUserSubscriptionRequest
{
    public DateTime StartDate { get; set; } = DateTime.UtcNow;
    public DateTime EndDate { get; set; } = DateTime.UtcNow.AddDays(30);
}
