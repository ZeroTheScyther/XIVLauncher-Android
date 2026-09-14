using System;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using XIVLauncher.Common.PlatformAbstractions;

namespace XlaBoot.Steam;

/// <summary>
/// XIVLauncher's ISteam, backed by a real Steam connection rather than the Steamworks SDK.
///
/// XIVLauncher.Common only ever reaches five of these members during a login: Initialize, IsValid,
/// BLoggedOn, GetAuthSessionTicketAsync and GetServerRealTime. The rest exist for the desktop
/// launcher's Steam Deck support and have nothing to do on a phone.
///
/// Signing in is interactive and asynchronous, which ISteam.Initialize cannot express, so the caller
/// signs in with <see cref="SignInAsync"/> before handing this to the Launcher. IsValid only reports
/// true once that has happened, which keeps Launcher.Login from calling Initialize at all.
/// </summary>
public sealed class SteamKitSteam : ISteam, IDisposable
{
    private readonly SteamSession _session;
    private uint _appId;

    // Held, deliberately not disposed, for as long as the login runs: TicketInfo.Dispose() cancels the
    // ticket with Steam, and Square Enix checks it server-side while Launcher.Login is still going.
    private SteamAuthTicket.TicketInfo? _ticket;

    public SteamKitSteam(uint appId, uint loginId)
    {
        _appId = appId;
        _session = new SteamSession(loginId);
    }

    /// <summary>The signed-in Steam account name, once there is one.</summary>
    public string? Account => _session.Account;

    /// <summary>Signs in to Steam. Returns the tokens to store so the next sign-in needs no password.</summary>
    public Task<SteamTokens> SignInAsync(SteamTokens? saved, ISteamPrompts prompts, CancellationToken ct = default)
        => _session.SignInAsync(saved, prompts, ct);

    public bool IsValid => _session.IsSignedIn;

    public bool BLoggedOn => _session.IsSignedIn;

    public void Initialize(uint appId) => _appId = appId;

    public async Task<byte[]?> GetAuthSessionTicketAsync()
    {
        _ticket = await _session.GetTicketAsync(_appId).ConfigureAwait(false);
        return _ticket.Ticket;
    }

    public uint GetServerRealTime() => _session.ServerRealTime;

    public void Shutdown() => Dispose();

    public void Dispose()
    {
        _ticket = null;
        _session.Dispose();
    }

    // ---- Not applicable on Android -----------------------------------------------------------
    //
    // The desktop launcher uses these for the Steam Deck's on-screen keyboard and overlay. Android
    // has its own keyboard and there is no overlay here, so they report "not available" rather than
    // throwing: XIVLauncher.Common never calls them during a login, and a throw would be worse if it
    // ever did.

    public bool BOverlayNeedsPresent => false;

    public bool IsAppInstalled(uint appId) => false;

    public string GetAppInstallDir(uint appId) => string.Empty;

    public bool ShowGamepadTextInput(bool password, bool multiline, string description, int maxChars, string existingText = "")
        => false;

    public string GetEnteredGamepadText() => string.Empty;

    public bool ShowFloatingGamepadTextInput(ISteam.EFloatingGamepadTextInputMode mode, int x, int y, int width, int height)
        => false;

    public bool IsRunningOnSteamDeck() => false;

    public void ActivateGameOverlayToWebPage(string url, bool modal = false) { }

#pragma warning disable CS0067 // Raised only by the Steam Deck keyboard, which this never shows.
    public event Action<bool>? OnGamepadTextInputDismissed;
#pragma warning restore CS0067
}
