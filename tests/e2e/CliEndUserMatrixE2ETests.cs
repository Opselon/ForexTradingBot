using Xunit;

namespace ForexTradingBot.EndToEnd;

public sealed class CliEndUserMatrixE2ETests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    [InlineData("status --help")]
    [InlineData("doctor --help")]
    [InlineData("logs --help")]
    [InlineData("start --help")]
    [InlineData("stop --help")]
    [InlineData("install --help")]
    [InlineData("migrate --help")]
    [InlineData("migrations --help")]
    [InlineData("config --help")]
    [InlineData("backup --help")]
    [InlineData("restore --help")]
    [InlineData("secrets --help")]
    [InlineData("secrets list --help")]
    [InlineData("secrets get --help")]
    [InlineData("secrets set --help")]
    [InlineData("secrets delete --help")]
    [InlineData("secrets rotate --help")]
    public async Task Every_public_cli_surface_has_discoverable_help(string commandLine)
    {
        var vault=EndUserCliHarness.TempVault();
        try
        {
            var r=await EndUserCliHarness.RunAsync(vault,commandLine.Split(' ',StringSplitOptions.RemoveEmptyEntries));
            Assert.Equal(0,r.ExitCode);
            Assert.DoesNotContain("Unhandled exception",r.AllOutput,StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NullReferenceException",r.AllOutput,StringComparison.OrdinalIgnoreCase);
        }
        finally { EndUserCliHarness.Cleanup(vault); }
    }

    [Theory]
    [InlineData("secrets get")]
    [InlineData("secrets get    ")]
    [InlineData("secrets delete")]
    [InlineData("secrets set KEY value --category NotARealCategory")]
    [InlineData("secrets get ../outside")]
    [InlineData("secrets get key/with/path")]
    [InlineData("restore does-not-exist.db")]
    [InlineData("install not-a-real-mode")]
    [InlineData("this-command-does-not-exist")]
    public async Task Invalid_end_user_inputs_fail_cleanly(string commandLine)
    {
        var vault=EndUserCliHarness.TempVault();
        try
        {
            var r=await EndUserCliHarness.RunAsync(vault,commandLine.Split(' ',StringSplitOptions.RemoveEmptyEntries));
            Assert.NotEqual(0,r.ExitCode);
            Assert.DoesNotContain("Unhandled exception",r.AllOutput,StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NullReferenceException",r.AllOutput,StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("at System.",r.AllOutput,StringComparison.Ordinal);
        }
        finally { EndUserCliHarness.Cleanup(vault); }
    }

    [Fact]
    [Trait("Category","EndToEnd")]
    [Trait("Surface","CLI")]
    public async Task Secret_lifecycle_survives_multiple_cli_processes()
    {
        var vault=EndUserCliHarness.TempVault();
        const string key="CLI_CROSS_PROCESS_SECRET";
        var value="cli-cross-process-"+Guid.NewGuid().ToString("N");
        try
        {
            Assert.Equal(0,(await EndUserCliHarness.RunAsync(vault,"secrets","set",key,value,"--category","Api")).ExitCode);
            var get=await EndUserCliHarness.RunAsync(vault,"secrets","get",key);
            Assert.Equal(0,get.ExitCode);
            Assert.Contains(value,get.StdOut,StringComparison.Ordinal);
            var list=await EndUserCliHarness.RunAsync(vault,"secrets","list");
            Assert.Equal(0,list.ExitCode);
            Assert.Contains(key,list.StdOut,StringComparison.Ordinal);
            Assert.DoesNotContain(value,list.StdOut,StringComparison.Ordinal);
        }
        finally { EndUserCliHarness.Cleanup(vault); }
    }
}