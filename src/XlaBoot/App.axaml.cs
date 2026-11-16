using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using XlaBoot.Localization;
using XlaBoot.ViewModels;
using XlaBoot.Views;

namespace XlaBoot;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private MainViewModel? _viewModel;

    /// <summary>Holds the MainView on Android, so a language change can swap in a freshly built one.</summary>
    private Decorator? _host;

    public override void OnFrameworkInitializationCompleted()
    {
        Loc.Apply();
        SeedGameLanguage();
        Loc.Changed += () => Dispatcher.UIThread.Post(ReloadViews);
        _viewModel = new MainViewModel();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            desktop.MainWindow = new MainWindow
            {
                DataContext = _viewModel
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            _host = new Decorator { Child = new MainView { DataContext = _viewModel } };
            singleViewPlatform.MainView = _host;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// A new install starts the game in the phone's language when the game has it. Existing installs never got the
    /// setting and keep English, so an app update does not change a player's game text behind their back.
    /// </summary>
    private static void SeedGameLanguage()
    {
        if (string.IsNullOrEmpty(AppHost.Get("game_language", ""))
            && !Provisioning.RuntimeInstaller.IsProvisioned(AppHost.FilesDir))
            AppHost.Set("game_language", Loc.GameLanguageForSystem());
    }

    /// <summary>
    /// Rebuilds the views after a launcher language change. Static text is resolved when a view is built, so a new
    /// MainView picks up the new language while the view model, and with it the login and any running job, stays.
    /// </summary>
    private void ReloadViews()
    {
        if (_viewModel == null)
            return;
        if (_host != null)
        {
            var old = _host.Child;
            _host.Child = null;
            // MainView subscribes to the view model through its DataContext; clearing it unsubscribes.
            if (old != null)
                old.DataContext = null;
            _host.Child = new MainView { DataContext = _viewModel };
        }
        else if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            if (window.Content is Control oldView)
                oldView.DataContext = null;
            window.Content = new MainView { DataContext = _viewModel };
        }
        _viewModel.RefreshHome();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}