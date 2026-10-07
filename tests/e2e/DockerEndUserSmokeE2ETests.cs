using System.Diagnostics;
using System.Net;
using Xunit;

namespace ForexTradingBot.EndToEnd;

public sealed class DockerEndUserSmokeE2ETests
{
    [Fact]
    [Trait("Category","EndToEnd")]
    [Trait("Surface","Docker")]
    public async Task Docker_compose_build_health_and_shutdown_are_real_end_to_end_operations()
    {
        var root=FindRoot();
        var compose=Path.Combine(root,"tests","e2e","docker-compose.e2e.yml");
        var project="forexbot-docker-e2e-"+Guid.NewGuid().ToString("N")[..10];
        try
        {
            var up=await Run("docker",["compose","-p",project,"-f",compose,"up","-d","--build"]);
            Assert.Equal(0,up.ExitCode);
            await WaitHealthy("http://127.0.0.1:18080/healthz");
            var ps=await Run("docker",["compose","-p",project,"-f",compose,"ps"]);
            Assert.Equal(0,ps.ExitCode);
            Assert.Contains("app",ps.StdOut,StringComparison.OrdinalIgnoreCase);
            Assert.Contains("postgres",ps.StdOut,StringComparison.OrdinalIgnoreCase);
            Assert.Contains("redis",ps.StdOut,StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await BestEffort("docker",["compose","-p",project,"-f",compose,"logs","--no-color"]);
            await BestEffort("docker",["compose","-p",project,"-f",compose,"down","-v","--remove-orphans"]);
        }
    }

    private static async Task WaitHealthy(string url)
    {
        using var client=new HttpClient{Timeout=TimeSpan.FromSeconds(5)};
        var deadline=DateTimeOffset.UtcNow.AddMinutes(6);
        while(DateTimeOffset.UtcNow<deadline)
        {
            try { if((await client.GetAsync(url)).StatusCode==HttpStatusCode.OK)return; } catch { }
            await Task.Delay(3000);
        }
        throw new Xunit.Sdk.XunitException($"Health endpoint did not become ready: {url}");
    }

    private static async Task<ProcessResult> Run(string file,IReadOnlyList<string> args)
    {
        var psi=new ProcessStartInfo{FileName=file,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};
        foreach(var arg in args)psi.ArgumentList.Add(arg);
        using var p=new Process{StartInfo=psi};
        Assert.True(p.Start());
        var o=p.StandardOutput.ReadToEndAsync();var e=p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return new(p.ExitCode,await o,await e);
    }

    private static async Task BestEffort(string file,IReadOnlyList<string> args){try{await Run(file,args);}catch{}}
    private static string FindRoot(){var d=new DirectoryInfo(AppContext.BaseDirectory);while(d is not null){if(File.Exists(Path.Combine(d.FullName,"ForexTradingBot.sln")))return d.FullName;d=d.Parent;}throw new DirectoryNotFoundException("ForexTradingBot.sln not found.");}
    private sealed record ProcessResult(int ExitCode,string StdOut,string StdErr);
}