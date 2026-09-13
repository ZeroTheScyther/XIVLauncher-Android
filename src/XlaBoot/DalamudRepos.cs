using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XlaBoot;

public enum AddRepoResult { Added, Duplicate, Invalid }

/// <summary>
/// Dalamud's custom plugin repos, edited from the launcher instead of Dalamud's tiny in-game settings window.
///
/// Dalamud keeps them in dalamudConfig.json (Newtonsoft, a "$type" on every object) as ThirdRepoList.$values, a list
/// of { Url, IsEnabled }. Only that list is touched; every other key round-trips through JsonNode unchanged. Dalamud
/// rewrites the whole file while running and on exit, so every call here reads the file fresh and writes it straight
/// back - nothing is cached between taps.
/// </summary>
public static class DalamudRepos
{
    private const string ConfigType = "Dalamud.Configuration.Internal.DalamudConfiguration, Dalamud";
    private const string ListType =
        "System.Collections.Generic.List`1[[Dalamud.Configuration.ThirdPartyRepoSettings, Dalamud]], System.Private.CoreLib";
    private const string RepoType = "Dalamud.Configuration.ThirdPartyRepoSettings, Dalamud";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Dalamud's own file has plain "+", "<" and non-ASCII; the default encoder would \u-escape them.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The config directory MainViewModel hands Dalamud is files/xlroaming.</summary>
    public static string ConfigPath(string filesDir) => Path.Combine(filesDir, "xlroaming", "dalamudConfig.json");

    public static IReadOnlyList<(string Url, bool IsEnabled)> List(string path)
    {
        if (!File.Exists(path))
            return Array.Empty<(string, bool)>();
        return Entries(Load(path)).ToList();
    }

    public static AddRepoResult Add(string path, string url)
    {
        url = url.Trim();
        if (!IsWebLink(url))
            return AddRepoResult.Invalid;
        var config = LoadOrNew(path);
        if (Entries(config).Any(e => Key(e.Url) == Key(url)))
            return AddRepoResult.Duplicate;
        Values(config).Add(NewEntry(url, true));
        Save(path, config);
        return AddRepoResult.Added;
    }

    /// <returns>False when the repo was not in the list (already removed, or Dalamud rewrote the file).</returns>
    public static bool Remove(string path, string url)
    {
        if (!File.Exists(path))
            return false;
        var config = Load(path);
        var values = Values(config);
        var match = values.FirstOrDefault(v => v is JsonObject o && Key(UrlOf(o)) == Key(url));
        if (match == null)
            return false;
        values.Remove(match);
        Save(path, config);
        return true;
    }

    /// <summary>Adds every repo in another dalamudConfig.json (typically copied from a PC) that is not already here.</summary>
    public static (int Added, int Skipped) Import(string path, string sourcePath)
    {
        JsonNode? source;
        try
        {
            source = JsonNode.Parse(File.ReadAllText(sourcePath));
        }
        catch (JsonException)
        {
            throw new InvalidDataException("That file isn't a dalamudConfig.json.");
        }
        if (source is not JsonObject sourceObject || sourceObject["ThirdRepoList"]?["$values"] is not JsonArray)
            throw new InvalidDataException("That file isn't a dalamudConfig.json.");

        var incoming = Entries(sourceObject).ToList();
        var config = LoadOrNew(path);
        var seen = new HashSet<string>(Entries(config).Select(e => Key(e.Url)));
        var values = Values(config);
        int added = 0, skipped = 0;
        foreach (var (url, enabled) in incoming)
        {
            if (!IsWebLink(url) || !seen.Add(Key(url)))
            {
                skipped++;
                continue;
            }
            values.Add(NewEntry(url.Trim(), enabled));
            added++;
        }
        if (added > 0)
            Save(path, config);
        return (added, skipped);
    }

    // ---- JSON ----------------------------------------------------------------------------------

    private static JsonObject Load(string path)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject config)
                return config;
        }
        catch (JsonException)
        {
        }
        // Never overwrite a file we could not read: that would wipe every Dalamud setting, not just the repos.
        throw new InvalidDataException("Dalamud's config file is damaged, so it was left alone. Launch the game once to let Dalamud repair it.");
    }

    /// <summary>
    /// Before Dalamud's first run there is no file. A minimal one with just the repo list is enough: Newtonsoft only
    /// sets the keys that are present, so Dalamud keeps its defaults for everything else.
    /// </summary>
    private static JsonObject LoadOrNew(string path) =>
        File.Exists(path) ? Load(path) : new JsonObject { ["$type"] = ConfigType };

    private static JsonArray Values(JsonObject config)
    {
        if (config["ThirdRepoList"] is not JsonObject list)
            config["ThirdRepoList"] = list = new JsonObject { ["$type"] = ListType };
        if (list["$values"] is not JsonArray values)
            list["$values"] = values = new JsonArray();
        return values;
    }

    private static IEnumerable<(string Url, bool IsEnabled)> Entries(JsonObject config)
    {
        if (config["ThirdRepoList"]?["$values"] is not JsonArray values)
            yield break;
        foreach (var node in values)
        {
            if (node is not JsonObject entry || UrlOf(entry) is not { Length: > 0 } url)
                continue;
            var enabled = entry["IsEnabled"] is JsonValue v && v.TryGetValue(out bool b) ? b : true;
            yield return (url, enabled);
        }
    }

    private static string UrlOf(JsonObject entry) =>
        entry["Url"] is JsonValue v && v.TryGetValue(out string? s) ? s : "";

    private static JsonObject NewEntry(string url, bool enabled) => new()
    {
        ["$type"] = RepoType,
        ["Url"] = url,
        ["IsEnabled"] = enabled,
    };

    private static void Save(string path, JsonObject config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, config.ToJsonString(WriteOptions));
        File.Move(tmp, path, overwrite: true);
    }

    // ---- URLs ----------------------------------------------------------------------------------

    private static bool IsWebLink(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && uri.Host.Length > 0;

    /// <summary>Same repo: ignore surrounding spaces, letter case and a trailing slash.</summary>
    private static string Key(string url) => url.Trim().TrimEnd('/').ToLowerInvariant();
}
