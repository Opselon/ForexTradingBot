using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.Storage.SQLite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Data
{
    /// <summary>
    /// انتخاب‌دهنده ی پایگاه داده: منطق انتخاب Provider (SqlServer / Postgres / Sqlite)
    /// را در یک جا متمرکز می‌کند تا هم تست‌پذیر باشد و هم در DbContext و Hangfire تکرار نشود.
    /// </summary>
    public static class DatabaseProviderConfigurator
    {
        /// <summary>مقادیر معتبرِ DatabaseProvider (بدون حساسیت به حروف).</summary>
        public const string SqlServer = "sqlserver";
        public const string Postgres = "postgres";
        public const string Sqlite = "sqlite";

        /// <summary>
        /// مقدار خامِ DatabaseProvider را نرمال می‌کند (مثلاً "PostgreSQL" → "postgres").
        /// </summary>
        /// <exception cref="NotSupportedException">اگر مقدار خالی یا ناشناخته باشد.</exception>
        public static string Normalize(string? rawProvider)
        {
            var normalized = rawProvider?.Trim().ToLowerInvariant();

            switch (normalized)
            {
                case null or "":
                    throw new NotSupportedException(
                        "DatabaseProvider is not configured. Valid values: SqlServer, Postgres, Sqlite.");

                case "sqlserver":
                case "sql-server":
                    return SqlServer;

                case "postgres":
                case "postgresql":
                case "pg":
                    return Postgres;

                case "sqlite":
                case "sql-lite":
                case "lite":
                    return Sqlite;

                default:
                    // نسخه/شناسه‌های رایج در env: sqlserver2019, sqlite3, ...
                    if (normalized.StartsWith("sqlserver", StringComparison.Ordinal))
                    {
                        return SqlServer;
                    }

                    if (normalized.StartsWith("sqlite", StringComparison.Ordinal))
                    {
                        return Sqlite;
                    }

                    if (normalized.StartsWith("postgres", StringComparison.Ordinal))
                    {
                        return Postgres;
                    }

                    throw new NotSupportedException(
                        $"Unsupported DatabaseProvider: '{rawProvider}'. Valid values: SqlServer, Postgres, Sqlite.");
            }
        }

        /// <summary>
        /// DbContext را بر اساس Provider پیکربندی می‌کند (SqlServer / Postgres / Sqlite).
        /// </summary>
        public static void ConfigureDbContext(DbContextOptionsBuilder options, string provider, string connectionString)
        {
            switch (Normalize(provider))
            {
                case SqlServer:
                    options.UseSqlServer(connectionString, sql =>
                    {
                        sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
                        sql.EnableRetryOnFailure(
                            maxRetryCount: 5,
                            maxRetryDelay: TimeSpan.FromSeconds(30),
                            errorNumbersToAdd: null);
                    });
                    break;

                case Postgres:
                    options.UseNpgsql(connectionString, npgsql =>
                    {
                        npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
                        npgsql.EnableRetryOnFailure(
                            maxRetryCount: 5,
                            maxRetryDelay: TimeSpan.FromSeconds(30),
                            errorCodesToAdd: null);
                    });
                    break;

                case Sqlite:
                    options.UseSqlite(connectionString);
                    break;
            }
        }

        /// <summary>
        /// Storage مناسبِ Hangfire را بر اساس Provider انتخاب می‌کند:
        /// SqlServer → SqlServer storage، Postgres → Postgres storage، Sqlite → SQLite storage.
        /// </summary>
        public static void ConfigureHangfireStorage(IGlobalConfiguration config, string provider, string connectionString)
        {
            switch (Normalize(provider))
            {
                case SqlServer:
                    config.UseSqlServerStorage(connectionString);
                    break;

                case Postgres:
                    config.UsePostgreSqlStorage(opts => opts.UseNpgsqlConnection(connectionString));
                    break;

                case Sqlite:
                {
                    // Hangfire.Storage.SQLite آرگومان را «مسیر فایل» می‌داند نه connection string؛
                    // اگر کل رشته پاس شود، فایلی به نام "Data Source=forexbot.db" ساخته می‌شود.
                    var sqlitePath = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
                    if (string.IsNullOrWhiteSpace(sqlitePath) || sqlitePath == ":memory:")
                    {
                        sqlitePath = "forexbot-hangfire.db";
                    }

                    config.UseSQLiteStorage(sqlitePath);
                    break;
                }
            }
        }

        /// <summary>
        /// اسکیمای مدل را -در صورت نبودِ جدول‌ها- با اسکریپت مناسبِ همان Provider می‌سازد.
        /// برای SqlServer از Migrate استفاده می‌شود (مهاجرت‌های موجود)، برای Postgres/Sqlite
        /// از GenerateCreateScript که خروجیِ کاملاً Provider-agnostic نیست ولی دقیقاً همان
        /// مدل را با تایپ‌های درستِ همان درایور می‌سازد.
        /// </summary>
        /// <returns>true اگر اسکیما ساخته یا مهاجرت اعمال شده باشد؛ false اگر از قبل موجود بود.</returns>
        public static async Task<bool> EnsureSchemaAsync(AppDbContext db, string provider, CancellationToken ct = default)
        {
            if (Normalize(provider) == SqlServer)
            {
                var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
                if (pending.Count == 0)
                {
                    return false;
                }

                await db.Database.MigrateAsync(ct);
                return true;
            }

            // Postgres / Sqlite: اگر جدول اصلی وجود نداشت، اسکیمای مدل را بساز.
            if (await HasApplicationSchemaAsync(db, ct))
            {
                return false;
            }

            var script = TranslateSchemaScript(provider, db.Database.GenerateCreateScript());
            await db.Database.ExecuteSqlRawAsync(script, ct);
            return true;
        }

        /// <summary>
        /// ترجمه‌ی تایپ‌های SQL Server که در مدل (HasColumnType / Column(TypeName)) hardcode شده‌اند
        /// به معادلِ درستِ هدف. بدون این ترجمه، اسکریپت SQLite/Postgres از نظر نحوی خطا می‌دهد.
        /// </summary>
        public static string TranslateSchemaScript(string provider, string script)
        {
            // مترجمِ خالص است: روی provider ناشناخته نباید پرتاب کند، فقط بی‌اثر باشد.
            string normalized;
            try
            {
                normalized = Normalize(provider);
            }
            catch (NotSupportedException)
            {
                return script;
            }

            switch (normalized)
            {
                case Sqlite:
                    // SQLite: نام‌های نوعِ دلخواه را می‌پذیرد؛ TEXT/NUMERIC دقیق‌ترین معادل‌اند.
                    script = System.Text.RegularExpressions.Regex.Replace(
                        script, @"\bnvarchar\s*\(\s*max\s*\)", "TEXT",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    script = System.Text.RegularExpressions.Regex.Replace(
                        script, @"\bnvarchar\s*\(\s*\d+\s*\)", "TEXT",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    script = System.Text.RegularExpressions.Regex.Replace(
                        script, @"\bdecimal\s*\(\s*\d+\s*,\s*\d+\s*\)", "TEXT",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    script = System.Text.RegularExpressions.Regex.Replace(
                        script, @"\bbit\b", "INTEGER",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    script = System.Text.RegularExpressions.Regex.Replace(
                        script, @"\bdatetime2\s*(?:\(\s*\d+\s*\))?", "TEXT",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    script = System.Text.RegularExpressions.Regex.Replace(
                        script, @"\buniqueidentifier\b", "TEXT",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    // فیلتر ایندکس‌های EF: WHERE [Col] IS NOT NULL  ->  WHERE "Col" IS NOT NULL
                    script = System.Text.RegularExpressions.Regex.Replace(
                        script, @"\[([^\]]+)\]", "\"$1\"");
                    script = TranslateSqlServerFunctions(script, "sqlite");
                    return script;

                case Postgres:
                    script = System.Text.RegularExpressions.Regex.Replace(
                        script, @"\bnvarchar\s*\(\s*max\s*\)", "text",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    script = System.Text.RegularExpressions.Regex.Replace(
                        script, @"\bnvarchar\s*\(\s*(\d+)\s*\)", "varchar($1)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    script = System.Text.RegularExpressions.Regex.Replace(
                        script, @"\bdatetime2\s*(?:\(\s*\d+\s*\))?", "timestamp with time zone",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    script = System.Text.RegularExpressions.Regex.Replace(
                        script, @"\buniqueidentifier\b", "uuid",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    script = System.Text.RegularExpressions.Regex.Replace(
                        script, @"\bdecimal\s*\(\s*(\d+)\s*,\s*(\d+)\s*\)", "numeric($1, $2)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    script = System.Text.RegularExpressions.Regex.Replace(
                        script, @"\bbit\b", "boolean",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    script = System.Text.RegularExpressions.Regex.Replace(
                        script, @"\bint\s+identity\b", "serial",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    // فیلتر ایندکس‌های EF: WHERE [Col] IS NOT NULL  ->  WHERE "Col" IS NOT NULL
                    script = System.Text.RegularExpressions.Regex.Replace(
                        script, @"\[([^\]]+)\]", "\"$1\"");
                    script = TranslateSqlServerFunctions(script, "postgres");
                    return script;

                default:
                    return script;
            }
        }

        /// <summary>
        /// ترجمه‌ی توابع SQL Server که در مدل (HasDefaultValueSql و مشابه) استفاده شده‌اند
        /// به معادلِ هدف. بدون این ترجمه، اسکیماسازی Postgres/SQLite روی GETUTCDATE و ... می‌شکند.
        /// </summary>
        private static string TranslateSqlServerFunctions(string script, string target)
        {
            if (target == "postgres")
            {
                // (GETUTCDATE()) -> (now() at time zone 'utc')
                script = System.Text.RegularExpressions.Regex.Replace(
                    script, @"\bGETUTCDATE\s*\(\s*\)", "(now() at time zone 'utc')",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                script = System.Text.RegularExpressions.Regex.Replace(
                    script, @"\bGETDATE\s*\(\s*\)", "now()",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                script = System.Text.RegularExpressions.Regex.Replace(
                    script, @"\bISNULL\s*\(", "COALESCE(",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                script = System.Text.RegularExpressions.Regex.Replace(
                    script, @"\bNEWID\s*\(\s*\)", "gen_random_uuid()",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return script;
            }

            // sqlite
            script = System.Text.RegularExpressions.Regex.Replace(
                script, @"\bGETUTCDATE\s*\(\s*\)", "datetime('now')",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            script = System.Text.RegularExpressions.Regex.Replace(
                script, @"\bGETDATE\s*\(\s*\)", "datetime('now')",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            script = System.Text.RegularExpressions.Regex.Replace(
                script, @"\bISNULL\s*\(", "IFNULL(",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            script = System.Text.RegularExpressions.Regex.Replace(
                script, @"\bNEWID\s*\(\s*\)",
                "(lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || lower(hex(randomblob(2))) || '-8' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(6))))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return script;
        }

        /// <summary>
        /// اطمینان از وجود خودِ دیتابیس (قبل از ساخت اسکیما):
        /// Postgres → اتصال به دیتابیس maintenance و CREATE DATABASE در صورت نیاز
        /// SqlServer → همانند Postgres با دیتابیس master
        /// Sqlite → فقط ساخت پوشه‌ی فایل دیتابیس (خودِ فایل را درایور می‌سازد)
        /// </summary>
        public static async Task EnsureDatabaseExistsAsync(string provider, string connectionString, CancellationToken ct = default)
        {
            switch (Normalize(provider))
            {
                case Sqlite:
                {
                    // Data Source=/abs/path/file.db یا Data Source=relative.db
                    var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
                    var dataSource = builder.DataSource;
                    if (!string.IsNullOrEmpty(dataSource)
                        && dataSource != ":memory:"
                        && !dataSource.StartsWith("file::memory:", StringComparison.OrdinalIgnoreCase))
                    {
                        var dir = Path.GetDirectoryName(Path.GetFullPath(dataSource));
                        if (!string.IsNullOrEmpty(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }
                    }
                    return;
                }

                case Postgres:
                {
                    var npgsql = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
                    var targetDb = npgsql.Database;
                    if (string.IsNullOrWhiteSpace(targetDb))
                    {
                        return;
                    }

                    npgsql.Database = "postgres"; // دیتابیس maintenance
                    await using var conn = new Npgsql.NpgsqlConnection(npgsql.ConnectionString);
                    await conn.OpenAsync(ct);

                    // فقط اگر وجود نداشت بساز (پارامترشده، بدون تزریق)
                    await using var check = conn.CreateCommand();
                    check.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name";
                    check.Parameters.AddWithValue("name", targetDb);
                    var exists = await check.ExecuteScalarAsync(ct) is not null;
                    if (exists)
                    {
                        return;
                    }

                    await using var create = conn.CreateCommand();
                    var quotedDb = "\"" + targetDb.Replace("\"", "\"\"") + "\"";
                    create.CommandText = "CREATE DATABASE " + quotedDb;
                    await create.ExecuteNonQueryAsync(ct);
                    return;
                }

                case SqlServer:
                    // SQL Server: EF خودش دیتابیس را هنگام MigrateAsync می‌سازد (تأیید شده با پروب).
                    // پیش‌ساخت دستی باعث race با SqlServerDatabaseCreator.Exists() می‌شود و
                    // خطای 1801 («database already exists») می‌دهد — پس اینجا کاری نمی‌کنیم.
                    return;


            }
        }

        /// <summary>تشخیص اینکه آیا اسکیمای مدل (حداقل جدول Users) از قبل ساخته شده است.</summary>
        private static async Task<bool> HasApplicationSchemaAsync(AppDbContext db, CancellationToken ct)
        {
            try
            {
                // COUNT روی جدول Users: اگر جدول نباشد استثنا می‌دهد.
                _ = await db.Users.AsNoTracking().CountAsync(ct);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
