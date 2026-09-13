using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace XlaBoot.ViewModels;

/// <summary>One log the Logs page can show.</summary>
public sealed partial class LogSource : ObservableObject
{
    public string Label { get; }
    public string Path { get; }

    [ObservableProperty]
    private bool _isSelected;

    public LogSource(string label, string path)
    {
        Label = label;
        Path = path;
    }
}

/// <summary>
/// The Logs page: the tail of Dalamud's and Wine's logs, readable on the phone without adb, and copyable into a bug
/// report. Only logs that exist are offered.
/// </summary>
public partial class LogsViewModel : ViewModelBase
{
    /// <summary>Enough to hold a crash and what led up to it, small enough to lay out instantly.</summary>
    private const int TailBytes = 96 * 1024;

    public ObservableCollection<LogSource> Sources { get; } = new();

    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private string _detail = "";

    public bool HasSources => Sources.Count > 0;

    private string? _selectedPath;

    public void Refresh()
    {
        var files = AppHost.FilesDir;
        var dalamudLogs = System.IO.Path.Combine(files, "xlroaming", "logs");
        var candidates = new[]
        {
            new LogSource("Dalamud", System.IO.Path.Combine(dalamudLogs, "dalamud.log")),
            new LogSource("Dalamud boot", System.IO.Path.Combine(dalamudLogs, "dalamud.boot.log")),
            new LogSource("Injector", System.IO.Path.Combine(dalamudLogs, "dalamud.injector.log")),
            new LogSource("Wine", System.IO.Path.Combine(files, "wine-test.log")),
            new LogSource("X server", System.IO.Path.Combine(files, "xserver.log")),
        };

        Sources.Clear();
        foreach (var source in candidates.Where(c => File.Exists(c.Path)))
            Sources.Add(source);
        OnPropertyChanged(nameof(HasSources));

        var selected = Sources.FirstOrDefault(s => s.Path == _selectedPath) ?? Sources.FirstOrDefault();
        Show(selected);
    }

    [RelayCommand]
    private void Select(LogSource source) => Show(source);

    [RelayCommand]
    private void Reload() => Refresh();

    /// <summary>Zips every log plus the launch settings, then lets the user save the zip wherever they like.</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task Export()
    {
        if (AppHost.SaveFileAs == null)
        {
            Detail = "Saving files isn't available on this platform.";
            return;
        }
        var name = LogExport.SuggestedName(DateTime.Now);
        var zip = System.IO.Path.Combine(AppHost.CacheDir.Length > 0 ? AppHost.CacheDir : System.IO.Path.GetTempPath(), name);
        try
        {
            string driver;
            try { driver = GraphicsDrivers.Selected(GraphicsDrivers.All()).Label; }
            catch (Exception) { driver = "unknown"; }
            var info = $"XIVLauncher Android {AppHost.VersionName} ({AppHost.VersionCode})\n"
                       + $"{AppHost.DeviceDescription}\n"
                       + $"Graphics driver: {driver}\n"
                       + $"Dalamud: {AppHost.Get("dalamud_enabled", "OFF")}\n"
                       + $"Exported {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}\n"
                       + $"Game folder: {MainViewModel.GamePath}\n";
            var count = await System.Threading.Tasks.Task.Run(() => LogExport.Create(AppHost.FilesDir, zip, info));
            if (count == 0)
            {
                Detail = "There are no logs to export yet.";
                return;
            }
            var saved = await AppHost.SaveFileAs(name, "application/zip", zip);
            Detail = saved == null ? "Export cancelled." : $"Saved {count} files to {saved}. Send it our way with your bug report.";
        }
        catch (Exception ex)
        {
            Detail = $"Could not export the logs: {ex.Message}";
        }
        finally
        {
            try { File.Delete(zip); }
            catch (Exception) { /* the cache dir is Android's to reclaim */ }
        }
    }

    private void Show(LogSource? source)
    {
        foreach (var s in Sources)
            s.IsSelected = s == source;
        _selectedPath = source?.Path;
        if (source == null)
        {
            Text = "";
            Detail = "No logs yet. They appear here after the game has been launched.";
            return;
        }

        try
        {
            using var stream = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var start = Math.Max(0, length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[length - start];
            stream.ReadExactly(buffer);
            var text = Encoding.UTF8.GetString(buffer);
            if (start > 0 && text.IndexOf('\n') is var cut and >= 0)
                text = text[(cut + 1)..]; // drop the partial first line
            Text = text.Length > 0 ? text : "(empty)";
            Detail = $"{source.Path}  ·  {length / 1024.0:F0} KB" + (start > 0 ? "  ·  showing the end" : "")
                     + $"  ·  updated {File.GetLastWriteTime(source.Path):HH:mm:ss}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Text = "";
            Detail = $"Could not read {source.Label}: {ex.Message}";
        }
    }
}
