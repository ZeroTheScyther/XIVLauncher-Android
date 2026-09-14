using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.Internal;

namespace XlaBoot.Steam;

/// <summary>
/// A credential-free check that the Steam client library works on this device, for adb-driven testing
/// (<c>am start --ez xla_steamprobe true</c>, read back from logcat with tag DOTNET).
///
/// Worth having separately from the real sign-in because the two things most likely to fail here fail
/// for reasons that have nothing to do with a Steam account: protobuf-net builds its serializers at
/// runtime, which is the kind of thing Mono on Android breaks, and the phone has to be able to reach
/// Steam's servers at all. Both are checked without anyone typing a password.
/// </summary>
public static class SteamProbe
{
    public static async Task RunAsync()
    {
        Log("starting");

        if (!ProtobufWorks())
            return;

        await ConnectsAsync().ConfigureAwait(false);
        Log("done");
    }

    /// <summary>Serializes a real Steam message, which is what exercises protobuf-net's runtime model.</summary>
    private static bool ProtobufWorks()
    {
        try
        {
            var message = new ClientMsgProtobuf<CMsgClientLogon>(EMsg.ClientLogon);
            message.Body.protocol_version = 65580;
            message.Body.client_language = "english";

            var bytes = message.Serialize();
            if (bytes.Length == 0)
            {
                Log("FAIL protobuf serialize produced no bytes");
                return false;
            }

            Log($"PASS protobuf serialize, {bytes.Length} bytes");
            return true;
        }
        catch (Exception ex)
        {
            Log($"FAIL protobuf serialize: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Reaches a Steam server and completes the encryption handshake. No account involved.</summary>
    private static async Task ConnectsAsync()
    {
        // SteamKit says why it gave up only through its own debug log; DisconnectedCallback carries no reason.
        DebugLog.AddListener((category, message) => Log($"  [{category}] {message}"));
        DebugLog.Enabled = true;

        var client = new SteamClient(SteamSession.Configuration);
        var manager = new CallbackManager(client);
        var connected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = connected;

        manager.Subscribe<SteamClient.ConnectedCallback>(_ => pending.TrySetResult("connected"));
        manager.Subscribe<SteamClient.DisconnectedCallback>(_ => pending.TrySetResult("disconnected"));

        using var stop = new CancellationTokenSource();
        var pump = Task.Factory.StartNew(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(250)); }
                catch (Exception ex) { Log($"pump: {ex.GetType().Name}: {ex.Message}"); return; }
            }
        }, TaskCreationOptions.LongRunning);

        var clock = Stopwatch.StartNew();
        try
        {
            // Same retry the real sign-in does: one Connect() is one server, and plenty are unreachable.
            for (var attempt = 1; attempt <= 6; attempt++)
            {
                connected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending = connected;

                client.Connect();
                var outcome = await connected.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

                if (outcome == "connected")
                {
                    Log($"PASS reached Steam in {clock.ElapsedMilliseconds}ms, after {attempt} attempt(s)");
                    return;
                }

                Log($"  attempt {attempt} did not connect; trying another server");
            }

            Log($"FAIL could not reach Steam in {clock.ElapsedMilliseconds}ms over 6 servers");
        }
        catch (TimeoutException)
        {
            Log("FAIL could not reach Steam within 30s");
        }
        catch (Exception ex)
        {
            Log($"FAIL connect: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { client.Disconnect(); } catch { /* already gone */ }
            stop.Cancel();
            try { await pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* fine */ }
        }
    }

    private static void Log(string message) => Console.WriteLine($"XlaSteamProbe: {message}");
}
