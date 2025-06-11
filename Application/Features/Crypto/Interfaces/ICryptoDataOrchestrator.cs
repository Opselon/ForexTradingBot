// -----------------
// NEW FILE
// -----------------
using Application.Features.Crypto.Dtos;
using Shared.Results;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Application.Features.Crypto.Interfaces
{
    public interface ICryptoDataOrchestrator
    {
        Task<Result<List<UnifiedCryptoDto>>> GetCryptoListAsync(int page, int perPage, CancellationToken cancellationToken);
        Task<Result<UnifiedCryptoDto>> GetCryptoDetailsAsync(string coinGeckoId, CancellationToken cancellationToken);
    }
}