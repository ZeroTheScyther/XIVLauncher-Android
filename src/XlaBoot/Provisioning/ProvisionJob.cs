using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace XlaBoot.Provisioning;

/// <summary>
/// The one provisioning run for this process.
///
/// Process-wide rather than owned by a view model, for two reasons. The activity can be destroyed and
/// recreated mid-install (a config change, the user leaving and coming back), and a fresh view model
/// must attach to the work already in flight instead of starting a second copy. And on this phone the
/// process itself is frozen by Samsung's Freecess as soon as it goes to the background, so the work has
/// to be paired with a foreground service - which is a process-level thing, not a screen-level one.
/// </summary>
public static class ProvisionJob
{
    private static readonly object Gate = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private static Task? _running;
    private static CancellationTokenSource? _cancel;

    /// <summary>The most recent progress, so a newly attached UI has something to show immediately.</summary>
    public static ProvisionProgress? Latest { get; private set; }

    /// <summary>Why the last run failed, or null. Kept so a recreated UI can still show the error.</summary>
    public static Exception? Failure { get; private set; }

    public static bool IsRunning
    {
        get { lock (Gate) return _running is { IsCompleted: false }; }
    }

    /// <summary>Fired on a worker thread for every progress update; the UI marshals.</summary>
    public static event Action<ProvisionProgress>? Progress;

    /// <summary>Fired on a worker thread when the run ends. The argument is null on success.</summary>
    public static event Action<Exception?>? Finished;

    /// <summary>
    /// Starts provisioning unless it is already running. Returns true if this call started it, so the
    /// caller knows whether it owns the keep-alive.
    /// </summary>
    public static bool Start(string filesDir, byte[] runWineSh, string gamePath)
    {
        lock (Gate)
        {
            if (_running is { IsCompleted: false })
                return false;

            Failure = null;
            Latest = null;
            _cancel?.Dispose();
            _cancel = new CancellationTokenSource();
            var cancel = _cancel.Token;

            // The service keeps the process alive; without it Android freezes the work the moment the
            // user leaves the app, and it stops silently - nothing throws.
            AppHost.StartKeepAlive?.Invoke("Setting up");

            _running = Task.Run(async () =>
            {
                Exception? failure = null;
                try
                {
                    await RuntimeInstaller.InstallAsync(filesDir, runWineSh, gamePath, Http, Report, cancel)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = ex;
                    Console.WriteLine($"XlaLauncher: provisioning failed: {ex}");
                }
                finally
                {
                    AppHost.StopKeepAlive?.Invoke();
                }
                Failure = failure;
                Finished?.Invoke(failure);
            }, cancel);
            return true;
        }
    }

    public static void Cancel()
    {
        lock (Gate)
            _cancel?.Cancel();
    }

    private static void Report(ProvisionProgress progress)
    {
        Latest = progress;
        AppHost.UpdateKeepAlive?.Invoke(progress.Step, progress.Fraction);
        Progress?.Invoke(progress);
    }
}
