using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace XlaBoot.Imports;

/// <summary>
/// DXVK builds imported from release archives (.tar.gz/.zip with x32/ and x64/) or Winlator WCP Hub .wcp
/// packages (system32/ and syswow64/, including the ARM64EC builds). The DLLs land in x64/ (64-bit, copied to
/// system32) and x32/ (32-bit, syswow64); run-wine.sh does the copying at launch.
/// </summary>
public static class DxvkImport
{
    /// <summary>
    /// DXVK 2.4.1-gplasync built as ARM64EC (scripts/build-dxvk-arm64ec.sh), installed by the runtime's dxvkec
    /// component into dxvk-import/ and selected by default. The overlay's x86-64 build stays as "stock".
    /// </summary>
    public const string BuiltInArm64Ec = "2.4.1-gplasync-arm64ec";

    public static ComponentStore Store(string filesDir) => new(filesDir, "dxvk-import", BuiltInArm64Ec);

    /// <summary>Imports the archive and selects it. Returns the version name. Throws with a message for the user.</summary>
    public static string Import(string archivePath, string? displayName, string filesDir)
    {
        // Pass 1: the name. profile.json can come after the DLLs, so look at everything first.
        string? profileVersion = null, profileType = null, folderVersion = null;
        ComponentArchive.ForEachFile(archivePath, (name, data) =>
        {
            if (name.Equals("profile.json", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(data);
                profileType = Str(doc.RootElement, "type");
                profileVersion = Str(doc.RootElement, "versionName");
            }
            else
            {
                folderVersion ??= VersionFromPath(name);
            }
        });
        if (profileType != null && !profileType.Equals("DXVK", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"That is a {profileType} package, not DXVK.");

        var version = !string.IsNullOrWhiteSpace(profileVersion) ? profileVersion.Trim()
                      : folderVersion ?? ComponentStore.NameFromFile(displayName, "dxvk-");
        if (version.StartsWith("dxvk-", StringComparison.OrdinalIgnoreCase))
            version = version["dxvk-".Length..];

        // Pass 2: the DLLs.
        return Store(filesDir).Install(version, staging =>
        {
            int extracted = 0;
            ComponentArchive.ForEachFile(archivePath, (name, data) =>
            {
                var parts = name.Split('/');
                var arch = parts.Length >= 2 ? ArchFolder(parts[^2]) : null;
                if (arch == null || !parts[^1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    return;
                var dir = Path.Combine(staging, arch);
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, parts[^1]);
                using (var file = File.Create(path))
                    data.CopyTo(file);
                if (new FileInfo(path).Length == 0)
                    File.Delete(path);
                else
                    extracted++;
            });
            if (extracted == 0)
                throw new InvalidDataException("No DLLs under x32/x64 or system32/syswow64, so this is not a DXVK build.");
        });
    }

    /// <summary>
    /// True when the next launch uses an ARM64EC dxgi.dll. Dalamud's default swapchain hook patches Present's first
    /// instructions as x86-64, which fails on ARM64EC code; see DalamudHookMode.
    /// </summary>
    public static bool SelectedIsArm64Ec(string filesDir)
    {
        var store = Store(filesDir);
        var version = store.Selected();
        return version != ComponentStore.Stock && IsArm64Ec(Path.Combine(store.Root, version, "x64", "dxgi.dll"));
    }

    /// <summary>
    /// An ARM64EC image keeps the AMD64 machine field so x64 code can load it. What marks it is a CHPE metadata
    /// pointer in the load config (offset 200 of the 64-bit layout). A plain x64 build has none, or no load config.
    /// </summary>
    public static bool IsArm64Ec(string dllPath)
    {
        try
        {
            var b = File.ReadAllBytes(dllPath);
            int pe = BitConverter.ToInt32(b, 0x3c);
            if (BitConverter.ToUInt16(b, pe + 4) != 0x8664)
                return false;
            int sections = BitConverter.ToUInt16(b, pe + 6);
            int optional = pe + 24;
            if (BitConverter.ToUInt16(b, optional) != 0x20b)
                return false;
            const int loadConfigEntry = 10;
            uint rva = BitConverter.ToUInt32(b, optional + 112 + loadConfigEntry * 8);
            if (rva == 0)
                return false;
            int table = optional + BitConverter.ToUInt16(b, pe + 20);
            for (int i = 0; i < sections; i++)
            {
                int s = table + i * 40;
                uint virtualSize = BitConverter.ToUInt32(b, s + 8);
                uint virtualAddress = BitConverter.ToUInt32(b, s + 12);
                uint rawSize = BitConverter.ToUInt32(b, s + 16);
                uint rawPointer = BitConverter.ToUInt32(b, s + 20);
                if (rva < virtualAddress || rva >= virtualAddress + Math.Max(virtualSize, rawSize))
                    continue;
                long config = rva - virtualAddress + rawPointer;
                const int chpeOffset = 200;
                return BitConverter.ToUInt32(b, (int)config) >= chpeOffset + 8
                       && BitConverter.ToUInt64(b, (int)config + chpeOffset) != 0;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>x64 for x64/ or system32/, x32 for x32/ or syswow64/, else null.</summary>
    private static string? ArchFolder(string dir) => dir.ToLowerInvariant() switch
    {
        "x64" or "system32" => "x64",
        "x32" or "syswow64" => "x32",
        _ => null,
    };

    /// <summary>
    /// The folder just above x32/ or x64/ ("dxvk-2.7.1/x64/d3d11.dll" -> "dxvk-2.7.1"). Null for a flat archive
    /// (x64/ at the root), whose name then comes from the file name, and for an entry outside x32/x64.
    /// </summary>
    private static string? VersionFromPath(string fullName)
    {
        var parts = fullName.Split('/');
        int idx = Array.FindLastIndex(parts, p => p == "x32" || p == "x64");
        return idx <= 0 || idx == parts.Length - 1 ? null : parts[idx - 1];
    }

    private static string? Str(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
