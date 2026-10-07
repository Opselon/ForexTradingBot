using Xunit;
namespace ForexTradingBot.EndToEnd;
public sealed class EndUserInput085Tests
{
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Unicode_secret_key_is_handled_as_user_input(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"secrets","get","ключ-85");Assert.NotEqual(0,r.ExitCode);Assert.DoesNotContain("ArgumentException",r.AllOutput,StringComparison.Ordinal);}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Very_long_unknown_argument_does_not_crash_process(){var v=EndUserCliHarness.TempVault();try{var x=new string('x',4096);var r=await EndUserCliHarness.RunAsync(v,"--unknown-"+x);Assert.NotEqual(0,r.ExitCode);Assert.DoesNotContain("OutOfMemoryException",r.AllOutput,StringComparison.Ordinal);}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Path_like_user_input_does_not_produce_framework_trace(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"restore",Path.Combine(v,"..","missing-85.db"));Assert.NotEqual(0,r.ExitCode);Assert.DoesNotContain("at System.",r.AllOutput,StringComparison.Ordinal);}finally{EndUserCliHarness.Cleanup(v);}}
}