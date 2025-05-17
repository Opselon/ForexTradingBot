using System.ComponentModel.DataAnnotations;

namespace Application.DTOs
{
    public class RegisterUserDto
    {
        [Required(ErrorMessage = "نام کاربری الزامی است.")]
        [StringLength(100, MinimumLength = 3, ErrorMessage = "نام کاربری باید بین 3 تا 100 کاراکتر باشد.")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "ایمیل الزامی است.")]
        [EmailAddress(ErrorMessage = "فرمت ایمیل نامعتبر است.")]
        [StringLength(200, ErrorMessage = "طول ایمیل نمی‌تواند بیش از 200 کاراکتر باشد.")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "شناسه تلگرام الزامی است.")]
        [StringLength(50, ErrorMessage = "طول شناسه تلگرام نمی‌تواند بیش از 50 کاراکتر باشد.")]
        public string TelegramId { get; set; } = string.Empty;

        // [Required(ErrorMessage = "رمز عبور الزامی است.")]
        // [StringLength(100, MinimumLength = 6, ErrorMessage = "رمز عبور باید حداقل 6 کاراکتر باشد.")]
        // public string Password { get; set; } = string.Empty; // برای پنل وب احتمالی
    }
}