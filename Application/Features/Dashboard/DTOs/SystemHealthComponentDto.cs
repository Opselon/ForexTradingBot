namespace Application.Features.Dashboard.DTOs
{
    public class SystemHealthComponentDto
    {
        public string Name { get; set; } = string.Empty; // e.g., "API Service", "Database"
        public string Status { get; set; } = "Unknown"; // e.g., "Operational", "Degraded", "Offline"
        public string StatusCssClass { get; set; } = "unknown"; // e.g., "ok", "degraded", "error" - for styling badge
        public string IconCssClass { get; set; } = "fas fa-question-circle"; // e.g., "fas fa-server", "fas fa-database"
    }
}
