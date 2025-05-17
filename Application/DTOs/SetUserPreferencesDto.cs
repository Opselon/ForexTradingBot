using System.ComponentModel.DataAnnotations;

namespace Application.DTOs
{
    public class SetUserPreferencesDto
    {
        [Required]
        public Guid UserId { get; set; } // یا از کاربر احراز هویت شده گرفته شود

        public IEnumerable<Guid> CategoryIds { get; set; } = new List<Guid>();
    }
}