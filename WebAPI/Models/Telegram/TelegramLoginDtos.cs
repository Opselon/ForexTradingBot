namespace WebAPI.Models.Telegram;

/// <summary>
/// Start of the panel login flow: the panel asks Telegram to send a login code
/// to the user's phone/app. Nothing is stored here — the code comes back in the
/// next step.
/// </summary>
public sealed record StartLoginRequest(
    string ApiId,
    string ApiHash,
    string PhoneNumber)
{
    public bool IsValid()
    {
        if (!int.TryParse(ApiId, out int id) || id <= 0)
            return false;
        if (string.IsNullOrWhiteSpace(ApiHash))
            return false;
        // Accept E.164-ish; the important part is a leading + and at least 7 digits.
        string digits = new string(PhoneNumber.Where(char.IsDigit).ToArray());
        return !string.IsNullOrWhiteSpace(PhoneNumber) && digits.Length >= 7;
    }
}

public sealed record StartLoginResult(
    bool Success,
    bool CodeSent,
    string SessionId,
    string Message,
    string[] Errors);

/// <summary>
/// Second step: the code the user received, plus the 2FA password when the
/// account has one. Either can be submitted — the flow keeps going until
/// Telegram accepts the session.
/// </summary>
public sealed record VerifyLoginRequest(
    string SessionId,
    string? VerificationCode,
    string? TwoFactorPassword)
{
    public bool HasSomething =>
        !string.IsNullOrWhiteSpace(VerificationCode)
        || !string.IsNullOrWhiteSpace(TwoFactorPassword);
}

public sealed record VerifyLoginResult(
    bool Success,
    bool NeedsVerificationCode,
    bool NeedsTwoFactorPassword,
    string? BotUserName,
    long? UserId,
    string? SessionFilePath,
    string Message,
    string[] Errors);

/// <summary>
/// Login state kept server-side while the user is typing a code. Short-lived by
/// design: it is cleared the moment a session file is written or the flow times out.
/// </summary>
public sealed record PendingTelegramLogin(
    string SessionId,
    int ApiId,
    string ApiHash,
    string PhoneNumber,
    DateTime StartedUtc,
    bool CodeSent);
