using System.ComponentModel.DataAnnotations;

namespace Application.DTOs
{
    /// <summary>
    /// Data transfer object for creating a new signal category.
    /// </summary>
    public class CreateSignalCategoryDto
    {
        #region Properties

        /// <summary>
        /// Gets or sets the name of the signal category.
        /// </summary>
        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        #endregion
    }
}