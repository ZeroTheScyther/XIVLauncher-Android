using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XlaBoot.Provisioning;

/// <summary>
/// What is currently installed, as <c>files/runtime-state.json</c>.
///
/// Deliberately NOT in SharedPreferences: that store holds the user's choices and is shared with the
/// in-game menu, and every write there regenerates <c>files/xla-settings.sh</c>. Derived state has no
/// business in the shell contract.
/// </summary>
public sealed class RuntimeState
{
    [JsonPropertyName("runtimeVersion")] public int RuntimeVersion { get; set; }

    /// <summary>Component name to the sha256 of the package it was unpacked from.</summary>
    [JsonPropertyName("components")] public Dictionary<string, string> Components { get; set; } = new();

    /// <summary>Set once the whole sequence has finished, so a half-provisioned tree is never used.</summary>
    [JsonPropertyName("complete")] public bool Complete { get; set; }

    public const string FileName = "runtime-state.json";

    private static readonly JsonSerializerOptions Options =
        new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static string PathIn(string filesDir) => Path.Combine(filesDir, FileName);

    public static RuntimeState Load(string filesDir)
    {
        try
        {
            var path = PathIn(filesDir);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(path), Options) ?? new RuntimeState();
        }
        catch (Exception)
        {
            // Unreadable or from a future build: provision from scratch rather than guess.
        }
        return new RuntimeState();
    }

    public void Save(string filesDir)
    {
        var path = PathIn(filesDir);
        // Written via a temp file so a kill mid-write cannot leave state that parses but lies.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
        File.Move(temp, path, true);
    }

    /// <summary>True when this component is already unpacked from exactly these bytes.</summary>
    public bool IsCurrent(RuntimeComponent component) =>
        Components.TryGetValue(component.Name, out var sha)
        && string.Equals(sha, component.Sha256, StringComparison.OrdinalIgnoreCase);
}
