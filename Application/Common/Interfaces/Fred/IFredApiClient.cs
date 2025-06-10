// File: Application/Common/Interfaces/IFredApiClient.cs
using Application.DTOs.Fred;
using Shared.Results; // Assuming you have a Result<T> type

namespace Application.Common.Interfaces
{
    public interface IFredApiClient
    {
        Task<Result<FredReleasesResponseDto>> GetEconomicReleasesAsync(int limit = 50, int offset = 0, CancellationToken cancellationToken = default);

        Task<Result<FredSeriesSearchResponseDto>> SearchEconomicSeriesAsync(string searchText, int limit = 10, CancellationToken cancellationToken = default);
        Task<Result<FredReleaseTablesResponseDto>> GetReleaseTablesAsync(int releaseId, int? elementId = null, CancellationToken cancellationToken = default);
    }
}