using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace XlaBoot.Imports;

/// <summary>
/// Reads the archives players import components from: .zip, and tars compressed with gzip, zstd or xz
/// (.tar.gz, .tzst, and Winlator/GameNative .wcp files, which are zstd or xz tars). The picker hands over a
/// copy under a generic name, so the format always comes from the magic bytes, never the extension.
/// </summary>
public static class ComponentArchive
{
    /// <summary>
    /// Calls <paramref name="onFile"/> for every regular file with its path (forward slashes, no leading "./")
    /// and a stream holding exactly its bytes. Directories, symlinks and hard links are never passed on.
    /// </summary>
    public static void ForEachFile(string path, Action<string, Stream> onFile)
    {
        using var file = File.OpenRead(path);
        var magic = new byte[6];
        int got = ReadExact(file, magic, 0, magic.Length);
        file.Position = 0;

        if (got >= 4 && magic[0] == 'P' && magic[1] == 'K' && magic[2] == 3 && magic[3] == 4)
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                    continue;
                using var data = entry.Open();
                onFile(Normalise(entry.FullName), data);
            }
            return;
        }

        using var tar = Decompress(file, magic, got)
                        ?? throw new InvalidDataException("That file is not a .zip, .tar.gz, .tzst or .wcp archive.");
        WalkTar(tar, onFile);
    }

    private static Stream? Decompress(Stream file, byte[] m, int got)
    {
        if (got >= 2 && m[0] == 0x1f && m[1] == 0x8b)
            return new GZipStream(file, CompressionMode.Decompress, leaveOpen: true);
        if (got >= 4 && m[0] == 0x28 && m[1] == 0xb5 && m[2] == 0x2f && m[3] == 0xfd)
            return new ZstdSharp.DecompressionStream(file, leaveOpen: true);
        if (got >= 6 && m[0] == 0xfd && m[1] == '7' && m[2] == 'z' && m[3] == 'X' && m[4] == 'Z' && m[5] == 0)
            return new SharpCompress.Compressors.Xz.XZStream(file);
        return null;
    }

    private static void WalkTar(Stream tar, Action<string, Stream> onFile)
    {
        var header = new byte[512];
        while (ReadExact(tar, header, 0, 512) == 512)
        {
            var name = Encoding.ASCII.GetString(header, 0, 100).TrimEnd('\0');
            if (name.Length == 0)
                continue; // end-of-archive zero blocks
            // ustar keeps long paths split across prefix (345..499) and name.
            if (Encoding.ASCII.GetString(header, 257, 5) == "ustar")
            {
                var prefix = Encoding.ASCII.GetString(header, 345, 155).TrimEnd('\0');
                if (prefix.Length > 0)
                    name = prefix + "/" + name;
            }
            char type = (char)header[156]; // one byte; the link name follows it
            long size = ParseOctal(header, 124, 12);
            if (type is '0' or '\0' or '7')
            {
                var entry = new SubStream(tar, size);
                onFile(Normalise(name), entry);
                entry.SkipRest();
            }
            else
            {
                Skip(tar, size);
            }
            Skip(tar, (512 - size % 512) % 512);
        }
    }

    private static string Normalise(string name)
    {
        name = name.Replace('\\', '/');
        while (name.StartsWith("./", StringComparison.Ordinal))
            name = name[2..];
        return name.TrimStart('/');
    }

    private static long ParseOctal(byte[] buffer, int offset, int count)
    {
        var s = Encoding.ASCII.GetString(buffer, offset, count).Trim('\0', ' ');
        return s.Length == 0 ? 0 : Convert.ToInt64(s, 8);
    }

    private static int ReadExact(Stream stream, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, offset + total, count - total);
            if (read <= 0)
                break;
            total += read;
        }
        return total;
    }

    private static void Skip(Stream stream, long bytes)
    {
        var buffer = new byte[8192];
        while (bytes > 0)
        {
            int read = stream.Read(buffer, 0, (int)Math.Min(bytes, buffer.Length));
            if (read <= 0)
                throw new InvalidDataException("The archive ends early.");
            bytes -= read;
        }
    }

    /// <summary>One tar member's bytes; reading past them returns 0, and SkipRest consumes what is left.</summary>
    private sealed class SubStream(Stream inner, long length) : Stream
    {
        private readonly long _length = length;
        private long _left = length;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left <= 0)
                return 0;
            int read = inner.Read(buffer, offset, (int)Math.Min(count, _left));
            if (read <= 0)
                throw new InvalidDataException("The archive ends early.");
            _left -= read;
            return read;
        }

        public void SkipRest()
        {
            Skip(inner, _left);
            _left = 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _length - _left; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
