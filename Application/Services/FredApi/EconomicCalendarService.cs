// File: Application/Services/FredApi/EconomicCalendarService.cs
using Application.Common.Interfaces.Fred;
using Application.DTOs.Fred;
using Shared.Results;

namespace Application.Services.FredApi
{
    public class EconomicCalendarService : IEconomicCalendarService
    {
        private readonly IFredApiClient _fredApiClient;

        public EconomicCalendarService(IFredApiClient fredApiClient)
        {
            _fredApiClient = fredApiClient;
        }

        /// <summary>
        /// Searches for economic data series using the FRED API.
        /// Handles potential network and API errors.
        /// </summary>
        /// <param name="searchText">The text to search for.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A Result object containing a list of matching series on success, or errors on failure.</returns>
        public async Task<Result<List<FredSeriesDto>>> SearchSeriesAsync(string searchText, CancellationToken cancellationToken = default)
        {
            // Basic validation (optional but good practice)
            if (string.IsNullOrWhiteSpace(searchText))
            {
                return Result<List<FredSeriesDto>>.Failure("Search text cannot be empty.");
            }

            try
            {
                // Call the external FRED API client to perform the search.
                // This is the primary point of failure requiring a try-catch for technical issues.
                // We limit results to 10 for UI simplicity.
                Result<FredSeriesSearchResponseDto> result = await _fredApiClient.SearchEconomicSeriesAsync(searchText, 10, cancellationToken);

                // Check the functional result returned by the API client.
                if (result.Succeeded)
                {
                    // If successful, return the list of series.
                    // Using null-forgiving operator assumes _fredApiClient guarantees Data is non-null on success.
                    return Result<List<FredSeriesDto>>.Success(result.Data!.Series);
                }
                else
                {
                    // If the API client reported a functional error, return those errors.
                    // Using null-conditional operator for safety accessing Errors.
                    return Result<List<FredSeriesDto>>.Failure(result.Errors ?? ["FRED API search failed with no specific errors reported."]);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically.
                // Log if needed.
                // _logger.LogInformation(ex, "FRED API search was cancelled for text '{SearchText}'.", searchText);
                return Result<List<FredSeriesDto>>.Failure($"Search request cancelled for '{searchText}'.");
            }
            catch (Exception)
            {
                // Catch any other unexpected technical exceptions (network issues, parsing errors etc.)
                // Log the detailed technical error for debugging.
                // _logger.LogError(ex, "An unexpected error occurred while searching FRED series for text '{SearchText}'.", searchText);

                // Return a generic failure result to the caller.
                return Result<List<FredSeriesDto>>.Failure($"An unexpected error occurred while searching. Please try again.");
                // Or include more detail for internal tracing/debugging:
                // return Result<List<FredSeriesDto>>.Failure($"An unexpected error occurred while searching. Please try again. (Details: {ex.Message})");
            }
        }


        /// <summary>
        /// Fetches the table tree structure for a specific economic release from the FRED API.
        /// Handles potential network and API errors.
        /// </summary>
        /// <param name="releaseId">The ID of the economic release.</param>
        /// <param name="elementId">Optional ID of a specific element within the release tree.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A Result object containing the release table structure on success, or errors on failure.</returns>
        public async Task<Result<FredReleaseTablesResponseDto>> GetReleaseTableTreeAsync(int releaseId, int? elementId, CancellationToken cancellationToken = default)
        {
            // Basic validation (optional)
            if (releaseId <= 0)
            {
                return Result<FredReleaseTablesResponseDto>.Failure("Release ID must be positive.");
            }
            // elementId can be null, so no validation needed unless you have specific constraints.

            try
            {
                // Call the external FRED API client to get the release table tree.
                // This is the primary point of failure requiring a try-catch for technical issues.
                // The _fredApiClient method is expected to return a Result<T>, which handles
                // functional API errors internally and packages them into the Result.
                Result<FredReleaseTablesResponseDto> result = await _fredApiClient.GetReleaseTablesAsync(releaseId, elementId, cancellationToken);

                // Directly return the result obtained from the API client.
                // If the API client throws a technical exception (like network error),
                // the catch block below will handle it.
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically.
                // Log this as an informational event if desired.
                // _logger.LogInformation(ex, "FRED API call for release table tree was cancelled for release {ReleaseId}, element {ElementId}.", releaseId, elementId);
                return Result<FredReleaseTablesResponseDto>.Failure($"Request cancelled for release {releaseId}.");
            }
            catch (Exception)
            {
                // Catch any other unexpected technical exceptions (network issues, parsing errors etc.)
                // Log the detailed technical error for debugging.
                // _logger.LogError(ex, "An unexpected error occurred while fetching FRED release table tree for release {ReleaseId}, element {ElementId}.", releaseId, elementId);

                // Return a generic failure result to the caller.
                // The caller should display a user-friendly message based on this failure.
                return Result<FredReleaseTablesResponseDto>.Failure($"An unexpected error occurred while fetching the release structure. Please try again.");
                // You might include ex.Message for internal logging but avoid exposing raw error messages to the user.
            }
        }





        /// <summary>
        /// Fetches economic release data from the FRED API with pagination.
        /// Handles potential network and API errors.
        /// </summary>
        /// <param name="pageNumber">The desired page number (1-based).</param>
        /// <param name="pageSize">The number of releases per page.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A Result object containing the list of releases on success, or errors on failure.</returns>
        public async Task<Result<List<FredReleaseDto>>> GetReleasesAsync(int pageNumber, int pageSize, CancellationToken cancellationToken = default)
        {
            // Basic validation (optional but good practice)
            if (pageNumber <= 0)
            {
                return Result<List<FredReleaseDto>>.Failure("Page number must be positive.");
            }
            if (pageSize <= 0)
            {
                return Result<List<FredReleaseDto>>.Failure("Page size must be positive.");
            }


            int offset = (pageNumber - 1) * pageSize;

            try
            {
                // Call the external FRED API client.
                // This is the primary point of failure requiring a try-catch.
                Result<FredReleasesResponseDto> result = await _fredApiClient.GetEconomicReleasesAsync(pageSize, offset, cancellationToken);

                // Check the functional result from the API client's perspective.
                if (result.Succeeded)
                {
                    // If successful, return the data. Assume Data.Releases is not null based on Succeeded.
                    // Using null-forgiving operator (!) is acceptable here if the contract of _fredApiClient
                    // guarantees Data is non-null when Succeeded is true.
                    return Result<List<FredReleaseDto>>.Success(result.Data!.Releases);
                }
                else
                {
                    // If the API client reported a functional error, return those errors.
                    // Using null-conditional operator for safety when accessing Errors.
                    return Result<List<FredReleaseDto>>.Failure(result.Errors ?? ["FRED API request failed with no specific errors reported."]);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically if needed.
                // This can happen if the CancellationToken passed from the caller is cancelled.
                // Log this as an informational event, not necessarily an error.
                // _logger.LogInformation(ex, "FRED API call was cancelled for page {Page}, size {Size}.", pageNumber, pageSize);
                return Result<List<FredReleaseDto>>.Failure($"Request cancelled for page {pageNumber}."); // Informative error
            }
            catch (Exception ex)
            {
                // Catch any other unexpected exceptions (network issues, parsing errors, etc.)
                // Log the technical error with full details.
                // _logger.LogError(ex, "An unexpected error occurred while fetching FRED releases for page {Page}, size {Size}.", pageNumber, pageSize);

                // Return a generic failure result to the caller.
                // The caller (e.g., HandleReleasesViewAsync) should display a user-friendly message.
                return Result<List<FredReleaseDto>>.Failure($"An unexpected error occurred while fetching economic releases. Please try again. (Details: {ex.Message})");
                // Or a more generic message depending on how much detail you want to expose:
                // return Result<List<FredReleaseDto>>.Failure("An unexpected error occurred while fetching economic releases. Please try again.");
            }
        }

    }
}