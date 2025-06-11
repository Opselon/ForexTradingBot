// -----------------
// NEW FILE
// -----------------
using Application.DTOs.Fmp;
using Shared.Results;

namespace Application.Common.Interfaces
{
    /// <summary>
    /// Defines the contract for a client that interacts with the Financial Modeling Prep (FMP) API.
    /// This abstraction allows the application to remain independent of the specific HTTP implementation.
    /// </summary>
    public interface IFmpApiClient
    {
        /// <summary>
        /// Asynchronously fetches a full quote for a single cryptocurrency from FMP.
        /// </summary>
        /// <param name="fmpSymbol">The FMP-formatted symbol (e.g., "BTCUSD").</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A Result containing the full FmpQuoteDto on success.</returns>
        Task<Result<FmpQuoteDto>> GetFullCryptoQuoteAsync(string fmpSymbol, CancellationToken cancellationToken);
        Task<Result<List<FmpQuoteDto>>> GetFullCryptoQuoteListAsync(CancellationToken cancellationToken);
    }
}