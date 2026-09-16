using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace XlaBoot.Provisioning;

/// <summary>What provisioning is doing right now, for the setup screen and the service notification.</summary>
public sealed record ProvisionProgress(
    string Step,
    long Done,
    long Total,
    double BytesPerSecond = 0)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : 0;
}

/// <summary>
/// Installs the Wine runtime into the app's private storage: fetch the manifest, download and verify each
/// package, unpack them in order, and do the steps between that are code rather than data.
///
/// Replaces the original shell provisioning over adb, which only ever worked because the package is
/// debuggable (<c>run-as</c>); a release build cannot be provisioned that way at all.
/// </summary>
public static class RuntimeInstaller
{
    /// <summary>
    /// The order packages are applied. NOT taken from the manifest: two steps in the middle are code, and
    /// the order itself is load-bearing - rootfs and vkextra both write <c>usr/lib</c> and overlap on five
    /// adrenotools hook libs, where vkextra's are the build that renders correctly.
    /// </summary>
    private static readonly string[] Order =
        { "wine", "prefix", "overlay", "rootfs", "vkextra", "alsa", "pulseaudio" };

    /// <summary>
    /// The oldest runtime this build of the app works with. An install older than this is not provisioned,
    /// so the next start runs the installer, which updates only the packages whose bytes changed. Without
    /// it an installed runtime is never checked again and an app update cannot reach the runtime.
    /// 2: Proton 11.0-2, whose ARM64EC cooperative suspend fixes the random freeze with Dalamud.
    /// </summary>
    public const int RequiredRuntimeVersion = 2;

    /// <summary>Headroom over the extracted size, for the packages in the cache and the shader cache.</summary>
    private const long SpareBytes = 512L * 1024 * 1024;

    public static bool IsProvisioned(string filesDir)
    {
        var state = RuntimeState.Load(filesDir);
        return state.Complete && state.RuntimeVersion >= RequiredRuntimeVersion
                              && PrefixSetup.Verify(filesDir).Count == 0;
    }

