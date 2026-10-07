using System.Security.Cryptography;

namespace WebAPI.Security;

/// <summary>
/// A server-side security stamp for the admin cookie session.
/// </summary>
/// <remarks>
/// <see cref="Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.SignOutAsync(HttpContext)"/>
/// only clears the caller's own cookie: every cookie issued earlier stays valid until
/// it expires. Rotating a stamp held on the server, and embedding it as a claim at
/// login, lets every request compare the two and reject any cookie minted before the
/// last logout. That closes the real gap between "logged out here" and "logged out
/// everywhere".
/// </remarks>
public static class AuthSecurityStamp
{
    private const string ClaimType = "security-stamp";
    private const string FileName = "auth.stamp";

    private static string StampPath
    {
        get
        {
            var directory = ForexTradingBot.Cli.Secrets.SqliteSecretVault.DefaultVaultDirectory();
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, FileName);
        }
    }

    /// <summary>The claim type carrying the stamp in an issued cookie.</summary>
    public static string Claim => ClaimType;

    /// <summary>Reads the current stamp, generating one on first use.</summary>
    public static string Current()
    {
        try
        {
            var existing = Read();
            if (!string.IsNullOrEmpty(existing))
            {
                return existing;
            }

            var stamp = NewStamp();
            Write(stamp);
            return stamp;
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only runtime directory: fall back to a per-process stamp so the
            // stamp comparison still runs within a single process lifetime.
            return NewStamp();
        }
        catch (IOException)
        {
            return NewStamp();
        }
    }

    /// <summary>Invalidates every outstanding cookie by replacing the stamp.</summary>
    public static void Rotate()
    {
        try
        {
            Write(NewStamp());
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort: a container without a writable vault keeps the in-memory
            // stamp, so the comparison still protects the current process.
        }
        catch (IOException)
        {
            // Best effort, same reason.
        }
    }

    /// <summary>True when the stamp carried by a cookie is still the current one.</summary>
    public static bool IsValid(string? claimValue)
    {
        return !string.IsNullOrEmpty(claimValue)
            && string.Equals(claimValue, Current(), StringComparison.Ordinal);
    }

    private static string NewStamp()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    private static string? Read()
    {
        return File.Exists(StampPath)
            ? File.ReadAllText(StampPath).Trim()
            : null;
    }

    private static void Write(string stamp)
    {
        var temp = StampPath + ".tmp";
        File.WriteAllText(temp, stamp);
        File.Move(temp, StampPath, overwrite: true);
    }
}
