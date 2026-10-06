using System.Diagnostics;
using Xunit;
namespace ForexTradingBot.EndToEnd;
internal static class EndUserCliHarness
{
 public static string TempVault(){var p=Path.Combine(Path.GetTempPath(),"forexbot-enduser-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(p);return p;}
 public static async Task<ProcessResult> RunAsync(string vault,params string[] args){
  var root=FindRoot();var psi=new ProcessStartInfo{FileName="dotnet",WorkingDirectory=root,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true};
  psi.Environment["FOREXBOT_VAULT_DIRECTORY"]=vault;psi.Environment["DOTNET_NOLOGO"]="1";psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"]="1";
  foreach(var a in new[]{"run","--project",Path.Combine(root,"Cli"),"-c","Release","--no-build","--no-restore","--"})psi.ArgumentList.Add(a);foreach(var a in args)psi.ArgumentList.Add(a);
  using var p=new Process{StartInfo=psi};Assert.True(p.Start());var o=p.StandardOutput.ReadToEndAsync();var e=p.StandardError.ReadToEndAsync();using var c=new CancellationTokenSource(TimeSpan.FromSeconds(30));
  try{await p.WaitForExitAsync(c.Token);}catch(OperationCanceledException){try{p.Kill(true);}catch{}throw new Xunit.Sdk.XunitException("CLI exceeded 30 second timeout.");}
  return new ProcessResult(p.ExitCode,await o,await e);
 }
 public static void Cleanup(string p){try{if(Directory.Exists(p))Directory.Delete(p,true);}catch{}}
 static string FindRoot(){var d=new DirectoryInfo(AppContext.BaseDirectory);while(d is not null){if(File.Exists(Path.Combine(d.FullName,"ForexTradingBot.sln")))return d.FullName;d=d.Parent!;}throw new DirectoryNotFoundException("ForexTradingBot.sln was not found.");}
 internal sealed record ProcessResult(int ExitCode,string StdOut,string StdErr){public string AllOutput=>StdOut+Environment.NewLine+StdErr;}
}