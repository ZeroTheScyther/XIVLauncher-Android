using System;
using System.IO;
using System.Linq;

namespace XlaBoot.Imports;

/// <summary>
/// The imported versions of one runtime component, kept beside the runtime under files/&lt;folder&gt;/:
/// one folder per version, selected.txt naming the one the next launch uses ("stock" = the runtime's own),
/// and stock-backup/, which run-wine.sh owns. The C# side only adds, selects and removes versions; run-wine.sh
/// does the copying into the prefix at launch, so nothing changes while the game is running.
/// </summary>
public sealed class ComponentStore(string filesDir, string folder)
{
    public const string Stock = "stock";
    private const string Backup = "stock-backup";
    private const string SelectedName = "selected.txt";
    private const string Staging = ".incoming";

    public string Root { get; } = Path.Combine(filesDir, folder);
    private string SelectedFile => Path.Combine(Root, SelectedName);

    /// <summary>The imported versions, by folder name.</summary>
    public string[] Versions()
    {
        if (!Directory.Exists(Root))
            return Array.Empty<string>();
        return Directory.GetDirectories(Root)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(v => v != Backup && !v.StartsWith('.'))
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The version the next launch uses, or "stock" when none is selected or it is gone.</summary>
    public string Selected()
    {
        try
        {
            var v = File.ReadAllText(SelectedFile).Trim();
            return Versions().Contains(v) ? v : Stock;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Stock;
        }
    }

    public void Select(string version)
    {
        if (version != Stock && !Versions().Contains(version))
            throw new ArgumentException($"{version} is not installed.", nameof(version));
        Directory.CreateDirectory(Root);
        File.WriteAllText(SelectedFile, version);
    }

    /// <summary>Deletes an imported version; if it was the selected one, the next launch goes back to stock.</summary>
    public void Remove(string version)
    {
        if (!Versions().Contains(version))
            return;
        if (Selected() == version)
            Select(Stock);
        Directory.Delete(Path.Combine(Root, version), true);
    }

    /// <summary>
    /// Drops run-wine.sh's copy of the prefix's stock files. Called after a runtime update reinstalls them, since
    /// the copy then holds the previous stock. With an imported build still selected, the next launch backs up
    /// the new stock files before copying the import over them.
    /// </summary>
    public void ForgetStockBackup()
    {
        var backup = Path.Combine(Root, Backup);
        try
        {
            if (Directory.Exists(backup))
                Directory.Delete(backup, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"XlaLauncher: could not clear the stock backup in {Root}: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Installs a version: <paramref name="fill"/> writes its files into an empty staging folder, which then
    /// replaces any existing copy of <paramref name="version"/> and becomes the selection. A failure in
    /// <paramref name="fill"/> leaves the installed versions untouched.
    /// </summary>
    public string Install(string version, Action<string> fill)
    {
        version = CheckName(version);
        var dest = Path.GetFullPath(Path.Combine(Root, version));
        if (!dest.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("The version name points outside the import folder.");

        var staging = Path.Combine(Root, Staging);
        if (Directory.Exists(staging))
            Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        try
        {
            fill(staging);
        }
        catch
        {
            Directory.Delete(staging, true);
            throw;
        }
        if (Directory.Exists(dest))
            Directory.Delete(dest, true);
        Directory.Move(staging, dest);
        Select(version);
        return version;
    }

    /// <summary>
    /// A version name from the picked file's name: without the archive extension and the component prefix
    /// ("FEXCore-2611.wcp" -> "2611"), or "import" when nothing is left.
    /// </summary>
    public static string NameFromFile(string? displayName, string prefix)
    {
        var name = Path.GetFileName(displayName ?? "");
        foreach (var ext in new[] { ".wcp", ".tzst", ".zst", ".xz", ".gz", ".zip", ".tar" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                name = name[..^ext.Length];
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            name = name[prefix.Length..];
        return name.Length > 0 ? name : "import";
    }

    /// <summary>A version name becomes a folder and a line in selected.txt: one plain segment, not ours.</summary>
    public static string CheckName(string version)
    {
        version = version.Trim();
        if (version.Length == 0 || version.StartsWith('.') || version.IndexOfAny(['/', '\\', '\n', '\r', '\0']) >= 0
            || version is Stock or Backup or SelectedName)
            throw new InvalidDataException($"\"{version}\" can't be used as a version name.");
        return version;
    }
}
