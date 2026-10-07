using Xunit;
namespace ForexTradingBot.EndToEnd;
public sealed class EndUserBoundary105Tests
{
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Empty_argument_is_rejected_without_crash(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"");Assert.NotEqual(0,r.ExitCode);Assert.DoesNotContain("NullReferenceException",r.AllOutput,StringComparison.Ordinal);}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Repeated_option_is_handled_deterministically(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"--help","--help");Assert.NotEqual(2,r.ExitCode);}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Numeric_looking_user_input_is_not_executed(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"secrets","get","1234567890");Assert.NotEqual(0,r.ExitCode);Assert.DoesNotContain("at System.",r.AllOutput,StringComparison.Ordinal);}finally{EndUserCliHarness.Cleanup(v);}}
}