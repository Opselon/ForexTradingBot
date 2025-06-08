// NEW, CLEAN INTERFACE
namespace Application.Interfaces
{
    public interface IDashboardService
    {
        Task<(int UserCount, int NewsItemCount)> GetDashboardStatsAsync(CancellationToken cancellationToken = default);
    }
}