package xla;

import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.graphics.Color;
import android.graphics.Typeface;
import android.os.BatteryManager;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.Looper;
import android.util.Log;
import android.util.SparseIntArray;
import android.util.TypedValue;
import android.view.View;
import android.widget.TextView;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileReader;
import java.io.FileWriter;
import java.io.IOException;
import java.util.Locale;

/**
 * Performance overlay: FPS plus CPU, GPU and battery temperature, sampled once a second.
 *
 * FPS is the PresentPixmap rate of the busiest X window, i.e. the game's swapchain (counted per window,
 * so it works inside Wine's virtual desktop where the game is not a top-level app window). Temperature
 * sensors are picked with GameNative's ranking (SystemMetricsSources) so the numbers compare 1:1 with
 * its HUD.
 *
 * Every sample also goes to files/perf.csv (rewritten each session), visible or not, so heat can be
 * compared between runs. It is also the session's heartbeat: the clock column lines it up with wine-test.log
 * and dalamud.log, and a freeze shows as fps dropping to 0 at a known time. The Logs page exports it.
 */
final class PerfHud {
    private static final String TAG = "XlaPerfHud";
    private static final long INTERVAL_MS = 1000;
    private static final String GPU_BUSY_PATH = "/sys/class/kgsl/kgsl-3d0/gpu_busy_percentage";
    private static final String GPU_FREQ_PATH = "/sys/class/kgsl/kgsl-3d0/devfreq/cur_freq";
    private static final String GPU_THERMAL_LEVEL_PATH = "/sys/class/kgsl/kgsl-3d0/thermal_pwrlevel";
    private static final String[] GPU_FIXED_PATHS = {
            "/sys/class/kgsl/kgsl-3d0/temp",
            "/sys/class/kgsl/kgsl-3d0/devfreq/temp",
            "/sys/class/misc/mali0/device/temp",
            "/sys/kernel/gpu/temp",
    };

    private interface Ranker { int rank(String type); }

    private final Context context;
    private final TextView view;
    private final Handler ui = new Handler(Looper.getMainLooper());
    private final SparseIntArray presents = new SparseIntArray();
    private final String cpuTempPath;
    private final String gpuTempPath;

    private HandlerThread thread;
    private volatile Handler worker;
    private File logFile;
    private long lastSampleNanos;
    private long startMillis;

    PerfHud(Context context) {
        this.context = context.getApplicationContext();

        view = new TextView(context);
        view.setTextSize(TypedValue.COMPLEX_UNIT_SP, 12);
        view.setTypeface(Typeface.MONOSPACE, Typeface.BOLD);
        view.setTextColor(Color.WHITE);
        view.setBackgroundColor(0x99000000);
        int px = dp(context, 6), py = dp(context, 3);
        view.setPadding(px, py, px, py);
        view.setText("FPS --");

        cpuTempPath = findSensor(new String[0], PerfHud::cpuRank);
        gpuTempPath = findSensor(GPU_FIXED_PATHS, PerfHud::gpuRank);
        Log.i(TAG, "sensors cpu=" + cpuTempPath + " gpu=" + gpuTempPath);
    }

    TextView getView() { return view; }

    void setVisible(boolean visible) {
        view.setVisibility(visible ? View.VISIBLE : View.GONE);
    }

    /** Called on the X server thread for every presented frame. */
    void onPresent(int windowId) {
        synchronized (presents) {
            presents.put(windowId, presents.get(windowId) + 1);
        }
    }

    void start(File logFile) {
        if (thread != null) return;
        this.logFile = logFile;
        startMillis = System.currentTimeMillis();
        try (FileWriter w = new FileWriter(logFile, false)) {
            // gpu_pwrlevel: 0 = unthrottled; cpu_max_mhz: each cpufreq policy's current cap (thermal throttling).
            w.write("t_s,fps,cpu_c,gpu_c,battery_c,gpu_busy_pct,gpu_mhz,gpu_pwrlevel,cpu_max_mhz,battery_ma,battery_mv,clock\n");
        } catch (IOException e) {
            Log.w(TAG, "perf log unavailable", e);
        }
        synchronized (presents) { presents.clear(); }
        lastSampleNanos = System.nanoTime();

        thread = new HandlerThread("xla-perf");
        thread.start();
        worker = new Handler(thread.getLooper());
        worker.postDelayed(sample, INTERVAL_MS);
    }

    void stop() {
        Handler w = worker;
        worker = null;
        if (w != null) w.removeCallbacksAndMessages(null);
        if (thread != null) {
            thread.quitSafely();
            thread = null;
        }
    }

    private final Runnable sample = new Runnable() {
        @Override
        public void run() {
            long now = System.nanoTime();
            int frames = 0;
            synchronized (presents) {
                for (int i = 0; i < presents.size(); i++) frames = Math.max(frames, presents.valueAt(i));
                presents.clear();
            }
            float fps = frames * 1e9f / Math.max(1L, now - lastSampleNanos);
            lastSampleNanos = now;

            Integer cpu = readTempC(cpuTempPath);
            Integer gpu = readTempC(gpuTempPath);
            Intent batteryStatus = readBatteryStatus();
            Float battery = batteryTempC(batteryStatus);

            final String text = String.format(Locale.US, "FPS %.0f  CPU %s  GPU %s  BAT %s",
                    fps, degrees(cpu), degrees(gpu),
                    battery == null ? "--" : String.format(Locale.US, "%.0f°C", battery));
            ui.post(() -> view.setText(text));

            appendLog(String.format(Locale.US, "%.0f,%.1f,%s,%s,%s,%s,%s,%s,%s,%s,%s,%tT\n",
                    (System.currentTimeMillis() - startMillis) / 1000f, fps,
                    cpu == null ? "" : cpu, gpu == null ? "" : gpu,
                    battery == null ? "" : String.format(Locale.US, "%.1f", battery),
                    readGpuBusy(), readScaled(GPU_FREQ_PATH, 1_000_000), readScaled(GPU_THERMAL_LEVEL_PATH, 1),
                    readCpuMaxMhz(), readBatteryCurrentMa(),
                    batteryStatus == null ? "" : batteryStatus.getIntExtra(BatteryManager.EXTRA_VOLTAGE, 0),
                    System.currentTimeMillis()));

            Handler w = worker;
            if (w != null) w.postDelayed(this, INTERVAL_MS);
        }
    };

