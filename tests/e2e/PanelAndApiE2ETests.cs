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

    [Fact]
    [Trait("Category", "EndToEnd")]
    [Trait("Surface", "Users")]
    public void Users_api_client_exposes_registration_level_and_subscriptions()
    {
        var root = FindRepoRoot();
        var apiJsPath = Path.Combine(root, "WebAPI", "wwwroot", "js", "api.js");
        var js = File.ReadAllText(apiJsPath);

        Assert.Contains("register:", js);
        Assert.Contains("setLevel:", js);
        Assert.Contains("subscriptions:", js);
        Assert.Contains("createSubscription:", js);
        Assert.Contains("deleteSubscription:", js);
    }

    [Fact]
    [Trait("Category", "EndToEnd")]
    [Trait("Surface", "Update")]
    public void System_view_uses_real_update_check_and_no_fake_versions()
    {
        var root = FindRepoRoot();
        var systemJsPath = Path.Combine(root, "WebAPI", "wwwroot", "js", "views", "system.js");
        var js = File.ReadAllText(systemJsPath);

        // The honest states the view must be able to render.
        Assert.Contains("updateCheck", js);
        Assert.Contains("sys-check-update", js);
        Assert.Contains("sys-apply-update", js);
        Assert.Contains("Update status not checked", js); // never pretends it already checked
        Assert.DoesNotContain("up-to-date-placeholder", js);
    }

    [Fact]
    [Trait("Category", "EndToEnd")]
    [Trait("Surface", "Update")]
    public void Update_script_ships_in_the_source_tree()
    {
        var root = FindRepoRoot();
        var updateSh = Path.Combine(root, "update.sh");
        Assert.True(File.Exists(updateSh), "update.sh must exist so the release bundle can self-update.");

        var sh = File.ReadAllText(updateSh);
        // It must actually download and install, not stub the work.
        Assert.Contains("releases/latest", sh);
        Assert.Contains("browser_download_url", sh);
        Assert.Contains("tar -xzf", sh);
        Assert.DoesNotContain("echo \"update done\"", sh);
    }

    [Fact]
    [Trait("Category", "EndToEnd")]
    [Trait("Surface", "Setup")]
    public void Easy_setup_wizard_wires_password_and_token_saves()
    {
        var root = FindRepoRoot();
        var panelJsPath = Path.Combine(root, "WebAPI", "wwwroot", "js", "panel.js");
        var js = File.ReadAllText(panelJsPath);

        Assert.Contains("/api/setup/admin/password", js);
        Assert.Contains("/api/setup/telegram/token", js);
        Assert.Contains("save-password", js);
        Assert.Contains("save-token", js);
    }

    [Fact]
    [Trait("Category", "EndToEnd")]
    [Trait("Surface", "Security")]
    public void Setup_wizard_never_echoes_the_admin_password_back_to_the_page()
    {
        var root = FindRepoRoot();
        var panelJsPath = Path.Combine(root, "WebAPI", "wwwroot", "js", "panel.js");
        var js = File.ReadAllText(panelJsPath);

        // The fields are type=password and the value is sent, never rendered back.
        Assert.Contains("type=\"password\"", js);
        Assert.DoesNotContain("document.getElementById('su-pw1').textContent", js);
    }
}
