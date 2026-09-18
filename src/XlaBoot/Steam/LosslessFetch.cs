using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;

namespace XlaBoot.Steam;

/// <summary>
/// Downloads Lossless.dll from the player's own Lossless Scaling install on Steam (app 993090).
///
/// The lsfg-vk layer carries no shaders of its own: it extracts them from this DLL at runtime. The DLL
/// is THS's proprietary code, so it is never bundled. Steam hands out a depot's decryption key only to
/// an account that owns the app, which makes the key request the ownership check.
///
/// Only the chunks of the one file are fetched, not the whole depot.
/// </summary>
public static class LosslessFetch
{
    public const uint AppId = 993090;
    public const string DllName = "Lossless.dll";
    private const string Branch = "public";

    // Enough to step past a bad CDN node or two without making a real outage take minutes to report.
    private const int ServerAttempts = 4;

    /// <summary>files/lsfg: Lossless.dll, plus the depot and manifest it came from.</summary>
    public static string Dir(string filesDir) => Path.Combine(filesDir, "lsfg");

    public static string DllPath(string filesDir) => Path.Combine(Dir(filesDir), DllName);

    public static bool IsInstalled(string filesDir) => File.Exists(DllPath(filesDir));

    /// <summary>
    /// Fetches the DLL into files/lsfg. Returns false when the copy there already matches Steam's current
    /// manifest and nothing was downloaded.
    /// </summary>
    public static async Task<bool> FetchAsync(SteamSession session, string filesDir, Action<string> progress,
        CancellationToken ct = default)
    {
        if (!session.IsSignedIn)
            throw new SteamSignInException("Not signed in to Steam.");

        var client = session.Client;
        var apps = client.GetHandler<SteamApps>()
                   ?? throw new SteamSignInException("SteamKit did not provide an apps handler.");
        var content = client.GetHandler<SteamContent>()
                      ?? throw new SteamSignInException("SteamKit did not provide a content handler.");

        var dir = Dir(filesDir);
        var stampPath = Path.Combine(dir, "manifest");
        var dllPath = DllPath(filesDir);

        progress("Asking Steam for Lossless Scaling...");
        var depots = await GetDepotsAsync(apps).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        // The stamp is "<depot> <manifest>". Same manifest as last time means the same bytes.
        var stamp = File.Exists(stampPath) ? File.ReadAllText(stampPath).Trim() : "";
        if (File.Exists(dllPath) && depots.Any(d => $"{d.DepotId} {d.ManifestId}" == stamp))
            return false;

        var servers = await GetServersAsync(content).ConfigureAwait(false);
        using var cdn = new Client(client);

        var anyKey = false;
        foreach (var (depotId, manifestId) in depots)
        {
            ct.ThrowIfCancellationRequested();

            var key = await apps.GetDepotDecryptionKey(depotId, AppId).ToTask().ConfigureAwait(false);
            if (key.Result != EResult.OK)
                continue; // a depot this account has no key for, such as a DLC's
            anyKey = true;

            var requestCode = await content.GetManifestRequestCode(depotId, AppId, manifestId, Branch)
                .ConfigureAwait(false);
            if (requestCode == 0)
                continue;

            var manifest = await OnAnyServer(servers, ct,
                server => cdn.DownloadManifestAsync(depotId, manifestId, requestCode, server, key.DepotKey))
                .ConfigureAwait(false);
            if (manifest.FilenamesEncrypted && !manifest.DecryptFilenames(key.DepotKey))
                continue;

            var file = manifest.Files?.FirstOrDefault(f =>
                string.Equals(Path.GetFileName(f.FileName.Replace('\\', '/')), DllName,
                    StringComparison.OrdinalIgnoreCase));
            if (file == null)
                continue;

            await DownloadFileAsync(cdn, servers, depotId, key.DepotKey, file, dir, progress, ct)
                .ConfigureAwait(false);
            File.WriteAllText(stampPath, $"{depotId} {manifestId}");
            return true;
        }

        if (!anyKey)
            throw new SteamSignInException("This Steam account does not own Lossless Scaling.",
                EResult.AccessDenied) { NotOwned = true };
        throw new SteamSignInException("Steam's copy of Lossless Scaling has no Lossless.dll.");
    }

