using Application.Features.Dashboard.DTOs;
using System.Threading.Tasks;

namespace Application.Features.Dashboard.Interfaces
{
    public interface IDashboardService
    {
        /// <summary>
        /// Gathers and processes all data required for the main dashboard.
        /// </summary>
        /// <returns>A DTO containing all necessary dashboard data.</returns>
        Task<DashboardDataDto> GetDashboardDataAsync();
    }
}
