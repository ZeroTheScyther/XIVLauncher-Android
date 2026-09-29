using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace XlaBoot;

/// <summary>
/// Imports a zip of Dalamud's pluginConfigs folder copied from a PC, so a player's plugins come up configured.
///
/// Dalamud keeps plugin settings next to its config file: files/xlroaming/pluginConfigs, one &lt;Plugin&gt;.json
/// and/or a &lt;Plugin&gt; folder per plugin. The zip may hold that folder's contents at its root, or the folder
/// itself (what zipping "pluginConfigs" on a PC gives). Existing files are overwritten; nothing else is removed.
/// Only call this while the game is not running: Dalamud writes these files back as plugins save.
/// </summary>
public static class PluginConfigs
{
    private const string FolderName = "pluginConfigs";

    public static string Directory(string filesDir) => Path.Combine(filesDir, "xlroaming", FolderName);

    /// <returns>How many plugins had settings in the zip.</returns>
    public static int Import(string zipPath, string filesDir)
    {
        var target = Path.GetFullPath(Directory(filesDir));
        ZipArchive archive;
        try { archive = ZipFile.OpenRead(zipPath); }
        catch (InvalidDataException) { throw new InvalidDataException("That file isn't a zip."); }
        using var _ = archive;

        var entries = archive.Entries
            .Select(e => (Entry: e, Path: e.FullName.Replace('\\', '/').TrimStart('/')))
            .Where(e => e.Path.Length > 0)
            .ToList();

        // A wrapping pluginConfigs/ folder is stripped; anything else is taken as the folder's contents.
        var tops = entries.Select(e => e.Path.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (tops.Count == 1 && tops[0].Equals(FolderName, StringComparison.OrdinalIgnoreCase)
            && entries.All(e => e.Path.Contains('/')))
            entries = entries
                .Select(e => (e.Entry, Path: e.Path[(e.Path.IndexOf('/') + 1)..]))
                .Where(e => e.Path.Length > 0)
                .ToList();

        var files = entries.Where(e => !e.Path.EndsWith('/')).ToList();
        if (!files.Any(e => !e.Path.Contains('/') && e.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("That zip has no plugin configs in it.");

        var plugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        System.IO.Directory.CreateDirectory(target);
        foreach (var (entry, path) in files)
        {
            var destination = Path.GetFullPath(Path.Combine(target, path));
            if (!destination.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue; // "../" or an absolute path: never written outside the folder

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temp = destination + ".importing";
            entry.ExtractToFile(temp, overwrite: true);
            File.Move(temp, destination, overwrite: true);

            // A plugin is its folder or its <Name>.json; backups and other loose files are copied but not counted.
            if (path.Contains('/'))
                plugins.Add(path.Split('/')[0]);
            else if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                plugins.Add(path[..^".json".Length]);
        }
        return plugins.Count;
    }
}
