using Android.App;
using Android.Content;
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

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var title = intent?.GetStringExtra(ExtraTitle) ?? "Working";
        var text = intent?.GetStringExtra(ExtraText) ?? "";
        var progress = intent?.GetIntExtra(ExtraProgress, -1) ?? -1;

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

        // Restarted by the system without an intent: there is nothing to resume from here, the app
        // re-checks and resumes on its own.
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        if (_wakeLock is { IsHeld: true })
            _wakeLock.Release();
        _wakeLock = null;
        base.OnDestroy();
    }

    private Notification BuildNotification(string title, string text, int progress)
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (System.OperatingSystem.IsAndroidVersionAtLeast(26) && manager != null)
        {
            // Low importance: informative, never a sound or a heads-up banner.
            var channel = new NotificationChannel(ChannelId, "Setup progress", NotificationImportance.Low);
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
        else
            builder.SetProgress(0, 0, true); // indeterminate until there is something to measure

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

    public static void Stop(Context context) =>
        context.StopService(new Intent(context, typeof(KeepAliveService)));
}
