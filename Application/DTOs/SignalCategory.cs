namespace Application.DTOs
{
    public class SignalCategoryDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        // public int SignalCount { get; set; } // تعداد سیگنال‌های این دسته (اختیاری، نیاز به محاسبه)
    }
}