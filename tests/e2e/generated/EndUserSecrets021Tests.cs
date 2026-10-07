using Xunit;
namespace ForexTradingBot.EndToEnd;
public sealed class EndUserSecrets021Tests
{
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Fresh_vault_can_list_without_plaintext(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"secrets","list");Assert.Equal(0,r.ExitCode);Assert.DoesNotContain("SECRET_VALUE",r.AllOutput,StringComparison.OrdinalIgnoreCase);}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Missing_secret_fails_closed(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"secrets","get","MISSING_21");Assert.NotEqual(0,r.ExitCode);Assert.DoesNotContain("NullReferenceException",r.AllOutput,StringComparison.Ordinal);}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Missing_delete_does_not_crash(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"secrets","delete","MISSING_21","--yes");Assert.NotEqual(0,r.ExitCode);Assert.DoesNotContain("Unhandled exception",r.AllOutput,StringComparison.OrdinalIgnoreCase);}finally{EndUserCliHarness.Cleanup(v);}}
}