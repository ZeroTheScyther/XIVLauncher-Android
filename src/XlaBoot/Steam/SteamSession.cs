using System;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.Authentication;

namespace XlaBoot.Steam;

/// <summary>
/// One signed-in connection to Steam, used only to fetch an auth session ticket for Square Enix.
///
/// This speaks the Steam protocol directly (SteamKit2), so no Steam client and no native library is
/// involved and nothing runs inside the Wine prefix. The ticket it returns is issued and signed by
/// Steam itself, which is what the Square Enix login checks; a locally faked one would be rejected.
///
/// The connection is short-lived by design: sign in, take a ticket, and drop it once the Square Enix
/// login is done. Nothing about the game launch needs Steam.
/// </summary>
public sealed class SteamSession : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    // Steam publishes far more servers than are reachable from any given network, and SteamKit tries
    // exactly one per Connect(), reporting a refusal as a plain disconnect. Working down the list is
    // the expected behaviour, not an error path: the first candidate failing is routine.
    private const int ConnectAttempts = 5;
    private static readonly TimeSpan LogOnTimeout = TimeSpan.FromSeconds(45);

    // Steam sends these unprompted, a moment after the logon is accepted. A ticket cannot be built
    // without one, so a sign-in is not finished until at least one has arrived.
    private static readonly TimeSpan ConnectTokenTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// TCP only. Steam's own server list is mostly WebSocket, but .NET for Android cannot open one:
    /// ClientWebSocket fails immediately against every Steam server, on every port including 443, while
    /// the plain TCP transport connects and completes the encryption handshake first try. Measured on
    /// the device, not assumed. Leaving the default (All) makes a sign-in depend on which kind of server
    /// the list happens to offer first.
    /// </summary>
    internal static readonly SteamConfiguration Configuration =
        SteamConfiguration.Create(builder => builder.WithProtocolTypes(ProtocolTypes.Tcp));

    private readonly SteamClient _client = new(Configuration);
    private readonly CallbackManager _manager;
    private readonly SteamUser _user;
    private readonly CancellationTokenSource _stop = new();
    private readonly uint _loginId;

    private readonly TaskCompletionSource<bool> _connectTokens =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _pump;
    private TaskCompletionSource<bool>? _connected;
    private TaskCompletionSource<SteamUser.LoggedOnCallback>? _loggedOn;
    private volatile bool _closing;
    private TimeSpan _serverOffset;

    /// <param name="loginId">
    /// Tells Steam this is a different client from any other logon of the same account on the same
    /// address. Without a distinct value, signing in here kicks the user's desktop Steam client (and
    /// the other way round). Stable per install; see MainViewModel.SteamLoginId.
    /// </param>
    public SteamSession(uint loginId)
    {
        _loginId = loginId;
        _manager = new CallbackManager(_client);
        _user = _client.GetHandler<SteamUser>()
                ?? throw new SteamSignInException("SteamKit did not provide a user handler.");

        _manager.Subscribe<SteamClient.ConnectedCallback>(_ => _connected?.TrySetResult(true));
        _manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _manager.Subscribe<SteamApps.GameConnectTokensCallback>(_ => _connectTokens.TrySetResult(true));
    }

    /// <summary>True once Steam has accepted the logon and sent a game connect token.</summary>
    public bool IsSignedIn { get; private set; }

    /// <summary>The account name Steam reported, which can differ in case from what was typed.</summary>
    public string? Account { get; private set; }

    /// <summary>
    /// Steam's clock, not the phone's. The ticket Square Enix receives is encrypted with a key derived
    /// from this, so a phone whose clock is off by a minute would produce a ticket they cannot read.
    /// </summary>
    public uint ServerRealTime => (uint)DateTimeOffset.UtcNow.Add(_serverOffset).ToUnixTimeSeconds();

    /// <summary>
    /// Signs in, asking the user only for what Steam actually demands. Pass the tokens from a previous
    /// sign-in to skip the password entirely; returns the tokens to store for next time.
    /// </summary>
    public async Task<SteamTokens> SignInAsync(SteamTokens? saved, ISteamPrompts prompts, CancellationToken ct = default)
    {
        StartPump();

        var account = saved?.Account;
        var refreshToken = saved?.RefreshToken;
        var guardData = saved?.GuardData;

        // Two passes at most: if a stored token turns out to be stale, ask for the password once and
        // try again. Any further failure is the user's to fix, not something to retry at.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (string.IsNullOrEmpty(refreshToken) || string.IsNullOrEmpty(account))
            {
                // Asked before connecting, deliberately. Steam closes a connection that has not logged
                // on within a few seconds, and typing a password takes longer than that, so connecting
                // first leaves nothing but a dead socket by the time the user is done.
                var credentials = await prompts.AskCredentialsAsync(account ?? "", previousWasWrong: attempt > 0)
                                      .ConfigureAwait(false)
                                  ?? throw new SteamSignInException("Steam sign-in cancelled.");

                prompts.Progress("Signing in to Steam...");
                var poll = await AuthenticateAsync(credentials, guardData, prompts, ct).ConfigureAwait(false);
                account = poll.AccountName;
                refreshToken = poll.RefreshToken;
                if (!string.IsNullOrEmpty(poll.NewGuardData))
                    guardData = poll.NewGuardData;
            }

            prompts.Progress("Signing in to Steam...");
            await ConnectAsync(ct).ConfigureAwait(false); // a refused logon drops the connection
            var result = await LogOnAsync(account!, refreshToken!, ct).ConfigureAwait(false);

            if (result.Result == EResult.OK)
            {
                prompts.Progress("Waiting for Steam...");
                await Wait(_connectTokens.Task, ConnectTokenTimeout,
                    "Steam accepted the sign-in but sent no game token.", ct).ConfigureAwait(false);

                Account = account;
                IsSignedIn = true;
                return new SteamTokens(account!, refreshToken!, guardData);
            }

            var failure = new SteamSignInException(SteamSignInException.Explain(result.Result), result.Result);
            if (attempt > 0 || !failure.NeedsPassword)
                throw failure;

            refreshToken = null; // stale stored token: fall through and ask for the password
        }

        throw new SteamSignInException("Could not sign in to Steam.");
    }

    /// <summary>
    /// Asks Steam for an auth session ticket for <paramref name="appId"/>. The account must own that
    /// app. Keep the returned ticket alive until Square Enix has checked it: disposing it withdraws
    /// the ticket from Steam's auth list.
    /// </summary>
    public async Task<SteamAuthTicket.TicketInfo> GetTicketAsync(uint appId, CancellationToken ct = default)
    {
        if (!IsSignedIn)
            throw new SteamSignInException("Not signed in to Steam.");

        await Wait(_connectTokens.Task, ConnectTokenTimeout,
            "Steam sent no game token.", ct).ConfigureAwait(false);

        var handler = _client.GetHandler<SteamAuthTicket>()
                      ?? throw new SteamSignInException("SteamKit did not provide a ticket handler.");

        try
        {
            return await handler.GetAuthSessionTicket(appId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not SteamSignInException)
        {
            // The usual cause is the account not owning the app, which SteamKit reports as a plain
            // exception carrying the EResult in its text.
            throw new SteamSignInException(
                $"Steam would not issue a ticket for app {appId}. Check that this Steam account owns the game. ({ex.Message})",
                EResult.Invalid, ex);
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        if (_client.IsConnected)
            return;

        Exception? last = null;

        for (var attempt = 0; attempt < ConnectAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _connected = waiter;

            try
            {
                // Each call takes the next candidate; SteamKit remembers which ones let it down.
                _client.Connect();
                await Wait(waiter.Task, ConnectTimeout, "Could not reach Steam.", ct).ConfigureAwait(false);
                return;
            }
            catch (SteamSignInException ex)
            {
                last = ex;
                Console.WriteLine($"XlaSteam: Steam server {attempt + 1}/{ConnectAttempts} did not answer; trying another.");
            }
        }

        throw new SteamSignInException(
            "Could not reach Steam. Check the connection and try again.", EResult.Invalid, last);
    }

    private async Task<SteamUser.LoggedOnCallback> LogOnAsync(string account, string refreshToken, CancellationToken ct)
    {
        var waiter = new TaskCompletionSource<SteamUser.LoggedOnCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
        _loggedOn = waiter;

        _user.LogOn(new SteamUser.LogOnDetails
        {
            Username = account,
            AccessToken = refreshToken,
            ShouldRememberPassword = true,
            LoginID = _loginId,
            MachineName = "XIVLauncher Android",
        });

        return await Wait(waiter.Task, LogOnTimeout, "Steam did not answer the sign-in.", ct).ConfigureAwait(false);
    }

    private async Task<AuthPollResult> AuthenticateAsync(
        SteamCredentials credentials, string? guardData, ISteamPrompts prompts, CancellationToken ct)
    {
        // Steam hangs up on connections that have not logged on, and the Steam Guard step can easily
        // outlast one. SteamKit reports that as InvalidOperationException("must be connected"), so
        // reconnect and ask again rather than making the user retype the password.
        for (var tries = 0; ; tries++)
        {
            await ConnectAsync(ct).ConfigureAwait(false);

            try
            {
                var session = await _client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                {
                    Username = credentials.Account,
                    Password = credentials.Password,
                    IsPersistentSession = true,
                    GuardData = guardData,
                    DeviceFriendlyName = "XIVLauncher Android",
                    Authenticator = new PromptAuthenticator(prompts),
                }).ConfigureAwait(false);

                return await session.PollingWaitForResultAsync(ct).ConfigureAwait(false);
            }
            catch (AuthenticationException ex)
            {
                throw new SteamSignInException(SteamSignInException.Explain(ex.Result), ex.Result, ex);
            }
            catch (InvalidOperationException ex) when (tries == 0)
            {
                Console.WriteLine($"XlaSteam: Steam dropped the connection during sign-in; reconnecting. ({ex.Message})");
            }
        }
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result == EResult.OK)
            // ServerTime is built from a unix timestamp, so compare in UTC whatever kind it carries.
            _serverOffset = DateTime.SpecifyKind(callback.ServerTime, DateTimeKind.Utc) - DateTime.UtcNow;

        _loggedOn?.TrySetResult(callback);
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        if (_closing)
            return;

        IsSignedIn = false;
        var dropped = new SteamSignInException("Steam closed the connection.");
        _connected?.TrySetException(dropped);
        _loggedOn?.TrySetException(dropped);
    }

    private void StartPump()
    {
        if (_pump != null)
            return;

        _pump = Task.Factory.StartNew(() =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    _manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(250));
                }
                catch (Exception ex)
                {
                    if (_closing)
                        return;
                    Console.WriteLine($"XlaSteam: callback pump: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }, TaskCreationOptions.LongRunning);
    }

    private static async Task<T> Wait<T>(Task<T> task, TimeSpan timeout, string timeoutMessage, CancellationToken ct)
    {
        try
        {
            return await task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new SteamSignInException(timeoutMessage);
        }
    }

    public void Dispose()
    {
        if (_closing)
            return;

        _closing = true;
        IsSignedIn = false;

        try { _client.Disconnect(); } catch { /* already gone */ }
        _stop.Cancel();

        // The pump only ever waits 250ms at a time, so this is a formality rather than a real join.
        try { _pump?.Wait(TimeSpan.FromSeconds(2)); } catch { /* nothing to salvage */ }

        _stop.Dispose();
    }

    /// <summary>Turns Steam Guard into the launcher's overlays. Called from SteamKit's polling thread.</summary>
    private sealed class PromptAuthenticator : IAuthenticator
    {
        private readonly ISteamPrompts _prompts;

        public PromptAuthenticator(ISteamPrompts prompts) => _prompts = prompts;

        public async Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
            => await _prompts.AskGuardCodeAsync(SteamGuardKind.Mobile, "", previousCodeWasIncorrect).ConfigureAwait(false)
               ?? throw new SteamSignInException("Steam sign-in cancelled.");

        public async Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
            => await _prompts.AskGuardCodeAsync(SteamGuardKind.Email, email, previousCodeWasIncorrect).ConfigureAwait(false)
               ?? throw new SteamSignInException("Steam sign-in cancelled.");

        public Task<bool> AcceptDeviceConfirmationAsync() => _prompts.AskWaitForDeviceConfirmationAsync();
    }
}
