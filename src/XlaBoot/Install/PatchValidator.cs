using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using XIVLauncher.Common;
using XIVLauncher.Common.Game.Patch.PatchList;
using XIVLauncher.Common.Patching.ZiPatch;

namespace XlaBoot.Install;

public enum PatchCheck
{
    Pass,
    BadLength,
    BadHash,
    /// <summary>A boot patch that does not parse, or has a chunk whose CRC does not match.</summary>
    Corrupt,
    UnknownHashType,
}

/// <summary>
/// Decides whether a downloaded patch file is the one the patch list describes. A port of upstream's
/// private <c>PatchManager.CheckPatchValidity</c>, which we cannot call.
///
/// Two paths, because the two repositories are verified differently: game patches carry SHA-1 hashes of
/// fixed-size blocks, while boot patches carry no hash at all and are checked by parsing every chunk and
/// comparing its CRC.
/// </summary>
public static class PatchValidator
{
    public static PatchCheck Check(PatchListEntry entry, string path, CancellationToken cancel = default)
    {
        if (entry.HashType != "sha1")
            return entry.GetRepo() == Repository.Boot ? CheckBoot(path, cancel) : PatchCheck.UnknownHashType;

        using var stream = File.OpenRead(path);
        if (stream.Length != entry.Length)
            return PatchCheck.BadLength;

        var blockSize = (int)entry.HashBlockSize;
        var parts = (int)Math.Ceiling((double)entry.Length / blockSize);
        if (entry.Hashes == null || entry.Hashes.Length < parts)
            return PatchCheck.BadHash;

        var block = new byte[blockSize];
        for (var i = 0; i < parts; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var read = stream.ReadAtLeast(block, blockSize, throwOnEndOfStream: false);
            var hash = Convert.ToHexString(SHA1.HashData(block.AsSpan(0, read)));
            if (!hash.Equals(entry.Hashes[i], StringComparison.OrdinalIgnoreCase))
                return PatchCheck.BadHash;
        }
        return PatchCheck.Pass;
    }

    private static PatchCheck CheckBoot(string path, CancellationToken cancel)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var patch = new ZiPatchFile(stream, needsChecksum: true);
            foreach (var chunk in patch.GetChunks())
            {
                cancel.ThrowIfCancellationRequested();
                if (!chunk.IsChecksumValid)
                    return PatchCheck.Corrupt;
            }
            return PatchCheck.Pass;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Truncated or not a ZiPatch at all.
            return PatchCheck.Corrupt;
        }
    }
}
