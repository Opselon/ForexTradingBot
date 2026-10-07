using Xunit;
namespace ForexTradingBot.EndToEnd;
public sealed class EndUserCliContract007Tests
{
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Help_exposes_migrate_to_an_end_user(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"--help");Assert.Equal(0,r.ExitCode);Assert.Contains("migrate",r.AllOutput,StringComparison.OrdinalIgnoreCase);}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Version_works_without_configuration(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"--version");Assert.Equal(0,r.ExitCode);Assert.False(string.IsNullOrWhiteSpace(r.StdOut));}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Invalid_input_fails_without_stack_trace(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"--definitely-not-real-7");Assert.NotEqual(0,r.ExitCode);Assert.DoesNotContain("System.NullReferenceException",r.AllOutput,StringComparison.Ordinal);Assert.DoesNotContain("at ForexTradingBot.",r.AllOutput,StringComparison.Ordinal);}finally{EndUserCliHarness.Cleanup(v);}}
}