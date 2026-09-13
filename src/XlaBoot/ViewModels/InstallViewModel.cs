using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XIVLauncher.Common.Game.Patch.PatchList;
using XlaBoot.Install;

namespace XlaBoot.ViewModels;

/// <summary>
/// The game install/update screen. Covers the login page while <see cref="GameInstallJob"/> runs and, on
/// failure, stays up with the reason until the user closes it.
/// </summary>
public partial class InstallViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _title = "Installing FFXIV";

    [ObservableProperty]
    private string _step = "Preparing";

    [ObservableProperty]
    private double _fraction;

    /// <summary>"12.3 GB of 45.6 GB · 11.2 MB/s · about 1 h 5 min left".</summary>
    [ObservableProperty]
    private string _detail = "";

    [ObservableProperty]
    private string _error = "";

    [ObservableProperty]
    private bool _isWorking;

    [ObservableProperty]
    private bool _isCancelling;

    /// <summary>See <see cref="SetupViewModel.NeedsBackgroundPermission"/>; a game install is far longer.</summary>
    [ObservableProperty]
    private bool _needsBackgroundPermission;

    public bool HasError => Error.Length > 0;

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    // Smoothed download rate, so the time estimate does not jump around with every report.
    private double _rate;

    public InstallViewModel()
    {
        GameInstallJob.Progress += OnProgress;

        // A recreated screen attaches to the install already running. It does not continue into the game
        // afterwards - that belonged to the login that started it - so it just reports the outcome.
        if (GameInstallJob.Current is { } running)
        {
            Title = GameInstallJob.Title;
            if (GameInstallJob.Latest is { } latest)
                OnProgress(latest);
            _ = WatchAsync(running);
        }
    }

    /// <summary>Runs the install with this screen up. True when it finished; false leaves the reason on screen.</summary>
    public Task<bool> RunAsync(string title, string gamePath, IReadOnlyList<PatchListEntry> patches)
    {
        Title = title;
        Step = "Preparing";
        Fraction = 0;
        Detail = "";
        _rate = 0;
        return WatchAsync(GameInstallJob.Run(title, gamePath, patches));
    }

    private async Task<bool> WatchAsync(Task job)
    {
        Error = "";
        IsCancelling = false;
        NeedsBackgroundPermission = AppHost.IsBackgroundAllowed?.Invoke() == false;
        IsOpen = true;
        IsWorking = true;

        try
        {
            await job;
            IsOpen = false;
            return true;
        }
        catch (OperationCanceledException)
        {
            // Nothing is lost: finished patches are recorded and a partial download resumes.
            IsOpen = false;
            return false;
        }
        catch (Exception ex)
        {
            Error = ex.Message.Length > 0 ? ex.Message : ex.GetType().Name;
            return false;
        }
        finally
        {
            IsWorking = false;
            IsCancelling = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        IsCancelling = true;
        GameInstallJob.Cancel();
    }

    [RelayCommand]
    private void Close()
    {
        Error = "";
        IsOpen = false;
    }

    [RelayCommand]
    private void AllowBackground()
    {
        AppHost.RequestBackgroundAllowed?.Invoke();
        NeedsBackgroundPermission = false;
    }

    private void OnProgress(InstallProgress p) => Dispatcher.UIThread.Post(() =>
    {
        Step = IsCancelling ? "Stopping after this step…" : p.Step;
        Fraction = p.Fraction;

        if (p.BytesPerSecond > 0)
            _rate = _rate <= 0 ? p.BytesPerSecond : _rate * 0.8 + p.BytesPerSecond * 0.2;

        var detail = $"{Gigabytes(p.DownloadedBytes)} of {Gigabytes(p.TotalBytes)} downloaded";
        if (p.BytesPerSecond > 0 && _rate > 0)
        {
            detail += $" · {_rate / 1048576.0:F1} MB/s";
            var remaining = TimeSpan.FromSeconds((p.TotalBytes - p.DownloadedBytes) / _rate);
            detail += $" · about {Duration(remaining)} left";
        }
        Detail = detail;
    });

    private static string Gigabytes(long bytes) => bytes >= 1L << 30
        ? $"{bytes / 1073741824.0:F1} GB"
        : $"{bytes / 1048576.0:F0} MB";

    private static string Duration(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours} h {t.Minutes} min"
        : t.TotalMinutes >= 1 ? $"{(int)Math.Ceiling(t.TotalMinutes)} min" : "a minute";
}
