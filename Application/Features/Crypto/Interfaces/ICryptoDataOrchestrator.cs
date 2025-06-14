using Shared.Results;

namespace Application.Features.Crypto.Interfaces
{
    public interface ICryptoDataOrchestrator
    {
        Task<Result<List<DTOs.Crypto.Dtos.UnifiedCryptoDto>>> GetCryptoListAsync(int page, int perPage, CancellationToken cancellationToken);
        Task<Result<DTOs.Crypto.Dtos.UnifiedCryptoDto>> GetCryptoDetailsAsync(string coinGeckoId, CancellationToken cancellationToken);
    }
}