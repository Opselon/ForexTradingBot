using System.ComponentModel.DataAnnotations;

namespace Application.DTOs
{
    public class CreateSignalCategoryDto
    {
        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;
    }
}