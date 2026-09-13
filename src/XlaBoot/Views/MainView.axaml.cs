using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using XlaBoot.ViewModels;

namespace XlaBoot.Views;

public partial class MainView : UserControl
{
    private MainViewModel? _viewModel;

    public MainView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            // The brush is null until the theme's style setter runs, and changes again with the
            // light/dark variant, so follow the property rather than sampling it once.
            topLevel.PropertyChanged += OnTopLevelPropertyChanged;
            topLevel.BackRequested += OnBackRequested;
            MatchWindowBackground(topLevel.Background);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            topLevel.PropertyChanged -= OnTopLevelPropertyChanged;
            topLevel.BackRequested -= OnBackRequested;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTopLevelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TemplatedControl.BackgroundProperty && sender is TopLevel topLevel)
            MatchWindowBackground(topLevel.Background);
    }

    /// <summary>
    /// Paints the Android window with the same colour Avalonia paints, so the display-cutout strip
    /// down the short edge in landscape stops showing white (the game view has its own copy of this fix).
    /// Reading the colour back instead of hardcoding
    /// one keeps it right in either theme; an unknown brush is left alone rather than guessed at.
    /// </summary>
    private static void MatchWindowBackground(IBrush? background)
    {
        if (AppHost.SetWindowBackground == null || background is not ISolidColorBrush solid)
            return;
        var color = solid.Color;
        AppHost.SetWindowBackground(((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel != null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as MainViewModel;
        if (_viewModel != null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>
    /// Android Back (gesture or button) while the launcher is up: close the open dialog, else go to Home, else leave
    /// the app as usual. While the game runs MainActivity consumes Back before Avalonia sees it.
    /// </summary>
    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null)
            return;
        if (_viewModel.IsOtpPromptOpen)
        {
            _viewModel.CancelOtpCommand.Execute(null);
            e.Handled = true;
        }
        else if (_viewModel.IsNoGamePromptOpen)
        {
            _viewModel.IsNoGamePromptOpen = false;
            e.Handled = true;
        }
        else
        {
            e.Handled = _viewModel.GoBack();
        }
    }

    /// <summary>Copy button on the Logs page: the shown tail, for pasting into a bug report.</summary>
    private async void CopyLog(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        try
        {
            await clipboard.SetTextAsync(_viewModel.Logs.Text);
            _viewModel.Logs.Detail = "Copied to the clipboard.";
        }
        catch (Exception ex)
        {
            _viewModel.Logs.Detail = $"Could not copy: {ex.GetType().Name}";
        }
    }

    // Put the caret in the code box as soon as the OTP prompt opens, so typing can start right away.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsOtpPromptOpen) && _viewModel is { IsOtpPromptOpen: true })
            Dispatcher.UIThread.Post(() => OtpBox.Focus(), DispatcherPriority.Loaded);
        // A log reads bottom-up: open it at the newest line.
        if (e.PropertyName == nameof(MainViewModel.IsLogsPage) && _viewModel is { IsLogsPage: true })
            Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);
    }
}
