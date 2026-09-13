using System;
using System.Threading.Tasks;

namespace XlaBoot;

/// <summary>A file the user picked with the platform's file picker, copied somewhere we can read it.</summary>
public sealed record PickedFile(string Path, string Name);

/// <summary>
/// The platform's half of the launcher: everything the shared Avalonia code needs but cannot do
/// itself. MainActivity fills these in, the same way it already does for the saved login and the
/// X server, so the view models stay free of Android references.
/// </summary>
public static class AppHost
{
    /// <summary>
    /// Whether the launcher is on screen. Kept current by MainActivity; true by default so a platform that
    /// never reports it behaves as if the user were watching.
    /// </summary>
    public static volatile bool IsInForeground = true;

    /// <summary>This build's Android versionCode, for the update check. 0 when unknown.</summary>
    public static long VersionCode;

    /// <summary>This build's Android versionName ("0.1.0"), shown on the About page. Empty when unknown.</summary>
    public static string VersionName = "";

    /// <summary>Opens a web page in the user's browser (the update download).</summary>
    public static Action<Uri>? OpenUrl;

    /// <summary>App-private storage root (the Wine runtime, the prefix, the settings file).</summary>
    public static string FilesDir = "/data/user/0/uk.aetherworks.xivlauncher/files";

    /// <summary>Where the APK's own .so files were extracted (libevshim.so lives here).</summary>
    public static string NativeLibDir = "";

    /// <summary>The app's cache dir, where in-flight runtime packages are kept.</summary>
    public static string CacheDir = "";

    /// <summary>
    /// Free bytes on the volume holding a given path. A platform hook because DriveInfo is unreliable on
    /// Android's emulated storage, where StatFs is the dependable answer.
    /// </summary>
    public static Func<string, long>? FreeBytes;

    /// <summary>Reads a file shipped inside the APK (run-wine.sh). Returns null when absent.</summary>
    public static Func<string, byte[]?>? ReadAsset;

    /// <summary>
    /// Keeps the process alive and visible while long work runs, with a progress notification.
    /// Required, not decorative: Samsung's Freecess freezes a backgrounded process and the work simply
    /// stops with nothing thrown and nothing logged.
    /// </summary>
    public static Action<string>? StartKeepAlive;

    /// <summary>Updates the keep-alive notification: step text and 0..1 progress.</summary>
    public static Action<string, double>? UpdateKeepAlive;

    public static Action? StopKeepAlive;

    /// <summary>
    /// Whether the OS will let this app keep working in the background. On Samsung a foreground service
    /// and a wake lock are not enough - Freecess freezes the process and disables its wakelock anyway -
    /// and being on the battery exemption list is what stops that. Null means "cannot tell".
    /// </summary>
    public static Func<bool>? IsBackgroundAllowed;

    /// <summary>Asks the user for that exemption. A single system dialog.</summary>
    public static Action? RequestBackgroundAllowed;

    /// <summary>
    /// Asks for the storage permission and resolves to whether access was granted. One ordinary
    /// runtime dialog is enough: measured on device, READ_+WRITE_EXTERNAL_STORAGE alone grants full
    /// access to the shared volume (All-files-access is an alternative, not an addition), and a grant
    /// takes effect in the running process with no restart - the pid was unchanged across a denied run
    /// and a granted one. So no Settings deep-link and no "restart the app" step.
    /// </summary>
    public static Func<Task<bool>>? RequestStoragePermission;

    /// <summary>
    /// Opens Android's "All files access" page for this app and resolves when the user comes back. Only
    /// needed to write to a removable SD card; internal storage works with the ordinary permission.
    /// </summary>
    public static Func<Task>? RequestAllFilesAccess;

    /// <summary>
    /// Reads one setting, or null when unset. Backed by the same SharedPreferences the in-game menu
    /// uses (xla.XlaSettings), so the two front ends can never disagree.
    /// </summary>
    public static Func<string, string?>? GetSetting;

    /// <summary>Writes one setting.</summary>
    public static Action<string, string>? SetSetting;

    /// <summary>
    /// Regenerates files/xla-settings.sh from the stored settings (xla.XServerHost.syncSettings).
    /// Java owns that file; this is how a change made here reaches run-wine.sh.
    /// </summary>
    public static Action? FlushSettings;

    /// <summary>Android's folder picker. Resolves to a filesystem path, or null if cancelled.</summary>
    public static Func<Task<string?>>? PickFolder;

    /// <summary>Android's file picker. Resolves to a readable local copy, or null if cancelled.</summary>
    public static Func<Task<PickedFile?>>? PickFile;

    /// <summary>
    /// Android's "save as" picker: (suggested name, MIME type, local file) -> the chosen location's display name, or
    /// null if cancelled. Copies the local file there; no storage permission needed.
    /// </summary>
    public static Func<string, string, string, Task<string?>>? SaveFileAs;

    /// <summary>Device, SoC, RAM and Android version lines for bug-report headers. Empty when unknown.</summary>
    public static string DeviceDescription = "";

    /// <summary>
    /// Paints the Android window behind the Avalonia surface, as 0xAARRGGBB. In landscape the display
    /// cutout leaves a strip beside the camera that the app's own surface does not cover, and that
    /// strip shows the activity theme's background - white, against a dark launcher. The
    /// colour is read from what Avalonia actually paints, so it follows the light/dark theme.
    /// </summary>
    public static Action<uint>? SetWindowBackground;

    public static string Get(string key, string fallback)
    {
        var value = GetSetting?.Invoke(key);
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    /// <summary>Writes the setting and mirrors the launch-time settings file, so a change is never half-applied.</summary>
    public static void Set(string key, string value)
    {
        SetSetting?.Invoke(key, value);
        FlushSettings?.Invoke();
    }

    /// <summary>Moves a setting to the next of its allowed values and returns it.</summary>
    public static string Cycle(string key, string[] options)
    {
        var current = Get(key, options[0]);
        var index = Array.IndexOf(options, current);
        var next = options[(index < 0 ? 0 : index + 1) % options.Length];
        Set(key, next);
        return next;
    }
}
