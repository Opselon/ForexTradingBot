// -----------------
// NEW FILE
// -----------------
using Application.DTOs.Fmp;
using Shared.Results;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Application.Common.Interfaces
{
    /// <summary>
    /// Defines the contract for a client that interacts with the Financial Modeling Prep (FMP) API.
    /// This abstraction allows the application to remain independent of the specific HTTP implementation.
    /// </summary>
    public interface IFmpApiClient
    {
        /// <summary>
        /// Asynchronously fetches a list of quotes for all available cryptocurrencies from FMP.
        /// </summary>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A Result containing a list of FmpQuoteDto objects on success.</returns>
        Task<Result<List<FmpQuoteDto>>> GetCryptoQuotesAsync(CancellationToken cancellationToken);
    }
}