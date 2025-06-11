using Application.DTOs.Fmp;
using Shared.Results;
namespace Application.Features.Fmp.Interfaces
{
    public interface IFmpService
    {
        Task<Result<List<FmpQuoteDto>>> GetTopCryptosAsync(int count, CancellationToken cancellationToken);
        Task<Result<FmpQuoteDto>> GetCryptoDetailsAsync(string fmpSymbol, CancellationToken cancellationToken);
    }
}