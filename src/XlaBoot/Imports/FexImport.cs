using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace XlaBoot.Imports;

/// <summary>
/// FEXCore builds imported from a Winlator WCP Hub or GameNative .wcp (or any archive holding the two DLLs).
/// Only the Windows halves are swapped: libarm64ecfex.dll (64-bit game code) and libwow64fex.dll (32-bit),
/// both in system32. The unix loaders Proton loads them through stay the runtime's own; newer packages no
/// longer ship them at all.
/// </summary>
public static class FexImport
{
    public static readonly string[] Dlls = { "libarm64ecfex.dll", "libwow64fex.dll" };

    public static ComponentStore Store(string filesDir) => new(filesDir, "fex-import");

    /// <summary>Imports the archive and selects it. Returns the version name. Throws with a message for the user.</summary>
    public static string Import(string archivePath, string? displayName, string filesDir)
    {
        // One pass: profile.json usually comes first, but nothing guarantees it, so read everything we need.
        var dlls = Dlls.ToDictionary(d => d, _ => (byte[]?)null, StringComparer.OrdinalIgnoreCase);
        string? type = null, versionName = null;
        int versionCode = 0;
        ComponentArchive.ForEachFile(archivePath, (name, data) =>
        {
            var file = name[(name.LastIndexOf('/') + 1)..];
            if (name.Equals("profile.json", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                type = Str(root, "type");
                versionName = Str(root, "versionName");
                if (root.TryGetProperty("versionCode", out var code) && code.ValueKind == JsonValueKind.Number)
                    versionCode = code.GetInt32();
            }
            else if (dlls.ContainsKey(file) && dlls[file] == null)
            {
                using var buffer = new MemoryStream();
                data.CopyTo(buffer);
                dlls[file] = buffer.ToArray();
            }
        });

        if (type != null && !type.Equals("FEXCore", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"That is a {type} package, not FEXCore.");
        var missing = dlls.Where(d => d.Value == null || d.Value.Length < 2 || d.Value[0] != 'M' || d.Value[1] != 'Z')
            .Select(d => d.Key).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException("No FEXCore build inside: " + string.Join(" and ", missing) + " missing.");

        var store = Store(filesDir);
        var version = FreeName(store, ComponentStore.CheckName(VersionName(versionName, versionCode, displayName)), dlls!);
        return store.Install(version, dir =>
        {
            foreach (var (name, bytes) in dlls)
                File.WriteAllBytes(Path.Combine(dir, name), bytes!);
        });
    }

    /// <summary>
    /// "2611" from the WCP Hub, "2611-gn-6" for GameNative's 2611-gn build 6 (the same as its file name), or the
    /// picked file's name when the package has no profile.json.
    /// </summary>
    private static string VersionName(string? versionName, int versionCode, string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(versionName))
            return versionCode > 0 ? $"{versionName.Trim()}-{versionCode}" : versionName.Trim();
        return ComponentStore.NameFromFile(displayName, "FEXCore-");
    }

    /// <summary>
    /// Builds from different sources can share a version name (the WCP Hub's and GameNative's 2609 differ).
    /// Re-importing the same build keeps its name; a different build under a taken name becomes "2609-2".
    /// </summary>
    private static string FreeName(ComponentStore store, string version, IReadOnlyDictionary<string, byte[]?> dlls)
    {
        for (int n = 1; ; n++)
        {
            var candidate = n == 1 ? version : $"{version}-{n}";
            var dir = Path.Combine(store.Root, candidate);
            if (!Directory.Exists(dir))
                return candidate;
            if (dlls.All(d => File.Exists(Path.Combine(dir, d.Key))
                              && File.ReadAllBytes(Path.Combine(dir, d.Key)).AsSpan().SequenceEqual(d.Value)))
                return candidate;
        }
    }

    private static string? Str(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
