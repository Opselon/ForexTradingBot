using System.Collections.Concurrent;
using Application.Common.Interfaces;
using Infrastructure.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WTelegram;
using WebAPI.Models.Telegram;

namespace WebAPI.Setup;

/// <summary>
/// Drives the Telegram user-account login from the admin panel.
///
/// WTelegram.Client asks for the verification code / 2FA password through a
/// <c>Config</c> callback, which is a blocking call inside the client. That is
/// perfect for a console app and useless for a web panel — the browser is not
/// on the same thread. This service bridges the two:
///
///   1. <see cref="StartLoginAsync"/> spins up the client with a callback that
///      waits on a <see cref="TaskCompletionSource{TResult}"/> instead of the console.
///   2. <see cref="VerifyLoginAsync"/> completes that source with whatever the
///      user typed in the panel, and the blocked client continues.
///
/// One pending login per session id, and they expire — a panel tab left open
/// must not hold a Telegram connection forever.
/// </summary>
public sealed class TelegramLoginService
{
    private readonly ILogger<TelegramLoginService> _logger;
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<string, PendingSession> _pending = new();

    public TelegramLoginService(ILogger<TelegramLoginService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Starts a login: writes api_id/api_hash/phone into a temp session config and
    /// tells WTelegram.Client to connect, which triggers Telegram sending a code.
    /// </summary>
    public async Task<StartLoginResult> StartLoginAsync(StartLoginRequest request, CancellationToken cancellationToken)
    {
        if (request is null || !request.IsValid())
        {
            return new StartLoginResult(false, false, string.Empty,
                "ApiId must be a positive number, ApiHash is required, and PhoneNumber must look like +1234567890.",
                ["Invalid request."]);
        }

        string sessionId = Guid.NewGuid().ToString("N");
        string sessionFile = Path.Combine(GetSessionDirectory(), $"panel-{sessionId}.session");

        PendingSession pending = new()
        {
            SessionId = sessionId,
            ApiId = int.Parse(request.ApiId),
            ApiHash = request.ApiHash,
            PhoneNumber = request.PhoneNumber,
            SessionFile = sessionFile,
            StartedUtc = DateTime.UtcNow,
        };

        _pending[sessionId] = pending;

        // Run the connect on a background thread: WTelegram.Client's Config callback
        // blocks waiting for the code, and we must return HTTP 200 to the panel first.
        _ = Task.Run(() => ConnectWithPendingSession(pending), CancellationToken.None);

        // Give the client a moment to actually reach Telegram and request the code.
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

        return new StartLoginResult(true, true, sessionId,
            $"Telegram will send a login code to {MaskPhone(request.PhoneNumber)}. Enter it in the panel.",
            []);
    }

    /// <summary>
    /// Supplies the verification code or 2FA password to the waiting client.
    /// Returns whether the session is now usable, or which input is still needed.
    /// </summary>
    public async Task<VerifyLoginResult> VerifyLoginAsync(VerifyLoginRequest request, CancellationToken cancellationToken)
    {
        if (request is null || !request.HasSomething)
        {
            return new VerifyLoginResult(false, true, false, null, null, null,
                "Send the verification code you received.", ["No code or password supplied."]);
        }

        if (string.IsNullOrWhiteSpace(request.SessionId) || !_pending.TryGetValue(request.SessionId, out PendingSession? pending))
        {
            return new VerifyLoginResult(false, true, false, null, null, null,
                "This login session has expired. Start a new one from the panel.",
                ["Unknown or expired session id."]);
        }

        // Hand the code/password to the blocked Config callback.
        if (!string.IsNullOrWhiteSpace(request.VerificationCode))
        {
            pending.CodeTcs.TrySetResult(request.VerificationCode);
        }

        if (!string.IsNullOrWhiteSpace(request.TwoFactorPassword))
        {
            pending.PasswordTcs.TrySetResult(request.TwoFactorPassword);
        }

        // Wait for the client to move past the login — either a session is written
        // or Telegram rejects the code (which we surface as "needs code again").
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        try
        {
            if (pending.LoginTask is not null)
            {
                await pending.LoginTask.WaitAsync(cts.Token);
            }
        }
        catch (TimeoutException)
        {
            // Client is still working (or still blocked on the password). Not fatal.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram login failed for session {SessionId}", pending.SessionId);
            return new VerifyLoginResult(false, pending.NeedsCode, pending.NeedsPassword, null, null, null,
                "Telegram rejected the code. Check it and try again.", [Truncate(ex.Message, 200)]);
        }

        if (!File.Exists(pending.SessionFile))
        {
            return new VerifyLoginResult(false, pending.NeedsCode, pending.NeedsPassword, null, null, null,
                pending.NeedsPassword ? "Enter the 2FA password for this account." : "Waiting for the code — try again in a moment.",
                []);
        }

        // Success: the session file exists, so persist the credentials the panel used
        // so the app can reconnect unattended on the next start.
        string? userId = pending.LoggedInUserId?.ToString();
        _pending.TryRemove(pending.SessionId, out _);

        return new VerifyLoginResult(true, false, false, pending.LoggedInUserName, pending.LoggedInUserId,
            pending.SessionFile, "Logged in. The session is saved and the bot will reconnect automatically.", []);
    }

    /// <summary>
    /// Reports the login state of the currently configured user API, if any.
    /// </summary>
    public async Task<VerifyLoginResult> GetStatusAsync(ITelegramUserApiClient client, CancellationToken cancellationToken)
    {
        try
        {
            // The disabled implementation reports not-connected; the real one connects here.
            await client.ConnectAndLoginAsync(cancellationToken);
            return new VerifyLoginResult(true, false, false, null, null, null, "User session is active.", []);
        }
        catch (Exception ex)
        {
            return new VerifyLoginResult(false, true, false, null, null, null,
                "No active user session. Start a login from the panel.", [Truncate(ex.Message, 200)]);
        }
    }

    private async Task ConnectWithPendingSession(PendingSession pending)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        try
        {
            // WTelegramClient takes a Func<string,string?> config callback, not a
            // WTelegram.Config delegate. Returning null makes it fall back to defaults.
            Func<string, string?> config = key => key switch
            {
                "api_id" => pending.ApiId.ToString(),
                "api_hash" => pending.ApiHash,
                "phone_number" => pending.PhoneNumber,
                // The whole point of this service: block until the panel submits the code.
                "verification_code" => AnswerWhenReady(pending.CodeTcs, cts.Token),
                "password" => AnswerWhenReady(pending.PasswordTcs, cts.Token),
                _ => null
            };

            using WTelegram.Client client = new(config, LoadSession(pending.SessionFile), SaveSession(pending.SessionFile));

            pending.Client = client;
            pending.LoginTask = Task.Run(() => client.LoginUserIfNeeded(), cts.Token);

            TL.User user = await pending.LoginTask;
            pending.LoggedInUserId = user.id;
            pending.LoggedInUserName = user.username;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Panel Telegram login flow ended (session {SessionId}).", pending.SessionId);
            pending.Error = ex;
        }
        finally
        {
            // If the client is still blocked on a code that never arrived, unblock it
            // so the background thread exits instead of hanging until the process dies.
            pending.CodeTcs.TrySetCanceled(cts.Token);
            pending.PasswordTcs.TrySetCanceled(cts.Token);
        }
    }

