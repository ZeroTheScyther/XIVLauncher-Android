using System;
using System.IO;

namespace XlaBoot;

/// <summary>What we were able to determine about a candidate game folder.</summary>
public enum GameStatus
{
    /// <summary>Nothing is installed there. The app may offer to install it.</summary>
    Missing,

    /// <summary>Something may be there, but this process cannot read it - almost always a missing
    /// storage permission. Never treat this as "no game": offering to download 128 GB over an install
    /// the user already has is the worst outcome available.</summary>
    Unreadable,

    /// <summary>A readable install, with its version.</summary>
    Present,
}

/// <summary>The outcome of <see cref="GameLocation.Check"/>, with a sentence fit to show the user.</summary>
public readonly record struct GameCheck(GameStatus Status, string Version, string Message)
{
    public bool IsPresent => Status == GameStatus.Present;
}

/// <summary>
/// Where the game is and whether we can actually reach it.
///
/// The distinction this type exists for: on Android's emulated storage a file's *metadata* is readable
/// without the storage permission while its *contents* are not. Measured on device with the permission
/// revoked, <c>new FileInfo(ffxiv_dx11.exe).Length</c> returned the correct 51874048 bytes while
/// <c>File.OpenRead</c> on the same path threw <see cref="UnauthorizedAccessException"/>. So an
/// existence or length check reports a healthy install that cannot be launched, and the failure only
/// surfaces much later inside Wine. Every check here opens a file instead.
/// </summary>
public static class GameLocation
{
    /// <summary>
    /// Where the app installs the game when the user has not chosen somewhere else. Public and
    /// top-level so it survives uninstalling the app, which is the point: nobody should lose a
    /// 100 GB download because they reinstalled a launcher.
    /// </summary>
    public const string DefaultPath = "/storage/emulated/0/XIVLauncher/FFXIV";

    /// <summary>The folder in use: the user's choice from the settings screen, else the default.</summary>
    public static string Current => AppHost.Get("game_path", DefaultPath);

    /// <summary>Records the user's (or the installer's) choice of folder.</summary>
    public static void Use(string path) => AppHost.Set("game_path", path);

    /// <summary>
    /// Whether this process can read and write the shared volume at all. Probed rather than asked,
    /// because the permission state and the access that results from it are not the same question -
    /// either the storage permission or All-files-access grants it (measured on device).
    /// </summary>
    public static bool HasStorageAccess()
    {
        try
        {
            Directory.GetFileSystemEntries("/storage/emulated/0");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether this process can create and write files in <paramref name="path"/>, creating the folder if
    /// needed. Probed by writing, for the same reason <see cref="Check"/> opens a file: a removable SD card
    /// lists and reads fine with the ordinary storage permission but refuses writes without All-files access.
    /// </summary>
    public static bool CanWrite(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".xla-write-probe");
            File.WriteAllBytes(probe, new byte[] { 1 });
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens the game's main executable to decide what is really there. <paramref name="path"/> is the
    /// folder holding <c>boot</c> and <c>game</c>, as the settings picker requires.
    /// </summary>
    public static GameCheck Check(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new GameCheck(GameStatus.Missing, "", "No game folder is set.");

        var exe = Path.Combine(path, "game", "ffxiv_dx11.exe");
        try
        {
            // One byte is enough: it is the read itself that the permission gates.
            using var file = File.OpenRead(exe);
            if (file.ReadByte() < 0)
                return new GameCheck(GameStatus.Missing, "",
                    "game\\ffxiv_dx11.exe is empty here. The game cannot start from this folder.");
        }
        catch (UnauthorizedAccessException)
        {
            return new GameCheck(GameStatus.Unreadable, "",
                "This folder cannot be read. The app needs permission to access your storage.");
        }
        catch (FileNotFoundException)
        {
            return new GameCheck(GameStatus.Missing, "",
                "No game\\ffxiv_dx11.exe here. The game cannot start from this folder.");
        }
        catch (DirectoryNotFoundException)
        {
            return new GameCheck(GameStatus.Missing, "", "This folder does not exist.");
        }
        catch (IOException ex)
        {
            return new GameCheck(GameStatus.Unreadable, "", $"Could not read it: {ex.GetType().Name}.");
        }

        return new GameCheck(GameStatus.Present, Version(path), $"FFXIV found, version {Version(path)}.");
    }

    /// <summary>The installed game version, or "unknown" when the file is absent or unreadable.</summary>
    public static string Version(string path)
    {
        try { return File.ReadAllText(Path.Combine(path, "game", "ffxivgame.ver")).Trim(); }
        catch (Exception) { return "unknown"; }
    }
}
