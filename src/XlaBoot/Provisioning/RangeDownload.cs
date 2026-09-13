using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace XlaBoot.Provisioning;

/// <summary>
/// One resumable HTTP download to a file of known length. Shared by the runtime packages and the game
/// patches, which differ only in how the result is verified afterwards.
/// </summary>
public static class RangeDownload
{
    private const int BufferBytes = 1 << 18;

    /// <summary>
    /// A read that delivers nothing for this long is treated as a dead connection. HttpClient.Timeout does
    /// not cover the body once headers have arrived, so without this a stalled transfer on a flaky mobile
    /// connection hangs forever instead of retrying.
    /// </summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Reports bytes of this file present on disk, and the current rate.</summary>
    public delegate void Progress(long done, long total, double bytesPerSecond);

    /// <summary>
    /// Brings <paramref name="destination"/> up to <paramref name="expectedBytes"/> by Range-resuming
    /// whatever is already there. Says nothing about whether the bytes are right; the caller verifies.
    /// </summary>
    public static async Task FetchAsync(Uri source, long expectedBytes, string destination, HttpClient http,
        Action<HttpRequestMessage>? prepare, Progress? progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var have = File.Exists(destination) ? new FileInfo(destination).Length : 0;
        if (have > expectedBytes)
        {
            // Longer than it should be: something else wrote here, so there is nothing to resume.
            TryDelete(destination);
            have = 0;
        }
        if (have == expectedBytes)
        {
            progress?.Invoke(have, expectedBytes, 0);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        prepare?.Invoke(request);
        if (have > 0)
            request.Headers.Range = new RangeHeaderValue(have, null);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel)
            .ConfigureAwait(false);

        // A server that ignores Range answers 200 with the whole file; restart rather than append to it.
        if (have > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            have = 0;
            TryDelete(destination);
        }
        response.EnsureSuccessStatusCode();

        using var input = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        using var output = new FileStream(destination, have > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.Read, BufferBytes);

        var buffer = new byte[BufferBytes];
        var done = have;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var sinceReport = 0L;
        var reportedAt = 0L;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        while (true)
        {
            stall.CancelAfter(StallTimeout);
            int read;
            try
            {
                read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                // Not the user: the connection went quiet. Surface as a network error so callers retry.
                throw new HttpRequestException("The download stalled (no data for 60 seconds).");
            }
            if (read == 0)
                break;

            await output.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
            done += read;
            sinceReport += read;
            if (progress != null && clock.ElapsedMilliseconds - reportedAt >= 500)
            {
                var seconds = (clock.ElapsedMilliseconds - reportedAt) / 1000.0;
                progress(done, expectedBytes, seconds > 0 ? sinceReport / seconds : 0);
                reportedAt = clock.ElapsedMilliseconds;
                sinceReport = 0;
            }
        }
        // Flush to the device, not just to the page cache: the next step reads this file back and a
        // process kill in between must not leave a file that looks complete but is not.
        output.Flush(true);
        progress?.Invoke(done, expectedBytes, 0);
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception) { /* the next write mode handles it */ }
    }
}
