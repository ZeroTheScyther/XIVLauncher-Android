using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using XIVLauncher.Common;
using XIVLauncher.Common.Game.Patch.PatchList;
using XIVLauncher.Common.Patching.ZiPatch;
using XIVLauncher.Common.Patching.ZiPatch.Util;
using XlaBoot.Provisioning;
using XlaBoot.Localization;

namespace XlaBoot.Install;

/// <summary>One progress report from the installer.</summary>
/// <param name="Step">What is happening now, fit to show the user.</param>
/// <param name="PatchNumber">1-based index of the patch being worked on.</param>
/// <param name="DownloadedBytes">Bytes of the whole chain downloaded so far, including finished patches.</param>
/// <param name="AppliedBytes">Bytes of the whole chain applied so far.</param>
/// <param name="TotalBytes">Size of every patch in the chain.</param>
public sealed record InstallProgress(string Step, int PatchNumber, int PatchCount,
    long DownloadedBytes, long AppliedBytes, long TotalBytes, double BytesPerSecond)
{
    /// <summary>Downloading and applying weighted equally: on a phone the apply is not free.</summary>
    public double Fraction => TotalBytes > 0
        ? Math.Clamp((DownloadedBytes + AppliedBytes) / (2.0 * TotalBytes), 0, 1)
        : 0;
}

/// <summary>
/// Not enough room to continue, with the numbers to show the user. Normally thrown before anything runs
/// out; <see cref="Free"/> is -1 when the disk filled up anyway mid-write (another app used the space).
/// </summary>
public sealed class NotEnoughSpaceException(long required, long free) : IOException(Describe(required, free))
{
    public long Required { get; } = required;
    public long Free { get; } = free;

    private static string Describe(long required, long free) => free < 0
        ? Loc.F(Strings.Space_RanOut, Gigabytes(required))
        : Loc.F(Strings.Space_NotEnough, Gigabytes(required), Gigabytes(free), Gigabytes(required - free));

    internal static string Gigabytes(long bytes) => $"{Math.Max(bytes, 0) / 1073741824.0:F1} GB";
}

/// <summary>
/// Installs or updates the game from a patch list: the boot chain from <c>CheckBootVersion</c>, or the
/// game chain that <c>Login</c> returns as <c>NeedsPatchGame</c>. A fresh install is simply the longest
/// chain, starting from the base version.
///
/// Upstream's <c>PatchManager</c> and <c>PatchInstaller</c> are deliberately not used: they call
/// <c>Environment.Exit</c> on failure, leak a mutex on the hash-failure path, and queue downloads with no
/// regard for disk space. The pieces underneath them are public, so this is the orchestration only.
///
/// Each patch is downloaded, verified, applied, recorded and deleted in order, and the next one downloads
/// while the current one applies. That overlap only happens when there is room for both; otherwise it
/// falls back to one patch at a time, whose peak disk use (the game plus one patch) is what makes a large
/// install fit on a phone at all.
///
/// Resuming needs no journal. The <c>.ver</c> files are the checkpoint: after an interruption the server
/// hands back only the patches still missing, and a half-downloaded patch file is Range-resumed.
/// </summary>
public static class GameInstaller
{
    /// <summary>Headroom kept free: Android misbehaves badly on a completely full data partition.</summary>
    public const long ReserveBytes = 1L << 30;

    private const int DownloadAttempts = 5;

    /// <summary>
    /// Where patches wait between download and apply: inside the game folder, so they are always on the same
    /// volume as the game. With the game on an SD card, app-private storage would be a different (and often
    /// much smaller) disk, and the space check would be measuring the wrong one. Removed when a chain ends.
    /// </summary>
    public static string PatchStore(string gamePath) => Path.Combine(gamePath, ".xla-patches");

    /// <summary>
    /// True when a previous install started here but never produced the game executable. Such a folder
    /// is resumed without asking again whether to install.
    /// </summary>
    public static bool HasPartialInstall(string gamePath)
    {
        var game = new DirectoryInfo(gamePath);
        return Repository.Boot.GetVerFile(game).Exists
               && !File.Exists(Path.Combine(gamePath, "game", "ffxiv_dx11.exe"));
    }

    /// <summary>
    /// Free space the whole chain needs before it starts: every remaining patch applied, plus the largest
    /// one sitting in the store at the same time, plus <see cref="ReserveBytes"/>. Bytes already
    /// downloaded count as paid for. Deliberately the same shape as upstream's check (the sum of the
    /// patch lengths), which is known to be a safe estimate of what applying them adds.
    /// </summary>
    public static long RequiredBytes(IReadOnlyList<PatchListEntry> patches, string patchStore)
    {
        if (patches.Count == 0)
            return 0;
        var downloaded = patches.Sum(p => PartialBytes(p, patchStore));
        return patches.Sum(p => p.Length) + patches.Max(p => p.Length) + ReserveBytes - downloaded;
    }

