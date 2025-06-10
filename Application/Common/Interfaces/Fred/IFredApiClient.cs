// File: Application/Common/Interfaces/IFredApiClient.cs
using Application.DTOs.Fred;
using Shared.Results; // Assuming you have a Result<T> type

namespace Application.Common.Interfaces
{
    public interface IFredApiClient
    {
        Task<Result<FredReleasesResponseDto>> GetEconomicReleasesAsync(int limit = 50, int offset = 0, CancellationToken cancellationToken = default);
    }
}