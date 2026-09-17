using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XlaBoot.Views;

namespace XlaBoot.ViewModels;

public enum LauncherPage { Home, Login, Settings, Logs, About }

/// <summary>
/// The launcher's pages. Home, Settings and Logs sit on the bottom navigation; Login (reached from Play) and About are
/// pushed pages with a back arrow. Android's Back goes to Home from anywhere, then leaves the app.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomePage), nameof(IsLoginPage), nameof(IsSettingsPage), nameof(IsLogsPage),
        nameof(IsAboutPage), nameof(ShowBottomNav))]
    private LauncherPage _page = LauncherPage.Home;

    public bool IsHomePage => Page == LauncherPage.Home;
    public bool IsLoginPage => Page == LauncherPage.Login;
    public bool IsSettingsPage => Page == LauncherPage.Settings;
    public bool IsLogsPage => Page == LauncherPage.Logs;
    public bool IsAboutPage => Page == LauncherPage.About;
    public bool ShowBottomNav => Page is LauncherPage.Home or LauncherPage.Settings or LauncherPage.Logs;

    public LogsViewModel Logs { get; } = new();

    partial void OnPageChanged(LauncherPage value)
    {
        switch (value)
        {
            case LauncherPage.Home: RefreshHome(); break;
            case LauncherPage.Settings: Settings.Open(); break;
            case LauncherPage.Logs: Logs.Refresh(); break;
        }
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        // A login in progress owns the screen: its status line is on the login page.
        if (IsBusy)
            return;
        // The game card opens Settings on General, where the install location is.
        if (page == "Game")
        {
            Settings.TabIndex = 0;
            page = nameof(LauncherPage.Settings);
        }
        if (!System.Enum.TryParse<LauncherPage>(page, out var target))
            return;
        if (target == Page && target == LauncherPage.Home)
            RefreshHome();
        Page = target;
    }

    /// <summary>Android Back. False when already home, so the platform does its default (leave the app).</summary>
    public bool GoBack()
    {
        if (Page == LauncherPage.Home)
            return false;
        if (!IsBusy)
            Page = LauncherPage.Home;
        return true;
    }

    /// <summary>About page's "Buy me a coffee".</summary>
    [RelayCommand]
    private void OpenKofi() => AppHost.OpenUrl?.Invoke(new System.Uri("https://ko-fi.com/zerothescyther"));

    /// <summary>About page's Discord button.</summary>
    [RelayCommand]
    private void OpenDiscord() => AppHost.OpenUrl?.Invoke(new System.Uri("https://discord.gg/FghfnnhRV8"));

    // ---- Home ------------------------------------------------------------------------------------

    [ObservableProperty]
    private string _gameTitle = "";

    [ObservableProperty]
    private string _gameDetail = "";

    [ObservableProperty]
    private Geometry _gameIcon = Icons.CheckCircle;

    [ObservableProperty]
    private bool _isGameMissing;

    public string PlayDetail => SafeModeNextLaunch ? "Launch in safe mode (no plugins)" : "Launch Final Fantasy XIV";

    public string VersionText => AppHost.VersionName.Length > 0
        ? $"{AppHost.VersionName} ({AppHost.VersionCode})"
        : "Development build";

    /// <summary>Re-checked every time Home is shown: the folder or the storage permission may have changed in settings.</summary>
    public void RefreshHome()
    {
        var game = GameLocation.Check(GamePath);
        switch (game.Status)
        {
            case GameStatus.Present:
                GameTitle = "Game installed";
                GameDetail = game.Version.Length > 0 ? $"Version {game.Version}" : "Version unknown";
                GameIcon = Icons.CheckCircle;
                IsGameMissing = false;
                break;
            case GameStatus.Unreadable:
                GameTitle = "Game folder not readable";
                GameDetail = "Tap Play to allow storage access, or pick the folder in Settings.";
                GameIcon = Icons.Warning;
                IsGameMissing = true;
                break;
            default:
                GameTitle = "No game found";
                GameDetail = "Tap Play to install it, or pick your copy in Settings.";
                GameIcon = Icons.Warning;
                IsGameMissing = true;
                break;
        }
        OnPropertyChanged(nameof(PlayDetail));
    }
}
