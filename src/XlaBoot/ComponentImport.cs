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

        byte[] header = new byte[2];
        using (var fs = File.OpenRead(archivePath))
            fs.Read(header, 0, 2);

        bool isGzip = header[0] == 0x1f && header[1] == 0x8b;

        string detectedVersion = null;
        if (!isGzip)
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var first = archive.Entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.FullName));
            if (first != null)
            {
                detectedVersion = DetectZipVersion(first.FullName);
            }
        }
        else
        {
            using var stream = File.OpenRead(archivePath);
            using var gzip = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress);
            detectedVersion = DetectTarVersion(gzip);
        }

        if (!string.IsNullOrEmpty(detectedVersion))
        {
            version = detectedVersion;
        }

        // Sanitize: reject path-traversal or empty names.
        version = version.Trim();
        if (string.IsNullOrEmpty(version) || version.Contains('/') || version.Contains('\\') || version == "..")
        {
            throw new ArgumentException("Invalid DXVK version name.", nameof(archivePath));
        }

        var dest = Path.Combine(root, version);
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.CreateDirectory(dest);

        int extracted = 0;
        if (!isGzip)
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
                extracted += ExtractDllEntry(entry, dest);
        }
        else
        {
            using var stream = File.OpenRead(archivePath);
            using var gzip = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress);
            extracted = ExtractTar(gzip, dest);
        }

        if (extracted == 0)
        {
            Directory.Delete(dest, true);
            throw new InvalidDataException("No DXVK DLLs found in archive.");
        }

        File.WriteAllText(SelectedFile(filesDir), version);
    }

    private static string DetectZipVersion(string fullName)
    {
        var name = fullName.Replace('\\', '/').TrimStart('/');
        var parts = name.Split('/');
        // Find x32/x64 anywhere in path; version is the folder immediately before it.
        int idx = Array.FindLastIndex(parts, p => p == "x32" || p == "x64");
        if (idx > 0)
        {
            var top = parts[idx - 1];
            if (!string.IsNullOrEmpty(top) && top != ".")
            {
                if (top.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
                    top = Path.GetFileNameWithoutExtension(top);
                if (top.StartsWith("dxvk-", StringComparison.OrdinalIgnoreCase))
                    top = top["dxvk-".Length..];
                return top;
            }
        }
        // Flat zip: x32/... at root -> use "import" as version.
        if (parts.Length > 0 && (parts[0] == "x32" || parts[0] == "x64"))
            return "import";
        // Fallback: first non-empty segment.
        var first = parts.FirstOrDefault(p => !string.IsNullOrEmpty(p) && p != ".");
        if (first != null)
        {
            if (first.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
                first = Path.GetFileNameWithoutExtension(first);
            if (first.StartsWith("dxvk-", StringComparison.OrdinalIgnoreCase))
                first = first["dxvk-".Length..];
            return first;
        }
        return "import";
    }

    private static int ExtractDllEntry(ZipArchiveEntry entry, string destRoot)
    {
        var name = entry.FullName.Replace('\\', '/');
        var parts = name.Split('/');
        int idx = Array.FindLastIndex(parts, p => p == "x32" || p == "x64");
        if (idx < 0 || idx >= parts.Length - 1)
            return 0;
        if (!parts[^1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return 0;

        var subdir = parts[idx];
        var destDir = Path.Combine(destRoot, subdir);
        Directory.CreateDirectory(destDir);
        var destPath = Path.Combine(destDir, parts[^1]);
        entry.ExtractToFile(destPath, overwrite: true);
        return 1;
    }

    private static int ExtractTar(Stream tarStream, string destRoot)
    {
        int extracted = 0, skipped = 0;
        var buffer = new byte[8192];
        while (true)
        {
            int read = ReadExact(tarStream, buffer, 0, 512);
            if (read == 0) break;
            if (read < 512) break;

            var name = System.Text.Encoding.ASCII.GetString(buffer, 0, 100).TrimEnd('\0');
            if (string.IsNullOrEmpty(name)) continue;

            var type = System.Text.Encoding.ASCII.GetString(buffer, 156, 12).TrimEnd('\0');
            long size = ParseOctal(buffer, 124, 12);

            bool isSymlink = type.Equals("2", StringComparison.Ordinal) || type.Equals("l", StringComparison.Ordinal);
            if (isSymlink)
            {
                Skip(tarStream, size);
                long symPad = (512 - (size % 512)) % 512;
                if (symPad > 0) Skip(tarStream, symPad);
                continue;
            }

            var relative = name.Replace('\\', '/').TrimStart('/');
            var parts = relative.Split('/');
            int idx = Array.FindLastIndex(parts, p => p == "x32" || p == "x64");
            if (idx >= 0 && idx < parts.Length - 1)
            {
                var subdir = parts[idx];
                var fileName = parts[^1];
                if (!fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    Skip(tarStream, size);
                }
                else
                {
                    var destDir = Path.Combine(destRoot, subdir);
                    Directory.CreateDirectory(destDir);
                    var destPath = Path.Combine(destDir, fileName);
                    using var file = File.Create(destPath);
                    long dataRemaining = size;
                    while (dataRemaining > 0)
                    {
                        int toRead = (int)Math.Min(dataRemaining, buffer.Length);
                        int n = tarStream.Read(buffer, 0, toRead);
                        if (n <= 0) break;
                        file.Write(buffer, 0, n);
                        dataRemaining -= n;
                    }
                    extracted++;
                }
            }
            else
            {
                Skip(tarStream, size);
                skipped++;
            }

            long pad = (512 - (size % 512)) % 512;
            if (pad > 0) Skip(tarStream, pad);
        }
        return extracted;
    }

    private static string? DetectTarVersion(Stream tarStream)
    {
        var buffer = new byte[512];
        for (int i = 0; i < 20; i++)
        {
            int read = ReadExact(tarStream, buffer, 0, 512);
            if (read == 0) break;
            if (read < 512) break;

            var name = System.Text.Encoding.ASCII.GetString(buffer, 0, 100).TrimEnd('\0');
            if (string.IsNullOrEmpty(name)) continue;

            var type = System.Text.Encoding.ASCII.GetString(buffer, 156, 12).TrimEnd('\0');
            if (type.Equals("5", StringComparison.Ordinal)) continue;

            long size = ParseOctal(buffer, 124, 12);
            if (size > 0 && name.Contains('/'))
            {
                var parts = name.Replace('\\', '/').TrimStart('/').Split('/');
                if (parts.Length > 0)
                {
                    var top = parts[0];
                    if (!string.IsNullOrEmpty(top) && top != ".")
                    {
                        if (top.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
                            top = Path.GetFileNameWithoutExtension(top);
                        if (top.StartsWith("dxvk-", StringComparison.OrdinalIgnoreCase))
                            top = top["dxvk-".Length..];
                        return top;
                    }
                }
            }

            long pad = (512 - (size % 512)) % 512;
            if (pad > 0) Skip(tarStream, pad);
            if (size > 0) Skip(tarStream, size);
            long pad2 = (512 - (size % 512)) % 512;
            if (pad2 > 0) Skip(tarStream, pad2);
        }
        return null;
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

    public static string[] DxvkVersions(string filesDir)
    {
        var root = DxvkRoot(filesDir);
        if (!Directory.Exists(root)) return Array.Empty<string>();
        return Directory.GetDirectories(root)
            .Select(Path.GetFileName)
            .Where(v => v != "stock-backup")
            .OrderBy(n => n)
            .ToArray();
    }

    public static string SelectedVersion(string filesDir)
    {
        var sel = SelectedFile(filesDir);
        if (File.Exists(sel))
        {
            var v = File.ReadAllText(sel).Trim();
            if (Directory.Exists(Path.Combine(DxvkRoot(filesDir), v)))
                return v;
        }
        return "stock";
    }

    public static void SelectVersion(string filesDir, string version)
    {
        File.WriteAllText(SelectedFile(filesDir), version);
    }

    public static void RemoveDxvk(string filesDir, string version)
    {
        var dest = Path.Combine(DxvkRoot(filesDir), version);
        if (Directory.Exists(dest))
            Directory.Delete(dest, true);
        if (SelectedVersion(filesDir) == version)
            File.WriteAllText(SelectedFile(filesDir), "stock");
    }
}
