using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XlaBoot.Provisioning;

/// <summary>
/// Installs XIVLauncher Android Helper, the small Dalamud plugin that shows the device's battery in game.
///
/// The plugin lives in its own repository and ships on its own schedule: Dalamud's plugin API changes with game
/// patches far more often than this app does, and an API bump must not require a new APK. So the app adds the
/// plugin's Dalamud repository to dalamudConfig.json and installs the current build itself, after which Dalamud
/// updates it like any other third-party plugin.
///
/// Nothing here is allowed to stop a launch. No network, a rate-limited GitHub or a broken zip all end the same
/// way: a line in the launcher log, and the game starts without it.
/// </summary>
public static class HelperPlugin
{
    /// <summary>Dalamud's third-party repository list points here; the file is written by the plugin's release job.</summary>
    public const string RepositoryUrl =
        "https://raw.githubusercontent.com/ZeroTheScyther/XIVLauncher-Android-Helper/main/repo.json";

    /// <summary>The plugin's InternalName, which is also its folder under installedPlugins.</summary>
    public const string InternalName = "XlaAndroidHelper";

    /// <summary>What a Dalamud repository says about one plugin. Only the parts needed to install it.</summary>
    public sealed record Release(string AssemblyVersion, Uri Download);

    public static string PluginDirectory(string filesDir) =>
        Path.Combine(filesDir, "xlroaming", "installedPlugins", InternalName);

    /// <summary>
    /// Makes sure the repository is listed and the current build is installed. Returns what it did, for the log.
    /// </summary>
    public static async Task<string> EnsureAsync(string filesDir, HttpClient http, CancellationToken cancel)
    {
        DalamudRepos.Add(DalamudRepos.ConfigPath(filesDir), RepositoryUrl);

        var json = await http.GetStringAsync(RepositoryUrl, cancel).ConfigureAwait(false);
        var release = Parse(json) ?? throw new InvalidDataException("the plugin repository lists no build");

        var target = Path.Combine(PluginDirectory(filesDir), release.AssemblyVersion);
        if (File.Exists(Path.Combine(target, InternalName + ".dll")))
            return $"helper plugin {release.AssemblyVersion} already installed";

        var zip = await http.GetByteArrayAsync(release.Download, cancel).ConfigureAwait(false);
        Install(filesDir, release, zip);
        return $"installed helper plugin {release.AssemblyVersion}";
    }

    /// <summary>Reads the first plugin entry out of a Dalamud repository file.</summary>
    public static Release? Parse(string repositoryJson)
    {
        using var document = JsonDocument.Parse(repositoryJson);
        var entries = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray()
            : default;
        foreach (var entry in entries)
        {
            if (entry.TryGetProperty("InternalName", out var name) && name.GetString() != InternalName)
                continue;
            if (!entry.TryGetProperty("AssemblyVersion", out var version)
                || !entry.TryGetProperty("DownloadLinkInstall", out var link)
                || version.GetString() is not { Length: > 0 } assemblyVersion
                || !Uri.TryCreate(link.GetString(), UriKind.Absolute, out var download)
                || download.Scheme != Uri.UriSchemeHttps)
                continue;
            return new Release(assemblyVersion, download);
        }
        return null;
    }

    /// <summary>
    /// Unpacks the plugin into installedPlugins/&lt;InternalName&gt;/&lt;version&gt;, the layout Dalamud scans, and drops
    /// any older build so the plugin list does not grow a copy per update.
    /// </summary>
    public static void Install(string filesDir, Release release, byte[] zip)
    {
        var root = PluginDirectory(filesDir);
        var target = Path.Combine(root, release.AssemblyVersion);
        var staging = target + ".part";

        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        try
        {
            using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    // Flat archive by Dalamud's convention; anything with a path is not ours to write.
                    if (entry.FullName.Contains('/') || entry.FullName.Contains('\\') || entry.Name.Length == 0)
                        continue;
                    entry.ExtractToFile(Path.Combine(staging, entry.Name), overwrite: true);
                }
            }
            if (!File.Exists(Path.Combine(staging, InternalName + ".dll")))
                throw new InvalidDataException("the downloaded plugin has no assembly in it");

            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }

        foreach (var old in Directory.GetDirectories(root).Where(d => !string.Equals(
                     Path.GetFileName(d), release.AssemblyVersion, StringComparison.Ordinal)))
        {
            try { Directory.Delete(old, recursive: true); }
            catch (IOException) { /* in use by a running game; Dalamud ignores the spare copy */ }
        }
    }

    /// <summary>Removes the plugin, for when the setting is turned off. Leaves the repository entry alone.</summary>
    public static void Remove(string filesDir)
    {
        var root = PluginDirectory(filesDir);
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
