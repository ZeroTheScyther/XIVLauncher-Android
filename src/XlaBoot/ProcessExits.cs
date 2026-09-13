using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XlaBoot;

/// <summary>How the app's process ended, as Android recorded it.</summary>
/// <param name="When">When the process died.</param>
/// <param name="Reason">Android's reason, already turned into a sentence for the player.</param>
/// <param name="Detail">Android's own description, plus the process size where it is known.</param>
/// <param name="OutOfMemory">The system killed it to reclaim memory.</param>
public sealed record ProcessExit(DateTime When, string Reason, string Detail, bool OutOfMemory);

/// <summary>
/// Why the app disappeared last time. When Android kills the process - which is what happens on a phone with too
/// little memory to hold the game - nothing can be logged at the time: the process is simply gone, the X server with
/// it, and the player sees the game vanish. Android keeps the reason, so it is written to the launcher log on the
/// next start, where the Logs page and a log export can show it.
/// </summary>
public static class ProcessExits
{
    /// <summary>Set by MainActivity: the recent exits Android recorded for this app, newest first.</summary>
    public static Func<IReadOnlyList<ProcessExit>>? Recent;

    private static string StampPath => Path.Combine(AppHost.FilesDir, "last-exit-noted");

    /// <summary>
    /// Writes any exit the launcher has not already reported to the launcher log. Called once per start.
    /// </summary>
    public static void Report()
    {
        List<ProcessExit> exits;
        try
        {
            exits = (Recent?.Invoke() ?? Array.Empty<ProcessExit>()).OrderBy(e => e.When).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"XlaLauncher: could not read exit reasons: {ex.GetType().Name}");
            return;
        }
        if (exits.Count == 0)
            return;

        var since = LastNoted();
        foreach (var exit in exits.Where(e => e.When > since))
            AppLog.Note($"Previous session ended {exit.When:yyyy-MM-dd HH:mm:ss}: {exit.Reason}"
                        + (exit.Detail.Length > 0 ? $" [{exit.Detail}]" : ""));
        Remember(exits[^1].When);
    }

    /// <summary>The newest recorded exit, for a bug report's header. Empty when Android has nothing.</summary>
    public static string Newest()
    {
        try
        {
            var exit = (Recent?.Invoke() ?? Array.Empty<ProcessExit>()).OrderByDescending(e => e.When).FirstOrDefault();
            return exit == null ? "" : $"{exit.When:yyyy-MM-dd HH:mm:ss} {exit.Reason}"
                                       + (exit.Detail.Length > 0 ? $" [{exit.Detail}]" : "");
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static DateTime LastNoted()
    {
        try
        {
            if (File.Exists(StampPath) && long.TryParse(File.ReadAllText(StampPath).Trim(), out var ticks))
                return new DateTime(ticks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return DateTime.MinValue;
    }

    private static void Remember(DateTime when)
    {
        try
        {
            File.WriteAllText(StampPath, when.Ticks.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
