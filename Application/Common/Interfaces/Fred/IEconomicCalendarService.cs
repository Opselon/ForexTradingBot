// File: Application/Interfaces/IEconomicCalendarService.cs
using Application.DTOs.Fred;
using Shared.Results;

namespace Application.Interfaces
{
    public interface IEconomicCalendarService
    {
        Task<Result<List<FredReleaseDto>>> GetReleasesAsync(int pageNumber, int pageSize, CancellationToken cancellationToken = default);
        Task<Result<List<FredSeriesDto>>> SearchSeriesAsync(string searchText, CancellationToken cancellationToken = default);
        Task<Result<FredReleaseTablesResponseDto>> GetReleaseTableTreeAsync(int releaseId, int? elementId, CancellationToken cancellationToken = default);
    }
}