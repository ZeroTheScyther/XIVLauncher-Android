using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XlaBoot.Provisioning;

namespace XlaBoot;

/// <summary>A newer build of the app than the one running.</summary>
public sealed record AppRelease(long VersionCode, string VersionName, Uri Url);

/// <summary>
/// Tells the user when a newer APK has been published. The app is sideloaded, so nothing else will.
///
/// It only points at the download: installing an APK from inside the app would need REQUEST_INSTALL_PACKAGES,
/// which we deliberately do not hold, and the browser plus Android's own installer already does it properly.
///
/// Each GitHub release carries <c>app-version.json</c> (the release workflow writes it):
/// <c>{ "versionCode": 262560315, "versionName": "1.0", "url": "https://.../XIVLauncher.apk" }</c>. A missing file,
/// no network or a malformed answer all mean "no update" - this must never get in the way of playing.
/// </summary>
public static class AppUpdate
{
    public const string FileName = "app-version.json";

    /// <summary>
    /// GitHub serves this path from the newest non-prerelease release. It is a plain redirect, not the REST API, so it
    /// isn't subject to the API's per-IP rate limit.
    /// </summary>
    public static readonly Uri LatestReleaseUrl =
        new("https://github.com/ZeroTheScyther/XIVLauncher-Android/releases/latest/download/" + FileName);

    public static async Task<AppRelease?> CheckAsync(long runningVersionCode, HttpClient http, CancellationToken cancel)
    {
        try
        {
            using var response = await http.GetAsync(LatestReleaseUrl, cancel).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            var json = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            return Parse(json, runningVersionCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            Console.WriteLine($"XlaLauncher: update check skipped ({ex.GetType().Name})");
            return null;
        }
    }

    /// <summary>The release described by <paramref name="json"/> if it is newer than the running build.</summary>
    public static AppRelease? Parse(string json, long runningVersionCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var code = root.GetProperty("versionCode").GetInt64();
            var name = root.TryGetProperty("versionName", out var n) ? n.GetString() ?? "" : "";
            var url = root.GetProperty("url").GetString();
            if (code <= runningVersionCode || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps)
                return null;
            return new AppRelease(code, name, uri);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
