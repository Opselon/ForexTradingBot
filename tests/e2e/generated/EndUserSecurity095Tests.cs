using Xunit;
namespace ForexTradingBot.EndToEnd;
public sealed class EndUserSecurity095Tests
{
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Help_does_not_emit_environment_secret_values(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"--help");Assert.DoesNotContain("TELEGRAM_BOT_TOKEN=",r.AllOutput,StringComparison.Ordinal);Assert.DoesNotContain("CRYPTO_PAY_API_TOKEN=",r.AllOutput,StringComparison.Ordinal);}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Invalid_secret_operation_has_no_framework_trace(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"secrets","get","../outside-95");Assert.NotEqual(0,r.ExitCode);Assert.DoesNotContain("at System.",r.AllOutput,StringComparison.Ordinal);}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Unknown_command_is_fail_closed(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"this-command-does-not-exist-95");Assert.NotEqual(0,r.ExitCode);Assert.DoesNotContain("Unhandled exception",r.AllOutput,StringComparison.OrdinalIgnoreCase);}finally{EndUserCliHarness.Cleanup(v);}}
}