using System;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XlaBoot.Provisioning;

namespace XlaBoot.ViewModels;

/// <summary>
/// First-run setup: downloads and installs the Wine runtime before the login page is usable.
///
/// Shown over the login form rather than replacing it, so when setup finishes the user is already
/// looking at the thing they came for. Nothing here is needed on later launches - the runtime is
/// checked once and this stays closed.
/// </summary>
public partial class SetupViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _step = "Preparing";

    [ObservableProperty]
    private double _fraction;

    /// <summary>"128 MB of 384 MB at 4.2 MB/s", or empty before the first byte.</summary>
    [ObservableProperty]
    private string _detail = "";

    [ObservableProperty]
    private string _error = "";

    [ObservableProperty]
    private bool _isWorking;

    public bool HasError => Error.Length > 0;

    /// <summary>
    /// Shown while the OS may freeze the download. Not a blocker: if the process is frozen the work
    /// resumes the moment the user comes back, which is verified behaviour - but on this phone Samsung
    /// froze a foreground service holding a wake lock, so without the exemption a long install needs
    /// the user to sit and watch it.
    /// </summary>
    [ObservableProperty]
    private bool _needsBackgroundPermission;

    [RelayCommand]
    private void AllowBackground()
    {
        AppHost.RequestBackgroundAllowed?.Invoke();
        // Re-checked when the user returns; the dialog result is not reported back to us.
        NeedsBackgroundPermission = false;
    }

    /// <summary>Called when the launcher is shown again, so the prompt disappears once granted.</summary>
    public void RefreshBackgroundPermission()
    {
        if (IsOpen && AppHost.IsBackgroundAllowed?.Invoke() == false)
            NeedsBackgroundPermission = true;
        else
            NeedsBackgroundPermission = false;
    }

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    /// <summary>Raised on the UI thread once the runtime is installed and verified.</summary>
    public event Action? Finished;

    public SetupViewModel()
    {
        // The work belongs to the process, not to this screen: the activity can be recreated mid-install
        // and must attach to the run already in flight rather than starting a second one.
        ProvisionJob.Progress += OnProgress;
        ProvisionJob.Finished += OnJobFinished;
    }

    /// <summary>
    /// Shows setup and starts work if the runtime is missing or out of date. Attaches to a run already
    /// in progress. When the runtime is already installed this raises <see cref="Finished"/> at once and
    /// stays closed, so a normal launch goes straight to the login form.
    /// </summary>
    public void Begin()
    {
        if (ProvisionJob.IsRunning)
        {
            IsOpen = true;
            IsWorking = true;
            if (ProvisionJob.Latest is { } latest)
                OnProgress(latest);
            return;
        }

        if (RuntimeInstaller.IsProvisioned(AppHost.FilesDir))
        {
            // No install runs on this path, so this is where an app update's run-wine.sh reaches an existing
            // install. 10 KB, once per app start.
            if (AppHost.ReadAsset?.Invoke("run-wine.sh") is { } runWineSh)
            {
                try
                {
                    PrefixSetup.WriteLaunchScript(AppHost.FilesDir, runWineSh);
                }
                catch (IOException ex)
                {
                    Console.WriteLine($"XlaLauncher: could not refresh run-wine.sh: {ex.Message}");
                }
            }
            Finished?.Invoke();
            return;
        }

        IsOpen = true;
        StartJob();
    }

    [RelayCommand]
    private void Retry() => StartJob();

    private void StartJob()
    {
        if (ProvisionJob.IsRunning)
            return;

        Error = "";
        Step = "Preparing";
        Fraction = 0;
        Detail = "";

        var runWineSh = AppHost.ReadAsset?.Invoke("run-wine.sh");
        if (runWineSh == null)
        {
            // Shipped in the APK, so its absence means a broken build rather than anything the user did.
            Error = "run-wine.sh is missing from the app package; this build is broken.";
            return;
        }

        NeedsBackgroundPermission = AppHost.IsBackgroundAllowed?.Invoke() == false;
        IsWorking = true;
        ProvisionJob.Start(AppHost.FilesDir, runWineSh, MainViewModel.GamePath);
    }

    /// <summary>Progress arrives from a worker thread, so it is marshalled before touching bindings.</summary>
    private void OnProgress(ProvisionProgress progress) => Dispatcher.UIThread.Post(() =>
    {
        Step = progress.Step;
        Fraction = progress.Fraction;
        Detail = progress.Total > 0
            ? $"{Megabytes(progress.Done)} of {Megabytes(progress.Total)}"
              + (progress.BytesPerSecond > 0 ? $" at {Megabytes((long)progress.BytesPerSecond)}/s" : "")
            : "";
    });

    private void OnJobFinished(Exception? failure) => Dispatcher.UIThread.Post(() =>
    {
        IsWorking = false;
        if (failure == null)
        {
            IsOpen = false;
            Finished?.Invoke();
            return;
        }
        if (failure is OperationCanceledException)
        {
            Error = "Setup was cancelled.";
            return;
        }
        // The installer and downloader throw with sentences meant for a user; prefer those.
        Error = failure.Message.Length > 0 ? failure.Message : failure.GetType().Name;
    });

    private static string Megabytes(long bytes) => $"{bytes / 1048576.0:F0} MB";
}
