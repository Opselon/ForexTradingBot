using Application.Common.Interfaces;
using Application.DTOs.Admin; // For DynamicSettingDto if used directly, or just rely on IDynamicSetting
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WebAPI.Controllers
{
    [ApiController]
    [Route("api/settings")]
    [Authorize(Roles = "Admin")]
    public class DynamicSettingsController : ControllerBase
    {
        private readonly IDynamicConfigurationService _dynamicConfigService;
        private readonly ILogger<DynamicSettingsController> _logger;

        public DynamicSettingsController(
            IDynamicConfigurationService dynamicConfigService,
            ILogger<DynamicSettingsController> logger)
        {
            _dynamicConfigService = dynamicConfigService;
            _logger = logger;
        }

        /// <summary>
        /// Retrieves all manageable application settings.
        /// Sensitive values are masked for display.
        /// </summary>
        [HttpGet("all")]
        [ProducesResponseType(typeof(IEnumerable<IDynamicSetting>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetAllSettings(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Attempting to retrieve all dynamic settings for admin UI.");
            try
            {
                IEnumerable<IDynamicSetting> settings = await _dynamicConfigService.GetAllSettingsAsync(cancellationToken);
                // Convert to concrete DTO if necessary for serialization, though IEnumerable<IDynamicSetting> should work if DynamicSettingDto implements it.
                // SECURITY: never serialize the raw value of a sensitive setting to the
                // client. The DTO contract includes both `value` and `displayValue` in
                // the JSON, so passing s.Value through here leaks secrets (the admin
                // password was observed in a live response). Sensitive keys get only
                // the masked display string; the raw value stays server-side.
                List<DynamicSettingDto> settingsDto = settings.Select(s => new DynamicSettingDto(
                    s.Key,
                    s.IsSensitive ? null : s.Value,
                    s.DisplayValue, // Masked/display value from service
                    s.IsSensitive,
                    s.Description,
                    s.IsPersistedInDb,
                    s.IsOverriddenByEnvironment,
                    s.LastModifiedUtc // Now directly accessible from IDynamicSetting
                )).ToList();
                _logger.LogInformation("Successfully retrieved {Count} dynamic settings.", settingsDto.Count);
                return Ok(settingsDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving all dynamic settings.");
                return StatusCode(StatusCodes.Status500InternalServerError, "An error occurred while retrieving settings.");
            }
        }

        public class UpdateSettingsRequest
        {
            // Allows client to send only the settings they want to change
            public Dictionary<string, string?> SettingsToUpdate { get; set; } = [];
        }

        /// <summary>
        /// Updates one or more application settings.
        /// </summary>
        /// <param name="request">The settings to update.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        [HttpPost("update")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> UpdateSettings([FromBody] UpdateSettingsRequest request, CancellationToken cancellationToken)
        {
            if (request == null || request.SettingsToUpdate == null || !request.SettingsToUpdate.Any())
            {
                ModelState.AddModelError("SettingsToUpdate", "No settings provided for update.");
                return BadRequest(new ValidationProblemDetails(ModelState));
            }

            _logger.LogInformation("Attempting to update {Count} dynamic settings.", request.SettingsToUpdate.Count);
            try
            {
                await _dynamicConfigService.UpdateSettingsAsync(request.SettingsToUpdate, cancellationToken);
                _logger.LogInformation("Dynamic settings updated successfully via API.");
                // Consider what to return. 204 is good for successful update.
                // The client might want the updated settings list, but a separate GET is cleaner.
                return NoContent();
            }
            catch (ArgumentException ex) // Catch specific exceptions for bad input if thrown by service
            {
                _logger.LogWarning(ex, "Invalid arguments provided for settings update.");
                ModelState.AddModelError("SettingsToUpdate", ex.Message);
                return BadRequest(new ValidationProblemDetails(ModelState));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating dynamic settings.");
                // Avoid exposing raw exception details to client unless it's a controlled message
                return StatusCode(StatusCodes.Status500InternalServerError, "An error occurred while updating settings.");
            }
        }
    }
}
