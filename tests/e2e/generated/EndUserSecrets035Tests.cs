using Xunit;
namespace ForexTradingBot.EndToEnd;
public sealed class EndUserSecrets035Tests
{
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Secret_help_is_available(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"secrets","--help");Assert.Equal(0,r.ExitCode);Assert.Contains("list",r.AllOutput,StringComparison.OrdinalIgnoreCase);Assert.Contains("set",r.AllOutput,StringComparison.OrdinalIgnoreCase);}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Invalid_category_is_rejected(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"secrets","set","EDGE_35","value","--category","NotARealCategory");Assert.NotEqual(0,r.ExitCode);Assert.DoesNotContain("Unhandled exception",r.AllOutput,StringComparison.OrdinalIgnoreCase);}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Blank_key_is_rejected(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"secrets","get","   ");Assert.NotEqual(0,r.ExitCode);Assert.DoesNotContain("NullReferenceException",r.AllOutput,StringComparison.Ordinal);}finally{EndUserCliHarness.Cleanup(v);}}
}