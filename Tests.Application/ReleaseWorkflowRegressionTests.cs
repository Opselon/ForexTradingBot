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

        // The multi-arch publish matrix is the single artifact producer; it must be
        // gated on the end-user E2E suite, otherwise broken releases can be published.
        var publish = jobs.GetProperty("job_08_publish_artifacts");
        var needs = publish.GetProperty("needs");
        var values = needs.ValueKind == JsonValueKind.Array
            ? needs.EnumerateArray().Select(x => x.GetString()).ToArray()
            : [needs.GetString()];

        Assert.Contains("job_07b_end_user_e2e", values);
    }

    [Fact]
    public void Release_publish_matrix_covers_all_supported_architectures()
    {
        var job = LoadWorkflow().RootElement.GetProperty("jobs")
            .GetProperty("job_08_publish_artifacts");

        var includes = job.GetProperty("strategy")
            .GetProperty("matrix")
            .GetProperty("include")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("rid").GetString())
            .ToArray();

        Assert.Equal(
            new[] { "linux-x64", "linux-arm64", "win-x64", "win-arm64", "osx-x64", "osx-arm64" },
            includes);

        // Artifacts must be self-contained single-file bundles so end users need no
        // .NET runtime, and the CLI must be published alongside the API.
        var steps = job.GetProperty("steps");
        foreach (var project in new[] { "WebAPI/WebAPI.csproj", "Cli/ForexTradingBot.Cli.csproj" })
        {
            Assert.Contains(steps.EnumerateArray(), s =>
                s.TryGetProperty("run", out var run) &&
                run.GetString()!.Contains(project, StringComparison.Ordinal) &&
                run.GetString()!.Contains("--self-contained true", StringComparison.Ordinal));
        }

        // The end-user setup files must be bundled into every archive.
        Assert.Contains(steps.EnumerateArray(), s =>
            s.TryGetProperty("run", out var run) &&
            run.GetString()!.Contains("install.sh", StringComparison.Ordinal));
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
    public void Release_final_collection_flattens_and_forwards_artifacts()
    {
        // download-artifact nests each artifact in its own subdirectory; the release
        // upload glob only matches a flat layout, so a flatten step is mandatory.
        var job = LoadWorkflow().RootElement.GetProperty("jobs")
            .GetProperty("job_17_final_cleanup_artifacts");

        Assert.Contains(job.GetProperty("steps").EnumerateArray(), s =>
            s.TryGetProperty("shell", out var shell) &&
            shell.GetString() == "bash" &&
            s.TryGetProperty("run", out var run) &&
            run.GetString()!.Contains("Flatten", StringComparison.OrdinalIgnoreCase));

        // Each GitHub Actions job runs on a fresh runner, so the flattened bundle must
        // be re-published as an artifact and re-downloaded by the release job,
        // otherwise the upload finds no files.
        Assert.Contains(job.GetProperty("steps").EnumerateArray(), s =>
            s.TryGetProperty("uses", out var uses) &&
            uses.GetString()!.StartsWith("actions/upload-artifact", StringComparison.Ordinal));

        var releaseJob = LoadWorkflow().RootElement.GetProperty("jobs")
            .GetProperty("job_18_create_release");

        Assert.Contains(releaseJob.GetProperty("steps").EnumerateArray(), s =>
            s.TryGetProperty("uses", out var uses) &&
            uses.GetString()!.StartsWith("actions/download-artifact", StringComparison.Ordinal));
    }

    [Fact]
    public void Release_bundle_keeps_setup_files_executable_and_ships_env_example()
    {
        var job = LoadWorkflow().RootElement.GetProperty("jobs")
            .GetProperty("job_08_publish_artifacts");

        var bundleStep = job.GetProperty("steps").EnumerateArray()
            .Single(s => (s.TryGetProperty("name", out var n) &&
                          n.GetString()?.Contains("Bundle", StringComparison.OrdinalIgnoreCase) == true));
        var run = bundleStep.GetProperty("run").GetString()!;

        // The scripts must stay executable inside the archive, otherwise end users
        // hit "Permission denied" right after extracting.
        Assert.Contains("chmod 755", run, StringComparison.Ordinal);
        Assert.Contains("install.sh", run, StringComparison.Ordinal);
        // docker-compose reads .env; shipping the example is required for the one-command path.
        Assert.Contains(".env.example", run, StringComparison.Ordinal);
        Assert.Contains("QUICKSTART.md", run, StringComparison.Ordinal);
    }

    private static JsonDocument LoadWorkflow()
    {
        var root = FindRoot();
        var path = Path.Combine(root, ".github", "workflows", "release.yml");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ForexTradingBot.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("ForexTradingBot.sln was not found.");
    }

}
