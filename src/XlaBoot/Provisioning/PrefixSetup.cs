using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XlaBoot.Provisioning;

/// <summary>
/// The steps between and after unpacking the runtime packages - what the original shell provisioning scripts
/// used to do over adb. These have to be code rather than data: two of them depend on the app's real files dir,
/// which no archive can know.
/// </summary>
public static class PrefixSetup
{
    /// <summary>The d3d/dxgi DLLs the overlay replaces with DXVK 2.4.1-gplasync and vkd3d 2.14.1.</summary>
    private static readonly string[] WrappedDlls =
    {
        "d3d8", "d3d9", "d3d10", "d3d10_1", "d3d10core", "d3d11", "d3d12", "d3d12core", "dxgi",
    };

    private static string Prefix(string filesDir) => Path.Combine(filesDir, "prefix", ".wine");

    private static string DriveC(string filesDir) => Path.Combine(Prefix(filesDir), "drive_c");

    /// <summary>
    /// Makes Wine's own builtin PEs reachable from <c>C:\windows\system32</c>.
    ///
    /// An ARM64EC process resolves its emulated x86-64 imports as real files in system32, and Wine's
    /// builtins live outside the prefix under <c>wine/lib/wine</c>, so without these links Wine dies with
    /// <c>could not load kernel32.dll (c0000135)</c>.
    ///
    /// The links must be ABSOLUTE and are therefore generated here rather than shipped in the prefix
    /// image: their targets contain the app's files dir, which embeds the package name and the Android
    /// user id (<c>/data/user/0/...</c>), so a pre-baked link would dangle under a secondary user, a work
    /// profile, or a package rename.
    ///
    /// Returns how many links were created per directory.
    /// </summary>
    public static (int System32, int SysWow64) LinkBuiltins(string filesDir)
    {
        return (
            Link(Path.Combine(filesDir, "wine", "lib", "wine", "aarch64-windows"),
                 Path.Combine(DriveC(filesDir), "windows", "system32")),
            Link(Path.Combine(filesDir, "wine", "lib", "wine", "i386-windows"),
                 Path.Combine(DriveC(filesDir), "windows", "syswow64")));

        static int Link(string source, string destination)
        {
            if (!Directory.Exists(source))
                return 0;
            Directory.CreateDirectory(destination);
            var created = 0;
            foreach (var builtin in Directory.EnumerateFiles(source))
            {
                var link = Path.Combine(destination, Path.GetFileName(builtin));
                // Skip names the prefix already provides (FEXCore's own DLLs), and names the overlay has
                // already replaced with a real DXVK build if it ran first - either order gives the same
                // end state.
                if (File.Exists(link) || Directory.Exists(link))
                    continue;
                try
                {
                    File.CreateSymbolicLink(link, builtin);
                    created++;
                }
                catch (IOException)
                {
                    // Raced with something else creating the name; the name existing is the goal.
                }
            }
            return created;
        }
    }

    /// <summary>
    /// Marks the parts of the runtime that have to be executable. The Wine binaries ship mode 0640 in
    /// the upstream package, so without this <c>wine</c> simply cannot be run.
    /// </summary>
    public static void MakeExecutable(string filesDir)
    {
        foreach (var path in Executables(filesDir))
        {
            if (!File.Exists(path))
                continue;
            try
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch (Exception)
            {
                // Reported by the launch failing; nothing useful to do here.
            }
        }
    }

    private static IEnumerable<string> Executables(string filesDir)
    {
        var bin = Path.Combine(filesDir, "wine", "bin");
        if (Directory.Exists(bin))
        {
            foreach (var file in Directory.EnumerateFiles(bin))
                yield return file;
        }
        yield return Path.Combine(filesDir, "pulseaudio", "pactl");
        yield return Path.Combine(filesDir, "run-wine.sh");
    }

