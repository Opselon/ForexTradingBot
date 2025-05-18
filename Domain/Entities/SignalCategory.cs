// File: Domain/Entities/SignalCategory.cs
#region Usings
using System.ComponentModel.DataAnnotations;
#endregion

namespace Domain.Entities
{
    public class SignalCategory
    {
        #region Core Properties
        public Guid Id { get; set; }

        [Required]
        [MaxLength(100)]
        public string Name { get; set; } = null!;

        /// <summary>
        /// (Optional) A brief description of the signal category.
        /// </summary>
        [MaxLength(500)]
        public string? Description { get; set; } // ✅ اضافه شد

        /// <summary>
        /// Indicates if this category is active and should be used/displayed.
        /// </summary>
        public bool IsActive { get; set; } = true; //  مثال: برای فعال/غیرفعال کردن دسته‌ها

        /// <summary>
        /// (Optional) Sort order for displaying categories.
        /// </summary>
        public int SortOrder { get; set; } = 0;
        #endregion

        #region Navigation Properties
        /// <summary>
        /// Collection of Signals belonging to this category.
        /// </summary>
        public virtual ICollection<Signal> Signals { get; set; } = new List<Signal>(); // ✅ virtual

        /// <summary>
        /// Collection of UserSignalPreferences linking users to this category.
        /// This defines which users are interested in this category.
        /// </summary>
        public virtual ICollection<UserSignalPreference> UserPreferences { get; set; } = new List<UserSignalPreference>(); // ✅ اضافه شد
        #endregion

        #region Constructors
        public SignalCategory()
        {
            Id = Guid.NewGuid();
            Signals = new List<Signal>();
            UserPreferences = new List<UserSignalPreference>();
            IsActive = true;
        }
        #endregion
    }
}