using Xunit;
namespace ForexTradingBot.EndToEnd;
public sealed class EndUserRuntime080Tests
{
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Status_is_safe_on_unconfigured_machine(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"status");Assert.DoesNotContain("NullReferenceException",r.AllOutput,StringComparison.Ordinal);Assert.DoesNotContain("at System.",r.AllOutput,StringComparison.Ordinal);}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Doctor_is_safe_on_unconfigured_machine(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"doctor");Assert.DoesNotContain("NullReferenceException",r.AllOutput,StringComparison.Ordinal);Assert.DoesNotContain("at ForexTradingBot.",r.AllOutput,StringComparison.Ordinal);}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Logs_is_safe_when_no_runtime_exists(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"logs");Assert.DoesNotContain("NullReferenceException",r.AllOutput,StringComparison.Ordinal);Assert.DoesNotContain("Unhandled exception",r.AllOutput,StringComparison.OrdinalIgnoreCase);}finally{EndUserCliHarness.Cleanup(v);}}
}