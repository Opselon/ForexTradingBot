// File: Application/Interfaces/IEconomicCalendarService.cs
using Application.DTOs.Fred;
using Shared.Results;

namespace Application.Interfaces
{
    public interface IEconomicCalendarService
    {
        Task<Result<List<FredReleaseDto>>> GetReleasesAsync(int pageNumber, int pageSize, CancellationToken cancellationToken = default);
    }
}