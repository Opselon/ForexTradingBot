using System.ComponentModel.DataAnnotations;

namespace Application.DTOs
{
    /// <summary>
    /// Data transfer object for setting user preferences.
    /// </summary>
    public class SetUserPreferencesDto
    {
        #region Properties

        /// <summary>
        /// Gets or sets the unique identifier of the user.
        /// </summary>
        /// <remarks>
        /// This can also be obtained from the authenticated user context.
        /// </remarks>
        [Required]
        public Guid UserId { get; set; } // یا از کاربر احراز هویت شده گرفته شود

        /// <summary>
        /// Gets or sets the collection of category identifiers for user preferences.
        /// </summary>
        public IEnumerable<Guid> CategoryIds { get; set; } = new List<Guid>();

        #endregion
    }
}