    private void appendLog(String line) {
        File f = logFile;
        if (f == null) return;
        try (FileWriter w = new FileWriter(f, true)) {
            w.write(line);
        } catch (IOException ignored) {
            // Best effort: the overlay matters more than the log.
        }
    }

    private Intent readBatteryStatus() {
        try {
            // Sticky broadcast: no receiver is registered, this just returns the latest battery state.
            return context.registerReceiver(null, new IntentFilter(Intent.ACTION_BATTERY_CHANGED));
        } catch (RuntimeException e) {
            return null;
        }
    }

    private static Float batteryTempC(Intent status) {
        if (status == null) return null;
        int tenths = status.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, Integer.MIN_VALUE);
        return tenths == Integer.MIN_VALUE ? null : tenths / 10f;
    }

    /** Instantaneous battery current in mA as the fuel gauge reports it (sign is vendor-defined). */
    private String readBatteryCurrentMa() {
        BatteryManager manager = (BatteryManager) context.getSystemService(Context.BATTERY_SERVICE);
        if (manager == null) return "";
        int microAmps = manager.getIntProperty(BatteryManager.BATTERY_PROPERTY_CURRENT_NOW);
        return microAmps == Integer.MIN_VALUE ? "" : String.valueOf(microAmps / 1000);
    }

    private static String readScaled(String path, long divisor) {
        String raw = readLine(new File(path));
        if (raw == null) return "";
        try {
            return String.valueOf(Long.parseLong(raw.trim()) / divisor);
        } catch (NumberFormatException e) {
            return "";
        }
    }

    /** "policy0cap/policy6cap" in MHz. */
    private static String readCpuMaxMhz() {
        File[] policies = new File("/sys/devices/system/cpu/cpufreq").listFiles();
        if (policies == null) return "";
        java.util.Arrays.sort(policies);
        StringBuilder caps = new StringBuilder();
        for (File policy : policies) {
            if (!policy.getName().startsWith("policy")) continue;
            if (caps.length() > 0) caps.append('/');
            caps.append(readScaled(new File(policy, "scaling_max_freq").getPath(), 1000));
        }
        return caps.toString();
    }

    private static String readGpuBusy() {
        String raw = readLine(new File(GPU_BUSY_PATH));
        return raw == null ? "" : raw.replaceAll("[^0-9]", "");
    }

    private static String degrees(Integer c) {
        return c == null ? "--" : c + "°C";
    }

    // ---- sensor discovery (GameNative SystemMetricsSources ranking) ----------------------------

    private static int cpuRank(String type) {
        if (type.contains("cpu-silicon")) return 0;
        if (type.contains("cpu-0")) return 1;
        if (type.contains("cpu") && !type.contains("gpu")) return 2;
        if (type.contains("soc")) return 3;
        if (type.contains("s5p-tmu")) return 4;
        if (type.contains("cputop")) return 5;
        if (type.contains("tsens")) return 6;
        if (type.contains("cluster")) return 7;
        if (type.contains("big") || type.contains("little")) return 8;
        return -1;
    }

    private static int gpuRank(String type) {
        if (type.contains("gpu-silicon")) return 0;
        if (type.contains("gpu")) return 1;
        if (type.contains("g3d")) return 2;
        if (type.contains("kgsl")) return 3;
        if (type.contains("mali")) return 4;
        return -1;
    }

    /** First readable fixed path, else the best-ranked readable thermal zone (ties: path order). */
    private static String findSensor(String[] fixedPaths, Ranker ranker) {
        for (String path : fixedPaths) {
            if (readTempC(path) != null) return path;
        }
        String best = null;
        int bestRank = Integer.MAX_VALUE;
        File[] zones = new File("/sys/class/thermal").listFiles();
        if (zones == null) return null;
        for (File zone : zones) {
            if (!zone.getName().startsWith("thermal_zone")) continue;
            String type = readLine(new File(zone, "type"));
            if (type == null) continue;
            int rank = ranker.rank(type.trim().toLowerCase(Locale.US));
            if (rank < 0) continue;
            String path = new File(zone, "temp").getPath();
            if (readTempC(path) == null) continue;
            if (rank < bestRank || (rank == bestRank && path.compareTo(best) < 0)) {
                best = path;
                bestRank = rank;
            }
        }
        return best;
    }

    private static Integer readTempC(String path) {
        if (path == null) return null;
        String raw = readLine(new File(path));
        if (raw == null) return null;
        try {
            int value = Integer.parseInt(raw.trim());
            int celsius = value > 1000 ? (value + 500) / 1000 : value;
            return celsius >= 1 && celsius <= 150 ? celsius : null;
        } catch (NumberFormatException e) {
            return null;
        }
    }

    private static String readLine(File file) {
        try (BufferedReader r = new BufferedReader(new FileReader(file))) {
            return r.readLine();
        } catch (IOException | SecurityException e) {
            return null;
        }
    }

    static int dp(Context context, int dp) {
        return Math.round(dp * context.getResources().getDisplayMetrics().density);
    }
}
