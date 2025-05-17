namespace Shared.Constants
{
    /// <summary>
    /// مقادیر ثابت برای نام نقش‌های کاربری در برنامه.
    /// استفاده از این ثابت‌ها به جای رشته‌های مستقیم، از خطاهای تایپی جلوگیری می‌کند و خوانایی را افزایش می‌دهد.
    /// </summary>
    public static class AppRoles
    {
        public const string Administrator = "Admin";
        public const string User = "User"; // نقش کاربر عادی
        public const string FreeUser = "FreeUser";
        public const string PremiumUser = "PremiumUser"; // یا سطوح مختلف مانند Bronze, Silver, Gold

        // می‌توانید نقش‌های دیگری را نیز در اینجا اضافه کنید.
        // public const string Moderator = "Moderator";
        // public const string Editor = "Editor";

        public static class Policies
        {
            // اگر از Authorization Policies استفاده می‌کنید، می‌توانید نام آن‌ها را نیز اینجا تعریف کنید.
            // public const string CanManageUsers = "CanManageUsers";
            // public const string CanCreateSignals = "CanCreateSignals";
        }
    }
}