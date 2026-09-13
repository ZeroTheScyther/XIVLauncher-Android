package xla;

import android.content.Context;
import android.util.Log;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileWriter;
import java.io.InputStreamReader;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.TimeUnit;

/**
 * GameNative's PulseAudio setup (PulseAudioComponent), trimmed: the PulseAudio daemon ships as an APK
 * native lib (libpulseaudio.so is a PIE executable), its modules + pactl live in files/pulseaudio
 * (the runtime's pulseaudio package), and the only sink is module-aaudio-sink. Wine's winepulse
 * connects to the unix socket given here (PULSE_SERVER in run-wine.sh).
 *
 * This is the audio route GameNative uses by default and for its FFXIV container; Wine's ALSA
 * driver fails to open the android_aserver device on this Wine build (-EOPNOTSUPP).
 */
final class PulseAudioServer {
    private static final String TAG = "XlaPulseAudio";
    private static final String EXECUTABLE = "libpulseaudio.so";

    private PulseAudioServer() {}

    /** Starts the daemon listening on socketPath. Blocks until it has daemonized; call off the UI thread. */
    static void start(Context context, String socketPath, String tmpDir) {
        String nativeLibDir = context.getApplicationInfo().nativeLibraryDir;
        File workingDir = new File(context.getFilesDir(), "pulseaudio");
        File modulesDir = new File(workingDir, "modules");
        if (!new File(modulesDir, "module-aaudio-sink.so").isFile()) {
            Log.e(TAG, "PulseAudio modules missing in " + modulesDir + " - provision pulseaudio.tgz");
            return;
        }

        stop(); // a daemon left over from a previous app process would hold the socket
        new File(socketPath).delete();
        File configDir = new File(workingDir, ".config"); // stale cookie from an earlier run
        deleteRecursively(configDir);

        try (FileWriter w = new FileWriter(new File(workingDir, "default.pa"), false)) {
            w.write("load-module module-native-protocol-unix auth-anonymous=1 auth-cookie-enabled=false socket=\""
                    + socketPath + "\"\n");
            w.write("load-module module-aaudio-sink volume=1.0 performance_mode=1 low_latency=true\n");
        } catch (Exception e) {
            Log.e(TAG, "could not write default.pa", e);
            return;
        }

        List<String> command = new ArrayList<>();
        command.add(nativeLibDir + "/" + EXECUTABLE);
        command.add("--system=false");
        command.add("--disable-shm=true");
        command.add("--fail=false");
        command.add("-n");
        command.add("--file=default.pa");
        command.add("--daemonize=true");
        command.add("--use-pid-file=false");
        command.add("--exit-idle-time=-1");

        ProcessBuilder builder = new ProcessBuilder(command)
                .directory(workingDir)
                .redirectErrorStream(true);
        builder.environment().clear();
        builder.environment().put("LD_LIBRARY_PATH", "/system/lib64:" + nativeLibDir + ":" + modulesDir);
        builder.environment().put("HOME", workingDir.getPath());
        builder.environment().put("TMPDIR", tmpDir);

        try {
            Process process = builder.start();
            StringBuilder output = new StringBuilder();
            try (BufferedReader r = new BufferedReader(new InputStreamReader(process.getInputStream()))) {
                String line;
                while ((line = r.readLine()) != null) output.append(line).append('\n');
            }
            boolean exited = process.waitFor(10, TimeUnit.SECONDS);
            Log.i(TAG, "daemon started (launcher exit=" + (exited ? process.exitValue() : "timeout") + ") "
                    + output.toString().trim());
        } catch (Exception e) {
            Log.e(TAG, "could not start PulseAudio", e);
        }
    }

    /** Kills every PulseAudio daemon of this app's uid (/proc only lists our own processes). */
    static void stop() {
        int myUid = android.os.Process.myUid();
        File[] entries = new File("/proc").listFiles();
        if (entries == null) return;
        for (File entry : entries) {
            int pid;
            try { pid = Integer.parseInt(entry.getName()); }
            catch (NumberFormatException e) { continue; }
            String cmdline = readCmdline(pid);
            if (cmdline != null && cmdline.contains(EXECUTABLE) && XServerHost.uidOf(pid) == myUid) {
                android.os.Process.killProcess(pid);
            }
        }
    }

    private static String readCmdline(int pid) {
        try (FileInputStream in = new FileInputStream("/proc/" + pid + "/cmdline")) {
            byte[] buffer = new byte[512];
            int n = in.read(buffer);
            return n > 0 ? new String(buffer, 0, n) : null;
        } catch (Exception e) {
            return null;
        }
    }

    private static void deleteRecursively(File file) {
        File[] children = file.listFiles();
        if (children != null) for (File child : children) deleteRecursively(child);
        file.delete();
    }
}
