using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Shared.Extensions
{
    public static class StringExtensions
    {
        /// <summary>
        /// بررسی می‌کند که آیا رشته null، خالی یا فقط شامل فضای خالی است.
        /// </summary>
        public static bool IsNullOrWhiteSpace(this string? value)
        {
            return string.IsNullOrWhiteSpace(value);
        }

        /// <summary>
        /// بررسی می‌کند که آیا رشته null یا خالی است.
        /// </summary>
        public static bool IsNullOrEmpty(this string? value)
        {
            return string.IsNullOrEmpty(value);
        }

        /// <summary>
        /// رشته را به یک مقدار enum مشخص تبدیل می‌کند.
        /// </summary>
        public static T ToEnum<T>(this string value, bool ignoreCase = true) where T : struct, Enum
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(value));
            }
            return (T)Enum.Parse(typeof(T), value, ignoreCase);
        }

        /// <summary>
        /// سعی می‌کند رشته را به یک مقدار enum مشخص تبدیل کند.
        /// </summary>
        public static bool TryToEnum<T>(this string value, out T enumValue, bool ignoreCase = true) where T : struct, Enum
        {
            return Enum.TryParse<T>(value, ignoreCase, out enumValue);
        }

        /// <summary>
        /// یک رشته را با استفاده از الگوریتم SHA256 هش می‌کند.
        /// </summary>
        public static string ToSha256(this string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            using (var sha256 = SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(value));
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < bytes.Length; i++)
                {
                    builder.Append(bytes[i].ToString("x2"));
                }
                return builder.ToString();
            }
        }

        /// <summary>
        /// رشته را به فرمت Title Case تبدیل می‌کند.
        /// مثال: "hello world" -> "Hello World"
        /// </summary>
        public static string ToTitleCase(this string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;
            return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.ToLower());
        }

        /// <summary>
        /// کاراکترهای HTML را از رشته حذف یا جایگزین می‌کند.
        /// </summary>
        public static string StripHtml(this string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            // این یک پیاده‌سازی ساده است، برای موارد پیچیده از کتابخانه‌های HTML Agility Pack و ... استفاده کنید.
            return Regex.Replace(value, "<.*?>", String.Empty);
        }

        /// <summary>
        /// رشته را به تعداد کاراکتر مشخص کوتاه می‌کند و در صورت نیاز "..." اضافه می‌کند.
        /// </summary>
        public static string Truncate(this string value, int maxLength, string truncationSuffix = "...")
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value;

            if (maxLength <= truncationSuffix.Length)
                return truncationSuffix; // یا مقدار مناسب دیگری

            return value.Substring(0, maxLength - truncationSuffix.Length) + truncationSuffix;
        }
    }
}