using System.ComponentModel.DataAnnotations;

namespace Application.DTOs
{
    public class CreateSignalAnalysisDto
    {
        [Required]
        public Guid SignalId { get; set; }

        [Required]
        [StringLength(150)]
        public string AnalystName { get; set; } = string.Empty;

        [Required]
        [StringLength(2000)]
        public string Notes { get; set; } = string.Empty;
    }
}