using FluentAssertions;
using Infrastructure.Data;

namespace Tests.Application;

/// <summary>
/// Tests for the multi-database core: provider normalization and
/// schema-script translation for SQLite / PostgreSQL.
/// These are the two functions that decide whether the very first startup
/// succeeds on a machine the app was never run on before.
/// </summary>
public class DatabaseProviderConfiguratorTests
{
    // ---------------- Normalize ----------------

    [Theory]
    [InlineData("SqlServer", "sqlserver")]
    [InlineData("sql-server", "sqlserver")]
    [InlineData("SQLSERVER", "sqlserver")]
    [InlineData("SqlServer2019", "sqlserver")]
    [InlineData("Postgres", "postgres")]
    [InlineData("PostgreSQL", "postgres")]
    [InlineData("pg", "postgres")]
    [InlineData("Postgres17", "postgres")]
    [InlineData("Sqlite", "sqlite")]
    [InlineData("sql-lite", "sqlite")]
    [InlineData("lite", "sqlite")]
    [InlineData("sqlite3", "sqlite")]
    [InlineData("  SQLite  ", "sqlite")]
    public void Normalize_accepts_aliases_and_is_case_insensitive(string raw, string expected)
        => DatabaseProviderConfigurator.Normalize(raw).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mysql")]
    [InlineData("mongodb")]
    [InlineData("oracle")]
    public void Normalize_rejects_unknown_or_empty_values(string? raw)
    {
        var act = () => DatabaseProviderConfigurator.Normalize(raw);
        act.Should().Throw<NotSupportedException>()
            .WithMessage("*Valid values: SqlServer, Postgres, Sqlite*");
    }

    // ---------------- TranslateSchemaScript: SQLite ----------------

    [Fact]
    public void Sqlite_translates_sqlserver_type_names()
    {
        var script = """
            CREATE TABLE "Users" (
                "Id" uniqueidentifier NOT NULL,
                "Username" nvarchar(max) NOT NULL,
                "Bio" nvarchar(256) NULL,
                "Balance" decimal(18, 8) NOT NULL,
                "CreatedAt" datetime2 NOT NULL,
                "IsActive" bit NOT NULL
            );
            """;

        var out_ = DatabaseProviderConfigurator.TranslateSchemaScript("sqlite", script);

        out_.Should().NotContain("nvarchar", "nvarchar is not valid in SQLite");
        out_.Should().NotContain("datetime2");
        out_.Should().NotContain("uniqueidentifier");
        out_.Should().NotContain("bit NOT", "SQL Server 'bit' must be rewritten");
        out_.Should().NotContain("decimal(", "decimal(18,8) becomes TEXT in SQLite");
        out_.Should().Contain("TEXT");
    }

    [Fact]
    public void Sqlite_translates_sqlserver_default_functions()
    {
        var script = """
            CREATE TABLE "RssSources" (
                "CreatedAt" datetime2 NOT NULL DEFAULT GETUTCDATE(),
                "Name" nvarchar(200) NOT NULL DEFAULT GETDATE(),
                "Fallback" nvarchar(100) NULL DEFAULT ISNULL(N'x', N''),
                "RowId" uniqueidentifier NOT NULL DEFAULT NEWID()
            );
            """;

        var out_ = DatabaseProviderConfigurator.TranslateSchemaScript("sqlite", script);

        out_.Should().NotContain("GETUTCDATE");
        out_.Should().NotContain("GETDATE");
        out_.Should().NotContain("ISNULL", "ISNULL must become IFNULL in SQLite");
        out_.Should().NotContain("NEWID");
        out_.Should().Contain("datetime('now')");
    }

    [Fact]
    public void Sqlite_rewrites_indexed_filtered_index_brackets()
    {
        var script = @"CREATE INDEX ""IX_NewsItems"" ON ""NewsItems"" (""RssSourceId"", ""SourceItemId"") WHERE [SourceItemId] IS NOT NULL;";

        var out_ = DatabaseProviderConfigurator.TranslateSchemaScript("sqlite", script);

        out_.Should().NotContain("[", "SQL Server bracket quoting is invalid elsewhere");
        out_.Should().Contain("\"SourceItemId\" IS NOT NULL");
    }

