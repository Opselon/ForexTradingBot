using Application.DTOs; // برای UserDto, RegisterUserDto, UpdateUserDto

namespace Application.Interfaces // ✅ Namespace صحیح برای اینترفیس‌های سرویس
{
    /// <summary>
    /// اینترفیس برای سرویس مدیریت کاربران.
    /// عملیات اصلی مربوط به کاربران مانند ثبت نام، دریافت اطلاعات، به‌روزرسانی و حذف را تعریف می‌کند.
    /// </summary>
    public interface IUserService
    {
        /// <summary>
        /// یک کاربر را بر اساس شناسه تلگرام آن به صورت ناهمزمان پیدا می‌کند.
        /// </summary>
        /// <param name="telegramId">شناسه تلگرام کاربر.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>UserDto در صورت یافتن؛ در غیر این صورت null.</returns>
        Task<UserDto?> GetUserByTelegramIdAsync(string telegramId, CancellationToken cancellationToken = default);

        /// <summary>
        /// تمام کاربران را به صورت ناهمزمان برمی‌گرداند.
        /// </summary>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>لیستی از UserDto.</returns>
        Task<List<UserDto>> GetAllUsersAsync(CancellationToken cancellationToken = default); // تغییر نام برای وضوح

        /// <summary>
        /// یک کاربر را بر اساس شناسه آن به صورت ناهمزمان پیدا می‌کند.
        /// </summary>
        /// <param name="id">شناسه کاربر.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>UserDto در صورت یافتن؛ در غیر این صورت null.</returns>
        Task<UserDto?> GetUserByIdAsync(Guid id, CancellationToken cancellationToken = default); // اضافه کردن CancellationToken

        /// <summary>
        /// یک کاربر جدید را با استفاده از اطلاعات ارائه شده ثبت می‌کند.
        /// همچنین یک کیف پول توکن برای کاربر جدید ایجاد می‌کند.
        /// </summary>
        /// <param name="registerDto">اطلاعات لازم برای ثبت نام کاربر.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        /// <returns>UserDto کاربر ایجاد شده.</returns>
        Task<UserDto> RegisterUserAsync(RegisterUserDto registerDto, CancellationToken cancellationToken = default); // تغییر ورودی به RegisterUserDto

        /// <summary>
        /// اطلاعات یک کاربر موجود را به‌روزرسانی می‌کند.
        /// </summary>
        /// <param name="userId">شناسه کاربری که باید به‌روز شود.</param>
        /// <param name="updateDto">اطلاعات جدید برای به‌روزرسانی کاربر.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task UpdateUserAsync(Guid userId, UpdateUserDto updateDto, CancellationToken cancellationToken = default);

        /// <summary>
        /// یک کاربر را بر اساس شناسه آن حذف می‌کند.
        /// </summary>
        /// <param name="id">شناسه کاربری که باید حذف شود.</param>
        /// <param name="cancellationToken">توکن برای لغو عملیات.</param>
        Task DeleteUserAsync(Guid id, CancellationToken cancellationToken = default); // تغییر نام برای هماهنگی و اضافه کردن CancellationToken
    }
}