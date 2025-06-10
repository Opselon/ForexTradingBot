// File: Application/Services/FredApi/EconomicCalendarService.cs
using Application.Common.Interfaces;
using Application.DTOs.Fred;
using Application.Interfaces;
using Shared.Results;

namespace Application.Services
{
    public class EconomicCalendarService : IEconomicCalendarService
    {
        private readonly IFredApiClient _fredApiClient;

        public EconomicCalendarService(IFredApiClient fredApiClient)
        {
            _fredApiClient = fredApiClient;
        }

        public async Task<Result<List<FredReleaseDto>>> GetReleasesAsync(int pageNumber, int pageSize, CancellationToken cancellationToken = default)
        {
            int offset = (pageNumber - 1) * pageSize;
            var result = await _fredApiClient.GetEconomicReleasesAsync(pageSize, offset, cancellationToken);

            if (result.Succeeded)
            {
                // We could add more logic here, like caching or filtering, before returning.
                return Result<List<FredReleaseDto>>.Success(result.Data!.Releases);
            }

            return Result<List<FredReleaseDto>>.Failure(result.Errors);
        }
    }
}