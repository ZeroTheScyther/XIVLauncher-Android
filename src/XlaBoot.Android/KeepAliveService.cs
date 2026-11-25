using Android.App;
using Android.Content;
using Android.Net.Wifi;
using Android.OS;

namespace XlaBoot.Android;

/// <summary>
/// Keeps the process alive, and visible to the user, while provisioning or a game install runs.
///
/// This is load-bearing on this hardware, not defensive: Samsung's Freecess freezes a backgrounded
/// process (<c>FreecessController: FZ ... reason: LEV</c> in logcat) and the work thread simply stops —
/// nothing throws, nothing logs. Two attempts to measure a two-minute decode died that way before the
/// cause was found.
///
/// targetSdk 28 keeps this simple: <c>foregroundServiceType</c> is only required from targetSdk 34, and
/// the POST_NOTIFICATIONS runtime permission only from 33, so a plain startForeground is enough.
/// </summary>
[Service(Exported = false)]
public class KeepAliveService : Service
{
    private const string ChannelId = "xla_progress";
    private const int NotificationId = 4201;

    public const string ExtraTitle = "title";
    public const string ExtraText = "text";
    public const string ExtraProgress = "progress";

    private PowerManager.WakeLock? _wakeLock;
    private WifiManager.WifiLock? _wifiLock;
    private bool _gameSession;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var title = intent?.GetStringExtra(ExtraTitle) ?? XlaBoot.Localization.Strings.Notif_Working;
        var text = intent?.GetStringExtra(ExtraText) ?? "";
        var progress = intent?.GetIntExtra(ExtraProgress, -1) ?? -1;
        if (progress == GameSession)
            _gameSession = true;

        StartForeground(NotificationId, BuildNotification(title, text, progress));

        // A partial wake lock keeps the CPU running with the screen off; the download and the ZiPatch
        // apply are both long enough to outlast the display.
        if (_wakeLock == null)
        {
            var power = (PowerManager?)GetSystemService(PowerService);
            _wakeLock = power?.NewWakeLock(WakeLockFlags.Partial, "xla:provision");
            _wakeLock?.SetReferenceCounted(false);
            _wakeLock?.Acquire();
        }

        // The wake lock keeps the CPU up, not the radio: with the screen off, Wi-Fi drops into power save
        // and an overnight download crawls. HIGH_PERF is the only mode that holds with the screen off
        // (LOW_LATENCY needs it on); deprecated in API 34 but still the documented way to ask.
        if (_wifiLock == null)
        {
            var wifi = (WifiManager?)ApplicationContext?.GetSystemService(WifiService);
#pragma warning disable CA1422
            _wifiLock = wifi?.CreateWifiLock(global::Android.Net.WifiMode.FullHighPerf, "xla:provision");
#pragma warning restore CA1422
            _wifiLock?.SetReferenceCounted(false);
            _wifiLock?.Acquire();
        }

        // Restarted by the system without an intent: there is nothing to resume from here, the app
        // re-checks and resumes on its own.
        return StartCommandResult.NotSticky;
    }

    /// <summary>
    /// The app was swiped out of recents. The foreground service keeps this process (and with it Wine and the
    /// game) alive past that, so a game session is ended here; otherwise it runs on unseen, eating the battery.
    /// Provisioning and installs are left alone: surviving the task is what this service is for.
    /// </summary>
    public override void OnTaskRemoved(Intent? rootIntent)
    {
        if (_gameSession)
        {
            // Stopped first: a process killed with this service still started is brought back by Android
            // (start reason "started-service"), notification and all.
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
            XServerHost.EndSessionOnTaskRemoved(this);
        }
        base.OnTaskRemoved(rootIntent);
    }

    public override void OnDestroy()
    {
        if (_wakeLock is { IsHeld: true })
            _wakeLock.Release();
        _wakeLock = null;
        if (_wifiLock is { IsHeld: true })
            _wifiLock.Release();
        _wifiLock = null;
        base.OnDestroy();
    }

    private Notification BuildNotification(string title, string text, int progress)
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (System.OperatingSystem.IsAndroidVersionAtLeast(26) && manager != null)
        {
            // Low importance: informative, never a sound or a heads-up banner.
            var channel = new NotificationChannel(ChannelId, XlaBoot.Localization.Strings.Notif_Channel, NotificationImportance.Low);
            channel.SetShowBadge(false);
            manager.CreateNotificationChannel(channel);
        }

        // Tapping it returns to the launcher rather than starting a second task.
        var open = PendingIntent.GetActivity(this, 0,
            new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.NewTask),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var builder = new Notification.Builder(this, ChannelId)
            .SetContentTitle(title)
            .SetContentText(text)
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetContentIntent(open);

        if (progress >= 0)
            builder.SetProgress(100, progress, false);
        else if (progress != GameSession)
            builder.SetProgress(0, 0, true); // indeterminate until there is something to measure
        if (progress == GameSession)
            builder.SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay);

        return builder.Build();
    }

    // ---- Control surface used by AppHost -------------------------------------------------------

    public static void Start(Context context, string title, string text = "", double fraction = -1)
    {
        var intent = new Intent(context, typeof(KeepAliveService))
            .PutExtra(ExtraTitle, title)
            .PutExtra(ExtraText, text)
            .PutExtra(ExtraProgress, fraction < 0 ? -1 : (int)(fraction * 100));
        context.StartService(intent);
    }

    /// <summary>Progress value for a running game: no progress bar.</summary>
    private const int GameSession = -2;

    /// <summary>
    /// Keeps a game session reachable while the player is in another app. Without a foreground service the
    /// app drops to a background process state, and newer Android (seen on 17) cuts the network for those
    /// (netpolicy: blocked=APP_BACKGROUND, firewall rule "&lt;uid&gt;-background-default" about 5 s after
    /// leaving), so the game loses its connection to the server. The process is replaced when the session
    /// ends (XServerHost.exitGame), which ends the service with it.
    /// </summary>
    public static void StartGameSession(Context context)
    {
        var intent = new Intent(context, typeof(KeepAliveService))
            .PutExtra(ExtraTitle, XlaBoot.Localization.Strings.Notif_GameRunning)
            .PutExtra(ExtraProgress, GameSession);
        context.StartService(intent);
    }

    public static void Stop(Context context) =>
        context.StopService(new Intent(context, typeof(KeepAliveService)));
}
