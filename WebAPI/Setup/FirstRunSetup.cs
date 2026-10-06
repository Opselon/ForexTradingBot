using System.Text;
using System.Text.Json;
using Infrastructure.Data;
using Microsoft.Extensions.Configuration;

namespace WebAPI.Setup
{
    /// <summary>
    /// نصب‌ساز اولین اجرا (First-Run Setup):
    /// اگر DatabaseProvider / ConnectionString تنظیم نشده باشد، در کنسول تعاملی
    /// از کاربر می‌پرسد کدام پایگاه داده را می‌خواهد (Postgres / Sqlite / SqlServer)؛
    /// در محیط غیرتعاملی (کانتینر، CI، لوله‌ی stdout) به‌صورت خودکار Sqlite را انتخاب می‌کند
    /// تا برنامه بدون هیچ پیش‌نیازی بالا بیاید.
    /// انتخاب کاربر در appsettings.Local.json ذخیره می‌شود.
    /// </summary>
    public static class FirstRunSetup
    {
        public const string LocalSettingsFileName = "appsettings.Local.json";

        /// <summary>آیا تنظیمات دیتابیس ناقص است و باید Wizard اجرا شود؟</summary>
        public static bool NeedsSetup(IConfiguration configuration)
        {
            var provider = configuration["DatabaseProvider"];
            var cs = configuration.GetConnectionString("DefaultConnection");
            return string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(cs);
        }

        /// <summary>
        /// اجرای Wizard (یا انتخاب پیش‌فرض غیرتعاملی) و برگرداندن پیکربندی نهایی.
        /// </summary>
        public static (string Provider, string ConnectionString) Run(IConfiguration configuration)
        {
            var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;

            if (!interactive)
            {
                // حالت غیرتعاملی (کانتینر/CI): Sqlite → صفر پیش‌نیاز، همیشه کار می‌کند.
                var fallbackProvider = Environment.GetEnvironmentVariable("DatabaseProvider");
                var fallbackCs = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");

                if (!string.IsNullOrWhiteSpace(fallbackProvider) && !string.IsNullOrWhiteSpace(fallbackCs))
                {
                    return (fallbackProvider, fallbackCs);
                }

                return ("Sqlite", "Data Source=forexbot.db");
            }

            return Prompt();
        }

        /// <summary>ذخیره‌ی انتخاب در appsettings.Local.json (پایین‌ترین اولویتِ فایل‌ها، بالاتر از env نیست).</summary>
        public static void Persist(string provider, string connectionString, string baseDirectory)
        {
            var path = Path.Combine(baseDirectory, LocalSettingsFileName);
            var payload = new Dictionary<string, object?>
            {
                ["DatabaseProvider"] = provider,
                ["ConnectionStrings"] = new Dictionary<string, string?>
                {
                    ["DefaultConnection"] = connectionString,
                },
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        // ------------------------------------------------------------------
        //  UI
        // ------------------------------------------------------------------

        private static (string Provider, string ConnectionString) Prompt()
        {
            Console.WriteLine();
            WriteAccent("  ╔══════════════════════════════════════════════════════╗");
            WriteAccent("  ║        ForexTradingBot — First-Run Setup            ║");
            WriteAccent("  ║   پایگاه داده‌ی مورد نظر خود را انتخاب کنید           ║");
            WriteAccent("  ╚══════════════════════════════════════════════════════╝");
            Console.WriteLine();
            Console.WriteLine("   1) SQLite      — سریع‌ترین، بدون نصب، فایل محلی      ");
            Console.WriteLine("   2) PostgreSQL  — پیشنهادی برای سرور و production      ");
            Console.WriteLine("   3) SQL Server  — مناسب محیط‌های ویندوزی موجود        ");
            Console.WriteLine();

            while (true)
            {
                Console.Write("  انتخاب شما [1/2/3] (پیش‌فرض: 1): ");
                var key = Console.ReadLine()?.Trim();

                switch (key)
                {
                    case "" or "1":
                        return ("Sqlite", PromptSqlite());

                    case "2":
                        return ("Postgres", PromptPostgres());

                    case "3":
                        return ("SqlServer", PromptSqlServer());

                    default:
                        Console.WriteLine("  ! عدد 1، 2 یا 3 را وارد کنید.");
                        break;
                }
            }
        }

        private static string PromptSqlite()
        {
            Console.Write("  مسیر فایل دیتابیس [forexbot.db]: ");
            var file = Console.ReadLine()?.Trim();
            return string.IsNullOrEmpty(file) ? "Data Source=forexbot.db" : $"Data Source={file}";
        }

        private static string PromptPostgres()
        {
            var host = Ask("  Host [localhost]: ", "localhost");
            var port = Ask("  Port [5432]: ", "5432");
            var db = Ask("  Database [forexbotdb]: ", "forexbotdb");
            var user = Ask("  Username [postgres]: ", "postgres");
            var pass = Ask("  Password []: ", string.Empty, secret: true);
            Console.WriteLine();

            return $"Host={host};Port={port};Database={db};Username={user};Password={pass}";
        }

        private static string PromptSqlServer()
        {
            var host = Ask("  Server [localhost]: ", "localhost");
            var db = Ask("  Database [ForexBotDb]: ", "ForexBotDb");
            var integrated = Ask("  Integrated Security? (y/n) [y]: ", "y").Equals("y", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine();

            return integrated
                ? $"Server={host};Database={db};Trusted_Connection=True;TrustServerCertificate=True"
                : $"Server={host};Database={db};User Id={Ask("  User Id [sa]: ", "sa")};Password={Ask("  Password []: ", string.Empty, secret: true)};TrustServerCertificate=True";
        }

        private static string Ask(string label, string defaultValue, bool secret = false)
        {
            Console.Write(label);
            var value = secret ? ReadSecret() : Console.ReadLine();
            value = value?.Trim();
            return string.IsNullOrEmpty(value) ? defaultValue : value;
        }

        /// <summary>خواندن رمز بدون نمایش در کنسول (تایپ echo نمی‌شود).</summary>
        private static string ReadSecret()
        {
            var sb = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    break;
                }

                if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
                {
                    sb.Length--;
                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    sb.Append(key.KeyChar);
                }
            }

            return sb.ToString();
        }

        private static void WriteAccent(string line) => Console.WriteLine(line);

        /// <summary>بارگذاری appsettings.Local.json (اگر وجود داشت) به کانفیگ.</summary>
        public static IConfigurationBuilder AddLocalSettings(this IConfigurationBuilder builder, string baseDirectory)
        {
            var path = Path.Combine(baseDirectory, LocalSettingsFileName);
            if (File.Exists(path))
            {
                builder.AddJsonFile(path, optional: false, reloadOnChange: false);
            }

            return builder;
        }
    }
}
