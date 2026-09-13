using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace XlaBoot.Provisioning;

/// <summary>
/// One downloadable piece of the Wine runtime. <see cref="File"/> is relative to the manifest, and
/// content-addressed (<c>pkg/wine-8b522c80.tar.zst</c>), so a component's URL changes exactly when its
/// bytes do - that is what lets the CDN cache packages forever while the manifest stays mutable.
/// </summary>
public sealed class RuntimeComponent
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("extractedBytes")] public long ExtractedBytes { get; set; }

    /// <summary>Where the package's contents land, relative to the app's private files dir.</summary>
    [JsonPropertyName("dest")] public string Dest { get; set; } = "";
}

/// <summary>
/// The index of the runtime bundle, built by <c>scripts/bundle-runtime.sh</c> and fetched from the release host
/// at first run. <c>sequence</c> is advisory: <see cref="RuntimeInstaller"/> owns the real order,
/// because two steps between components are code rather than data (linking Wine's builtin PEs into
/// system32, then deleting the d3d DLLs those links create before the overlay unpacks over them).
/// </summary>
public sealed class RuntimeManifest
{
    [JsonPropertyName("schema")] public int Schema { get; set; }
    [JsonPropertyName("runtimeVersion")] public int RuntimeVersion { get; set; }
    [JsonPropertyName("components")] public List<RuntimeComponent> Components { get; set; } = new();

    /// <summary>The schema this build of the app understands.</summary>
    public const int SupportedSchema = 1;

    public const string FileName = "runtime-manifest.json";

    /// <summary>
    /// Where the runtime bundle is published: a Cloudflare R2 bucket behind a custom domain. Verified to
    /// serve Range requests, which PackageDownloader's resume depends on.
    ///
    /// Overridable through the <c>bundle_base</c> setting so a tester can point at staging or at a local
    /// copy (<c>adb push bundle /sdcard/xla-bundle</c>) - that local form runs the same code as a
    /// real install.
    /// </summary>
    public const string DefaultBaseUrl = "https://xivlauncher.aetherworks.uk/";

    /// <summary>The base the app should use: the stored override if set, otherwise the release host.</summary>
    public static Uri BaseUri()
    {
        var configured = AppHost.Get("bundle_base", DefaultBaseUrl);
        if (Uri.TryCreate(configured, UriKind.Absolute, out var uri))
            return uri;
        // A local directory path rather than a URI.
        if (Directory.Exists(configured))
            return new Uri(configured.EndsWith('/') ? configured : configured + "/");
        return new Uri(DefaultBaseUrl);
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public long TotalDownloadBytes => Components.Sum(c => c.Bytes);

    public long TotalExtractedBytes => Components.Sum(c => c.ExtractedBytes);

    public RuntimeComponent? Find(string name) =>
        Components.FirstOrDefault(c => c.Name == name);

    /// <summary>
    /// Reads the manifest from <paramref name="baseUri"/>. Accepts an http(s) URL or a local
    /// directory/file URI - the local form is what makes the dev loop test the real provisioning code
    /// (<c>adb push bundle /sdcard/xla-bundle</c>) instead of a separate shell path.
    /// </summary>
    public static async Task<RuntimeManifest> FetchAsync(Uri baseUri, HttpClient http, CancellationToken cancel)
    {
        var source = Resolve(baseUri, FileName);
        string json;
        if (source.IsFile)
            json = await System.IO.File.ReadAllTextAsync(source.LocalPath, cancel).ConfigureAwait(false);
        else
            json = await GetWithRetryAsync(source, http, cancel).ConfigureAwait(false);

        var manifest = JsonSerializer.Deserialize<RuntimeManifest>(json, Options)
                       ?? throw new InvalidDataException("The runtime manifest was empty.");
        if (manifest.Schema != SupportedSchema)
            throw new InvalidDataException(
                $"The runtime manifest is schema {manifest.Schema}, but this version of the app understands "
                + $"{SupportedSchema}. Update the app.");
        if (manifest.Components.Count == 0)
            throw new InvalidDataException("The runtime manifest lists no components.");
        foreach (var component in manifest.Components)
        {
            if (component.Name.Length == 0 || component.File.Length == 0 || component.Sha256.Length != 64)
                throw new InvalidDataException($"The runtime manifest entry '{component.Name}' is incomplete.");
        }
        return manifest;
    }

    /// <summary>
    /// Fetches the manifest, retrying transient failures. This is the first network call the app makes,
    /// and it fires within half a second of process start - early enough that DNS reliably failed with
    /// EAI_NODATA while the very same lookup succeeded moments later. It is also the call most likely to
    /// meet a phone whose connection is not up yet, so it must not be a single shot.
    /// </summary>
    private static async Task<string> GetWithRetryAsync(Uri source, HttpClient http, CancellationToken cancel)
    {
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await http.GetStringAsync(source, cancel).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < attempts)
            {
                Console.WriteLine($"XlaProvision: manifest fetch attempt {attempt} failed "
                                  + $"({ex.InnerException?.GetType().Name ?? ex.GetType().Name}); retrying");
                await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), cancel).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                // Last attempt: say something a user can act on rather than "Connection failure".
                throw new HttpRequestException(
                    "Could not reach the download server. Check your internet connection and try again.", ex);
            }
        }
    }

    /// <summary>Resolves a manifest-relative path against the base, for both http and file bases.</summary>
    public static Uri Resolve(Uri baseUri, string relative)
    {
        var text = baseUri.ToString();
        if (!text.EndsWith('/'))
            baseUri = new Uri(text + "/");
        return new Uri(baseUri, relative);
    }
}
