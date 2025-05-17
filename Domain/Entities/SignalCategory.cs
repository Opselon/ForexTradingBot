using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations; // برای Data Annotations مانند [Required], [MaxLength]

namespace Domain.Entities
{
    /// <summary>
    /// موجودیتی برای دسته‌بندی سیگنال‌های معاملاتی.
    /// هر سیگنال می‌تواند به یک یا چند دسته تعلق داشته باشد (اگرچه در این مدل ساده، هر سیگنال به یک دسته تعلق دارد از طریق Signal.CategoryId).
    /// دسته‌بندی‌ها به کاربران کمک می‌کنند تا سیگنال‌های مورد علاقه خود را فیلتر و دنبال کنند.
    /// نمونه‌ها: "جفت ارزهای اصلی فارکس"، "فلزات گرانبها"، "شاخص‌های سهام".
    /// </summary>
    public class SignalCategory
    {
        /// <summary>
        /// شناسه یکتای دسته سیگنال.
        /// به عنوان کلید اصلی (Primary Key) در پایگاه داده استفاده می‌شود.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// نام دسته سیگنال.
        /// این نام برای نمایش به کاربران و شناسایی دسته استفاده می‌شود.
        /// نیازمندی: این فیلد اجباری است و باید منحصر به فرد باشد (در سطح پایگاه داده با یک Unique Constraint تضمین می‌شود).
        /// </summary>
        [Required(ErrorMessage = "نام دسته سیگنال الزامی است.")]
        [MaxLength(100, ErrorMessage = "طول نام دسته نمی‌تواند بیش از 100 کاراکتر باشد.")]
        //  [Index(IsUnique = true)] // اگر از EF Core با قابلیت Fluent API استفاده نمی‌کنید، این Attribute از System.ComponentModel.DataAnnotations.Schema برای نسخه‌های قدیمی‌تر یا ابزارهای دیگر است.
        // در EF Core 5+، Unique Index معمولاً از طریق Fluent API در DbContext تعریف می‌شود.
        public string Name { get; set; } = null!; // مقداردهی اولیه برای جلوگیری از هشدار nullability. EF Core یا سازنده باید مقدار معتبر را تضمین کنند.

        /// <summary>
        /// مجموعه‌ای از سیگنال‌هایی که به این دسته تعلق دارند.
        /// این یک خصوصیت ناوبری برای رابطه یک-به-چند بین SignalCategory و Signal است.
        /// (هر دسته می‌تواند چندین سیگنال داشته باشد).
        /// به صورت پیش‌فرض به عنوان یک لیست خالی مقداردهی اولیه می‌شود تا از NullReferenceException جلوگیری شود.
        /// </summary>
        public ICollection<Signal> Signals { get; set; } = new List<Signal>();

        // ملاحظات و نیازمندی‌های اضافی بالقوه:
        // 1. Description (string?): یک توضیح اختیاری برای دسته که جزئیات بیشتری در مورد آن ارائه می‌دهد.
        // 2. ParentCategoryId (Guid?): برای پشتیبانی از دسته‌بندی‌های تودرتو (والد-فرزند).
        //    public Guid? ParentCategoryId { get; set; }
        //    public SignalCategory? ParentCategory { get; set; }
        //    public ICollection<SignalCategory> SubCategories { get; set; } = new List<SignalCategory>();
        // 3. IsActive (bool): برای فعال/غیرفعال کردن یک دسته بدون حذف آن.
        // 4. SortOrder (int): برای تعیین ترتیب نمایش دسته‌ها به کاربر.
        // 5. IconUrl (string?): برای نمایش یک آیکون مرتبط با دسته.

        // سازنده پیش‌فرض برای EF Core
        public SignalCategory() { }

        // سازنده برای ایجاد یک دسته جدید با نام مشخص
        // public SignalCategory(string name)
        // {
        //     if (string.IsNullOrWhiteSpace(name))
        //         throw new ArgumentException("نام دسته نمی‌تواند خالی باشد.", nameof(name));
        //     if (name.Length > 100)
        //         throw new ArgumentOutOfRangeException(nameof(name), "طول نام دسته نمی‌تواند بیش از 100 کاراکتر باشد.");

        //     Id = Guid.NewGuid();
        //     Name = name;
        //     Signals = new List<Signal>();
        // }
    }
}