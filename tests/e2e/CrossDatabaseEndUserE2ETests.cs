using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace ForexTradingBot.EndToEnd;

public sealed class CrossDatabaseEndUserE2ETests
{
    [Fact]
    [Trait("Category","EndToEnd")]
    [Trait("Surface","CrossDB")]
    public async Task PostgreSql_and_sqlite_expose_the_same_end_user_database_contract()
    {
        var root=FindRoot();
        var compose=Path.Combine(root,"tests","e2e","docker-compose.crossdb.e2e.yml");
        var project="forexbot-crossdb-"+Guid.NewGuid().ToString("N")[..10];
        try
        {
            var up=await Run("docker",["compose","-p",project,"-f",compose,"up","-d","--build"]);
            Assert.Equal(0,up.ExitCode);
            await WaitHealthy("http://127.0.0.1:18180/healthz");
            await WaitHealthy("http://127.0.0.1:18181/healthz");
            await AssertProvider("http://127.0.0.1:18180",project,compose,"postgres","Host=postgres;Port=5432;Database=forexbotdb;Username=forexbot;Password=crossdb-password");
            await AssertProvider("http://127.0.0.1:18181",project,compose,"sqlite","Data Source=/app/data/crossdb.sqlite");
            var pg=await Run("docker",["compose","-p",project,"-f",compose,"restart","app-postgres"]);
            Assert.Equal(0,pg.ExitCode);
            await WaitHealthy("http://127.0.0.1:18180/healthz");
            var sqlite=await Run("docker",["compose","-p",project,"-f",compose,"restart","app-sqlite"]);
            Assert.Equal(0,sqlite.ExitCode);
            await WaitHealthy("http://127.0.0.1:18181/healthz");
        }
        finally
        {
            await BestEffort("docker",["compose","-p",project,"-f",compose,"logs","--no-color"]);
            await BestEffort("docker",["compose","-p",project,"-f",compose,"down","-v","--remove-orphans"]);
        }
    }

    private static async Task AssertProvider(string baseUrl,string project,string compose,string provider,string connection)
    {
        // /api/config/test is Admin-authorized, so log in with the bootstrap password
        // the app writes on first run before probing the provider.
        var password=await Run("docker",["compose","-p",project,"-f",compose,"exec","-T","app-"+provider,"sh","-lc","cat /app/data/vault/bootstrap/admin-password.txt"]);
        Assert.True(password.ExitCode==0,$"could not read bootstrap password from app-{provider}: {password.StdErr}");
        var passwordValue=password.StdOut.Trim();
        Assert.False(string.IsNullOrWhiteSpace(passwordValue));

        using var handler=new HttpClientHandler{AllowAutoRedirect=false};
        using var client=new HttpClient(handler){BaseAddress=new Uri(baseUrl),Timeout=TimeSpan.FromSeconds(15)};
        var login=await client.PostAsJsonAsync("/api/auth/login",new{username="admin",password=passwordValue});
        Assert.Equal(HttpStatusCode.OK,login.StatusCode);

        var response=await client.PostAsJsonAsync("/api/config/test",new {databaseProvider=provider,dbConn=connection,botToken=(string?)null,redisConn=provider=="postgres"?"redis:6379":null});
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("OK",json.RootElement.GetProperty("databaseStatus").GetString());
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