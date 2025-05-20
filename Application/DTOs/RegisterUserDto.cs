using System.ComponentModel.DataAnnotations;

namespace Application.DTOs
{
    #region Register User DTO

    /// <summary>
    /// Data transfer object for user registration.
    /// </summary>
    public class RegisterUserDto
    {
        /// <summary>
        /// Gets or sets the username.
        /// </summary>
        [Required(ErrorMessage = "Username is required.")]
        [StringLength(100, MinimumLength = 3, ErrorMessage = "Username must be between 3 and 100 characters.")]
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the email address.
        /// </summary>
        [Required(ErrorMessage = "Email is required.")]
        [EmailAddress(ErrorMessage = "Invalid email format.")]
        [StringLength(200, ErrorMessage = "Email length cannot exceed 200 characters.")]
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Telegram ID.
        /// </summary>
        [Required(ErrorMessage = "Telegram ID is required.")]
        [StringLength(50, ErrorMessage = "Telegram ID length cannot exceed 50 characters.")]
        public string TelegramId { get; set; } = string.Empty;

        // /// <summary>
        // /// Gets or sets the password. (For a potential web panel)
        // /// </summary>
        // [Required(ErrorMessage = "Password is required.")]
        // [StringLength(100, MinimumLength = 6, ErrorMessage = "Password must be at least 6 characters.")]
        // public string Password { get; set; } = string.Empty; // For a potential web panel
    }

    #endregion
}