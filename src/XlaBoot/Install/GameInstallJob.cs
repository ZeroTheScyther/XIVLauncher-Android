using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using XIVLauncher.Common.Game.Patch.PatchList;

namespace XlaBoot.Install;

/// <summary>
/// The one game install or update for this process. Process-wide for the same reasons as
/// <see cref="Provisioning.ProvisionJob"/>: a recreated screen must attach to the work in flight rather than
/// start a second copy writing into the same files, and the keep-alive that stops Android freezing the
/// download belongs to the process.
/// </summary>
public static class GameInstallJob
{
    private static readonly object Gate = new();

    // Timeout covers connecting and headers; a stalled body is caught by RangeDownload's own stall check.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private static Task? _running;
    private static CancellationTokenSource? _cancel;

    /// <summary>What the running job is doing ("Installing FFXIV"), for a screen that attaches later.</summary>
    public static string Title { get; private set; } = "";

    public static InstallProgress? Latest { get; private set; }

    public static bool IsRunning
    {
        get { lock (Gate) return _running is { IsCompleted: false }; }
    }

    /// <summary>The run in flight, or null.</summary>
    public static Task? Current
    {
        get { lock (Gate) return _running is { IsCompleted: false } ? _running : null; }
    }

    public static bool IsCancelling
    {
        get { lock (Gate) return _cancel is { IsCancellationRequested: true } && IsRunning; }
    }

    /// <summary>Fired on a worker thread for every progress update; the UI marshals.</summary>
    public static event Action<InstallProgress>? Progress;

    /// <summary>
    /// Starts installing <paramref name="patches"/> into <paramref name="gamePath"/>, or returns the run
    /// already in flight. The task faults with the installer's exception, whose message is written for users.
    /// </summary>
    public static Task Run(string title, string gamePath, IReadOnlyList<PatchListEntry> patches)
    {
        lock (Gate)
        {
            if (_running is { IsCompleted: false })
                return _running;

            Title = title;
            Latest = null;
            _cancel?.Dispose();
            _cancel = new CancellationTokenSource();
            var cancel = _cancel.Token;

            AppHost.StartKeepAlive?.Invoke(title);
            _running = Task.Run(async () =>
            {
                try
                {
                    await GameInstaller.InstallAsync(gamePath, patches, GameInstaller.PatchStore(gamePath),
                        Http, AppHost.FreeBytes, Report, cancel).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"XlaLauncher: game install failed: {ex}");
                    throw;
                }
                finally
                {
                    AppHost.StopKeepAlive?.Invoke();
                }
            }, CancellationToken.None);
            return _running;
        }
    }

    /// <summary>Stops at the next safe point: mid-download at once, mid-apply only once that patch is in.</summary>
    public static void Cancel()
    {
        lock (Gate)
            _cancel?.Cancel();
    }

    private static void Report(InstallProgress progress)
    {
        Latest = progress;
        AppHost.UpdateKeepAlive?.Invoke(progress.Step, progress.Fraction);
        Progress?.Invoke(progress);
    }
}
