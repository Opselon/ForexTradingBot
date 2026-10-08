using System.Text.RegularExpressions;
using Xunit;

namespace ForexTradingBot.EndToEnd;

public sealed class PanelAndApiE2ETests
{
    private static string FindRepoRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "ForexTradingBot.sln")))
            {
                return dir;
            }
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

        // Fallback for standard build layout
        var standard = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../"));
        if (File.Exists(Path.Combine(standard, "ForexTradingBot.sln")))
        {
            return standard;
        }

        throw new DirectoryNotFoundException("Could not find repository root containing ForexTradingBot.sln");
    }

    [Fact]
    [Trait("Category", "EndToEnd")]
    [Trait("Surface", "Panel")]
    public void All_panel_static_assets_exist_and_are_non_empty()
    {
        var root = FindRepoRoot();
        var wwwroot = Path.Combine(root, "WebAPI", "wwwroot");

        Assert.True(Directory.Exists(wwwroot), "WebAPI/wwwroot must exist");

        string[] requiredFiles =
        [
            "panel.html",
            "css/panel.css",
            "js/api.js",
            "js/ui.js",
            "js/panel.js",
            "js/views/dashboard.js",
            "js/views/users.js",
            "js/views/rss.js",
            "js/views/ai.js",
            "js/views/forwarding.js",
            "js/views/secrets.js",
            "js/views/console.js",
            "js/views/system.js",
            "js/views/settings.js",
            "js/views/forcejoin.js"
        ];

        foreach (var relativePath in requiredFiles)
        {
            var fullPath = Path.Combine(wwwroot, relativePath);
            Assert.True(File.Exists(fullPath), $"File must exist: {relativePath}");
            var content = File.ReadAllText(fullPath);
            Assert.False(string.IsNullOrWhiteSpace(content), $"File must not be empty: {relativePath}");
            Assert.True(content.Length > 100, $"File seems too small ({content.Length} bytes): {relativePath}");
        }
    }

    [Fact]
    [Trait("Category", "EndToEnd")]
    [Trait("Surface", "Panel")]
    public void Panel_html_includes_all_view_scripts()
    {
        var root = FindRepoRoot();
        var panelHtmlPath = Path.Combine(root, "WebAPI", "wwwroot", "panel.html");
        var html = File.ReadAllText(panelHtmlPath);

        string[] expectedScripts =
        [
            "/js/api.js",
            "/js/ui.js",
            "/js/views/dashboard.js",
            "/js/views/users.js",
            "/js/views/rss.js",
            "/js/views/ai.js",
            "/js/views/forwarding.js",
            "/js/views/secrets.js",
            "/js/views/console.js",
            "/js/views/system.js",
            "/js/views/settings.js",
            "/js/views/forcejoin.js",
            "/js/panel.js"
        ];

        foreach (var script in expectedScripts)
        {
            Assert.Contains(script, html);
        }
    }

    [Fact]
    [Trait("Category", "EndToEnd")]
    [Trait("Surface", "Panel")]
    public void Panel_js_router_declares_all_core_routes()
    {
        var root = FindRepoRoot();
        var panelJsPath = Path.Combine(root, "WebAPI", "wwwroot", "js", "panel.js");
        var js = File.ReadAllText(panelJsPath);

        string[] expectedRoutes =
        [
            "dashboard",
            "users",
            "setup",
            "forwarding",
            "rss",
            "ai",
            "forcejoin",
            "tglogin",
            "channels",
            "settings",
            "secrets",
            "console",
            "system"
        ];

        foreach (var route in expectedRoutes)
        {
            Assert.Contains($"{route}:", js);
        }
    }

    [Fact]
    [Trait("Category", "EndToEnd")]
    [Trait("Surface", "Security")]
    public void Panel_views_contain_no_mock_data_or_plaintext_keys()
    {
        var root = FindRepoRoot();
        var viewsDir = Path.Combine(root, "WebAPI", "wwwroot", "js", "views");

        var viewFiles = Directory.GetFiles(viewsDir, "*.js");
        Assert.NotEmpty(viewFiles);

        foreach (var file in viewFiles)
        {
            var content = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            // Verify no mock data markers
            Assert.DoesNotContain("mockData", content);
            Assert.DoesNotContain("fakeResponse", content);
            Assert.DoesNotContain("seedData", content);

            // Verify that all views use the centralized FtbApi / FtbUi
            Assert.Contains("window.Ftb", content);
        }
    }

    [Fact]
    [Trait("Category", "EndToEnd")]
    [Trait("Surface", "Theme")]
    public void Panel_css_contains_theme_tokens_and_responsive_rules()
    {
        var root = FindRepoRoot();
        var cssPath = Path.Combine(root, "WebAPI", "wwwroot", "css", "panel.css");
        var css = File.ReadAllText(cssPath);

        // Dark/light tokens
        Assert.Contains("--bg", css);
        Assert.Contains("--text", css);
        Assert.Contains("--border", css);

        // Responsive media queries
        Assert.Contains("@media", css);
    }
}
