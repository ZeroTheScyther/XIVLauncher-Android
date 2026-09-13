using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace XlaBoot.Provisioning;

/// <summary>
/// Fetches one runtime package and proves it arrived intact.
///
/// Resumable by HTTP Range, because this runs on a phone: 188 MB over mobile data will be interrupted.
/// Verified by SHA-256 against the manifest, because a truncated or CDN-mangled package would otherwise
/// surface much later as an unexplainable Wine failure.
/// </summary>
public static class PackageDownloader
{
    /// <summary>Reports bytes of this package present on disk, and the current rate.</summary>
    public delegate void Progress(long done, long total, double bytesPerSecond);

    /// <summary>
    /// Ensures <paramref name="destination"/> holds exactly the bytes the manifest describes, and
    /// returns its path. An existing complete file is verified and reused; a partial one is resumed.
    /// </summary>
    public static async Task<string> EnsureAsync(Uri source, RuntimeComponent component, string destination,
        HttpClient http, Progress? progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        // A local base (a bundle built by scripts/bundle-runtime.sh and pushed to the device) needs no download at all.
        if (source.IsFile)
        {
            await VerifyAsync(source.LocalPath, component, cancel).ConfigureAwait(false);
            progress?.Invoke(component.Bytes, component.Bytes, 0);
            return source.LocalPath;
        }

        for (var attempt = 1; ; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                await RangeDownload.FetchAsync(source, component.Bytes, destination, http, null,
                    progress == null ? null : (d, t, r) => progress(d, t, r), cancel).ConfigureAwait(false);
                await VerifyAsync(destination, component, cancel).ConfigureAwait(false);
                return destination;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < 4)
            {
                // A corrupt resume is the likely cause of a hash failure, so start the file over.
                if (ex is InvalidDataException)
                    RangeDownload.TryDelete(destination);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancel).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Throws <see cref="InvalidDataException"/> unless the file matches the manifest exactly.</summary>
    private static async Task VerifyAsync(string path, RuntimeComponent component, CancellationToken cancel)
    {
        var length = new FileInfo(path).Length;
        if (length != component.Bytes)
            throw new InvalidDataException(
                $"{component.Name}: expected {component.Bytes} bytes but got {length}.");

        await using var file = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(file, cancel).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash).ToLower(CultureInfo.InvariantCulture);
        if (!string.Equals(actual, component.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"{component.Name} is corrupt: sha256 {actual[..12]}… does not match the manifest's "
                + $"{component.Sha256[..12]}….");
    }
}
