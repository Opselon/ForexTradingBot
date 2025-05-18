namespace Application.DTOs
{
    public class UserSignalPreferenceDto // برای نمایش علاقه‌مندی‌های کاربر
    {
        public Guid CategoryId { get; set; }
        public string CategoryName { get; set; } = string.Empty; // نام دسته برای نمایش
        public DateTime SubscribedAt { get; set; } // همان CreatedAt از UserSignalPreference
    }
}