using System;
using SteamKit2;

namespace XlaBoot.Steam;

/// <summary>
/// A Steam sign-in that did not get far enough to ask for a ticket. Distinct from XIVLauncher's own
/// SteamException, which covers the ticket and the Square Enix side of the login.
/// </summary>
public sealed class SteamSignInException : Exception
{
    public SteamSignInException(string message, EResult result = EResult.Invalid, Exception? inner = null)
        : base(message, inner)
        => Result = result;

    /// <summary>Steam's own result code, when there was one.</summary>
    public EResult Result { get; }

    /// <summary>
    /// True when Steam refused an app ownership ticket because the account does not own that app. The
    /// caller knows which app it asked for and can say something more useful than Steam's own wording.
    /// </summary>
    public bool NotOwned { get; init; }

    /// <summary>True when the stored refresh token is no longer good and a password is needed again.</summary>
    public bool NeedsPassword => Result is EResult.InvalidPassword or EResult.AccessDenied or EResult.Expired
        or EResult.InvalidSignature or EResult.Revoked or EResult.InvalidParam;

    /// <summary>Plain wording for the login screen; Steam's enum names mean nothing to a player.</summary>
    public static string Explain(EResult result) => result switch
    {
        EResult.InvalidPassword => "Steam did not accept that password.",
        EResult.AccountLogonDenied => "Steam needs the code it mailed to your account.",
        EResult.AccountLoginDeniedNeedTwoFactor => "Steam needs the code from your Steam mobile app.",
        EResult.AccountDisabled => "That Steam account is disabled.",
        EResult.AccountLockedDown => "That Steam account is locked.",
        EResult.RateLimitExceeded => "Too many Steam sign-in attempts. Wait a few minutes and try again.",
        EResult.ServiceUnavailable or EResult.TryAnotherCM => "Steam is not reachable right now.",
        EResult.Expired or EResult.AccessDenied => "The saved Steam sign-in expired. Enter your password again.",
        _ => $"Steam refused the sign-in ({result}).",
    };
}
