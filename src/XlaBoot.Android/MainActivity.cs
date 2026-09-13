using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using Avalonia;
using Avalonia.Android;
using XlaBoot;

namespace XlaBoot.Android;

[Activity(
    Label = "XIVLauncher",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@mipmap/appicon",
    RoundIcon = "@mipmap/appicon_round",
    MainLauncher = true,
    // Deliberately NOT SingleTask: the storage permission dialog launched from a single-task activity
    // opened in its own task, was cancelled 16 ms later with no UI, and the result never came back - the login
    // hung on "Waiting for storage permission". The second-instance crash SingleTask was hiding is
    // handled in OnCreate instead. SingleTop keeps a re-launch of an already-resumed activity in OnNewIntent.
    LaunchMode = LaunchMode.SingleTop,
    // AdjustResize, not the theme's adjustPan: the whole UI is one surface, so Android cannot know where the text
    // field is and pans the window by a guess, which hid the password box behind the keyboard. Resizing gives
    // Avalonia a smaller window instead, and MainView scrolls the focused field into what is left.
    WindowSoftInputMode = SoftInput.StateUnspecified | SoftInput.AdjustResize,
    // The X server lives and dies with this activity (OnDestroy stops it), so any config change
    // that would recreate the activity kills the game. A Bluetooth keyboard disconnecting
    // (CONFIG_KEYBOARD/KEYBOARD_HIDDEN) did exactly that; handle every change that can occur mid-game.
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode
        | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : AvaloniaMainActivity<App>
{
    /// <summary>Guest-visible root. Wine sees its /tmp here, so the X socket lands in it.</summary>
    private string RootPath => System.IO.Path.Combine(FilesDir!.AbsolutePath, "rootfs");

    /// <summary>The live instance, so a newer one can take the shared MainView away from it.</summary>
    private static System.WeakReference<MainActivity>? _current;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Avalonia hands every activity the same MainView and only detaches it in OnDestroy. When Android creates a
        // second MainActivity while the first is still alive, attaching that view again throws "already has a
        // visual parent" and kills the app. Detach it from the older instance first.
        if (_current != null && _current.TryGetTarget(out var previous) && previous != this && !previous.IsDestroyed)
            previous.Content = null;
        base.OnCreate(savedInstanceState);
        _current = new System.WeakReference<MainActivity>(this);

        // Let the window span the display cutout, so the strip beside the camera in landscape is ours
        // to paint (AppHost.SetWindowBackground fills it with whatever Avalonia paints) instead of a
        // system letterbox over the activity theme's white background. Insets and the soft
        // keyboard are left alone on purpose: decorFitsSystemWindows stays true here, unlike the game
        // view, because the login form needs the window to resize for the keyboard.
        if (System.OperatingSystem.IsAndroidVersionAtLeast(28) && Window?.Attributes is { } attributes)
        {
            attributes.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
            Window.Attributes = attributes;
        }
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        var vm = typeof(XlaBoot.ViewModels.MainViewModel);
        XlaBoot.ViewModels.MainViewModel.NativeLibDir = ApplicationInfo!.NativeLibraryDir!;
        XlaBoot.ViewModels.MainViewModel.FilesDir = FilesDir!.AbsolutePath;
        XlaBoot.ViewModels.MainViewModel.ShowXServer = ShowXServer;
        XlaBoot.ViewModels.MainViewModel.LoadCredentials = LoadCredentials;
        XlaBoot.ViewModels.MainViewModel.SaveCredentials = SaveCredentials;
#if DEBUG
        // adb test hooks (am start --ez xla_autorun|xla_autologin true). Release builds ignore intent extras.
        XlaBoot.ViewModels.MainViewModel.AutoRunWineTest = Intent?.GetBooleanExtra("xla_autorun", false) ?? false;
        XlaBoot.ViewModels.MainViewModel.AutoLogin = Intent?.GetBooleanExtra("xla_autologin", false) ?? false;
#endif

        // Settings screen: the same SharedPreferences the in-game menu uses, and Java's regeneration
        // of files/xla-settings.sh after every change (see xla.XlaSettings for who owns which key).
        AppHost.GetSetting = key =>
        {
            try { return Prefs.GetString(key, null); }
            catch (Java.Lang.ClassCastException) { return null; } // a key that used to hold another type
        };
        AppHost.SetSetting = (key, value) => Prefs.Edit()!.PutString(key, value)!.Apply();
        AppHost.FlushSettings = () => XServerHost.SyncSettings(this);
        AppHost.PickFolder = PickFolder;
        AppHost.PickFile = PickArchive;
        AppHost.SaveFileAs = SaveFileAs;
        AppHost.DeviceDescription = DescribeDevice();
        XlaBoot.ProcessExits.Recent = RecentExits;
        AppHost.RequestStoragePermission = RequestStoragePermission;
        AppHost.CacheDir = CacheDir!.AbsolutePath;
        try
        {
            var info = PackageManager!.GetPackageInfo(PackageName!, 0)!;
            AppHost.VersionCode = System.OperatingSystem.IsAndroidVersionAtLeast(28) ? info.LongVersionCode : info.VersionCode;
            AppHost.VersionName = info.VersionName ?? "";
        }
        catch (System.Exception) { /* no update check without a known version */ }
        AppHost.OpenUrl = uri => RunOnUiThread(() =>
        {
            try { StartActivity(new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(uri.AbsoluteUri))); }
            catch (System.Exception e) { System.Console.WriteLine("XlaLauncher: no browser: " + e.GetType().Name); }
        });

        // StatFs rather than DriveInfo: on Android's emulated storage DriveInfo is unreliable, and
        // upstream's own space check (PlatformHelpers.GetDiskFreeSpace -> new DriveInfo) is the most
        // likely thing in the patch path to throw here.
        AppHost.FreeBytes = path =>
        {
            try
            {
                var stat = new global::Android.OS.StatFs(path);
                return stat.AvailableBlocksLong * stat.BlockSizeLong;
            }
            catch (System.Exception)
            {
                return 0; // unknown; the caller treats 0 as "do not block on this"
            }
        };

        AppHost.ReadAsset = name =>
        {
            try
            {
                using var asset = Assets!.Open(name);
                using var buffer = new System.IO.MemoryStream();
                asset.CopyTo(buffer);
                return buffer.ToArray();
            }
            catch (System.Exception e)
            {
                System.Console.WriteLine($"XlaLauncher: asset {name} unavailable: {e.GetType().Name}");
                return null;
            }
        };

        // Long work must be paired with a foreground service or Android freezes the process; see
        // KeepAliveService. Throttled because progress fires several times a second and each update is
        // a notification rebuild.
        var context = ApplicationContext!;
        var keepAliveTitle = "Setting up";
        AppHost.StartKeepAlive = title =>
        {
            keepAliveTitle = title;
            KeepAliveService.Start(context, title);
        };
        AppHost.StopKeepAlive = () => KeepAliveService.Stop(context);
        var lastNotified = System.DateTime.MinValue;
        AppHost.UpdateKeepAlive = (step, fraction) =>
        {
            var now = System.DateTime.UtcNow;
            if ((now - lastNotified).TotalMilliseconds < 700)
                return;
            lastNotified = now;
            KeepAliveService.Start(context, keepAliveTitle, step, fraction);
        };

        AppHost.IsBackgroundAllowed = () =>
        {
            try
            {
                var power = (global::Android.OS.PowerManager?)GetSystemService(PowerService);
                return power?.IsIgnoringBatteryOptimizations(PackageName!) ?? true;
            }
            catch (System.Exception)
            {
                return true; // cannot tell; do not nag
            }
        };

        AppHost.RequestBackgroundAllowed = () => RunOnUiThread(() =>
        {
            try
            {
                StartActivity(new Intent(
                    global::Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations,
                    global::Android.Net.Uri.Parse("package:" + PackageName)));
            }
            catch (System.Exception e)
            {
                System.Console.WriteLine("XlaLauncher: battery exemption dialog unavailable: " + e.GetType().Name);
            }
        });

        AppHost.RequestAllFilesAccess = RequestAllFilesAccess;

        AppHost.SetWindowBackground = argb => RunOnUiThread(() =>
        {
            System.Console.WriteLine($"XlaLauncher: window background -> #{argb:X8}");
            Window?.SetBackgroundDrawable(new global::Android.Graphics.Drawables.ColorDrawable(
                new global::Android.Graphics.Color(unchecked((int)argb))));
        });
        try { XServerHost.SyncSettings(this); }
        catch (System.Exception e) { System.Console.WriteLine("XlaLauncher: settings sync failed: " + e.GetType().Name); }

        XlaBoot.ViewModels.MainViewModel.ExternalProbe = () =>
        {
            try
            {
                var ext = GetExternalFilesDir(null)?.AbsolutePath ?? "(null)";
                var pub = global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath ?? "(null)";
                var state = global::Android.OS.Environment.ExternalStorageState;
                var mgr = global::Android.OS.Environment.IsExternalStorageManager;
                return $"extFiles={ext} pub={pub} state={state} isMgr={mgr}";
            }
            catch (System.Exception e) { return e.GetType().Name + ": " + e.Message; }
        };

        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }

    /// <summary>
    /// Swaps the Avalonia UI out for the vendored X server's renderer view and starts listening.
    /// Returns the socket path so the caller can confirm the guest will find it.
    /// Blocks until the UI thread has done the work.
    /// </summary>
    private string ShowXServer(int width, int height)
    {
        var done = new System.Threading.ManualResetEventSlim(false);
        string result = "";

        RunOnUiThread(() =>
        {
            try
            {
                Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
                RequestedOrientation = ScreenOrientation.SensorLandscape;

                // The launcher wants AdjustResize so the keyboard cannot cover a text field, but the game view is
                // the X screen: resizing it under a running game would change the screen size mid-session. A soft
                // keyboard shown over the game must overlay it instead.
                Window?.SetSoftInputMode(SoftInput.AdjustNothing);

                // The X view is the whole game screen. Paint the window black so the cutout/nav-bar insets
                // stop showing the theme's white background; XServerHost.Create goes immersive.
                Window?.SetBackgroundDrawable(
                    new global::Android.Graphics.Drawables.ColorDrawable(global::Android.Graphics.Color.Black));

                // Renderer comes from the settings screen: OpenGL is the fallback for a device the
                // Vulkan (zero-copy scanout) path cannot drive.
                var useGl = AppHost.Get("renderer", "VULKAN") == "GL";
                var view = XServerHost.Create(this, width, height, useGl);
                SetContentView(view, new ViewGroup.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

                XServerHost.Start(RootPath);
                result = XServerHost.SocketPath(RootPath);
            }
            catch (System.Exception e)
            {
                result = "ERROR " + e.GetType().Name + ": " + e.Message;
            }
            finally { done.Set(); }
        });

        done.Wait(15000);
        return result;
    }

    // ---- Settings store and pickers --------------------------------------------------------------

    private ISharedPreferences Prefs =>
        GetSharedPreferences("xla_settings", FileCreationMode.Private)!;

    // Kept clear of Avalonia's own request codes, which start from 0.
    private const int RequestPickFolder = 9101;
    private const int RequestPickArchive = 9102;
    private const int RequestSaveFile = 9105;
    private const int RequestStorage = 9103;

    private TaskCompletionSource<Intent?>? _pending;
    private TaskCompletionSource<bool>? _pendingStorage;

    private const int RequestAllFiles = 9104;
    private TaskCompletionSource? _pendingAllFiles;

    /// <summary>
    /// Android's per-app "All files access" page. The only grant that lets an app write to a removable SD
    /// card on Android 11+; there is no in-app dialog for it, so the result is simply the user coming back.
    /// </summary>
    private Task RequestAllFilesAccess()
    {
        if (!System.OperatingSystem.IsAndroidVersionAtLeast(30))
            return Task.CompletedTask;
        _pendingAllFiles?.TrySetResult();
        _pendingAllFiles = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            StartActivityForResult(new Intent(
                global::Android.Provider.Settings.ActionManageAppAllFilesAccessPermission,
                global::Android.Net.Uri.Parse("package:" + PackageName)), RequestAllFiles);
        }
        catch (System.Exception e)
        {
            System.Console.WriteLine("XlaLauncher: all-files access page unavailable: " + e.GetType().Name);
            _pendingAllFiles.TrySetResult();
        }
        return _pendingAllFiles.Task;
    }

    private Task<Intent?> Pick(Intent intent, int requestCode)
    {
        _pending?.TrySetResult(null); // a picker was already open; whoever waited on it gets nothing
        _pending = new TaskCompletionSource<Intent?>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartActivityForResult(intent, requestCode);
        return _pending.Task;
    }

    /// <summary>
    /// Asks for the storage permission. Resolves to the access that actually resulted, probed rather
    /// than inferred from the dialog's answer: either this pair or All-files-access grants the same
    /// access, so a "denied" result can still mean the app can read the volume.
    /// </summary>
    private Task<bool> RequestStoragePermission()
    {
        if (XlaBoot.GameLocation.HasStorageAccess())
            return Task.FromResult(true);

        // Only one dialog at a time; a second caller waits on the first.
        if (_pendingStorage != null)
            return _pendingStorage.Task;

        _pendingStorage = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            RequestPermissions(new[]
            {
                global::Android.Manifest.Permission.ReadExternalStorage,
                global::Android.Manifest.Permission.WriteExternalStorage,
            }, RequestStorage);
        }
        catch (System.Exception e)
        {
            System.Console.WriteLine("XlaLauncher: storage permission request failed: " + e.GetType().Name);
            var failed = _pendingStorage;
            _pendingStorage = null;
            failed.TrySetResult(XlaBoot.GameLocation.HasStorageAccess());
        }
        return _pendingStorage?.Task ?? Task.FromResult(XlaBoot.GameLocation.HasStorageAccess());
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions,
        global::Android.Content.PM.Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != RequestStorage)
            return;
        var pending = _pendingStorage;
        _pendingStorage = null;
        // The grant takes effect in this process immediately - verified on device, pid unchanged - so
        // the access probe is meaningful right now and there is nothing to restart.
        pending?.TrySetResult(XlaBoot.GameLocation.HasStorageAccess());
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == RequestAllFiles)
        {
            var waiting = _pendingAllFiles;
            _pendingAllFiles = null;
            waiting?.TrySetResult();
            return;
        }
        if (requestCode != RequestPickFolder && requestCode != RequestPickArchive && requestCode != RequestSaveFile)
            return;
        var pending = _pending;
        _pending = null;
        pending?.TrySetResult(resultCode == Result.Ok ? data : null);
    }

    /// <summary>
    /// Android's folder picker, resolved to a real filesystem path - which is what both the prefix's
    /// A: symlink and XIVLauncher.Common's DirectoryInfo need, and this app can use because
    /// targetSdk 28 plus MANAGE_EXTERNAL_STORAGE give it an ordinary view of /storage.
    /// </summary>
    private async Task<string?> PickFolder()
    {
        var data = await Pick(new Intent(Intent.ActionOpenDocumentTree), RequestPickFolder);
        var uri = data?.Data;
        if (uri == null)
            return null;

        // A tree document id is "<volume>:<path within it>": "primary" is internal storage, anything
        // else is the volume's name under /storage (an SD card).
        var documentId = global::Android.Provider.DocumentsContract.GetTreeDocumentId(uri);
        var parts = documentId?.Split(':', 2);
        if (parts == null || parts.Length == 0)
            throw new System.InvalidOperationException("That folder is not on device storage; pick one under Internal storage or an SD card.");

        var root = parts[0].Equals("primary", System.StringComparison.OrdinalIgnoreCase)
            ? global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath
            : "/storage/" + parts[0];
        var path = parts.Length > 1 && parts[1].Length > 0 ? root + "/" + parts[1] : root;

        if (path == null || !System.IO.Directory.Exists(path))
            throw new System.InvalidOperationException("That folder is not on device storage; pick one under Internal storage or an SD card.");
        return path;
    }

    /// <summary>
    /// Android's file picker, copied into the cache dir so it can be read as an ordinary file. The
    /// picked file can live behind any content provider (Downloads, Drive), which has no path at all,
    /// so a copy is the only way to hand it to a plain reader.
    /// </summary>
    private async Task<PickedFile?> PickArchive()
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        var data = await Pick(intent, RequestPickArchive);
        var uri = data?.Data;
        if (uri == null)
            return null;

        var target = System.IO.Path.Combine(CacheDir!.AbsolutePath, "import.zip");
        using (var input = ContentResolver!.OpenInputStream(uri)
                           ?? throw new System.InvalidOperationException("That file could not be opened."))
        using (var output = System.IO.File.Create(target))
            await input.CopyToAsync(output);

        return new PickedFile(target, DisplayName(uri) ?? "the file");
    }

    /// <summary>
    /// Android's "save as" document picker (ACTION_CREATE_DOCUMENT): the user chooses where the file goes - Downloads,
    /// Drive, anywhere a provider offers - and the local file is copied there. Needs no storage permission.
    /// </summary>
    private async Task<string?> SaveFileAs(string suggestedName, string mimeType, string localPath)
    {
        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType(mimeType);
        intent.PutExtra(Intent.ExtraTitle, suggestedName);
        var data = await Pick(intent, RequestSaveFile);
        var uri = data?.Data;
        if (uri == null)
            return null;

        using (var output = ContentResolver!.OpenOutputStream(uri, "wt")
                            ?? throw new System.InvalidOperationException("That location could not be written to."))
        using (var input = System.IO.File.OpenRead(localPath))
            await input.CopyToAsync(output);
        return DisplayName(uri) ?? suggestedName;
    }

    /// <summary>
    /// How this app's process ended the last few times, as Android recorded it. This is the only way to explain a
    /// process the system killed - typically out of memory with the game running, where nothing of ours survives to
    /// log anything. Available from Android 11; older devices simply get nothing.
    /// </summary>
    private System.Collections.Generic.IReadOnlyList<XlaBoot.ProcessExit> RecentExits()
    {
        var exits = new System.Collections.Generic.List<XlaBoot.ProcessExit>();
        if (!System.OperatingSystem.IsAndroidVersionAtLeast(30))
            return exits;
        var manager = (global::Android.App.ActivityManager?)GetSystemService(ActivityService);
        var records = manager?.GetHistoricalProcessExitReasons(PackageName, 0, 5);
        if (records == null)
            return exits;

        foreach (var record in records)
        {
            var when = System.DateTimeOffset.FromUnixTimeMilliseconds(record.Timestamp).LocalDateTime;
            var described = record.Description is { Length: > 0 } d ? d : "";
            // The binding hands back a plain int; name it for the switch below.
            var code = (global::Android.App.ApplicationExitInfoReason)record.Reason;
            var outOfMemory = code == global::Android.App.ApplicationExitInfoReason.LowMemory
                // A kill from lmkd is reported as SIGNALED (SIGKILL, status 9) rather than LOW_MEMORY on some
                // builds - Samsung's among them - so the description is what names the killer.
                || (code == global::Android.App.ApplicationExitInfoReason.Signaled
                    && described.Contains("lmk", System.StringComparison.OrdinalIgnoreCase));
            var reason = code switch
            {
                _ when outOfMemory =>
                    "Android closed the app because the phone ran out of memory. Close other apps, "
                    + "turn on RAM Plus, or lower the game resolution and texture settings.",
                global::Android.App.ApplicationExitInfoReason.UserRequested => "closed by the user",
                global::Android.App.ApplicationExitInfoReason.UserStopped => "stopped from Android's settings",
                global::Android.App.ApplicationExitInfoReason.ExitSelf => "the app exited normally",
                global::Android.App.ApplicationExitInfoReason.Crash => "the app crashed (unhandled exception)",
                global::Android.App.ApplicationExitInfoReason.CrashNative => "a native crash (segfault or abort)",
                global::Android.App.ApplicationExitInfoReason.Anr => "Android closed the app after it stopped responding",
                global::Android.App.ApplicationExitInfoReason.ExcessiveResourceUsage => "Android closed the app for using too many resources",
                global::Android.App.ApplicationExitInfoReason.Freezer => "Android froze and then closed the app in the background",
                global::Android.App.ApplicationExitInfoReason.Signaled => "the process was killed (signal)",
                global::Android.App.ApplicationExitInfoReason.DependencyDied => "a process it depends on died",
                global::Android.App.ApplicationExitInfoReason.PackageUpdated => "the app was updated",
                _ => $"ended ({code})",
            };
            // Android's description is often empty; the process size is often the only useful part.
            var parts = new System.Collections.Generic.List<string>();
            if (described.Length > 0)
                parts.Add(described);
            if (record.Rss > 0)
                parts.Add($"using {record.Rss / 1024.0:F0} MB");
            exits.Add(new XlaBoot.ProcessExit(when, reason, string.Join(", ", parts), outOfMemory));
        }
        return exits;
    }

    /// <summary>
    /// The bug-report header's device lines. All of it is public build/system info: no permission is involved.
    /// Locals rather than inline, because "global::" inside an interpolation hole reads as a format specifier.
    /// </summary>
    private string DescribeDevice()
    {
        var manufacturer = global::Android.OS.Build.Manufacturer;
        var model = global::Android.OS.Build.Model;
        var device = global::Android.OS.Build.Device;
        var release = global::Android.OS.Build.VERSION.Release;
        var api = (int)global::Android.OS.Build.VERSION.SdkInt;
        var abi = string.Join(", ", global::Android.OS.Build.SupportedAbis ?? System.Array.Empty<string>());
        var lines = $"Device: {manufacturer} {model} ({device})\n";

        // SOC_MANUFACTURER/SOC_MODEL exist from Android 12; before that the board name is the best there is.
        if (System.OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            var socMaker = global::Android.OS.Build.SocManufacturer;
            var socModel = global::Android.OS.Build.SocModel;
            lines += $"SoC: {socMaker} {socModel}{XlaBoot.DeviceInfo.FriendlySoc(socModel)}\n";
        }
        else
        {
            var board = global::Android.OS.Build.Hardware;
            lines += $"SoC: {board}\n";
        }

        try
        {
            var memory = new global::Android.App.ActivityManager.MemoryInfo();
            ((global::Android.App.ActivityManager)GetSystemService(ActivityService)!).GetMemoryInfo(memory);
            var totalGb = memory.TotalMem / 1073741824.0;
            lines += $"RAM: {totalGb:F1} GB\n";
        }
        catch (System.Exception) { /* optional */ }

        return lines + $"Android: {release} (API {api}), ABI {abi}";
    }

    private string? DisplayName(global::Android.Net.Uri uri)
    {
        try
        {
            using var cursor = ContentResolver!.Query(uri, null, null, null, null);
            if (cursor == null || !cursor.MoveToFirst())
                return null;
            var column = cursor.GetColumnIndex(global::Android.Provider.IOpenableColumns.DisplayName);
            return column >= 0 ? cursor.GetString(column) : null;
        }
        catch (System.Exception)
        {
            // Providers are not obliged to answer; the name is only used in a status line.
            return null;
        }
    }

    // ---- Back / Esc ----------------------------------------------------------------------------

    /// <summary>Same minimum hold as XTouchHandler: FFXIV samples input once per frame.</summary>
    private const long MinKeyHoldMs = 100;

    private long _escDownAt;

    /// <summary>
    /// While the game is up, xla.XServerHost sees keys first: the in-game menu, the controller's Guide
    /// button (opens the menu), controller buttons (Wine virtual pad; consuming them also stops Android
    /// from turning B/Circle into Back) and Back (edge-swipe gesture, nav button: opens the menu).
    /// Back must never reach Activity.finish(): OnDestroy stops the X server and kills the game.
    /// A physical Esc key goes to the game as Esc - what FFXIV uses to skip cutscenes and close menus.
    /// </summary>
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e != null && IsXServerRunning() && XServerHost.HandleKeyEvent(e))
            return true;

        if (e != null && e.KeyCode == Keycode.Back && IsXServerRunning())
            return true;

        if (e != null && e.KeyCode == Keycode.Escape && IsXServerRunning())
        {
            if (e.Action == KeyEventActions.Down && e.RepeatCount == 0)
            {
                _escDownAt = e.EventTime;
                XServerHost.InjectKey("KEY_ESC", true);
            }
            else if (e.Action == KeyEventActions.Up)
            {
                var remaining = MinKeyHoldMs - (e.EventTime - _escDownAt);
                if (remaining <= 0)
                    XServerHost.InjectKey("KEY_ESC", false);
                else
                    new Handler(Looper.MainLooper!).PostDelayed(() => XServerHost.InjectKey("KEY_ESC", false), remaining);
            }
            return true;
        }
        return base.DispatchKeyEvent(e);
    }

    public override bool DispatchGenericMotionEvent(MotionEvent? e)
    {
        if (e != null && IsXServerRunning() && XServerHost.HandleGenericMotionEvent(e))
            return true;
        return base.DispatchGenericMotionEvent(e);
    }

    private static bool IsXServerRunning()
    {
        try { return XServerHost.IsRunning; }
        catch { return false; }
    }

    // ---- Saved login -------------------------------------------------------------------------
    // Username is stored as-is; the password is AES-GCM encrypted with a non-exportable key that
    // lives in the Android Keystore, so the file in app-private storage is useless on its own.

    private const string CredentialKeyAlias = "xla_login";

    private string CredentialsPath => System.IO.Path.Combine(FilesDir!.AbsolutePath, "login.json");

    private sealed class StoredLogin
    {
        public string User { get; set; } = "";
        public string? Iv { get; set; }
        public string? Password { get; set; }
        public bool UseOtp { get; set; }
    }

    private static Java.Security.IKey GetOrCreateCredentialKey()
    {
        var keyStore = Java.Security.KeyStore.GetInstance("AndroidKeyStore")!;
        keyStore.Load(null);
        if (keyStore.ContainsAlias(CredentialKeyAlias))
            return keyStore.GetKey(CredentialKeyAlias, null)!;

        var generator = Javax.Crypto.KeyGenerator.GetInstance(
            global::Android.Security.Keystore.KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
        generator.Init(new global::Android.Security.Keystore.KeyGenParameterSpec.Builder(CredentialKeyAlias,
                global::Android.Security.Keystore.KeyStorePurpose.Encrypt | global::Android.Security.Keystore.KeyStorePurpose.Decrypt)
            .SetBlockModes(global::Android.Security.Keystore.KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(global::Android.Security.Keystore.KeyProperties.EncryptionPaddingNone)!
            .Build());
        return generator.GenerateKey()!;
    }

    private XlaBoot.ViewModels.SavedLogin? LoadCredentials()
    {
        try
        {
            if (!System.IO.File.Exists(CredentialsPath))
                return null;

            var stored = System.Text.Json.JsonSerializer.Deserialize<StoredLogin>(System.IO.File.ReadAllText(CredentialsPath));
            if (stored == null)
                return null;
            if (stored.Iv == null || stored.Password == null)
                return new(stored.User, "", stored.UseOtp);

            var cipher = Javax.Crypto.Cipher.GetInstance("AES/GCM/NoPadding")!;
            cipher.Init(Javax.Crypto.CipherMode.DecryptMode, GetOrCreateCredentialKey(),
                new Javax.Crypto.Spec.GCMParameterSpec(128, System.Convert.FromBase64String(stored.Iv)));
            var plain = cipher.DoFinal(System.Convert.FromBase64String(stored.Password))!;
            return new(stored.User, System.Text.Encoding.UTF8.GetString(plain), stored.UseOtp);
        }
        catch (System.Exception)
        {
            // A key reset (e.g. after an uninstall) makes the old ciphertext undecryptable; start clean.
            try { System.IO.File.Delete(CredentialsPath); } catch { /* nothing to clean up */ }
            return null;
        }
    }

    /// <summary>null forgets everything; an empty password keeps only the username and OTP setting.</summary>
    private void SaveCredentials(XlaBoot.ViewModels.SavedLogin? login)
    {
        if (login == null)
        {
            System.IO.File.Delete(CredentialsPath);
            return;
        }

        var stored = new StoredLogin { User = login.User, UseOtp = login.UseOtp };
        if (!string.IsNullOrEmpty(login.Password))
        {
            var cipher = Javax.Crypto.Cipher.GetInstance("AES/GCM/NoPadding")!;
            cipher.Init(Javax.Crypto.CipherMode.EncryptMode, GetOrCreateCredentialKey());
            stored.Iv = System.Convert.ToBase64String(cipher.GetIV()!);
            stored.Password = System.Convert.ToBase64String(cipher.DoFinal(System.Text.Encoding.UTF8.GetBytes(login.Password))!);
        }

        System.IO.File.WriteAllText(CredentialsPath, System.Text.Json.JsonSerializer.Serialize(stored));
    }

    protected override void OnResume()
    {
        base.OnResume();
        AppHost.IsInForeground = true;
    }

    protected override void OnPause()
    {
        AppHost.IsInForeground = false;
        base.OnPause();
    }

    /// <summary>Back from the notification shade, recents or another app: hide the system bars again.</summary>
    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus && IsXServerRunning())
            XServerHost.HideSystemBars();
    }

    protected override void OnDestroy()
    {
        try { XServerHost.Stop(); } catch { /* the server may never have been started */ }
        base.OnDestroy();
    }
}
