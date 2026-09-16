using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XIVLauncher.Common;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Patch.PatchList;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.PlatformAbstractions; // CommonUniqueIdCache lives here despite its folder

using XlaBoot.Install;

namespace XlaBoot.ViewModels;

/// <summary>
/// What "Remember login" keeps between launches. The one-time password itself is never stored, and
/// neither is the Steam password: Steam hands back a refresh token that stands in for it.
/// </summary>
/// <param name="SteamRefreshToken">A full Steam account credential. Stored encrypted, like the password.</param>
/// <param name="SteamGuardData">Steam Guard machine token, so a later sign-in can skip the code.</param>
public sealed record SavedLogin(
    string User,
    string Password,
    bool UseOtp,
    bool IsSteam = false,
    string? SteamAccount = null,
    string? SteamRefreshToken = null,
    string? SteamGuardData = null);

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _greeting = "";

    // Status lines also go to logcat (tag DOTNET), so adb-driven test launches can see why a login
    // stopped. They never contain the password: they are fixed texts or exception type + message.
    partial void OnGreetingChanged(string value) => Console.WriteLine($"XlaLauncher: {value}");

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _useOtp;

    [ObservableProperty]
    private bool _rememberLogin = true;

    /// <summary>The Settings page (bottom navigation).</summary>
    public SettingsViewModel Settings { get; } = new();

    /// <summary>Set when a newer build is published; shows the update link on the login page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateAvailable))]
    private AppRelease? _availableUpdate;

    public bool IsUpdateAvailable => AvailableUpdate != null;

    public string UpdateText => AvailableUpdate is { } u
        ? $"XIVLauncher {u.VersionName} is available. Tap to download."
        : "";

    partial void OnAvailableUpdateChanged(AppRelease? value) => OnPropertyChanged(nameof(UpdateText));

    private static readonly System.Net.Http.HttpClient UpdateHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private bool _updateChecked;

    /// <summary>Once per launch, in the background, after setup so the first-run download is not competing.</summary>
    private void CheckForUpdateOnce()
    {
        if (_updateChecked || AppHost.VersionCode <= 0)
            return;
        _updateChecked = true;
        _ = Task.Run(async () =>
        {
            var release = await AppUpdate.CheckAsync(AppHost.VersionCode, UpdateHttp, default);
            if (release != null)
                Dispatcher.UIThread.Post(() => AvailableUpdate = release);
        });
    }

    [RelayCommand]
    private void OpenUpdate()
    {
        if (AvailableUpdate is { } release)
            AppHost.OpenUrl?.Invoke(release.Url);
    }

    /// <summary>First-run runtime download. Covers the login page until the runtime is ready.</summary>
    public SetupViewModel Setup { get; } = new();

    /// <summary>Game install and update progress. Covers the login page while patches download and apply.</summary>
    public InstallViewModel Install { get; } = new();

    [RelayCommand]
    private void OpenSettings()
    {
        if (Page == LauncherPage.Settings)
            Settings.Open();
        else
            Page = LauncherPage.Settings;
    }

    // The checkboxes are Buttons drawn as checkboxes (see MainView.axaml), so they toggle through commands.
    [RelayCommand]
    private void ToggleUseOtp() => UseOtp = !UseOtp;

    [RelayCommand]
    private void ToggleRememberLogin() => RememberLogin = !RememberLogin;

    /// <summary>The eye button in the password field: shows the password on screen while held on. Never logged.</summary>
    [ObservableProperty]
    private bool _showPassword;

    [RelayCommand]
    private void ToggleShowPassword() => ShowPassword = !ShowPassword;

    /// <summary>
    /// Set by MainActivity from the "xla_autorun" intent extra (debug): run files/wine-cmd once the UI
    /// is up, so adb-driven tests need neither uiautomator nor a tap.
    /// </summary>
    public static bool AutoRunWineTest;

    /// <summary>
    /// Set by MainActivity from the "xla_autologin" intent extra (debug builds): press Log in &amp; Play
    /// once with the saved login, so benchmark launches use the real account path without a tap.
    /// </summary>
    public static bool AutoLogin;

    /// <summary>
    /// Settings > General > "Run in safe mode": the next Dalamud launch loads Dalamud but no plugins, so a plugin
    /// that crashes the game can be turned off from inside the game (/xlplugins). Deliberately not saved: it
    /// applies to one launch and clears itself, the way XIVLauncher's "Launch without any plugins" does.
    /// </summary>
    public static bool SafeModeNextLaunch;

    /// <summary>Set by MainActivity: returns the saved login or null.</summary>
    public static Func<SavedLogin?>? LoadCredentials;

    /// <summary>Set by MainActivity: stores the login; null forgets it.</summary>
    public static Action<SavedLogin?>? SaveCredentials;

    private bool _pendingAutoLogin;

    public MainViewModel()
    {
        // Android records why a process died, which is the only account of a kill the app could not log itself
        // (out of memory with the game running). Written to the launcher log, where the Logs page shows it.
        ProcessExits.Report();
        AppLog.Note($"Launcher started (version {AppHost.VersionName}, {AppHost.DeviceDescription.Replace("\n", "; ")})");

        IsFreeTrial = AppHost.Get("free_trial", "OFF") == "ON";

        try
        {
            if (LoadCredentials?.Invoke() is { } saved)
            {
                Username = saved.User;
                Password = saved.Password;
                UseOtp = saved.UseOtp;
                IsSteamAccount = saved.IsSteam;
                if (saved.SteamAccount != null && saved.SteamRefreshToken != null)
                    _steamTokens = new Steam.SteamTokens(saved.SteamAccount, saved.SteamRefreshToken, saved.SteamGuardData);
                OnPropertyChanged(nameof(SteamStatus));
            }
        }
        catch (Exception ex)
        {
            Greeting = $"Could not load the saved login: {ex.GetType().Name}";
        }

        // Installs the Wine runtime if it is missing, then gets out of the way. On every later launch
        // this closes immediately and the user sees the login form directly.
        Setup.Finished += () =>
        {
            RefreshHome();
            CheckForUpdateOnce();
            if (_pendingAutoLogin)
            {
                _pendingAutoLogin = false;
                Dispatcher.UIThread.Post(() => LoginAndPlayCommand.Execute(null), DispatcherPriority.Background);
            }
        };
        Dispatcher.UIThread.Post(Setup.Begin, DispatcherPriority.Background);

        if (AutoLogin)
        {
            AutoLogin = false; // once per process
            if (UseOtp || string.IsNullOrEmpty(Username) || string.IsNullOrEmpty(Password))
                Greeting = "Auto login needs a saved login without a one-time password.";
            else
                // Deferred until setup reports Finished, which fires immediately when the runtime is
                // already installed - so a benchmark launch still needs no tap.
                _pendingAutoLogin = true;
        }

        if (AutoRunWineTest)
        {
            AutoRunWineTest = false; // once per process, not again on activity recreation
            Dispatcher.UIThread.Post(RunWineTest, DispatcherPriority.Background);
        }
    }

    // Both live on AppHost, which the settings screen and the driver store also read; these stay as
    // the names MainActivity and the rest of this file already use.
    public static string NativeLibDir
    {
        get => AppHost.NativeLibDir;
        set => AppHost.NativeLibDir = value;
    }

    public static string FilesDir
    {
        get => AppHost.FilesDir;
        set => AppHost.FilesDir = value;
    }

    /// <summary>Set by MainActivity: (width, height) -> X11 socket path (or an "ERROR ..." string).</summary>
    public static Func<int, int, string>? ShowXServer;

    public static Func<string>? ExternalProbe;

    private const int DefaultScreenWidth = 1280;
    private const int DefaultScreenHeight = 720;

    /// <summary>
    /// X screen / Wine desktop size picked in the settings screen: xla.XlaSettings writes XLA_RESOLUTION=WxH
    /// into files/xla-settings.sh. The game fills that desktop in fullscreen or borderless mode.
    /// </summary>
    private static (int Width, int Height) GameResolution()
    {
        try
        {
            const string key = "XLA_RESOLUTION=";
            foreach (var line in File.ReadLines(FilesDir + "/xla-settings.sh"))
            {
                if (!line.StartsWith(key, StringComparison.Ordinal))
                    continue;
                var size = line[key.Length..].Split('x');
                if (size.Length == 2 && int.TryParse(size[0], out var w) && int.TryParse(size[1], out var h)
                    && w >= 640 && h >= 360)
                    return (w, h);
            }
        }
        catch (IOException)
        {
            // No settings file before the first launch: use the default.
        }
        return (DefaultScreenWidth, DefaultScreenHeight);
    }

    /// <summary>The FFXIV install, as picked in the settings screen (Android's folder picker).</summary>
    public static string GamePath => GameLocation.Current;

    // Same fallback XIVLauncher.Core uses when its launcher config cannot be fetched.
    private const string FrontierUrl = "https://launcher.finalfantasyxiv.com/v740/index.html?rc_lang={0}&time={1}";

    [RelayCommand]
    private async Task LoginAndPlay()
    {
        if (IsBusy)
            return;

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password))
        {
            Greeting = "Enter your username and password.";
            return;
        }

        if (!Provisioning.RuntimeInstaller.IsProvisioned(AppHost.FilesDir))
        {
            // Otherwise this fails much later, inside Wine, with nothing to point at.
            Greeting = "The runtime is not installed yet.";
            Setup.Begin();
            return;
        }

        // The game lives on shared storage, so without this permission the launch fails deep inside
        // Wine. One ordinary dialog, and the grant applies to this running process.
        if (!GameLocation.HasStorageAccess())
        {
            Greeting = "Waiting for storage permission...";
            var granted = AppHost.RequestStoragePermission != null && await AppHost.RequestStoragePermission();
            if (!granted)
            {
                Greeting = "XIVLauncher needs access to your storage to reach the game folder.";
                return;
            }
        }

        // Only now is a "no game" answer trustworthy: an unreadable folder reads as missing, and
        // offering to download the whole game over an install the user already has is the worst
        // outcome available.
        var game = GameLocation.Check(GamePath);
        // An install that was interrupted carries on without asking again: the user already said yes.
        var resuming = game.Status == GameStatus.Missing && GameInstaller.HasPartialInstall(GamePath);
        var installing = !game.IsPresent && (resuming || _installConfirmed);
        _installConfirmed = false;
        if (!game.IsPresent && !installing)
        {
            NoGameDetail = game.Status == GameStatus.Unreadable
                ? game.Message
                : $"Nothing is installed at {GamePath}.";
            IsNoGamePromptOpen = true;
            Greeting = "No game installation found.";
            return;
        }

        IsBusy = true;
        Steam.SteamKitSteam? steam = null;

        try
        {
            var gamePath = new DirectoryInfo(GamePath);
            // Steam service accounts have to present a ticket issued by Steam itself. Constructed here but
            // not signed in yet: signing in is interactive, so it waits until just before the login, after
            // any boot patching. Disposed in the finally below.
            steam = IsSteamAccount ? new Steam.SteamKitSteam(SteamAppId, SteamLoginId) : null;
            // No cache file: the unique-id cache lives in memory only for this spike.
            var launcher = new Launcher((ISteam?)steam, new CommonUniqueIdCache(null), FrontierUrl, "en-us");
            var installTitle = installing ? "Installing FFXIV" : "Updating FFXIV";

            // Boot comes first and needs no login. Login cannot even be attempted without it: building the
            // version report reads the boot executables, so on a fresh install they must exist beforehand.
            // Checked on every launch, as upstream does, so a new boot version never strands the user.
            Greeting = "Checking for updates...";
            PatchListEntry[] bootPatches;
            try
            {
                bootPatches = await launcher.CheckBootVersion(gamePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"XlaLauncher: boot version check failed: {ex}");
                Greeting = "Could not check for game updates. The servers may be under maintenance, "
                           + "or your connection is down. Try again in a moment.";
                return;
            }
            if (bootPatches.Length > 0)
            {
                Greeting = installing ? "Installing the game..." : "Updating the game...";
                if (!await Install.RunAsync(installTitle, GamePath, bootPatches))
                {
                    Greeting = Install.HasError ? "The install could not finish." : "Install paused.";
                    return;
                }
            }

            var otp = "";
            if (UseOtp)
            {
                Greeting = "Waiting for the one-time password...";
                var code = await PromptForOtp();
                if (code == null)
                {
                    Greeting = "Login cancelled.";
                    return;
                }
                otp = code;
            }

            if (steam != null)
            {
                Greeting = "Signing in to Steam...";
                var signedIn = await SignInToSteamAsync(steam);
                if (!ReferenceEquals(signedIn, steam))
                {
                    // The sign-in started over, so the Launcher is holding a dead Steam connection.
                    steam = signedIn;
                    launcher = new Launcher(steam, new CommonUniqueIdCache(null), FrontierUrl, "en-us");
                }
            }

            Greeting = "Logging in...";

            var result = await Task.Run(() => launcher.Login(
                Username.Trim(), Password, otp,
                isSteam: IsSteamAccount, useCache: false, gamePath,
                forceBaseVersion: false, isFreeTrial: IsFreeTrial, ClientLanguage.English));

            // Login() returned without throwing, so Square Enix accepted these credentials.
            try
            {
                SaveCredentials?.Invoke(RememberLogin
                    ? new SavedLogin(Username.Trim(), Password, UseOtp, IsSteamAccount,
                        _steamTokens?.Account, _steamTokens?.RefreshToken, _steamTokens?.GuardData)
                    : null);
            }
            catch (Exception ex)
            {
                Greeting = $"Logged in, but saving the login failed: {ex.GetType().Name}";
            }

            switch (result.State)
            {
                case Launcher.LoginState.Ok:
                    break;
                case Launcher.LoginState.NeedsPatchGame:
                    // The server lists only the repositories this account owns, so an account without
                    // every expansion gets a correspondingly smaller install.
                    Greeting = installing ? "Installing the game..." : "Updating the game...";
                    if (!await Install.RunAsync(installTitle, GamePath, result.PendingPatches))
                    {
                        Greeting = Install.HasError
                            ? "The install could not finish."
                            : "Install paused. Tap Log in & Play to carry on where it stopped.";
                        return;
                    }
                    // Upstream goes straight on into the game with the same session - unless the user has
                    // since left the app, where starting the game behind their back helps nobody.
                    if (!AppHost.IsInForeground)
                    {
                        Greeting = installing
                            ? "FFXIV is installed. Tap Log in & Play to start."
                            : "FFXIV is up to date. Tap Log in & Play to start.";
                        return;
                    }
                    break;
                case Launcher.LoginState.NeedsPatchBoot:
                    // Boot was patched moments ago, so the server rejecting it means the files were
                    // changed by something else. Upstream's answer too: there is no safe automatic repair.
                    Greeting = "The game's boot files are damaged, so it can neither update nor start. "
                               + "Delete the game folder and install it again.";
                    return;
                case Launcher.LoginState.NoService:
                    Greeting = "This account has no active subscription (NoService).";
                    return;
                case Launcher.LoginState.NoTerms:
                    Greeting = "The terms of service have not been accepted on this account (NoTerms).";
                    return;
                default:
                    Greeting = $"Login failed: {result.State}";
                    return;
            }

            if (!RememberLogin)
                Password = "";

            // Settings > Plugins > Dalamud. Checked here rather than cached: the toggle can change
            // between logins without restarting the app.
            var dalamudEnabled = AppHost.Get("dalamud_enabled", "OFF") == "ON";
            if (dalamudEnabled)
            {
                Greeting = "Preparing Dalamud...";
                await Provisioning.DalamudPrefixFix.EnsureAsync(FilesDir, DalamudHttp);
                await EnsureHelperPluginAsync();
            }

            // Safe mode is for one launch only: consumed here, so the next Log in & Play loads plugins again.
            var safeMode = dalamudEnabled && SafeModeNextLaunch;
            SafeModeNextLaunch = false;
            Greeting = safeMode ? "Login OK, starting the game in safe mode (no plugins)..." : "Login OK, starting the game...";
            var screen = GameResolution();
            AppLog.Note($"Starting the game at {screen.Width}x{screen.Height}"
                        + $", Dalamud {(dalamudEnabled ? safeMode ? "on (safe mode)" : "on" : "off")}"
                        + $", driver {GraphicsDrivers.Selected(GraphicsDrivers.All()).Label}");

            // Always Full Screen (GameSettingsPreset.ForceFullScreen). FFXIV.cfg only exists after the game's first
            // run; until then the game picks its own mode. Never worth failing a launch over.
            try
            {
                GameSettingsPreset.ForceFullScreen(GameSettingsPreset.ConfigPath(FilesDir), screen.Width, screen.Height);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"XlaLauncher: could not set Full Screen: {ex.GetType().Name}");
            }

            IGameRunner runner = dalamudEnabled ? new DalamudGameRunner(safeMode) : new WineCmdGameRunner();
            // HoldForUpdate (inside DalamudGameRunner) spins waiting for Dalamud's download, so this
            // goes through Task.Run like the login call above rather than blocking the UI thread.
            await Task.Run(() => launcher.LaunchGame(runner,
                result.UniqueId!,
                result.OauthLogin!.Region,
                result.OauthLogin.MaxExpansion,
                // Required for a Steam service account: IsSteam=1 is the only way the game can declare
                // the Steam platform, and the lobby refuses an account whose entitlement does not match
                // ("not yet been registered on this platform"). IS_FFXIV_LAUNCH_FROM_STEAM rides along
                // but ffxiv_dx11.exe never reads it; only ffxivboot does.
                isSteamServiceAccount: IsSteamAccount,
                // XIVLauncher.Common knows about IsSteam but not IsFreeTrial, and the game takes both.
                // Without it a trial account reaches the title screen and is then turned away at Start
                // with "not yet been registered on this platform or your subscription has expired":
                // the lobby has no entitlement to match. Added through additionalArguments, which
                // LaunchGame parses into the same argument list.
                additionalArguments: IsFreeTrial ? "IsFreeTrial=1" : "",
                gamePath,
                ClientLanguage.English,
                // Plain arguments: the encrypted form keys off GetTickCount, which is not guaranteed to
                // match between this process and the guest. Plain DEV.* args are proven to work here.
                encryptArguments: false,
                DpiAwareness.Unaware));
        }
        catch (Exception ex)
        {
            // The message never contains the password; the exception type tells OAuth errors
            // (wrong password / OTP) apart from network or file problems. Steam has its own set,
            // which say nothing useful to a player as they come.
            Greeting = ExplainLoginFailure(ex);
            AppLog.Note($"Login failed: {ex.GetType().Name}: {ex.Message}");
            // Wrappers like "Updater returned no integrity." say nothing without the cause they carry.
            if (ex.InnerException is { } inner)
                AppLog.Note($"  caused by {inner.GetType().Name}: {inner.Message}");
        }
        finally
        {
            CloseSteamPrompts();
            // Square Enix checked the ticket during Login(), and the game never talks to Steam, so
            // there is nothing left for the connection to do.
            steam?.Dispose();
            IsBusy = false;
        }
    }

    // ---- "No game installation found" prompt --------------------------------------------------

    [ObservableProperty]
    private bool _isNoGamePromptOpen;

    /// <summary>Why we are asking: which folder was checked, or that it could not be read.</summary>
    [ObservableProperty]
    private string _noGameDetail = "";

    /// <summary>
    /// "Yes, install it" - the app downloads the game into the folder it checked. Login is part of that:
    /// Square Enix only hands out the game patch list to a logged-in account.
    /// </summary>
    [RelayCommand]
    private void InstallGame()
    {
        IsNoGamePromptOpen = false;
        // Into the folder that was just checked and found empty, recorded so it survives a restart.
        GameLocation.Use(GamePath);
        _installConfirmed = true;
        LoginAndPlayCommand.Execute(null);
    }

    /// <summary>Set by "Yes, install it" for the Log in &amp; Play run it triggers; consumed there.</summary>
    private bool _installConfirmed;

    /// <summary>"No, I already have it" - straight to the folder picker in settings.</summary>
    [RelayCommand]
    private void ChooseGameFolder()
    {
        IsNoGamePromptOpen = false;
        Settings.TabIndex = 0;
        OpenSettings();
        Settings.Status = "Tap Game install location and pick the folder that holds the game's boot and game folders.";
    }

    /// <summary>
    /// Settings > Plugins > "In-game helper": installs (or removes) the plugin that shows the device's battery in
    /// game. It ships from its own repository, so this is a small download rather than part of the app.
    /// </summary>
    private async Task EnsureHelperPluginAsync()
    {
        if (AppHost.Get("helper_plugin", "ON") != "ON")
        {
            try { Provisioning.HelperPlugin.Remove(FilesDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return;
        }
        try
        {
            AppLog.Note(await Provisioning.HelperPlugin.EnsureAsync(FilesDir, DalamudHttp, default));
        }
        catch (Exception ex)
        {
            // Never worth failing a launch over: the game runs fine without it.
            AppLog.Note($"Could not install the in-game helper plugin: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---- One-time password prompt ------------------------------------------------------------

    [ObservableProperty]
    private bool _isOtpPromptOpen;

    [ObservableProperty]
    private string _otp = "";

    [ObservableProperty]
    private string _otpError = "";

    private TaskCompletionSource<string?>? _otpPrompt;

    /// <summary>Opens the OTP overlay; completes with the 6-digit code, or null if cancelled.</summary>
    private Task<string?> PromptForOtp()
    {
        Otp = "";
        OtpError = "";
        _otpPrompt = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsOtpPromptOpen = true;
        return _otpPrompt.Task;
    }

    [RelayCommand]
    private void SubmitOtp()
    {
        if (_otpPrompt == null)
            return;

        var code = Otp.Trim();
        if (!IsOtpCode(code))
        {
            OtpError = "Enter the 6-digit code.";
            return;
        }
        CloseOtpPrompt(code);
    }

    [RelayCommand]
    private void CancelOtp() => CloseOtpPrompt(null);

    private void CloseOtpPrompt(string? code)
    {
        var prompt = _otpPrompt;
        _otpPrompt = null;
        IsOtpPromptOpen = false;
        Otp = "";
        prompt?.TrySetResult(code);
    }

    // Like XIVLauncher's OTP dialog, the sixth digit submits on its own. Posted so the TextBox
    // finishes its binding update before the field is cleared.
    partial void OnOtpChanged(string value)
    {
        if (IsOtpPromptOpen && IsOtpCode(value.Trim()))
            Dispatcher.UIThread.Post(SubmitOtp);
    }

    private static bool IsOtpCode(string code) => code.Length == 6 && code.All(char.IsAsciiDigit);

    /// <summary>
    /// IGameRunner that hands the launch to the existing X server + run-wine.sh path: the game command
    /// goes into files/wine-cmd, which run-wine.sh sources.
    /// </summary>
    private sealed class WineCmdGameRunner : IGameRunner
    {
        public Process? Start(string path, string workingDirectory, string arguments,
            IDictionary<string, string> environment, DpiAwareness dpiAwareness)
        {
            if (path.Contains('\'') || arguments.Contains('\''))
                throw new InvalidOperationException("Launch command contains a quote character; refusing to write it to wine-cmd.");

            LaunchInWine($"\"{path}\" {arguments.Trim()}", environment);
            return null;
        }
    }

    /// <summary>
    /// IGameRunner that routes the launch through Dalamud.Injector instead of straight at the game exe,
    /// mirroring how UnixGameRunner picks between DalamudLauncher and a plain launch upstream. Falls back
    /// to <see cref="WineCmdGameRunner"/> when Dalamud has not caught up to the installed game version yet
    /// (the same "goes quiet after a patch" state GUIDE.md documents for the GameNative recipe) so a stale
    /// Dalamud build never strands the player.
    /// </summary>
    private sealed class DalamudGameRunner : IGameRunner
    {
        private readonly bool noPlugins;

        public DalamudGameRunner(bool noPlugins) => this.noPlugins = noPlugins;

        public Process? Start(string path, string workingDirectory, string arguments,
            IDictionary<string, string> environment, DpiAwareness dpiAwareness)
        {
            var gamePath = new DirectoryInfo(GamePath);
            var xlroaming = Path.Combine(FilesDir, "xlroaming");
            Directory.CreateDirectory(xlroaming);
            var runtimeDir = new DirectoryInfo(Path.Combine(xlroaming, "runtime"));

            var updater = new DalamudUpdater(
                DalamudHttp,
                new DirectoryInfo(Path.Combine(xlroaming, "addon")),
                runtimeDir,
                new DirectoryInfo(Path.Combine(xlroaming, "dalamudAssets")),
                cache: null,
                dalamudRolloutBucket: null)
            {
                Overlay = new NoOpDalamudLoadingOverlay(),
            };
            updater.Run(betaKind: null, betaKey: null);

            var launcher = new DalamudLauncher(
                new AndroidDalamudRunner(runtimeDir),
                updater,
                DalamudLoadMethod.EntryPoint,
                gamePath,
                configDirectory: new DirectoryInfo(xlroaming),
                logPath: new DirectoryInfo(Path.Combine(xlroaming, "logs")),
                ClientLanguage.English,
                injectionDelay: 0,
                fakeLogin: false,
                noPlugin: noPlugins,
                noThirdPlugin: false,
                troubleshootingData: "{}");

            // Blocks (busy-waits) until updater.Run's download finishes - fine here since the caller
            // already moved this whole Start() onto a background thread via Task.Run.
            if (launcher.HoldForUpdate(gamePath) != DalamudLauncher.DalamudInstallState.Ok)
            {
                Console.WriteLine("XlaLauncher: Dalamud does not support this game version yet, launching without it.");
                return new WineCmdGameRunner().Start(path, workingDirectory, arguments, environment, dpiAwareness);
            }

            return launcher.Run(new FileInfo(path), arguments, environment);
        }
    }

    /// <summary>
    /// IDalamudRunner for this port: instead of spawning wine directly (as CompatibilityTools.RunInPrefix
    /// does on desktop) and parsing its stdout for the game's PID, it builds the same Dalamud.Injector
    /// command line and hands it to the existing wine-cmd + run-wine.sh pipeline - the same fire-and-forget
    /// path WineCmdGameRunner uses, which is why this always returns null rather than a live Process.
    /// </summary>
    private sealed class AndroidDalamudRunner : IDalamudRunner
    {
        private readonly DirectoryInfo dotnetRuntime;

        public AndroidDalamudRunner(DirectoryInfo dotnetRuntime) => this.dotnetRuntime = dotnetRuntime;

        public Process? Run(FileInfo runner, bool fakeLogin, bool noPlugins, bool noThirdPlugins, FileInfo gameExe,
            string gameArgs, IDictionary<string, string> environment, DalamudLoadMethod loadMethod,
            DalamudStartInfo startInfo)
        {
            var injectorPath = ToWinePath(runner.FullName);
            var gameExePath = ToWinePath(gameExe.FullName);
            var dotnetRuntimePath = ToWinePath(dotnetRuntime.FullName);
            startInfo.LoggingPath = ToWinePath(startInfo.LoggingPath);
            startInfo.WorkingDirectory = ToWinePath(startInfo.WorkingDirectory);
            startInfo.ConfigurationPath = ToWinePath(startInfo.ConfigurationPath);
            startInfo.PluginDirectory = ToWinePath(startInfo.PluginDirectory);
            startInfo.AssetDirectory = ToWinePath(startInfo.AssetDirectory);

            environment["DALAMUD_RUNTIME"] = dotnetRuntimePath;
            environment["DOTNET_ROOT"] = dotnetRuntimePath;
            // .NET's W^X writes JIT code and backpatched stubs through a second RW mapping of the same pages.
            // FEX only invalidates translated code on writes it sees to the executable mapping, so it keeps
            // running stale translations: Dalamud died ~40 s in after "impossible" casts. XL.Core
            // and the working GameNative container both set this for Wine.
            environment["DOTNET_EnableWriteXorExecute"] = "0";

            var args = new List<string>
            {
                DalamudInjectorArgs.LAUNCH,
                DalamudInjectorArgs.Mode(loadMethod == DalamudLoadMethod.EntryPoint ? "entrypoint" : "inject"),
                DalamudInjectorArgs.Game(gameExePath),
                DalamudInjectorArgs.WorkingDirectory(startInfo.WorkingDirectory),
                DalamudInjectorArgs.ConfigurationPath(startInfo.ConfigurationPath),
                DalamudInjectorArgs.LoggingPath(startInfo.LoggingPath),
                DalamudInjectorArgs.PluginDirectory(startInfo.PluginDirectory),
                DalamudInjectorArgs.AssetDirectory(startInfo.AssetDirectory),
                DalamudInjectorArgs.ClientLanguage((int)startInfo.Language),
                DalamudInjectorArgs.DelayInitialize(startInfo.DelayInitializeMs),
                DalamudInjectorArgs.TsPackB64(Convert.ToBase64String(Encoding.UTF8.GetBytes(startInfo.TroubleshootingPackData))),
            };

            if (loadMethod == DalamudLoadMethod.ACLonly)
                args.Add(DalamudInjectorArgs.WITHOUT_DALAMUD);
            if (fakeLogin)
                args.Add(DalamudInjectorArgs.FAKE_ARGUMENTS);
            if (noPlugins)
                args.Add(DalamudInjectorArgs.NO_PLUGIN);
            if (noThirdPlugins)
                args.Add(DalamudInjectorArgs.NO_THIRD_PARTY);

            args.Add("--");
            args.Add(gameArgs);

            LaunchInWine($"\"{injectorPath}\" {string.Join(' ', args)}", environment);
            return null;
        }
    }

    /// <summary>No UI for the download overlay yet; the steps just go to logcat like every other status line.</summary>
    private sealed class NoOpDalamudLoadingOverlay : IDalamudLoadingOverlay
    {
        public void SetStep(IDalamudLoadingOverlay.DalamudUpdateStep step) =>
            Console.WriteLine($"XlaLauncher: Dalamud update step: {step}");

        public void SetVisible() { }
        public void SetInvisible() { }
        public void ReportProgress(long? size, long downloaded, double? progress) { }
    }

    /// <summary>HttpClient for Dalamud's own updater and the one-time ICU prefix fix; both are infrequent.</summary>
    private static readonly HttpClient DalamudHttp = new();

    /// <summary>
    /// Puts an already-mapped-drive path (see <see cref="PointDriveAtGame"/> and PrefixSetup.MapDrives) into
    /// the form Wine expects on its own command line: the most specific dosdevices entry wins, matching how
    /// Wine itself resolves a Unix path (the same rule <see cref="PointDriveAtGame"/>'s doc comment notes).
    /// </summary>
    private static string ToWinePath(string path)
    {
        var full = Path.GetFullPath(path);
        var driveC = Path.Combine(FilesDir, "prefix", ".wine", "drive_c");
        var roots = new (string Root, string Drive)[]
        {
            (GamePath, "A:"),
            (driveC, "C:"),
            ("/", "Z:"),
        };
        var (root, drive) = roots
            .Where(r => !string.IsNullOrEmpty(r.Root) && full.StartsWith(r.Root, StringComparison.Ordinal))
            .OrderByDescending(r => r.Root.Length)
            .First();
        var relative = full[root.Length..].TrimStart('/').Replace('/', '\\');
        return relative.Length == 0 ? $"{drive}\\" : $"{drive}\\{relative}";
    }

    /// <summary>
    /// Shared tail end of both game runners: wraps the launch command in winhandler.exe for the mouse
    /// camera bridge, writes files/wine-cmd, and starts the X server + run-wine.sh.
    /// </summary>
    private static void LaunchInWine(string launch, IDictionary<string, string> environment)
    {
        if (launch.Contains('\''))
            throw new InvalidOperationException("Launch command contains a quote character; refusing to write it to wine-cmd.");

        var (width, height) = GameResolution();
        PointDriveAtGame();

        // The game starts under winhandler.exe, the helper that turns the captured mouse into injected
        // Windows input (see com.winlator.winhandler.WinHandler for why X11 motion cannot move the camera).
        // It re-quotes every argument it passes on, so it launches start.exe rather than the game itself:
        // start.exe strips those quotes again and hands FFXIV its arguments exactly as before. Without the
        // helper the game still starts the old way, it just has no mouse camera.
        if (InstallWinHandler())
            launch = $"winhandler.exe start.exe /unix {launch}";

        var sb = new StringBuilder();
        foreach (var (key, value) in environment)
            sb.Append("export ").Append(key).Append('=').Append(ShellQuote(value)).Append('\n');
        // TARGET only for everything else. Every tuning variable lives in run-wine.sh, taking its value
        // from files/xla-settings.sh (the settings screen) - they used to be duplicated here, where they
        // silently won over anything the settings said.
        sb.Append($"TARGET='explorer /desktop=shell,{width}x{height} {launch}'\n");
        File.WriteAllText(FilesDir + "/wine-cmd", sb.ToString());

        StartXServerAndWine(width, height);
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Copies winhandler.exe from the APK into C:\windows, which is on Wine's PATH. Shipped in the APK rather than
    /// the prefix package because it has to match the app's side of its UDP protocol, and a prefix bump would
    /// reset the player's registry. Returns false if it could not be put in place.
    /// </summary>
    private static bool InstallWinHandler()
    {
        try
        {
            var bytes = AppHost.ReadAsset?.Invoke("winhandler.exe");
            if (bytes == null)
                return false;
            var target = Path.Combine(FilesDir, "prefix", ".wine", "drive_c", "windows", "winhandler.exe");
            var existing = new FileInfo(target);
            if (!existing.Exists || existing.Length != bytes.Length)
                File.WriteAllBytes(target, bytes);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"XlaLauncher: winhandler.exe unavailable, mouse camera will not work ({ex.GetType().Name})");
            return false;
        }
    }

    /// <summary>
    /// Points the prefix's A: drive at the game folder. Wine takes the most specific dosdevices entry
    /// when it converts a Unix path, so this is what turns the launch command into
    /// A:\game\ffxiv_dx11.exe rather than a long Z: path. Re-applied on every launch: the folder can
    /// be changed in the settings screen, and re-provisioning resets the link to the default.
    /// </summary>
    private static void PointDriveAtGame()
    {
        var link = Path.Combine(FilesDir, "prefix", ".wine", "dosdevices", "a:");
        try
        {
            if (new FileInfo(link).LinkTarget == GamePath)
                return;
            File.Delete(link); // does nothing when there is no link there
            Directory.CreateSymbolicLink(link, GamePath);
        }
        catch (Exception ex)
        {
            // Not fatal: Wine can still reach the game through Z:, which maps the whole filesystem.
            Console.WriteLine($"XlaLauncher: could not point A: at the game folder ({ex.GetType().Name})");
        }
    }

    /// <summary>Debug path (xla_autorun intent extra only): launch whatever files/wine-cmd currently holds.</summary>
    private void RunWineTest()
    {
        Greeting = "starting X server...";
        var (width, height) = GameResolution();
        StartXServerAndWine(width, height);
    }

    private static void StartXServerAndWine(int width, int height)
    {
        // The X server has to be listening before Wine's winex11.drv tries to connect, and
        // ShowXServer replaces this very UI with the renderer view - so hand off to a worker
        // thread immediately and never touch the view model again once the swap has happened.
        Task.Run(() =>
        {
            var socket = ShowXServer?.Invoke(width, height) ?? "(no hook)";
            File.WriteAllText(FilesDir + "/xserver.log",
                $"socket={socket}\nstorage={StorageProbe()}\n");

            if (socket.StartsWith("ERROR", StringComparison.Ordinal))
                return;

            Run("/system/bin/sh", FilesDir + "/run-wine.sh");
        });
    }

    private static string StorageProbe()
    {
        var sb = "";
        foreach (var p in new[] { "/storage/emulated/0", "/sdcard" })
        {
            var exists = Directory.Exists(p);
            var readable = "?";
            if (exists)
            {
                try { readable = Directory.GetFileSystemEntries(p).Length.ToString(); }
                catch (Exception e) { readable = e.GetType().Name; }
            }
            sb += $"{p}={(exists ? "Y" : "N")}/{readable} ";
        }
        if (ExternalProbe != null) sb += "| " + ExternalProbe();
        return sb;
    }

    private static string Run(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = FilesDir,
            };
            // The Android app process carries an LD_LIBRARY_PATH pointing at its own native lib
            // dir, which breaks Wine's resolution of the bionic rootfs libs. Start from an empty
            // env and let run-wine.sh set the whole contract.
            psi.Environment.Clear();
            psi.Environment["HOME"] = FilesDir;
            psi.Environment["NATIVE_LIB_DIR"] = NativeLibDir;

            using var proc = Process.Start(psi);
            string stdout = proc!.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return $"exit={proc.ExitCode} out=[{stdout.Trim()}] err=[{stderr.Trim()}]";
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }
}
