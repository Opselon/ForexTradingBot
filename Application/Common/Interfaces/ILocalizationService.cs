// File: Application/Common/Interfaces/ILocalizationService.cs
#region Usings
using System.Globalization; // برای CultureInfo
#endregion

namespace Application.Common.Interfaces
{
    /// <summary>
    /// Defines the contract for a service that provides localized string resources.
    /// </summary>
    public interface ILocalizationService
    {
        /// <summary>
        /// Gets the localized string for the specified resource key and culture.
        /// </summary>
        /// <param name="resourceKey">The key of the string resource.</param>
        /// <param name="cultureInfo">The culture for which to get the localized string. If null, current UI culture is used.</param>
        /// <returns>The localized string, or the key itself if not found.</returns>
        string GetString(string resourceKey, CultureInfo? cultureInfo = null);

        /// <summary>
        /// Gets the localized string for the specified resource key and formats it with the provided arguments.
        /// </summary>
        /// <param name="resourceKey">The key of the string resource.</param>
        /// <param name="cultureInfo">The culture for which to get the localized string. If null, current UI culture is used.</param>
        /// <param name="arguments">The arguments to format the string with.</param>
        /// <returns>The formatted localized string, or the key itself if not found.</returns>
        string GetString(string resourceKey, CultureInfo? cultureInfo = null, params object[] arguments);

        /// <summary>
        /// Gets the localized string for the specified resource key using the user's preferred language.
        /// </summary>
        /// <param name="userId">The system ID of the user to get their preferred language.</param>
        /// <param name="resourceKey">The key of the string resource.</param>
        /// <param name="arguments">The arguments to format the string with.</param>
        /// <returns>The localized string.</returns>
        Task<string> GetUserLocalizedStringAsync(Guid userId, string resourceKey, params object[] arguments);
    }
}