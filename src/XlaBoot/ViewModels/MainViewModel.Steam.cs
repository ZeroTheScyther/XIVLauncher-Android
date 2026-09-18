using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XIVLauncher.Common;
using XIVLauncher.Common.Game.Exceptions;
using XlaBoot.Steam;

namespace XlaBoot.ViewModels;

/// <summary>
/// The Steam half of the login page: the "Use Steam service account" box, and the two overlays a Steam
/// sign-in can need (password, then whichever Steam Guard step Steam asks for).
///
/// Steam is only ever contacted to fetch one auth session ticket for the Square Enix login. The game
/// itself never talks to Steam, so the connection is dropped as soon as the login is done.
/// </summary>
public partial class MainViewModel : ISteamPrompts
{
    /// <summary>"Use Steam service account" on the login page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SteamStatus))]
    private bool _isSteamAccount;

    [RelayCommand]
    private void ToggleIsSteamAccount() => IsSteamAccount = !IsSteamAccount;

    /// <summary>The Steam sign-in kept from last time, so a repeat login needs no Steam password.</summary>
    private SteamTokens? _steamTokens;

    /// <summary>Set when the user asks for a code instead of the push; see AskWaitForDeviceConfirmationAsync.</summary>
    private bool _preferSteamGuardCode;

    private CancellationTokenSource? _steamSignIn;

    /// <summary>The line under the checkbox: who the launcher will sign in to Steam as, if it already knows.</summary>
    public string SteamStatus => _steamTokens is { } tokens
        ? $"Signed in to Steam as {tokens.Account}."
        : "You will be asked to sign in to Steam.";

    /// <summary>
    /// Tells Steam this is a different client from the user's desktop Steam. Without a distinct value,
    /// signing in here logs them out there. Generated once and kept, so it stays the same client.
    /// </summary>
    private static uint SteamLoginId
    {
        get
        {
            var stored = AppHost.Get("steam_login_id", "");
            if (uint.TryParse(stored, out var id) && id != 0)
                return id;

            // Top bit kept clear: Steam treats the high bit as a marker for its own console clients.
            id = (uint)Random.Shared.Next(1, int.MaxValue);
            AppHost.Set("steam_login_id", id.ToString());
            return id;
        }
    }

    /// <summary>
    /// "Free trial account" on the login page. It describes the account, so it applies to standalone and
    /// Steam accounts alike: the lobby checks the entitlement and turns away anyone whose claim does not
    /// match, whatever the platform. For Steam it does double duty, picking which of the two Steam apps
    /// (full game or Free Trial) the auth ticket is asked for. Getting it wrong fails the login or the
    /// lobby, which is why it sits on the login page rather than in the settings screen.
    /// </summary>
    [ObservableProperty]
    private bool _isFreeTrial;

    [RelayCommand]
    private void ToggleIsFreeTrial() => IsFreeTrial = !IsFreeTrial;

    // Kept outside the saved login so it survives with or without "Remember login"; it describes the
    // account, not the session.
    partial void OnIsFreeTrialChanged(bool value) => AppHost.Set("free_trial", value ? "ON" : "OFF");

    /// <summary>Retail or Free Trial, which are separate apps on Steam and need separate tickets.</summary>
    private uint SteamAppId => IsFreeTrial ? Constants.STEAM_FT_APP_ID : Constants.STEAM_APP_ID;

