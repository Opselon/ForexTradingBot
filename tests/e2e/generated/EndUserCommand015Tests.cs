using Xunit;
namespace ForexTradingBot.EndToEnd;
public sealed class EndUserCommand015Tests
{
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Command_help_is_discoverable(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"doctor","--help");Assert.Equal(0,r.ExitCode);}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Unknown_option_is_rejected_cleanly(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"doctor","--not-a-real-option-15");Assert.NotEqual(0,r.ExitCode);Assert.DoesNotContain("NullReferenceException",r.AllOutput,StringComparison.Ordinal);}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Missing_required_context_never_emits_framework_stack_trace(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"doctor");Assert.DoesNotContain("at System.",r.AllOutput,StringComparison.Ordinal);Assert.DoesNotContain("at ForexTradingBot.",r.AllOutput,StringComparison.Ordinal);}finally{EndUserCliHarness.Cleanup(v);}}
}