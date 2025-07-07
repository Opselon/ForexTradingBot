using Application.Features.WebPanelConfiguration.DTOs;
using Application.Features.WebPanelConfiguration.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class WebPanelConfigController : ControllerBase
    {
        private readonly IWebPanelConfigService _configService;
        private readonly ILogger<WebPanelConfigController> _logger;

        public WebPanelConfigController(
            IWebPanelConfigService configService,
            ILogger<WebPanelConfigController> logger)
        {
            _configService = configService;
            _logger = logger;
        }

        // GET: api/webpanelconfig (gets the active one)
        [HttpGet]
        [ProducesResponseType(typeof(WebPanelConfigDto), 200)]
        [ProducesResponseType(404)]
        public async Task<ActionResult<WebPanelConfigDto>> GetActiveConfiguration()
        {
            _logger.LogInformation("API: Attempting to get active web panel configuration.");
            var config = await _configService.GetActiveConfigAsync();
            if (config == null)
            {
                _logger.LogWarning("API: No active web panel configuration found.");
                return NotFound("No active configuration found.");
            }
            return Ok(config);
        }

        // GET: api/webpanelconfig/all
        [HttpGet("all")]
        [ProducesResponseType(typeof(IEnumerable<WebPanelConfigDto>), 200)]
        public async Task<ActionResult<IEnumerable<WebPanelConfigDto>>> GetAllConfigurations()
        {
            _logger.LogInformation("API: Attempting to get all web panel configurations.");
            var configs = await _configService.GetAllConfigsAsync();
            return Ok(configs);
        }

        // GET: api/webpanelconfig/{id}
        [HttpGet("{id:int}")]
        [ProducesResponseType(typeof(WebPanelConfigDto), 200)]
        [ProducesResponseType(404)]
        public async Task<ActionResult<WebPanelConfigDto>> GetConfigurationById(int id)
        {
            _logger.LogInformation("API: Attempting to get web panel configuration with ID: {ConfigId}", id);
            var config = await _configService.GetConfigByIdAsync(id);
            if (config == null)
            {
                _logger.LogWarning("API: Web panel configuration with ID: {ConfigId} not found.", id);
                return NotFound($"Configuration with ID {id} not found.");
            }
            return Ok(config);
        }

        // POST: api/webpanelconfig
        [HttpPost]
        [ProducesResponseType(typeof(WebPanelConfigDto), 201)]
        [ProducesResponseType(400)]
        public async Task<ActionResult<WebPanelConfigDto>> CreateConfiguration([FromBody] CreateWebPanelConfigDto createDto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }
            _logger.LogInformation("API: Attempting to create a new web panel configuration.");
            var createdConfig = await _configService.AddConfigAsync(createDto);
            _logger.LogInformation("API: Successfully created web panel configuration with ID: {ConfigId}", createdConfig.Id);
            return CreatedAtAction(nameof(GetConfigurationById), new { id = createdConfig.Id }, createdConfig);
        }

        // PUT: api/webpanelconfig/{id}
        [HttpPut("{id:int}")]
        [ProducesResponseType(204)] // No Content
        [ProducesResponseType(400)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> UpdateConfiguration(int id, [FromBody] UpdateWebPanelConfigDto updateDto)
        {
            if (id != updateDto.Id)
            {
                _logger.LogWarning("API: Mismatch in route ID ({RouteId}) and payload ID ({PayloadId}) for update.", id, updateDto.Id);
                return BadRequest("ID mismatch between route and payload.");
            }

            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            _logger.LogInformation("API: Attempting to update web panel configuration with ID: {ConfigId}", id);
            var success = await _configService.UpdateConfigAsync(updateDto);
            if (!success)
            {
                _logger.LogWarning("API: Update failed for web panel configuration with ID: {ConfigId}. It might not exist.", id);
                return NotFound($"Configuration with ID {id} not found for update.");
            }

            _logger.LogInformation("API: Successfully updated web panel configuration with ID: {ConfigId}", id);
            return NoContent();
        }

        // DELETE: api/webpanelconfig/{id}
        [HttpDelete("{id:int}")]
        [ProducesResponseType(204)] // No Content
        [ProducesResponseType(404)]
        public async Task<IActionResult> DeleteConfiguration(int id)
        {
            _logger.LogInformation("API: Attempting to delete web panel configuration with ID: {ConfigId}", id);
            var success = await _configService.DeleteConfigAsync(id);
            if (!success)
            {
                _logger.LogWarning("API: Delete failed for web panel configuration with ID: {ConfigId}. It might not exist.", id);
                return NotFound($"Configuration with ID {id} not found for deletion.");
            }
            _logger.LogInformation("API: Successfully deleted web panel configuration with ID: {ConfigId}", id);
            return NoContent();
        }
    }
}
