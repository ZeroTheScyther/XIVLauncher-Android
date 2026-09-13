using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace XlaBoot.Provisioning;

/// <summary>
/// Two one-time, idempotent fixes to the prefix's system32, worked out against the GameNative recipe
/// (GUIDE.md #5 and #6) and reused here since this port's prefix is the same layout:
///
/// 1. Wine's builtin icu.dll forwards to icuuc68.dll, which Wine does not ship, so every .NET app in
///    the prefix - including Dalamud.Injector - fails to load ICU (Injector exit code 3).
/// 2. Dalamud's "needs the VC++ 2015-2019 redistributable" check wants ucrtbase_clr0400.dll and
///    vcruntime140_clr0400.dll, which only ship with a real .NET Framework install. The prefix already
///    has the real DLLs under their normal names; only the _clr0400 aliases are missing.
///
/// Both are applied lazily, right before a Dalamud-enabled launch, rather than during provisioning:
/// PrefixSetup.LinkBuiltins (which the aliasing depends on) can run again after this and would not
/// undo it, but doing it here means a plain (non-Dalamud) launch never pays for it.
/// </summary>
public static class DalamudPrefixFix
{
    // The last ICU4C build whose exports match what Wine's bundled icu.dll forwards to (icuuc68,
    // icuin68, icudt68). A newer version forwards to different symbol names and will not resolve -
    // see GUIDE.md #5's objdump check.
    private const string IcuNupkgUrl =
        "https://www.nuget.org/api/v2/package/Microsoft.ICU.ICU4C.Runtime.win-x64/68.2.0.9";

    private static readonly string[] IcuDlls = { "icuuc68.dll", "icuin68.dll", "icudt68.dll" };

    private static string System32(string filesDir) =>
        Path.Combine(filesDir, "prefix", ".wine", "drive_c", "windows", "system32");

    public static async Task EnsureAsync(string filesDir, HttpClient http)
    {
        var system32 = System32(filesDir);
        Directory.CreateDirectory(system32);

        Alias(system32, "ucrtbase.dll", "ucrtbase_clr0400.dll");
        Alias(system32, "vcruntime140.dll", "vcruntime140_clr0400.dll");

        if (IcuDlls.All(dll => File.Exists(Path.Combine(system32, dll))))
            return;

        Console.WriteLine("XlaLauncher: fetching ICU 68 for Dalamud (one-time, ~3 MB)...");
        byte[] nupkg;
        try
        {
            nupkg = await http.GetByteArrayAsync(IcuNupkgUrl).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"XlaLauncher: could not fetch ICU 68, Dalamud will likely fail to load ({ex.GetType().Name}: {ex.Message})");
            return;
        }

        try
        {
            using var zip = new ZipArchive(new MemoryStream(nupkg), ZipArchiveMode.Read);
            foreach (var name in IcuDlls)
            {
                // Matched by filename rather than a hardcoded internal path: the nupkg's own layout
                // (runtimes/win-x64/native/...) is not part of the public contract.
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    Console.WriteLine($"XlaLauncher: ICU package did not contain {name}");
                    continue;
                }
                await using var src = entry.Open();
                await using var dst = File.Create(Path.Combine(system32, name));
                await src.CopyToAsync(dst).ConfigureAwait(false);
            }
        }
        catch (InvalidDataException ex)
        {
            Console.WriteLine($"XlaLauncher: ICU package was not a valid zip ({ex.Message})");
        }
    }

    /// <summary>
    /// Copies (not links) the real DLL under an alias name: a legitimate alias of the real runtime, not
    /// a stub, matching what a genuine .NET Framework install would place there.
    /// </summary>
    private static void Alias(string system32, string source, string alias)
    {
        var target = Path.Combine(system32, alias);
        if (File.Exists(target))
            return;
        var origin = Path.Combine(system32, source);
        if (!File.Exists(origin))
            return; // Not linked in yet (PrefixSetup.LinkBuiltins runs first); the next launch retries.
        try
        {
            File.Copy(origin, target);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"XlaLauncher: could not alias {source} -> {alias} ({ex.GetType().Name})");
        }
    }
}
