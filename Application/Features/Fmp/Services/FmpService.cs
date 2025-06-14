using Application.Common.Interfaces;
using Application.DTOs.Fmp;
using Application.Features.Fmp.Interfaces;
using Microsoft.Extensions.Logging;
using Shared.Results;
namespace Application.Features.Fmp.Services
{
    public class FmpService : IFmpService
    {
        private readonly IFmpApiClient _apiClient;
        private readonly ILogger<FmpService> _logger;
        public FmpService(IFmpApiClient apiClient, ILogger<FmpService> logger) { _apiClient = apiClient; _logger = logger; }
        public async Task<Result<List<FmpQuoteDto>>> GetTopCryptosAsync(int count, CancellationToken cancellationToken)
        {
            Result<List<FmpQuoteDto>> quotesResult = await _apiClient.GetFullCryptoQuoteListAsync(cancellationToken);
            if (!quotesResult.Succeeded || quotesResult.Data == null) { return Result<List<FmpQuoteDto>>.Failure(quotesResult.Errors); }
            List<FmpQuoteDto> topCryptos = quotesResult.Data.Where(q => q.MarketCap.HasValue && q.MarketCap > 0 && q.Name != null).OrderByDescending(q => q.MarketCap).Take(count).ToList();
            return Result<List<FmpQuoteDto>>.Success(topCryptos);
        }
        public async Task<Result<FmpQuoteDto>> GetCryptoDetailsAsync(string fmpSymbol, CancellationToken cancellationToken)
        {
            return await _apiClient.GetFullCryptoQuoteAsync(fmpSymbol, cancellationToken);
        }
    }
}