using Xunit;
namespace ForexTradingBot.EndToEnd;
public sealed class EndUserMigration051Tests
{
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Migrations_list_is_user_callable(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"migrations");Assert.NotEqual(2,r.ExitCode);Assert.DoesNotContain("NullReferenceException",r.AllOutput,StringComparison.Ordinal);}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Invalid_migration_option_fails_cleanly(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"migrate","--connection","invalid-51");Assert.NotEqual(0,r.ExitCode);Assert.DoesNotContain("System.NullReferenceException",r.AllOutput,StringComparison.Ordinal);}finally{EndUserCliHarness.Cleanup(v);}}
 [Fact][Trait("Category","EndToEnd")][Trait("Surface","CLI")]
 public async Task Migration_help_remains_available_without_database(){var v=EndUserCliHarness.TempVault();try{var r=await EndUserCliHarness.RunAsync(v,"migrate","--help");Assert.Equal(0,r.ExitCode);}finally{EndUserCliHarness.Cleanup(v);}}
}