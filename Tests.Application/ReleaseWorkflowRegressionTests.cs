using System.Text.Json;

namespace Tests.Application;

public sealed class ReleaseWorkflowRegressionTests
{
    [Fact]
    public void Release_workflow_is_valid_json_and_has_versioned_tag_push_trigger()
    {
        var workflow = LoadWorkflow();

        var push = workflow.RootElement
            .GetProperty("on")
            .GetProperty("push");

        var tags = push.GetProperty("tags")
            .EnumerateArray()
            .Select(x => x.GetString())
            .ToArray();

        Assert.Contains("v*.*.*", tags);
    }

    [Fact]
    public void Release_artifacts_are_blocked_by_end_user_e2e()
    {
        var jobs = LoadWorkflow().RootElement.GetProperty("jobs");

        var e2e = jobs.GetProperty("job_07b_end_user_e2e");
        Assert.Equal("job_06_test_integration_redis", e2e.GetProperty("needs").GetString());

        foreach (var jobName in new[]
        {
            "job_08_publish_linux_artifact",
            "job_09_docker_meta",
            "job_13_build_win_package",
            "job_15_build_macos_package"
        })
        {
            var needs = jobs.GetProperty(jobName).GetProperty("needs");
            var values = needs.ValueKind == JsonValueKind.Array
                ? needs.EnumerateArray().Select(x => x.GetString()).ToArray()
                : [needs.GetString()];

            Assert.Contains("job_07b_end_user_e2e", values);
        }
    }

    [Fact]
    public void Release_final_collection_generates_sha256_checksums()
    {
        var source = File.ReadAllText(
            Path.Combine(FindRoot(), ".github", "workflows", "release.yml"));

        Assert.Contains("Generate SHA-256 checksums", source, StringComparison.Ordinal);
        Assert.Contains("sha256sum", source, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS.txt", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_restore_stage_has_a_dependency_vulnerability_gate()
    {
        var jobs = LoadWorkflow().RootElement.GetProperty("jobs");
        var steps = jobs.GetProperty("job_02_restore_dependencies").GetProperty("steps");

        var gate = steps.EnumerateArray()
            .SingleOrDefault(s => s.TryGetProperty("name", out var name) &&
                                  name.GetString()?.Contains("vulnerability", StringComparison.OrdinalIgnoreCase) == true);

        Assert.False(gate.ValueKind == JsonValueKind.Undefined);
        Assert.Contains("--vulnerable --include-transitive", gate.GetProperty("run").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Release_workflow_uses_current_node24_compatible_action_major_versions()
    {
        var source = File.ReadAllText(Path.Combine(FindRoot(), ".github", "workflows", "release.yml"));

        Assert.Contains("actions/checkout@v5", source, StringComparison.Ordinal);
        Assert.Contains("actions/setup-dotnet@v6", source, StringComparison.Ordinal);
        Assert.Contains("actions/upload-artifact@v7", source, StringComparison.Ordinal);
        Assert.Contains("actions/download-artifact@v8", source, StringComparison.Ordinal);
        Assert.Contains("docker/metadata-action@v6", source, StringComparison.Ordinal);
        Assert.Contains("docker/setup-buildx-action@v4", source, StringComparison.Ordinal);
        Assert.Contains("docker/build-push-action@v7", source, StringComparison.Ordinal);
        Assert.Contains("docker/login-action@v4", source, StringComparison.Ordinal);
        Assert.Contains("actions/cache@v6", source, StringComparison.Ordinal);
        Assert.Contains("softprops/action-gh-release@v3", source, StringComparison.Ordinal);
        Assert.DoesNotContain("softprops/action-gh-release@v2", source, StringComparison.Ordinal);

        Assert.DoesNotContain("actions/checkout@v4", source, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/setup-dotnet@v4", source, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/upload-artifact@v4", source, StringComparison.Ordinal);
    }


    [Fact]
    public void Labeler_workflow_uses_current_action_and_repo_config()
    {
        var root = FindRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "label.yml"));
        var config = File.ReadAllText(Path.Combine(root, ".github", "labeler.yml"));

        Assert.Contains("actions/labeler@v7", workflow, StringComparison.Ordinal);
        Assert.Contains("pull_request:", workflow, StringComparison.Ordinal);
        Assert.Contains("pull-requests: write", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/labeler@v7", workflow, StringComparison.Ordinal);
        Assert.Contains("Build trusted label configuration", workflow, StringComparison.Ordinal);
        Assert.Contains("mkdir -p \"$GITHUB_WORKSPACE/.github\"", workflow, StringComparison.Ordinal);
        Assert.Contains(".github/labeler.yml", workflow, StringComparison.Ordinal);

        foreach (var label in new[] { "code:", "ci:", "docker:" })
            Assert.Contains(label, config, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("install.ps1", "master")]
    [InlineData("install.sh", "master")]
    public void Installer_must_target_the_release_branch_and_not_ship_literal_credentials(
        string fileName,
        string expectedBranch)
    {
        var source = File.ReadAllText(Path.Combine(FindRoot(), fileName));

        Assert.Contains($"/{expectedBranch}/", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=admin", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TELEGRAM_BOT_TOKEN=123", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("123456:ABC", source, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonDocument LoadWorkflow()
    {
        var path = Path.Combine(FindRoot(), ".github", "workflows", "release.yml");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ForexTradingBot.sln")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
