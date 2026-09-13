using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Android.Content;
using Android.OS;

namespace XlaBoot.Android;

/// <summary>
/// Sends the device's battery to the in-game helper plugin.
///
/// The game cannot see the phone it runs on: Android denies apps /sys/class/power_supply, so Wine reports no
/// battery at all and nothing in game can show one. The app can read it, and the game is a child of this same
/// process, so a loopback datagram carries it across - the same shape as the mouse helper's bridge, minus the
/// handshake. Nothing waits on this and nothing fails if the plugin is missing; the packets simply go nowhere.
/// </summary>
public sealed class DeviceStatusSender : IDisposable
{
    /// <summary>The port XlaAndroidHelper listens on, next to the mouse helper's pair (7946/7947).</summary>
    private const int HelperPort = 7948;

    /// <summary>Android only broadcasts on a change, so a quiet hour would look like a dead launcher.</summary>
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(45);

    private readonly Context context;
    private readonly BatteryWatcher watcher;
    private readonly UdpClient socket = new();
    private readonly System.Threading.Timer heartbeat;
    private string lastSent = "";

    public DeviceStatusSender(Context context)
    {
        this.context = context;
        socket.Connect(new IPEndPoint(IPAddress.Loopback, HelperPort));
        watcher = new BatteryWatcher(Send);
        context.RegisterReceiver(watcher, new IntentFilter(Intent.ActionBatteryChanged));
        heartbeat = new System.Threading.Timer(_ => Resend(), null, Heartbeat, Heartbeat);
    }

    private void Send(Intent intent)
    {
        var level = intent.GetIntExtra(BatteryManager.ExtraLevel, -1);
        var scale = intent.GetIntExtra(BatteryManager.ExtraScale, -1);
        var percent = level >= 0 && scale > 0 ? level * 100 / scale : -1;
        var plugged = intent.GetIntExtra(BatteryManager.ExtraPlugged, 0) != 0;
        // Tenths of a degree, and some devices report nothing at all.
        var tenths = intent.GetIntExtra(BatteryManager.ExtraTemperature, 0);

        lastSent = "{\"v\":1,\"battery\":" + percent
                   + ",\"charging\":" + (plugged ? "true" : "false")
                   + ",\"temperature\":" + (tenths / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                   + "}";
        Resend();
    }

    private void Resend()
    {
        if (lastSent.Length == 0)
            return;
        try
        {
            var datagram = Encoding.UTF8.GetBytes(lastSent);
            socket.Send(datagram, datagram.Length);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // Nothing is listening (no plugin, or the game is closed). Not worth a line in the log every 45 s.
        }
    }

    public void Dispose()
    {
        heartbeat.Dispose();
        try { context.UnregisterReceiver(watcher); }
        catch (Java.Lang.IllegalArgumentException) { /* already gone with the activity */ }
        socket.Dispose();
    }

    /// <summary>Android's battery broadcast. Sticky, so the first callback arrives on registration.</summary>
    private sealed class BatteryWatcher : BroadcastReceiver
    {
        private readonly Action<Intent> onChanged;

        public BatteryWatcher(Action<Intent> onChanged) => this.onChanged = onChanged;

        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent != null)
                onChanged(intent);
        }
    }
}
