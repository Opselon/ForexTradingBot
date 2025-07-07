using Application.Features.Dashboard.DTOs;
using Application.Features.Dashboard.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
// Remove Cookie Auth for now, as it might not be set up for API controllers by default
// using Microsoft.AspNetCore.Authorization;

namespace WebAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    // [Authorize] // Future: Add authorization if dashboard data is sensitive
    public class DashboardController : ControllerBase
    {
        private readonly IDashboardService _dashboardService;
        private readonly ILogger<DashboardController> _logger;

        public DashboardController(
            IDashboardService dashboardService,
            ILogger<DashboardController> logger)
        {
            _dashboardService = dashboardService;
            _logger = logger;
        }

        /// <summary>
        /// Gets aggregated data for the main dashboard.
        /// </summary>
        /// <returns>A DTO containing various statistics, chart data, and feeds for the dashboard.</returns>
        [HttpGet("data")]
        [ProducesResponseType(typeof(DashboardDataDto), 200)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<DashboardDataDto>> GetDashboardData()
        {
            _logger.LogInformation("API: Attempting to get all dashboard data.");
            try
            {
                var dashboardData = await _dashboardService.GetDashboardDataAsync();
                return Ok(dashboardData);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "API: Error occurred while fetching dashboard data.");
                // Return a generic error response. Specifics are logged.
                return StatusCode(500, "An error occurred while processing your request for dashboard data.");
            }
        }
    }
}
