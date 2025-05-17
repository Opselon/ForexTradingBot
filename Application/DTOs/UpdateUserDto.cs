using System.ComponentModel.DataAnnotations;
using System;

namespace Application.DTOs
{
    public class UpdateUserDto
    {
        // معمولاً شناسه از مسیر URL یا از کاربر احراز هویت شده دریافت می‌شود و در DTO نیست.
        // public Guid Id { get; set; }

        [StringLength(100, MinimumLength = 3, ErrorMessage = "نام کاربری باید بین 3 تا 100 کاراکتر باشد.")]
        public string? Username { get; set; } // Nullable، چون ممکن است کاربر نخواهد آن را تغییر دهد

        [EmailAddress(ErrorMessage = "فرمت ایمیل نامعتبر است.")]
        [StringLength(200, ErrorMessage = "طول ایمیل نمی‌تواند بیش از 200 کاراکتر باشد.")]
        public string? Email { get; set; } // Nullable

        // TelegramId معمولاً ثابت است و تغییر نمی‌کند.
        // UserLevel توسط ادمین یا فرآیندهای دیگر تغییر می‌کند، نه مستقیماً توسط کاربر.
    }
}