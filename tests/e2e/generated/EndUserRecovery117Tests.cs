using Xunit;
namespace ForexTradingBot.EndToEnd;
public sealed class EndUserRecovery117Tests
{
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Missing_resource_returns_controlled_failure(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"restore",Path.Combine(v,"not-present-117.db"));Assert.NotEqual(0,r.ExitCode);Assert.DoesNotContain("Unhandled exception",r.AllOutput,StringComparison.OrdinalIgnoreCase);}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Recovery_command_can_still_show_help_after_failure(){var v=EndUserCliHarness.TempVault();try{var failed=await EndUserCliHarness.RunAsync(v,"restore",Path.Combine(v,"not-present-117.db"));Assert.NotEqual(0,failed.ExitCode);var help=await EndUserCliHarness.RunAsync(v,"restore","--help");Assert.Equal(0,help.ExitCode);}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Fresh_vault_remains_usable_after_failed_operation(){var v=EndUserCliHarness.TempVault();try{await EndUserCliHarness.RunAsync(v,"restore",Path.Combine(v,"not-present-117.db"));var list=await EndUserCliHarness.RunAsync(v,"secrets","list");Assert.Equal(0,list.ExitCode);}finally{EndUserCliHarness.Cleanup(v);}}
}