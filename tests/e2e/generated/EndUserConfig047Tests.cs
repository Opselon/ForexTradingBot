using Xunit;
namespace ForexTradingBot.EndToEnd;
public sealed class EndUserConfig047Tests
{
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Config_command_is_safe_on_fresh_machine(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"config");Assert.Equal(0,r.ExitCode);Assert.DoesNotContain("Exception",r.AllOutput,StringComparison.OrdinalIgnoreCase);}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Config_unknown_option_is_rejected(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"config","--not-real-47");Assert.NotEqual(0,r.ExitCode);Assert.DoesNotContain("NullReferenceException",r.AllOutput,StringComparison.Ordinal);}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Config_output_does_not_expose_placeholder_secret_material(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"config");Assert.DoesNotContain("TELEGRAM_BOT_TOKEN=",r.AllOutput,StringComparison.Ordinal);Assert.DoesNotContain("CRYPTO_PAY_API_TOKEN=",r.AllOutput,StringComparison.Ordinal);}finally{EndUserCliHarness.Cleanup(v);}}
}