    public static async Task InstallAsync(string gamePath, IReadOnlyList<PatchListEntry> patches, string patchStore,
        HttpClient http, Func<string, long>? freeBytes, Action<InstallProgress> report, CancellationToken cancel)
    {
        if (patches.Count == 0)
            return;

        // Upstream's EnsureGameDirectories is private; it is these three lines.
        Directory.CreateDirectory(Path.Combine(gamePath, "boot"));
        Directory.CreateDirectory(Path.Combine(gamePath, "game"));
        Directory.CreateDirectory(patchStore);
        DeleteStalePatches(patches, patchStore);

        var game = new DirectoryInfo(gamePath);
        var total = patches.Sum(p => p.Length);
        var free = freeBytes?.Invoke(gamePath) ?? 0;
        var required = RequiredBytes(patches, patchStore);
        // 0 means the platform could not tell; do not block on an unknown.
        if (free > 0 && free < required)
            throw new NotEnoughSpaceException(required, free);

        // Start of each patch within the chain, so either worker can turn its own count into a chain total.
        var offsets = new long[patches.Count];
        for (var i = 1; i < patches.Count; i++)
            offsets[i] = offsets[i - 1] + patches[i - 1].Length;

        // Shared by the download and the apply, which report from different threads.
        var gate = new object();
        long downloaded = 0, applied = 0;
        double rate = 0;
        string? installing = null;
        var installingNumber = 0;

        string Downloading(int i) => patches.Count == 1
            ? Strings.Installer_DownloadingOnly : Loc.F(Strings.Installer_DownloadingN, i + 1, patches.Count);
        string Installing(int i) => patches.Count == 1
            ? Strings.Installer_InstallingOnly : Loc.F(Strings.Installer_InstallingN, i + 1, patches.Count);
        string FileOf(int i) => Path.Combine(patchStore, patches[i].GetFilePath());

        // While an apply runs it names the step: it is the slower half, and the one a cancel waits for.
        void Report(string step, int number)
        {
            lock (gate)
                report(new InstallProgress(installing ?? step, installing != null ? installingNumber : number,
                    patches.Count, downloaded, applied, total, rate));
        }

        // Stops a download running ahead when the apply beside it fails.
        using var abandon = CancellationTokenSource.CreateLinkedTokenSource(cancel);

        async Task Fetch(int i)
        {
            await DownloadAsync(patches[i], FileOf(i), http, abandon.Token, (done, r) =>
            {
                lock (gate)
                {
                    downloaded = offsets[i] + done;
                    rate = r;
                    Report(Downloading(i), i + 1);
                }
            }).ConfigureAwait(false);
            lock (gate)
            {
                downloaded = offsets[i] + patches[i].Length;
                rate = 0;
            }
        }

        // Peak disk use while patch i applies and i + 1 downloads: the rest of i + 1, plus what applying
        // each of them adds. Re-checked per patch, because the user's other apps keep using the disk.
        long OverlapNeeded(int i) => patches[i + 1].Length - PartialBytes(patches[i + 1], patchStore)
                                     + patches[i + 1].Length + patches[i].Length + ReserveBytes;

        Task? next = null;
        for (var i = 0; i < patches.Count; i++)
        {
            var patch = patches[i];

            // With a download already running, a cancel surfaces from awaiting it instead.
            if (next == null)
            {
                cancel.ThrowIfCancellationRequested();
                free = freeBytes?.Invoke(gamePath) ?? 0;
                var needed = (patch.Length - PartialBytes(patch, patchStore)) + patch.Length + ReserveBytes;
                if (free > 0 && free < needed)
                    throw new NotEnoughSpaceException(needed, free);
                next = Fetch(i);
            }
            await next.ConfigureAwait(false);
            next = null;

            // The network would sit idle for the whole apply otherwise. Only with room for both patches at
            // once; without it the next one simply waits, which is the one-at-a-time install that always fit.
            if (i + 1 < patches.Count)
            {
                free = freeBytes?.Invoke(gamePath) ?? 0;
                if (free > 0 && free >= OverlapNeeded(i))
                    next = Fetch(i + 1);
            }

            lock (gate)
            {
                installing = Installing(i);
                installingNumber = i + 1;
            }
            try
            {
                // Not cancellable once started: stopping halfway through a patch is how an install gets
                // corrupted. A cancel lands after this patch is fully applied and recorded.
                var start = offsets[i];
                await Task.Run(() => Apply(patch, FileOf(i), gamePath, done =>
                {
                    lock (gate)
                    {
                        applied = start + done;
                        Report("", i + 1);
                    }
                }), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                abandon.Cancel();
                if (next != null)
                {
                    try { await next.ConfigureAwait(false); }
                    catch (Exception) { /* the apply failure is the one to report */ }
                }
                throw;
            }
            finally
            {
                lock (gate)
                    installing = null;
            }

            patch.GetRepo().SetVer(game, patch.VersionId);
            RangeDownload.TryDelete(FileOf(i));
            lock (gate)
                applied = offsets[i] + patch.Length;
        }

        VerToBck(game);
        try { Directory.Delete(patchStore, recursive: true); }
        catch (Exception) { /* leftovers are cleaned by the next chain's DeleteStalePatches */ }
        report(new InstallProgress(Strings.Settings_Done, patches.Count, patches.Count, total, total, total, 0));
    }

    private static async Task DownloadAsync(PatchListEntry patch, string file, HttpClient http,
        CancellationToken cancel, Action<long, double> progress)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            var before = File.Exists(file) ? new FileInfo(file).Length : 0;
            try
            {
                await RangeDownload.FetchAsync(new Uri(patch.Url), patch.Length, file, http,
                    request => request.Headers.TryAddWithoutValidation("User-Agent", Constants.PatcherUserAgent),
                    (done, _, rate) => progress(done, rate), cancel).ConfigureAwait(false);

                var check = await Task.Run(() => PatchValidator.Check(patch, file, cancel), cancel)
                    .ConfigureAwait(false);
                if (check == PatchCheck.Pass)
                    return;

                // Almost always a bad resume; start the file over.
                RangeDownload.TryDelete(file);
                throw new InvalidDataException($"{patch} failed verification ({check}).");
            }
            catch (Exception ex) when (IsOutOfSpace(ex))
            {
                throw new NotEnoughSpaceException(patch.Length + ReserveBytes, -1);
            }
            // A connection that keeps dropping but keeps delivering is a slow network, not a dead one:
            // only attempts that made no progress at all count towards giving up.
            // HttpClient reports its own timeout as a TaskCanceledException; only the user's Stop is final.
            catch (Exception ex) when (!cancel.IsCancellationRequested
                                       && (attempt < DownloadAttempts || Grew(file, before)))
            {
                Console.WriteLine($"XlaLauncher: download of {patch} failed (attempt {attempt}): {ex.GetType().Name}: {ex.Message}");
                if (Grew(file, before))
                    attempt = 0;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 << attempt)), cancel).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new HttpRequestException(
                    Strings.Installer_DownloadFailed, ex);
            }
        }
    }

    private static bool Grew(string file, long before) => File.Exists(file) && new FileInfo(file).Length > before;

    /// <summary>Upstream's <c>RemotePatchInstaller.InstallPatch</c>, chunk by chunk so it can report progress.</summary>
    private static void Apply(PatchListEntry patch, string file, string gamePath, Action<long> progress)
    {
        var target = Path.Combine(gamePath, patch.GetRepo() == Repository.Boot ? "boot" : "game");
        try
        {
            using var zipatch = ZiPatchFile.FromFileName(file);
            using var store = new SqexFileStreamStore();
            var config = new ZiPatchConfig(target) { Store = store };

            var lastReport = Environment.TickCount64;
            foreach (var chunk in zipatch.GetChunks())
            {
                chunk.ApplyChunk(config);
                if (Environment.TickCount64 - lastReport >= 500)
                {
                    progress(Math.Min(chunk.Offset + chunk.Size, patch.Length));
                    lastReport = Environment.TickCount64;
                }
            }
        }
        catch (Exception ex) when (IsOutOfSpace(ex))
        {
            throw new NotEnoughSpaceException(patch.Length + ReserveBytes, -1);
        }
        catch (Exception ex)
        {
            // The version was not advanced, so the next attempt re-applies this patch from the start -
            // upstream's own recovery model. If that fails too, the install itself is damaged.
            throw new IOException(Loc.F(Strings.Installer_InstallFailed, patch, ex.Message), ex);
        }
        progress(patch.Length);
    }

    /// <summary>Upstream copies every .ver to .bck once patching ends; the login sanity check reads both.</summary>
    private static void VerToBck(DirectoryInfo game)
    {
        foreach (var repo in Enum.GetValues<Repository>())
        {
            if (repo.GetVerFile(game).Exists)
                repo.SetVer(game, repo.GetVer(game), isBck: true);
        }
    }

    /// <summary>
    /// Removes patch files this chain no longer wants - for example one that was applied and recorded just
    /// before the process died, which the server will never list again. Only in the repositories this
    /// chain covers, so a boot run cannot delete an in-progress game download.
    /// </summary>
    private static void DeleteStalePatches(IReadOnlyList<PatchListEntry> patches, string patchStore)
    {
        var wanted = patches.Select(p => Path.GetFullPath(Path.Combine(patchStore, p.GetFilePath())))
            .ToHashSet(StringComparer.Ordinal);
        var roots = patches.Select(p => p.GetFilePath().Split(Path.DirectorySeparatorChar)[0]).Distinct();
        foreach (var root in roots)
        {
            var dir = Path.Combine(patchStore, root);
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (!wanted.Contains(Path.GetFullPath(file)))
                    RangeDownload.TryDelete(file);
            }
        }
    }

    private static long PartialBytes(PatchListEntry patch, string patchStore)
    {
        var info = new FileInfo(Path.Combine(patchStore, patch.GetFilePath()));
        return info.Exists ? Math.Min(info.Length, patch.Length) : 0;
    }

    /// <summary>ENOSPC (28) or EDQUOT (122), which .NET surfaces as an IOException carrying the errno.</summary>
    private static bool IsOutOfSpace(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is NotEnoughSpaceException)
                return false; // already the friendly form
            if (e is IOException && (e.HResult is 28 or 122 || e.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }
}
