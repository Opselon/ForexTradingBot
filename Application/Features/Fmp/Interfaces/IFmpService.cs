using Application.DTOs.Fmp;
using Shared.Results;

namespace Application.Features.Fmp.Interfaces
{
    /// <summary>
    /// Defines the application service contract for handling business logic related to
    /// cryptocurrency data obtained from the FMP source. This will act as our fallback service.
    /// </summary>
    public interface IFmpService
    {
        /// <summary>
        /// Gets a list of the top N cryptocurrencies by market cap from FMP.
        /// </summary>
        /// <param name="count">The number of top cryptos to return.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A Result containing a list of top FmpQuoteDto objects on success.</returns>
        Task<Result<List<FmpQuoteDto>>> GetTopCryptosAsync(int count, CancellationToken cancellationToken);
    }
}