    // ---------------- TranslateSchemaScript: Postgres ----------------

    [Fact]
    public void Postgres_translates_sqlserver_type_names()
    {
        var script = """
            CREATE TABLE "Users" (
                "Id" uniqueidentifier NOT NULL,
                "Username" nvarchar(max) NOT NULL,
                "Bio" nvarchar(256) NULL,
                "Balance" decimal(18, 8) NOT NULL,
                "CreatedAt" datetime2 NOT NULL,
                "UpdatedAt" datetime2(3) NOT NULL,
                "IsActive" bit NOT NULL
            );
            """;

        var out_ = DatabaseProviderConfigurator.TranslateSchemaScript("postgres", script);

        out_.Should().NotContain("nvarchar");
        out_.Should().NotContain("datetime2");
        out_.Should().NotContain("uniqueidentifier");
        out_.Should().NotContain("bit NOT");
        out_.Should().Contain("uuid");
        out_.Should().Contain("timestamp with time zone");
        out_.Should().Contain("boolean NOT NULL");
        out_.Should().Contain("numeric(18, 8)");
        out_.Should().Contain("varchar(256)");
        out_.Should().Contain("text");
    }

    [Fact]
    public void Postgres_translates_sqlserver_default_functions()
    {
        var script = """
            CREATE TABLE "RssSources" (
                "CreatedAt" timestamp NOT NULL DEFAULT GETUTCDATE()
            );
            """;

        var out_ = DatabaseProviderConfigurator.TranslateSchemaScript("postgres", script);

        out_.Should().NotContain("GETUTCDATE");
        out_.Should().Contain("now() at time zone 'utc'");
    }

    [Fact]
    public void Postgres_rewrites_filtered_index_brackets()
    {
        var script = @"CREATE INDEX ""IX_NewsItems"" ON ""NewsItems"" (""RssSourceId"", ""SourceItemId"") WHERE [SourceItemId] IS NOT NULL;";

        var out_ = DatabaseProviderConfigurator.TranslateSchemaScript("postgres", script);

        out_.Should().NotContain("[");
        out_.Should().Contain("\"SourceItemId\" IS NOT NULL");
    }

    [Fact]
    public void Postgres_never_leaves_literal_backspace_characters()
    {
        // Regression: "\b" inside a verbatim C# string was once compiled to the
        // backspace control char (0x08), silently killing every regex.
        var out_ = DatabaseProviderConfigurator.TranslateSchemaScript("postgres", "nvarchar(max)");
        out_.Should().NotContain("\b");
        out_.Should().Be("text");
    }

    [Fact]
    public void SqlServer_translation_is_a_no_op()
    {
        var script = "CREATE TABLE [dbo].[Users] (Id datetime2 NOT NULL DEFAULT GETUTCDATE())";
        DatabaseProviderConfigurator.TranslateSchemaScript("SqlServer", script)
            .Should().Be(script, "SQL Server is the source dialect — nothing to translate");
    }

    // ---------------- TranslateSchemaScript: idempotence & safety ----------------

    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void Translation_is_deterministic(string provider)
    {
        var script = "CREATE TABLE t (a nvarchar(max), b datetime2 DEFAULT GETUTCDATE(), c bit)";
        var once = DatabaseProviderConfigurator.TranslateSchemaScript(provider, script);
        var twice = DatabaseProviderConfigurator.TranslateSchemaScript(provider, once);

        once.Should().Be(twice, "translating twice must not change the result");
        once.Should().NotContain("\b", "no control characters may be produced");
        once.Should().NotContain("nvarchar");
        once.Should().NotContain("GETUTCDATE");
    }

    [Fact]
    public void Unknown_provider_leaves_script_untouched()
    {
        var script = "CREATE TABLE t (a nvarchar(max))";
        DatabaseProviderConfigurator.TranslateSchemaScript("mysql", script)
            .Should().Be(script);
    }
}