    /// <summary>
    /// Installs the launch script. It ships in the APK rather than the runtime bundle because it and the
    /// app's <c>XLA_*</c> settings contract have to move together.
    /// </summary>
    public static void WriteLaunchScript(string filesDir, byte[] runWineSh)
    {
        var path = Path.Combine(filesDir, "run-wine.sh");
        File.WriteAllBytes(path, runWineSh);
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception)
        {
            // Surfaces as the launch failing.
        }
    }

    /// <summary>
    /// Recreates the prefix's drive mappings. The shipped prefix image carries none: its originals were
    /// stale Winlator paths (<c>z:</c> and <c>e:</c> pointed into <c>com.winlator.cmod</c>), so
    /// <c>scripts/bundle-runtime.sh</c> strips the whole directory and they are made here instead.
    ///
    /// Wine picks the most specific dosdevices entry when it converts a Unix path, which is what turns
    /// the launch command into <c>A:\game\ffxiv_dx11.exe</c> rather than a long <c>Z:</c> path.
    /// </summary>
    public static void MapDrives(string filesDir, string gamePath)
    {
        var dosdevices = Path.Combine(Prefix(filesDir), "dosdevices");
        Directory.CreateDirectory(dosdevices);
        Map(Path.Combine(dosdevices, "c:"), "../drive_c");
        Map(Path.Combine(dosdevices, "z:"), "/");
        Map(Path.Combine(dosdevices, "a:"), gamePath);

        static void Map(string link, string target)
        {
            try
            {
                if (new FileInfo(link).LinkTarget == target)
                    return;
                File.Delete(link); // no-op when nothing is there
                Directory.CreateSymbolicLink(link, target);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"XlaProvision: could not map {Path.GetFileName(link)} -> {target}"
                                  + $" ({ex.GetType().Name})");
            }
        }
    }

    /// <summary>
    /// Drops the previous graphics driver before the new package unpacks, so a stale <c>.so</c> cannot
    /// linger beside the new one and be picked up by name.
    /// </summary>
    public static void ClearBundledDriver(string filesDir)
    {
        var turnip = Path.Combine(filesDir, "rootfs", "usr", "lib", "turnip");
        try
        {
            if (Directory.Exists(turnip))
                Directory.Delete(turnip, true);
        }
        catch (Exception)
        {
            // The new package overwrites what matters anyway.
        }
    }

    /// <summary>
    /// Whether the runtime looks usable. Deliberately checks the things that actually break a launch
    /// rather than merely that directories exist.
    /// </summary>
    /// <summary>
    /// Something wrong with an installed runtime, and which package can fix it.
    ///
    /// <paramref name="Component"/> is null when no package is at fault: the repair is one of the steps
    /// <c>RuntimeInstaller</c> runs at the end of every install anyway (writing run-wine.sh, chmod-ing
    /// the binaries, mapping the drives). Attribution is what lets a damaged runtime be repaired by
    /// redownloading only what is broken - a missing DXVK dll costs the 44 MB overlay, rather than
    /// replacing the prefix and discarding the player's in-prefix registry with it.
    /// </summary>
    public readonly record struct VerifyProblem(string Message, string? Component)
    {
        public override string ToString() => Message;
    }

    public static IReadOnlyList<VerifyProblem> Verify(string filesDir)
    {
        var problems = new List<VerifyProblem>();
        void Problem(string message, string? component) => problems.Add(new VerifyProblem(message, component));

        var wine = Path.Combine(filesDir, "wine", "bin", "wine");
        if (!File.Exists(wine))
            Problem("wine/bin/wine is missing", "wine");
        else
        {
            try
            {
                if ((File.GetUnixFileMode(wine) & UnixFileMode.UserExecute) == 0)
                    // MakeExecutable runs at the end of every install, so no package is at fault.
                    Problem("wine/bin/wine is not executable", null);
            }
            catch (Exception) { /* mode unreadable; the launch will say */ }
        }

        // Content that ONLY the prefix package provides. Checked explicitly because the links and the
        // overlay recreate the directories around these, so their absence is otherwise invisible.
        if (!File.Exists(Path.Combine(Prefix(filesDir), "system.reg")))
            Problem("the prefix has no system.reg, so the prefix package did not unpack where expected", "prefix");
        foreach (var fex in new[] { "libarm64ecfex.dll", "libwow64fex.dll" })
        {
            var info = new FileInfo(Path.Combine(DriveC(filesDir), "windows", "system32", fex));
            if (!info.Exists)
                Problem($"system32/{fex} is missing, so FEXCore cannot emulate the game (HODLL)", "prefix");
            else if (info.LinkTarget != null)
                Problem($"system32/{fex} is a link to a Wine builtin rather than the FEXCore build", "prefix");
        }

        var system32 = Path.Combine(DriveC(filesDir), "windows", "system32");
        if (!File.Exists(Path.Combine(system32, "kernel32.dll")))
            // LinkBuiltins runs after the wine package, so that is what has to be redone.
            Problem("the Wine builtins are not linked into system32 (the game would fail with c0000135)", "wine");
        // The overlay must have won over the builtin link, or DirectX goes through Wine's own stubs.
        var d3d11 = new FileInfo(Path.Combine(system32, "d3d11.dll"));
        if (!d3d11.Exists)
            Problem("system32/d3d11.dll is missing", "overlay");
        else if (d3d11.LinkTarget != null)
            Problem("system32/d3d11.dll is still a link to Wine's builtin, so DXVK was not applied", "overlay");

        foreach (var (required, owner) in new[]
                 {
                     (Path.Combine(filesDir, "rootfs", "usr", "lib", "libandroid-sysvshm.so"), "rootfs"),
                     (Path.Combine(filesDir, "rootfs", "usr", "share", "vulkan", "icd.d", "turnip_icd.aarch64.json"), "vkextra"),
                     (Path.Combine(filesDir, "rootfs", "usr", "lib", "turnip", "driver-name"), "vkextra"),
                     // Written from the APK's own asset on every install, so no package owns it.
                     (Path.Combine(filesDir, "run-wine.sh"), null),
                 })
        {
            if (!File.Exists(required))
                Problem($"{Path.GetRelativePath(filesDir, required)} is missing", owner);
        }

        var dosdevices = Path.Combine(Prefix(filesDir), "dosdevices");
        foreach (var drive in new[] { "c:", "z:", "a:" })
        {
            if (new FileInfo(Path.Combine(dosdevices, drive)).LinkTarget == null)
                // MapDrives runs at the end of every install and recreates all three.
                Problem($"the prefix has no {drive} mapping", null);
        }
        return problems;
    }

    /// <summary>
    /// The DLL names the overlay is expected to own. Exposed so a diagnostic can report which of them
    /// are still Wine builtins; the extractor replaces links in place, so no deletion pass is needed.
    /// </summary>
    public static IEnumerable<string> OverlayOwnedDlls(string filesDir)
    {
        foreach (var directory in new[] { "system32", "syswow64" })
        foreach (var dll in WrappedDlls)
            yield return Path.Combine(DriveC(filesDir), "windows", directory, dll + ".dll");
    }
}
