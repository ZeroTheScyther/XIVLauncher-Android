using System;
using System.IO;

namespace XlaBoot;

/// <summary>
/// The launcher's own log, shown as "Launcher" on the Logs page and included in a log export. It holds the things a
/// player cannot see for themselves: why the app died last time, when a launch started, what the runtime did.
/// Everything here also goes to logcat, but a phone user has no way to read that.
/// </summary>
public static class AppLog
{
    /// <summary>Kept small: this is read on a phone and pasted into bug reports, not archived.</summary>
    private const long MaxBytes = 128 * 1024;

    public static string Path(string filesDir) => System.IO.Path.Combine(filesDir, "launcher.log");

    public static void Note(string message)
    {
        Console.WriteLine($"XlaLauncher: {message}");
        try
        {
            var path = Path(AppHost.FilesDir);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            Trim(path);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be written must never stop a launch.
        }
    }

    /// <summary>Drops the oldest half once the file grows past the cap, on a line boundary.</summary>
    private static void Trim(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= MaxBytes)
            return;
        var text = File.ReadAllText(path);
        var cut = text.IndexOf('\n', text.Length / 2);
        File.WriteAllText(path, cut >= 0 ? text[(cut + 1)..] : "");
    }
}
