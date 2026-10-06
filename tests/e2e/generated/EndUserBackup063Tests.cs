using Xunit;
namespace ForexTradingBot.EndToEnd;
public sealed class EndUserBackup063Tests
{
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Restore_missing_path_fails_cleanly(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"restore",Path.Combine(v,"missing-63.db"));Assert.NotEqual(0,r.ExitCode);Assert.DoesNotContain("NullReferenceException",r.AllOutput,StringComparison.Ordinal);}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Backup_help_is_discoverable(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"backup","--help");Assert.Equal(0,r.ExitCode);}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Restore_help_is_discoverable(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"restore","--help");Assert.Equal(0,r.ExitCode);}finally{EndUserCliHarness.Cleanup(v);}}
}