using System;
using System.Formats.Tar;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace XlaBoot.Provisioning;

/// <summary>
/// Unpacks one <c>.tar.zst</c> runtime package.
///
/// This walks entries with <see cref="TarReader"/> rather than calling
/// <see cref="TarFile.ExtractToDirectory(Stream, string, bool)"/>, for two reasons. First, that helper
/// refuses any symlink whose target leaves the destination and aborts mid-extract - the Wine prefix's
/// <c>z: -&gt; /</c> is exactly that, and it is the same trap toybox tar hits. Second, walking entries is what lets us report progress.
///
/// Packages are extracted OVER an existing tree (the fonts/DXVK overlay lands on top of the prefix), so
/// every entry replaces what is already there.
/// </summary>
public static class ArchiveExtractor
{
    /// <summary>Reports bytes of uncompressed content written so far.</summary>
    public delegate void Progress(long extractedBytes);

    public static Task ExtractAsync(string archivePath, string destination, Progress? progress,
        CancellationToken cancel) =>
        Task.Run(() => Extract(archivePath, destination, progress, cancel), cancel);

    public static void Extract(string archivePath, string destination, Progress? progress,
        CancellationToken cancel)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination);

        using var file = File.OpenRead(archivePath);
        using var decompressed = new ZstdSharp.DecompressionStream(file);
        using var reader = new TarReader(decompressed);

        long written = 0;
        long lastReported = 0;
        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            cancel.ThrowIfCancellationRequested();

            var name = entry.Name;
            // Belt and braces: scripts/bundle-runtime.sh already filters the .wcp's 1761 AppleDouble sidecars.
            if (name.Length == 0 || Path.GetFileName(name).StartsWith("._", StringComparison.Ordinal)
                                 || Path.GetFileName(name) == ".DS_Store")
                continue;

            var target = Path.GetFullPath(Path.Combine(root, name));
            // An entry must never write outside its own package's destination. Note this constrains the
            // entry PATH only - a symlink's target is deliberately allowed to point anywhere, which is
            // the whole reason this class exists.
            if (target != root && !target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    ApplyMode(target, entry, isDirectory: true);
                    break;

                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                case TarEntryType.ContiguousFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    Replace(target);
                    using (var output = File.Create(target))
                        entry.DataStream?.CopyTo(output);
                    ApplyMode(target, entry, isDirectory: false);
                    written += entry.Length;
                    break;

                case TarEntryType.SymbolicLink:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    Replace(target);
                    // No mode is applied: a symlink's permissions are not meaningful on Linux, and
                    // File.SetUnixFileMode would follow the link and change the target instead.
                    File.CreateSymbolicLink(target, entry.LinkName);
                    break;

                case TarEntryType.HardLink:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    Replace(target);
                    var source = Path.GetFullPath(Path.Combine(root, entry.LinkName));
                    if (File.Exists(source))
                        File.Copy(source, target);  // .NET cannot create hard links; a copy is equivalent here
                    break;

                default:
                    // Character/block devices, FIFOs, global headers: nothing in these packages.
                    break;
            }

            // Coarse reporting: one callback per megabyte, not per entry (the Wine tree has ~9000).
            if (progress != null && written - lastReported >= 1 << 20)
            {
                lastReported = written;
                progress(written);
            }
        }
        progress?.Invoke(written);
    }

    /// <summary>
    /// Clears whatever occupies a name before writing it. Deleting first matters because the target may
    /// be a symlink into the Wine tree: opening it for write would follow the link and corrupt the
    /// linked-to builtin instead of replacing the link. That is the failure the old shell pipeline
    /// avoided by deleting the d3d DLLs before untarring the overlay.
    /// </summary>
    private static void Replace(string path)
    {
        try
        {
            if (File.Exists(path) || new FileInfo(path).LinkTarget != null)
                File.Delete(path);
        }
        catch (UnauthorizedAccessException)
        {
            // A directory is standing where a file should go; let the caller's write surface it.
        }
    }

    private static void ApplyMode(string path, TarEntry entry, bool isDirectory)
    {
        if (entry.Mode == default)
            return;
        try
        {
            // Keep the owner able to traverse and rewrite what we just unpacked, whatever the archive
            // recorded - the Wine binaries ship as 0640 and would otherwise not be executable.
            var mode = entry.Mode | UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (isDirectory)
                mode |= UnixFileMode.UserExecute;
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception)
        {
            // Some Android storage backends do not carry modes; the defaults are already usable.
        }
    }
}
