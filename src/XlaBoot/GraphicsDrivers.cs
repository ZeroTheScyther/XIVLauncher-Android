using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XlaBoot;

/// <summary>One installed Vulkan driver package.</summary>
/// <param name="Id">"" for the one shipped in the rootfs, otherwise its folder name under files/drivers.</param>
/// <param name="Directory">Absolute path handed to adrenotools as ADRENOTOOLS_DRIVER_PATH.</param>
public sealed record GraphicsDriver(string Id, string Label, string Detail, string Directory)
{
    /// <summary>The bundled driver is part of the runtime and cannot be deleted from the launcher.</summary>
    public bool IsBundled => Id.Length == 0;
}

/// <summary>
/// The Vulkan drivers the game can run on. Wine's guest-side Vulkan goes through Mesa's wrapper ICD,
/// which uses adrenotools to load a driver .so of our choosing instead of the stock Adreno blob —
/// that indirection is the whole reason a different Turnip build can fix a device that renders
/// wrongly, or make one work at all.
///
/// A driver package is the AdrenoTools layout: meta.json naming a libraryName, that one vulkan.*.so,
/// a driver-name file holding the library name (what run-wine.sh reads) and a writable temp/ scratch
/// directory. The bundled one lives in the rootfs; imported ones get a folder each under
/// files/drivers, which survives an app upgrade.
/// </summary>
public static class GraphicsDrivers
{
    /// <summary>Set by the launcher settings screen; read by run-wine.sh through XLA_DRIVER_DIR.</summary>
    private const string SelectedKey = "driver_dir";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string BundledDirectory => Path.Combine(AppHost.FilesDir, "rootfs", "usr", "lib", "turnip");

    private static string ImportRoot => Path.Combine(AppHost.FilesDir, "drivers");

    /// <summary>The bundled driver first, then every imported one, oldest first.</summary>
    public static List<GraphicsDriver> All()
    {
        var drivers = new List<GraphicsDriver> { Describe("", BundledDirectory, "Bundled Turnip") };
        try
        {
            foreach (var directory in Directory.GetDirectories(ImportRoot).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (!File.Exists(Path.Combine(directory, "driver-name")))
                    continue;
                drivers.Add(Describe(Path.GetFileName(directory), directory, Path.GetFileName(directory)));
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing imported yet.
        }
        return drivers;
    }

    /// <summary>The driver the next launch will use. Falls back to the bundled one if the selection is gone.</summary>
    public static GraphicsDriver Selected(List<GraphicsDriver> drivers)
    {
        var directory = AppHost.Get(SelectedKey, "");
        return drivers.FirstOrDefault(d => d.Directory == directory) ?? drivers[0];
    }

    public static void Select(GraphicsDriver driver) =>
        AppHost.Set(SelectedKey, driver.IsBundled ? "" : driver.Directory);

    /// <summary>
    /// Unpacks an AdrenoTools .zip into its own folder under files/drivers. Throws with a message
    /// meant for the user when the archive is not one.
    /// </summary>
    public static GraphicsDriver Import(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        // meta.json may sit at the root or one folder down, depending on how the package was zipped.
        var metaEntry = archive.Entries.FirstOrDefault(e =>
                            Path.GetFileName(e.FullName).Equals("meta.json", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("No meta.json inside, so this is not an AdrenoTools driver package.");
        var prefix = metaEntry.FullName[..^Path.GetFileName(metaEntry.FullName).Length];

        DriverMeta? meta;
        using (var stream = metaEntry.Open())
            meta = JsonSerializer.Deserialize<DriverMeta>(stream, JsonOptions);
        var library = meta?.LibraryName;
        if (meta == null || string.IsNullOrWhiteSpace(library))
            throw new InvalidOperationException("The package's meta.json does not name a libraryName.");
        if (archive.GetEntry(prefix + library) == null)
            throw new InvalidOperationException($"meta.json names {library}, but the package does not contain it.");

        var directory = Path.Combine(ImportRoot, Slug(meta, library));
        if (Directory.Exists(directory))
            Directory.Delete(directory, true); // re-importing the same driver replaces it
        Directory.CreateDirectory(directory);

        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var relative = entry.FullName[prefix.Length..];
            if (relative.Length == 0 || relative.EndsWith('/'))
                continue;
            // Never let an entry name write outside its own folder.
            var target = Path.GetFullPath(Path.Combine(directory, relative));
            if (!target.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }

        // run-wine.sh reads the library name from here (a shell builtin read, no subprocess), and
        // adrenotools wants a writable scratch directory inside the driver path.
        File.WriteAllText(Path.Combine(directory, "driver-name"), library);
        Directory.CreateDirectory(Path.Combine(directory, "temp"));

        return Describe(Path.GetFileName(directory), directory, Path.GetFileName(directory));
    }

    public static void Remove(GraphicsDriver driver)
    {
        if (driver.IsBundled)
            return;
        Directory.Delete(driver.Directory, true);
        if (AppHost.Get(SelectedKey, "") == driver.Directory)
            AppHost.Set(SelectedKey, "");
    }

    /// <summary>
    /// The package's own name for the row, its description and driver version underneath. Falls back
    /// to the folder name, because driver-name is all that is actually needed to run a package.
    /// </summary>
    private static GraphicsDriver Describe(string id, string directory, string fallbackLabel)
    {
        var label = fallbackLabel;
        var parts = new List<string>();
        try
        {
            var meta = JsonSerializer.Deserialize<DriverMeta>(File.ReadAllText(Path.Combine(directory, "meta.json")), JsonOptions);
            if (meta != null)
            {
                if (!string.IsNullOrWhiteSpace(meta.Name))
                    label = meta.Name!;
                if (!string.IsNullOrWhiteSpace(meta.Description))
                    parts.Add(meta.Description!);
                if (!string.IsNullOrWhiteSpace(meta.DriverVersion))
                    parts.Add(meta.DriverVersion!);
            }
        }
        catch (Exception)
        {
            // No or unreadable meta.json: the folder name and driver-name are enough to run it.
        }
        parts.Add(id.Length == 0 ? "shipped with the app" : "imported");
        return new GraphicsDriver(id, label, string.Join(" - ", parts), directory);
    }

    /// <summary>A folder name from the package's own name, so a re-import lands on the same folder.</summary>
    private static string Slug(DriverMeta meta, string library)
    {
        var source = string.Join('-', (meta.Name ?? Path.GetFileNameWithoutExtension(library)), meta.DriverVersion ?? "");
        var slug = new string(source.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray())
            .Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Length == 0 ? "driver" : slug;
    }

    /// <summary>The AdrenoTools meta.json. Only these four fields matter to us.</summary>
    private sealed class DriverMeta
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("driverVersion")] public string? DriverVersion { get; set; }
        [JsonPropertyName("libraryName")] public string? LibraryName { get; set; }
    }
}
