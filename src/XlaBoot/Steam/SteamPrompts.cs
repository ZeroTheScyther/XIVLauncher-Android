using System.Threading.Tasks;

namespace XlaBoot.Steam;

/// <summary>A Steam account name and password, as typed once at sign-in. The password is never stored.</summary>
public sealed record SteamCredentials(string Account, string Password);

/// <summary>What a finished sign-in leaves behind, so the next one needs no password.</summary>
/// <param name="Account">The Steam account name Steam itself reported, which may differ in case from what was typed.</param>
/// <param name="RefreshToken">Long-lived token used in place of the password. A full account credential: store it encrypted.</param>
/// <param name="GuardData">Steam Guard machine token, when Steam issued one. Lets a later sign-in skip the code.</param>
public sealed record SteamTokens(string Account, string RefreshToken, string? GuardData);

/// <summary>Which Steam Guard code Steam is asking for.</summary>
public enum SteamGuardKind
{
    /// <summary>The code shown in the Steam mobile app.</summary>
    Mobile,

    /// <summary>The code Steam mailed to the account's address.</summary>
    Email,
}

/// <summary>
/// The parts of a Steam sign-in only the user can answer. Implemented by the launcher's view model,
/// which turns each call into an overlay; the session itself knows nothing about the UI.
/// Every method is called from a background thread.
/// </summary>
public interface ISteamPrompts
{
    /// <summary>Asks for the Steam account name and password. Returns null if the user cancels.</summary>
    Task<SteamCredentials?> AskCredentialsAsync(string suggestedAccount, bool previousWasWrong);

    /// <summary>Asks for a Steam Guard code. Returns null if the user cancels.</summary>
    Task<string?> AskGuardCodeAsync(SteamGuardKind kind, string detail, bool previousWasWrong);

    /// <summary>
    /// Steam can confirm this sign-in from a push in the Steam mobile app. True waits for that,
    /// false falls back to typing a code.
    /// </summary>
    Task<bool> AskWaitForDeviceConfirmationAsync();

    /// <summary>A line of progress for the login screen.</summary>
    void Progress(string message);
}