    /// <summary>
    /// Brings the runtime up to date. Safe to call repeatedly: components already installed from the same
    /// bytes are skipped, so an interrupted run resumes rather than restarting.
    /// </summary>
    public static async Task InstallAsync(string filesDir, byte[] runWineSh, string gamePath,
        HttpClient http, Action<ProvisionProgress>? report, CancellationToken cancel)
    {
        var baseUri = RuntimeManifest.BaseUri();
        report?.Invoke(new ProvisionProgress("Checking for the runtime", 0, 0));
        var manifest = await RuntimeManifest.FetchAsync(baseUri, http, cancel).ConfigureAwait(false);
        // Installing an older runtime would leave the app unprovisioned again on the next start, and the
        // user in a loop with no explanation.
        if (manifest.RuntimeVersion < RequiredRuntimeVersion)
            throw new InvalidDataException(
                $"The runtime on the server (version {manifest.RuntimeVersion}) is older than this version of the app "
                + $"needs (version {RequiredRuntimeVersion}).");

        var state = RuntimeState.Load(filesDir);
        var components = Order
            .Select(name => manifest.Find(name)
                            ?? throw new InvalidDataException($"The manifest has no '{name}' component."))
            .ToList();

        // A component is redone if its bytes changed, and so is everything that layers on top of it -
        // re-extracting wine invalidates the builtin symlinks, and rootfs must be followed by vkextra.
        var stale = components.Where(c => !state.IsCurrent(c)).Select(c => c.Name).ToHashSet();

        // Nothing looks stale, yet the last run never reached the end: the recorded hashes describe what
        // the installer WROTE, never what is on disk now, and something changed the tree underneath us -
        // a run killed mid-unpack, a half-applied push, a user clearing part of the app's data. Without
        // this there is no work to do, Verify fails for the same reason as last time, and every retry
        // reports the same failure forever with no way out (which happened once an earlier provisioning left
        // d3d11.dll pointing at Wine's builtin).
        //
        // Ask Verify which package owns each problem rather than redoing everything: replacing the prefix
        // discards the player's in-prefix registry, so it must not happen as a side effect of repairing
        // something else. Problems with no owner are repaired by the steps at the end of this method.
        if (stale.Count == 0 && !state.Complete)
        {
            var broken = PrefixSetup.Verify(filesDir);
            var implicated = broken.Where(p => p.Component != null).Select(p => p.Component!).ToHashSet();
            if (implicated.Count > 0)
            {
                stale.UnionWith(implicated);
            }
            else if (broken.Count == 0)
            {
                // Healthy, just never marked complete: fall through to the finishing steps below, which
                // cost nothing, and let them set the flag. No download at all.
            }
            // Otherwise every problem is one the finishing steps repair, so again no download.
        }

        if (stale.Contains("wine")) stale.Add("prefix");
        if (stale.Contains("prefix")) stale.Add("overlay");
        if (stale.Contains("rootfs")) { stale.Add("vkextra"); stale.Add("alsa"); }
        var todo = components.Where(c => stale.Contains(c.Name)).ToList();

        if (todo.Count == 0 && state.Complete)
        {
            // A version bump with no changed package still has to be recorded, or IsProvisioned keeps
            // sending every start back here.
            if (state.RuntimeVersion != manifest.RuntimeVersion)
            {
                state.RuntimeVersion = manifest.RuntimeVersion;
                state.Save(filesDir);
            }
            // The launch script ships in the APK, not the bundle, so an app update must still replace it here:
            // returning before WriteLaunchScript kept every existing install on its first-setup copy.
            PrefixSetup.WriteLaunchScript(filesDir, runWineSh);
            report?.Invoke(new ProvisionProgress("The runtime is up to date", 1, 1));
            return;
        }

        EnsureSpace(filesDir, todo);

        // One progress scale across both phases, so the bar never restarts: downloading is counted as
        // the package size, unpacking as the extracted size.
        var total = todo.Sum(c => c.Bytes + c.ExtractedBytes);
        long completed = 0;

        var cache = Path.Combine(CacheRoot(filesDir), "runtime");
        Directory.CreateDirectory(cache);

        state.RuntimeVersion = manifest.RuntimeVersion;
        state.Complete = false;
        state.Save(filesDir);

        foreach (var component in todo)
        {
            cancel.ThrowIfCancellationRequested();

            var archive = await PackageDownloader.EnsureAsync(
                RuntimeManifest.Resolve(baseUri, component.File), component,
                Path.Combine(cache, Path.GetFileName(component.File)), http,
                (done, _, rate) => report?.Invoke(new ProvisionProgress(
                    $"Downloading {Describe(component.Name)}", completed + done, total, rate)),
                cancel).ConfigureAwait(false);
            completed += component.Bytes;

            // A stale driver must not survive beside the new one and be picked up by name.
            if (component.Name == "vkextra")
                PrefixSetup.ClearBundledDriver(filesDir);

            // Unpacking a new Wine over an old one leaves every file the new build dropped, and
            // LinkBuiltins links whatever it finds into system32. An old PE half loaded against a new
            // unix half does not work, so the old tree goes first.
            if (component.Name == "wine")
                ClearDirectory(Path.Combine(filesDir, component.Dest));

            var unpackBase = completed;
            await ArchiveExtractor.ExtractAsync(archive, Path.Combine(filesDir, component.Dest),
                extracted => report?.Invoke(new ProvisionProgress(
                    $"Installing {Describe(component.Name)}", unpackBase + extracted, total)),
                cancel).ConfigureAwait(false);
            completed = unpackBase + component.ExtractedBytes;

            // Wine's builtins have to be reachable from C:\windows\system32 before anything that layers
            // over them; the overlay then replaces the d3d DLLs among those links.
            if (component.Name == "prefix")
            {
                report?.Invoke(new ProvisionProgress("Linking Windows system files", completed, total));
                PrefixSetup.LinkBuiltins(filesDir);
            }

            // Recorded per component, so an interrupted run resumes at the right place.
            state.Components[component.Name] = component.Sha256;
            state.Save(filesDir);

            // The package is 188 MB at worst and is of no further use once unpacked.
            if (!RuntimeManifest.BaseUri().IsFile)
                TryDelete(archive);
        }

        report?.Invoke(new ProvisionProgress("Finishing setup", total, total));
        PrefixSetup.WriteLaunchScript(filesDir, runWineSh);
        PrefixSetup.MakeExecutable(filesDir);
        PrefixSetup.MapDrives(filesDir, gamePath);

        var problems = PrefixSetup.Verify(filesDir);
        if (problems.Count > 0)
        {
            // Clearing the hashes of the components still implicated means the next attempt sees them as
            // stale and redownloads them, instead of finding nothing to do and failing here identically.
            foreach (var problem in problems.Where(p => p.Component != null))
                state.Components.Remove(problem.Component!);
            state.Save(filesDir);
            throw new InvalidOperationException("The runtime did not install correctly: "
                                                + string.Join("; ", problems.Select(p => p.Message)));
        }

        state.Complete = true;
        state.Save(filesDir);
        report?.Invoke(new ProvisionProgress("Ready", total, total));
    }

    /// <summary>Names for the setup screen. Nobody outside this project knows what "vkextra" is.</summary>
    private static string Describe(string component) => component switch
    {
        "wine" => "the Windows compatibility layer",
        "prefix" => "the Windows environment",
        "overlay" => "graphics libraries and fonts",
        "rootfs" => "system libraries",
        "vkextra" => "the graphics driver",
        "alsa" => "audio configuration",
        "pulseaudio" => "the audio server",
        _ => component,
    };

    /// <summary>
    /// Refuses to start rather than filling the device. Uses <see cref="AppHost.FreeBytes"/> because
    /// <c>DriveInfo</c> is unreliable on Android's emulated storage.
    /// </summary>
    private static void EnsureSpace(string filesDir, IReadOnlyList<RuntimeComponent> todo)
    {
        if (todo.Count == 0)
            return; // Max() would throw on an empty sequence, and nothing is about to be written anyway.

        var needed = todo.Sum(c => c.ExtractedBytes) + todo.Max(c => c.Bytes) + SpareBytes;
        var free = AppHost.FreeBytes?.Invoke(filesDir) ?? 0;
        if (free > 0 && free < needed)
            throw new IOException(
                $"Not enough free space: the runtime needs about {needed / (1 << 30)} GB and "
                + $"{free / (1 << 30)} GB is available. Free some space and try again.");
    }

    /// <summary>The app's cache dir, a sibling of files/ on Android.</summary>
    private static string CacheRoot(string filesDir) =>
        AppHost.CacheDir.Length > 0 ? AppHost.CacheDir : Path.Combine(filesDir, "cache");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception) { /* the cache dir is Android's to reclaim */ }
    }

    /// <summary>
    /// Deletes a component's directory before it is reinstalled. Not best-effort: unpacking over a tree
    /// that could not be cleared is exactly the mixed install this exists to prevent.
    /// </summary>
    private static void ClearDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