    /// <summary>The app's depots with a public-branch manifest, from PICS rather than hardcoded ids.</summary>
    private static async Task<List<(uint DepotId, ulong ManifestId)>> GetDepotsAsync(SteamApps apps)
    {
        var tokens = await apps.PICSGetAccessTokens(AppId, null).ToTask().ConfigureAwait(false);
        tokens.AppTokens.TryGetValue(AppId, out var token);

        var info = await apps.PICSGetProductInfo(new SteamApps.PICSRequest(AppId, token), null).ToTask()
            .ConfigureAwait(false);
        var app = info.Results?.SelectMany(r => r.Apps).FirstOrDefault(a => a.Key == AppId).Value
                  ?? throw new SteamSignInException("Steam has no details for Lossless Scaling.");

        var result = new List<(uint, ulong)>();
        foreach (var depot in app.KeyValues["depots"].Children)
        {
            if (!uint.TryParse(depot.Name, out var depotId))
                continue; // "branches" and other metadata sit beside the depots

            var os = depot["config"]["oslist"].Value;
            if (!string.IsNullOrEmpty(os) && !os.Contains("windows", StringComparison.OrdinalIgnoreCase))
                continue;

            // Newer PICS data nests the id as manifests/public/gid; older data has it directly.
            var entry = depot["manifests"][Branch];
            var gid = entry["gid"].Value ?? entry.Value;
            if (ulong.TryParse(gid, out var manifestId))
                result.Add((depotId, manifestId));
        }

        if (result.Count == 0)
            throw new SteamSignInException("Steam lists no download for Lossless Scaling.");
        return result;
    }

    private static async Task<List<Server>> GetServersAsync(SteamContent content)
    {
        var servers = (await content.GetServersForSteamPipe().ConfigureAwait(false))
            .Where(s => s.Type is "SteamCache" or "CDN")
            .Where(s => s.AllowedAppIds.Length == 0 || s.AllowedAppIds.Contains(AppId))
            .OrderBy(s => s.WeightedLoad)
            .Take(ServerAttempts)
            .ToList();

        if (servers.Count == 0)
            throw new SteamSignInException("Steam offered no download servers.");
        return servers;
    }

    private static async Task<T> OnAnyServer<T>(List<Server> servers, CancellationToken ct, Func<Server, Task<T>> request)
    {
        Exception? last = null;
        foreach (var server in servers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await request(server).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                Console.WriteLine($"XlaSteam: {server.Host} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        throw new SteamSignInException("Could not download from Steam. Check the connection and try again.",
            EResult.Invalid, last);
    }

    private static async Task DownloadFileAsync(Client cdn, List<Server> servers, uint depotId, byte[] depotKey,
        DepotManifest.FileData file, string dir, Action<string> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, DllName + ".tmp");

        await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            output.SetLength((long)file.TotalSize);
            ulong done = 0;

            foreach (var chunk in file.Chunks)
            {
                progress($"Downloading Lossless.dll ({done * 100 / Math.Max(file.TotalSize, 1)}%)...");
                var buffer = new byte[chunk.UncompressedLength];
                // With the depot key given, SteamKit decrypts, decompresses and checks the chunk's checksum.
                var length = await OnAnyServer(servers, ct,
                    server => cdn.DownloadDepotChunkAsync(depotId, chunk, server, buffer, depotKey))
                    .ConfigureAwait(false);

                output.Position = (long)chunk.Offset;
                await output.WriteAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
                done += (ulong)length;
            }
        }

        byte[] hash;
        await using (var check = File.OpenRead(tmp))
            hash = await SHA1.HashDataAsync(check, ct).ConfigureAwait(false);
        if (!hash.AsSpan().SequenceEqual(file.FileHash))
        {
            File.Delete(tmp);
            throw new SteamSignInException("Lossless.dll did not download correctly. Try again.");
        }

        File.Move(tmp, Path.Combine(dir, DllName), overwrite: true);
    }
}
