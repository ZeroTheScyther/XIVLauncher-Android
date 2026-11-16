using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace XlaBoot;

public static class ComponentImporter
{
    private static string DxvkRoot(string filesDir) => Path.Combine(filesDir, "dxvk-import");
    private static string SelectedFile(string filesDir) => Path.Combine(DxvkRoot(filesDir), "selected.txt");

    public static void ImportDxvk(string archivePath, string filesDir)
    {
        var root = DxvkRoot(filesDir);
        Directory.CreateDirectory(root);

        var version = Path.GetFileNameWithoutExtension(archivePath);
        if (version.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            version = Path.GetFileNameWithoutExtension(version);
        if (version.StartsWith("dxvk-", StringComparison.OrdinalIgnoreCase))
            version = version["dxvk-".Length..];

        // The picker hands over a copy with a generic name, so the format comes from the gzip magic.
        var header = new byte[2];
        int got;
        using (var fs = File.OpenRead(archivePath))
            got = ReadExact(fs, header, 0, 2);
        bool isGzip = got == 2 && header[0] == 0x1f && header[1] == 0x8b;

        string? detectedVersion;
        if (!isGzip)
        {
            ZipArchive archive;
            try
            {
                archive = ZipFile.OpenRead(archivePath);
            }
            catch (InvalidDataException)
            {
                throw new InvalidDataException("That file is not a .zip or .tar.gz archive.");
            }
            using var _ = archive;
            detectedVersion = archive.Entries
                .Select(e => VersionFromPath(e.FullName))
                .FirstOrDefault(v => v != null);
        }
        else
        {
            using var stream = File.OpenRead(archivePath);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            detectedVersion = DetectTarVersion(gzip);
        }

        if (!string.IsNullOrEmpty(detectedVersion))
        {
            version = detectedVersion;
        }

        // The name becomes a folder under dxvk-import/ and a value in selected.txt, so it must be one plain
        // path segment that is not one of our own entries.
        version = version.Trim();
        if (version.Length == 0 || version.StartsWith('.') || version.Contains('/') || version.Contains('\\')
            || version is "stock" or "stock-backup" or "selected.txt")
            throw new InvalidDataException($"\"{version}\" can't be used as a DXVK version name.");
        var dest = Path.GetFullPath(Path.Combine(root, version));
        if (!dest.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("The version name points outside dxvk-import.");

        // Extract beside it first, so a bad archive never costs the copy already installed.
        var staging = Path.Combine(root, ".incoming");
        if (Directory.Exists(staging))
            Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        int extracted;
        try
        {
            if (!isGzip)
            {
                using var archive = ZipFile.OpenRead(archivePath);
                extracted = archive.Entries.Sum(e => ExtractDllEntry(e, staging));
            }
            else
            {
                using var stream = File.OpenRead(archivePath);
                using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                extracted = ExtractTar(gzip, staging);
            }
            if (extracted == 0)
                throw new InvalidDataException("No DLLs under x32/ or x64/, so this is not a DXVK build.");
        }
        catch
        {
            Directory.Delete(staging, true);
            throw;
        }
        if (Directory.Exists(dest))
            Directory.Delete(dest, true);
        Directory.Move(staging, dest);

        File.WriteAllText(SelectedFile(filesDir), version);
    }

    /// <summary>
    /// The version folder from an entry path: the directory just above x32/ or x64/, minus a "dxvk-"
    /// prefix ("dxvk-2.7.1/x64/d3d11.dll" -> "2.7.1"). A flat archive (x64/ at the root) has none, so it
    /// is "import". Null for an entry outside x32/x64.
    /// </summary>
    private static string? VersionFromPath(string fullName)
    {
        var parts = fullName.Replace('\\', '/').TrimStart('/').Split('/');
        int idx = Array.FindLastIndex(parts, p => p == "x32" || p == "x64");
        if (idx < 0 || idx == parts.Length - 1)
            return null;
        if (idx == 0)
            return "import";
        var top = parts[idx - 1];
        if (top.StartsWith("dxvk-", StringComparison.OrdinalIgnoreCase))
            top = top["dxvk-".Length..];
        return top;
    }

    private static int ExtractDllEntry(ZipArchiveEntry entry, string destRoot)
    {
        var name = entry.FullName.Replace('\\', '/');
        var parts = name.Split('/');
        int idx = Array.FindLastIndex(parts, p => p == "x32" || p == "x64");
        if (idx < 0 || idx != parts.Length - 2 || entry.Length == 0
            || !parts[^1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return 0;

        var subdir = parts[idx];
        var destDir = Path.Combine(destRoot, subdir);
        Directory.CreateDirectory(destDir);
        var destPath = Path.Combine(destDir, parts[^1]);
        entry.ExtractToFile(destPath, overwrite: true);
        return 1;
    }

    /// <summary>
    /// Walks a tar stream, handing each regular file to <paramref name="onFile"/> with its full path and
    /// size; the callback must consume exactly that many bytes or return false to have them skipped.
    /// Directories, symlinks and hard links carry no data we want and are skipped.
    /// </summary>
    private static void WalkTar(Stream tar, Func<string, long, bool> onFile)
    {
        var header = new byte[512];
        while (ReadExact(tar, header, 0, 512) == 512)
        {
            var name = System.Text.Encoding.ASCII.GetString(header, 0, 100).TrimEnd('\0');
            if (name.Length == 0)
                continue; // the zero blocks at the end, or padding between members
            // ustar keeps long paths split across prefix (345..499) and name.
            if (System.Text.Encoding.ASCII.GetString(header, 257, 5) == "ustar")
            {
                var prefix = System.Text.Encoding.ASCII.GetString(header, 345, 155).TrimEnd('\0');
                if (prefix.Length > 0)
                    name = prefix + "/" + name;
            }
            char type = (char)header[156]; // one byte; the link name follows it
            long size = ParseOctal(header, 124, 12);
            bool regular = type == '0' || type == '\0' || type == '7';
            if (!(regular && onFile(name, size)))
                Skip(tar, size);
            long pad = (512 - size % 512) % 512;
            if (pad > 0)
                Skip(tar, pad);
        }
    }

    private static int ExtractTar(Stream tarStream, string destRoot)
    {
        int extracted = 0;
        var buffer = new byte[8192];
        WalkTar(tarStream, (name, size) =>
        {
            var parts = name.Replace('\\', '/').TrimStart('/').Split('/');
            int idx = Array.FindLastIndex(parts, p => p == "x32" || p == "x64");
            if (idx < 0 || idx != parts.Length - 2 || size == 0
                || !parts[^1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                return false;
            var destDir = Path.Combine(destRoot, parts[idx]);
            Directory.CreateDirectory(destDir);
            using var file = File.Create(Path.Combine(destDir, parts[^1]));
            for (long left = size; left > 0;)
            {
                int n = tarStream.Read(buffer, 0, (int)Math.Min(left, buffer.Length));
                if (n <= 0)
                    throw new InvalidDataException("The archive ended in the middle of " + parts[^1] + ".");
                file.Write(buffer, 0, n);
                left -= n;
            }
            extracted++;
            return true;
        });
        return extracted;
    }

    private static string? DetectTarVersion(Stream tarStream)
    {
        string? version = null;
        WalkTar(tarStream, (name, _) =>
        {
            version ??= VersionFromPath(name);
            return false;
        });
        return version;
    }

    private static int ReadExact(Stream stream, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, offset + total, count - total);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }

    private static long ParseOctal(byte[] buffer, int offset, int count)
    {
        var s = System.Text.Encoding.ASCII.GetString(buffer, offset, count).TrimEnd('\0', ' ');
        if (string.IsNullOrEmpty(s)) return 0;
        return Convert.ToInt64(s, 8);
    }

    private static void Skip(this Stream stream, long bytes)
    {
        while (bytes > 0)
        {
            int read = stream.Read(new byte[Math.Min((int)bytes, 8192)], 0, (int)Math.Min(bytes, 8192));
            if (read <= 0) break;
            bytes -= read;
        }
    }

    /// <summary>The imported versions, by folder name. stock-backup and the staging folder are not versions.</summary>
    public static string[] DxvkVersions(string filesDir)
    {
        var root = DxvkRoot(filesDir);
        if (!Directory.Exists(root))
            return Array.Empty<string>();
        return Directory.GetDirectories(root)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(v => v != "stock-backup" && !v.StartsWith('.'))
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The version the next launch copies in, or "stock" when none is selected or it is gone.</summary>
    public static string SelectedVersion(string filesDir)
    {
        try
        {
            var v = File.ReadAllText(SelectedFile(filesDir)).Trim();
            return DxvkVersions(filesDir).Contains(v) ? v : "stock";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "stock";
        }
    }

    public static void SelectVersion(string filesDir, string version)
    {
        Directory.CreateDirectory(DxvkRoot(filesDir));
        File.WriteAllText(SelectedFile(filesDir), version);
    }

    /// <summary>Deletes an imported version; if it was the selected one, the next launch goes back to stock.</summary>
    public static void RemoveDxvk(string filesDir, string version)
    {
        if (!DxvkVersions(filesDir).Contains(version))
            return;
        if (SelectedVersion(filesDir) == version)
            SelectVersion(filesDir, "stock");
        Directory.Delete(Path.Combine(DxvkRoot(filesDir), version), true);
    }
}
