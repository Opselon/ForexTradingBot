// -----------------
// NEW FILE
// -----------------
using Application.DTOs.CoinGecko;
using Shared.Results;

namespace Application.Common.Interfaces.CoinGeckoApiClient
{
    /// <summary>
    /// Defines the contract for a client that interacts with the public CoinGecko API.
    /// This abstraction decouples the application from the concrete HTTP implementation.
    /// </summary>
    public interface ICoinGeckoApiClient
    {





        /// <summary>
        /// Asynchronously fetches the list of top-7 trending coins from CoinGecko.
        /// This endpoint is free and does not require an API key.
        /// </summary>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A Result containing a list of TrendingCoinDto objects on success.</returns>
        Task<Result<List<TrendingCoinDto>>> GetTrendingCoinsAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Asynchronously fetches detailed information for a single coin by its CoinGecko ID.
        /// </summary>
        /// <param name="id">The unique ID of the coin (e.g., "bitcoin").</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A Result containing the detailed CoinDetailsDto for the specified coin on success.</returns>
        Task<Result<CoinDetailsDto>> GetCoinDetailsAsync(string id, CancellationToken cancellationToken);


        /// <summary>
        /// Asynchronously fetches a paginated list of coins from the CoinGecko markets endpoint.
        /// </summary>
        /// <param name="page">The page number to retrieve.</param>
        /// <param name="perPage">The number of coins to retrieve per page.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A Result containing a list of CoinMarketDto objects on success.</returns>
        Task<Result<List<CoinMarketDto>>> GetCoinMarketsAsync(int page, int perPage, CancellationToken cancellationToken);
    }
}