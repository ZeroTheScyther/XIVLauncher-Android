using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace XlaBoot;

/// <summary>
/// The Logs page's export: one zip of everything useful for a bug report - Dalamud's logs folder, Wine's and the X
/// server's logs, the per-second performance samples (perf.csv, the session heartbeat), the launch settings (xla-settings.sh holds no credentials) and a short info header.
/// Logs may still be open for writing, so each is read with full sharing; a file that can't be read is noted in
/// info.txt instead of failing the whole export.
/// </summary>
public static class LogExport
{
    public static string SuggestedName(DateTime now) => $"XIVLauncher-logs-{now:yyyyMMdd-HHmmss}.zip";

    /// <returns>How many files went into the zip.</returns>
    public static int Create(string filesDir, string zipPath, string info)
    {
        var entries = new (string Source, string Name)[]
        {
            (AppLog.Path(filesDir), "launcher.log"),
            (Path.Combine(filesDir, "wine-test.log"), "wine-test.log"),
            (Path.Combine(filesDir, "xserver.log"), "xserver.log"),
            (Path.Combine(filesDir, "perf.csv"), "perf.csv"),
            (Path.Combine(filesDir, "xla-settings.sh"), "xla-settings.sh"),
        };
        var dalamudLogs = Path.Combine(filesDir, "xlroaming", "logs");
        if (Directory.Exists(dalamudLogs))
            entries = entries.Concat(Directory.GetFiles(dalamudLogs).OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => (f, "dalamud/" + Path.GetFileName(f)))).ToArray();

        var skipped = new System.Text.StringBuilder();
        var count = 0;
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var (source, name) in entries)
            {
                if (!File.Exists(source))
                    continue;
                try
                {
                    using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    entry.LastWriteTime = File.GetLastWriteTime(source);
                    using var output = entry.Open();
                    input.CopyTo(output);
                    count++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped.AppendLine($"not included: {name} ({ex.GetType().Name}: {ex.Message})");
                }
            }

            using var writer = new StreamWriter(zip.CreateEntry("info.txt").Open());
            writer.Write(info);
            if (skipped.Length > 0)
                writer.Write("\n" + skipped);
        }
        return count;
    }
}
