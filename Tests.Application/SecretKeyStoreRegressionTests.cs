using System.Security.Cryptography;
using ForexTradingBot.Cli.Secrets;

namespace Tests.Application;

public sealed class SecretKeyStoreRegressionTests : IDisposable
{
    private readonly string _directory;

    public SecretKeyStoreRegressionTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "forexbot-key-regression-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void LoadOrCreateKey_creates_a_32_byte_key_and_reloads_the_same_key()
    {
        var first = SecretKeyStore.LoadOrCreateKey(_directory);
        var second = SecretKeyStore.LoadOrCreateKey(_directory);

        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);
        Assert.True(File.Exists(Path.Combine(_directory, "secrets.key")));
    }

    [Fact]
    public void Deleting_key_file_forces_creation_of_a_new_key()
    {
        var first = SecretKeyStore.LoadOrCreateKey(_directory);
        File.Delete(Path.Combine(_directory, "secrets.key"));

        var second = SecretKeyStore.LoadOrCreateKey(_directory);

        Assert.Equal(32, second.Length);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Invalid_existing_key_material_fails_loudly()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "secrets.key");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });

        Assert.ThrowsAny<Exception>(() => SecretKeyStore.LoadOrCreateKey(_directory));
    }

    [Fact]
    public void Parallel_loads_converge_on_one_persisted_key()
    {
        var results = Enumerable.Range(0, 8)
            .AsParallel()
            .Select(_ => SecretKeyStore.LoadOrCreateKey(_directory))
            .ToArray();

        Assert.Equal(8, results.Length);
        Assert.All(results, key => Assert.Equal(32, key.Length));
        Assert.Single(results.Select(Convert.ToBase64String).Distinct());
    }
}