    /// <summary>
    /// Signs in to Steam so the Square Enix login can ask it for a ticket. Retries once if the user
    /// asks for a Steam Guard code rather than waiting for the push.
    /// </summary>
    /// <param name="steam">
    /// The session the Launcher was built with. Returned as-is on success; a different instance comes
    /// back only if the sign-in had to start over, because a cancelled Steam connection cannot be reused.
    /// </param>
    private async Task<SteamKitSteam> SignInToSteamAsync(SteamKitSteam steam)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                _steamSignIn = new CancellationTokenSource();
                try
                {
                    _steamTokens = await steam.SignInAsync(_steamTokens, this, _steamSignIn.Token);
                    OnPropertyChanged(nameof(SteamStatus));
                    return steam;
                }
                catch (OperationCanceledException) when (_preferSteamGuardCode && attempt == 0)
                {
                    // The user asked to type a code instead of approving the push. The choice has to be
                    // made before Steam is polled, so the sign-in starts over with it set.
                    steam.Dispose();
                    steam = new SteamKitSteam(SteamAppId, SteamLoginId);
                }
                finally
                {
                    _steamSignIn.Dispose();
                    _steamSignIn = null;
                    CloseSteamPrompts();
                }
            }
        }
        catch
        {
            steam.Dispose();
            throw;
        }
    }

    // ---- Lossless Scaling --------------------------------------------------------------------

    /// <summary>While set, the Steam status lines go here instead of only to the login page's greeting.</summary>
    private Action<string>? _steamStatusSink;

    private const string GameSignInReason =
        "Your Square Enix account is linked to Steam, so Steam has to vouch for this login. Your Steam password is not saved.";

    private const string LosslessSignInReason =
        "In order to use this feature you must have Lossless Scaling on Steam. Please connect to your Steam account to download it.";

    /// <summary>Red line for the next sign-in dialog when it is not a wrong password, e.g. an account without the app.</summary>
    private string? _steamSignInNotice;

    /// <summary>The line under "Sign in to Steam": why the dialog is asking.</summary>
    [ObservableProperty]
    private string _steamSignInReason = GameSignInReason;

    /// <summary>
    /// Settings > Graphics > Lossless Scaling: signs in to Steam and fetches the player's own Lossless.dll.
    ///
    /// The sign-in is kept in its own slot (MainActivity's lsfg-steam.json), never in the saved game login:
    /// the account that owns Lossless Scaling need not be the one the game logs in with, and storing it there
    /// would switch the game login to it. Tried in order: that slot, then the game's saved Steam sign-in, then
    /// a fresh sign-in. An account that does not own the app is dropped and the next one is tried, so a game
    /// account without Lossless Scaling leads to the sign-in dialog rather than a dead end.
    /// </summary>
    private async Task<bool> FetchLosslessAsync(Action<string> status)
    {
        var cached = LoadLosslessSteam?.Invoke();
        var candidates = new[] { cached, _steamTokens, null }
            .Where((t, i) => i == 2 || t != null)
            .DistinctBy(t => t?.Account.ToLowerInvariant())
            .ToList();

        _steamStatusSink = status;
        SteamSignInReason = LosslessSignInReason;
        try
        {
            for (var i = 0; ; i++)
            {
                var saved = candidates[i];
                try
                {
                    var (tokens, downloaded) = await FetchLosslessWithAsync(saved, status);
                    SaveLosslessSteam?.Invoke(tokens);
                    if (saved != null && ReferenceEquals(saved, _steamTokens))
                    {
                        _steamTokens = tokens; // refreshed, same account
                        OnPropertyChanged(nameof(SteamStatus));
                    }
                    return downloaded;
                }
                catch (SteamSignInException ex) when (ex.NotOwned && saved != null && i + 1 < candidates.Count)
                {
                    if (ReferenceEquals(saved, cached))
                        SaveLosslessSteam?.Invoke(null);
                    // Shown in the sign-in dialog that comes next, so it is clear why it is asking again.
                    _steamSignInNotice = LosslessFetch.NotOwnedMessage;
                }
            }
        }
        finally
        {
            _steamStatusSink = null;
            _steamSignInNotice = null;
            SteamSignInReason = GameSignInReason;
        }
    }

    private async Task<(SteamTokens Tokens, bool Downloaded)> FetchLosslessWithAsync(SteamTokens? saved, Action<string> status)
    {
        var session = new SteamSession(SteamLoginId);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                _steamSignIn = new CancellationTokenSource();
                try
                {
                    var tokens = await session.SignInAsync(saved, this, _steamSignIn.Token);
                    var downloaded = await LosslessFetch.FetchAsync(session, AppHost.FilesDir, status, _steamSignIn.Token);
                    return (tokens, downloaded);
                }
                catch (OperationCanceledException) when (_preferSteamGuardCode && attempt == 0)
                {
                    // "Enter a code instead": same restart as SignInToSteamAsync.
                    session.Dispose();
                    session = new SteamSession(SteamLoginId);
                }
                finally
                {
                    _steamSignIn.Dispose();
                    _steamSignIn = null;
                    CloseSteamPrompts();
                }
            }
        }
        finally
        {
            session.Dispose();
        }
    }

    // ---- Steam password overlay --------------------------------------------------------------

    [ObservableProperty]
    private bool _isSteamSignInOpen;

    [ObservableProperty]
    private string _steamAccountInput = "";

    [ObservableProperty]
    private string _steamPasswordInput = "";

    [ObservableProperty]
    private string _steamSignInError = "";

    [ObservableProperty]
    private bool _showSteamPassword;

    [RelayCommand]
    private void ToggleShowSteamPassword() => ShowSteamPassword = !ShowSteamPassword;

    private TaskCompletionSource<SteamCredentials?>? _steamSignInPrompt;

    [RelayCommand]
    private void SubmitSteamSignIn()
    {
        if (_steamSignInPrompt == null)
            return;

        var account = SteamAccountInput.Trim();
        if (account.Length == 0 || SteamPasswordInput.Length == 0)
        {
            SteamSignInError = "Enter your Steam account name and password.";
            return;
        }

        var credentials = new SteamCredentials(account, SteamPasswordInput);
        SteamPasswordInput = "";
        IsSteamSignInOpen = false;
        var prompt = _steamSignInPrompt;
        _steamSignInPrompt = null;
        prompt.TrySetResult(credentials);
    }

    [RelayCommand]
    private void CancelSteamSignIn()
    {
        SteamPasswordInput = "";
        IsSteamSignInOpen = false;
        var prompt = _steamSignInPrompt;
        _steamSignInPrompt = null;
        prompt?.TrySetResult(null);
    }

    // ---- Steam Guard overlay -----------------------------------------------------------------

    [ObservableProperty]
    private bool _isSteamGuardOpen;

    /// <summary>True while waiting for the push in the Steam mobile app, false while asking for a code.</summary>
    [ObservableProperty]
    private bool _isSteamGuardWaiting;

    [ObservableProperty]
    private string _steamGuardDetail = "";

    [ObservableProperty]
    private string _steamGuardCode = "";

    [ObservableProperty]
    private string _steamGuardError = "";

    private TaskCompletionSource<string?>? _steamGuardPrompt;

    [RelayCommand]
    private void SubmitSteamGuard()
    {
        if (_steamGuardPrompt == null)
            return;

        // Steam Guard codes are five characters and not only digits, unlike the Square Enix one-time
        // password, so this cannot reuse the OTP check or auto-submit on a digit count.
        var code = SteamGuardCode.Trim().ToUpperInvariant();
        if (code.Length != 5)
        {
            SteamGuardError = "Enter the 5-character code.";
            return;
        }

        CloseSteamGuard(code);
    }

    [RelayCommand]
    private void CancelSteamGuard()
    {
        // While waiting for the push, no prompt is outstanding to answer with null: Steam is being
        // polled, and stopping that is the only way out.
        if (IsSteamGuardWaiting)
            _steamSignIn?.Cancel();

        CloseSteamGuard(null);
    }

    /// <summary>
    /// "Enter a code instead" while waiting for the push. Whether to wait for the push has to be decided
    /// before Steam is polled, so this cancels the sign-in; SignInToSteamAsync starts it again at once.
    /// </summary>
    [RelayCommand]
    private void UseSteamGuardCode()
    {
        _preferSteamGuardCode = true;
        _steamSignIn?.Cancel();
        CloseSteamGuard(null);
    }

    private void CloseSteamGuard(string? code)
    {
        var prompt = _steamGuardPrompt;
        _steamGuardPrompt = null;
        IsSteamGuardOpen = false;
        SteamGuardCode = "";
        prompt?.TrySetResult(code);
    }

    private void CloseSteamPrompts()
    {
        IsSteamSignInOpen = false;
        IsSteamGuardOpen = false;
        SteamPasswordInput = "";
        SteamGuardCode = "";
        _steamSignInPrompt?.TrySetResult(null);
        _steamSignInPrompt = null;
        _steamGuardPrompt?.TrySetResult(null);
        _steamGuardPrompt = null;
    }

    // ---- ISteamPrompts -----------------------------------------------------------------------
    // Called from SteamKit's own threads, so every one of these hops to the UI thread before touching
    // a bound property. Explicitly implemented: they are the Steam session's contract, not commands.

    async Task<SteamCredentials?> ISteamPrompts.AskCredentialsAsync(string suggestedAccount, bool previousWasWrong)
    {
        var prompt = new TaskCompletionSource<SteamCredentials?>(TaskCreationOptions.RunContinuationsAsynchronously);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _steamSignInPrompt = prompt;
            if (string.IsNullOrWhiteSpace(SteamAccountInput))
                SteamAccountInput = suggestedAccount;
            SteamPasswordInput = "";
            SteamSignInError = previousWasWrong
                ? "The saved Steam sign-in is no longer valid. Enter your password again."
                : _steamSignInNotice ?? "";
            _steamSignInNotice = null;
            Greeting = "Waiting for your Steam sign-in...";
            IsSteamSignInOpen = true;
        });

        return await prompt.Task;
    }

    async Task<string?> ISteamPrompts.AskGuardCodeAsync(SteamGuardKind kind, string detail, bool previousWasWrong)
    {
        var prompt = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _steamGuardPrompt = prompt;
            IsSteamGuardWaiting = false;
            SteamGuardCode = "";
            SteamGuardError = previousWasWrong ? "That code was not accepted." : "";
            SteamGuardDetail = kind == SteamGuardKind.Email
                ? $"Steam emailed a 5-character code to {detail}."
                : "The 5-character code from your Steam mobile app.";
            Greeting = "Waiting for Steam Guard...";
            IsSteamGuardOpen = true;
        });

        return await prompt.Task;
    }

    async Task<bool> ISteamPrompts.AskWaitForDeviceConfirmationAsync()
    {
        if (_preferSteamGuardCode)
            return false;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsSteamGuardWaiting = true;
            SteamGuardError = "";
            SteamGuardDetail = "Steam sent a confirmation to your Steam mobile app. Approve it there to continue.";
            Greeting = "Waiting for Steam Guard...";
            IsSteamGuardOpen = true;
        });

        // Steam has already pushed the prompt; true just means "poll until it is approved".
        return true;
    }

    void ISteamPrompts.Progress(string message) => Dispatcher.UIThread.Post(() => Greeting = message);

    // ---- Failure messages --------------------------------------------------------------------

    /// <summary>
    /// Turns a failed login into something a player can act on. The Steam cases all look like ordinary
    /// exceptions otherwise, and "SteamLinkNeededException" tells nobody what to do about it.
    /// </summary>
    private static string ExplainLoginFailure(Exception ex) => ex switch
    {
        SteamLinkNeededException =>
            "This Steam account is not linked to a Square Enix account yet. Link it once in the official "
            + "launcher on a PC, then try again here.",

        SteamWrongAccountException wrong =>
            $"This Steam account is linked to the Square Enix ID \"{wrong.ImposedUserName}\", "
            + $"not \"{wrong.ChosenUserName}\".",

        SteamTicketNullException =>
            "Steam did not return a login ticket. Check that this Steam account owns FINAL FANTASY XIV, "
            + "then try again.",

        SteamSignInException steam => steam.Message,

        SteamException steam =>
            $"Could not authenticate with Steam: {steam.Message}",

        OperationCanceledException => "Steam sign-in cancelled.",

        _ => $"{ex.GetType().Name}: {ex.Message}",
    };
}