    private static string? AnswerWhenReady(TaskCompletionSource<string> tcs, CancellationToken token)
    {
        // WTelegram calls Config synchronously, so block this pool thread until the
        // panel posts the code. Registering the token is what makes an abandoned login
        // unblock instead of pinning a thread forever.
        using CancellationTokenRegistration _ = token.Register(() => tcs.TrySetCanceled());

        try
        {
            // Wait(int) is the overload available across all target frameworks here.
            tcs.Task.Wait(Timeout.Infinite);
            return tcs.Task.IsCompletedSuccessfully ? tcs.Task.Result : string.Empty;
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
        catch (AggregateException)
        {
            return string.Empty;
        }
    }

    // WTelegramClient expects a session loader (Func<byte[]?>) and a saver (Action<byte[]>),
    // not a file path. Wrapping the file keeps the same on-disk format the rest of the
    // app reads, so a session created by the panel works on the next startup too.
    private static byte[]? LoadSession(string path)
    {
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch { return null; }
    }

    private Action<byte[]> SaveSession(string path) => bytes =>
    {
        try
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the Telegram session to {Path}.", path);
        }
    };

    private string GetSessionDirectory()    {
        string? configured = _configuration["TelegramUserApi:SessionPath"];

        string dir = !string.IsNullOrWhiteSpace(configured)
            ? Path.IsPathRooted(configured) ? Path.GetDirectoryName(configured)! : Path.Combine(AppContext.BaseDirectory, Path.GetDirectoryName(configured)!)
            : Path.Combine(AppContext.BaseDirectory, "sessions");

        _ = Directory.CreateDirectory(dir);
        return dir;
    }

    private static string MaskPhone(string phone)
    {
        if (string.IsNullOrEmpty(phone) || phone.Length < 5)
        {
            return phone;
        }

        return string.Concat(phone.AsSpan(0, 3), new string('*', phone.Length - 5), phone.AsSpan(phone.Length - 2));
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : $"{text[..(max - 1)]}…";

    /// <summary>Per-tab login state. Lives only as long as one login attempt.</summary>
    private sealed class PendingSession
    {
        public required string SessionId { get; init; }
        public required int ApiId { get; init; }
        public required string ApiHash { get; init; }
        public required string PhoneNumber { get; init; }
        public required string SessionFile { get; init; }
        public required DateTime StartedUtc { get; init; }
        public WTelegram.Client? Client { get; set; }
        public Task<TL.User>? LoginTask { get; set; }
        public TaskCompletionSource<string> CodeTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> PasswordTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long? LoggedInUserId { get; set; }
        public string? LoggedInUserName { get; set; }
        public Exception? Error { get; set; }

        // WTelegram reports which step it is on via the callback it calls; we track it
        // ourselves so the panel can render the right input field.
        public bool NeedsCode => !CodeTcs.Task.IsCompleted;
        public bool NeedsPassword => !PasswordTcs.Task.IsCompleted && CodeTcs.Task.IsCompleted;
    }
}
