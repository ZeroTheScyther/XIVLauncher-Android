package xla;

import android.content.Context;
import android.content.SharedPreferences;
import android.util.Log;

import java.io.File;
import java.io.FileWriter;
import java.io.IOException;
import java.util.Locale;

/**
 * The app's whole settings store, shared by two very different front ends:
 *
 *  - the in-game side menu (xla.GameMenu), which owns the settings that apply live while the game
 *    runs: overlay, upscaler, screen fit, on-screen pad, mouse mode, stick dead zone. Those keep
 *    their value lists here, because the menu cycles them.
 *  - the launcher's settings screen (C#, XlaBoot.ViewModels.SettingsViewModel), which owns everything
 *    that can only take effect on the next launch: game location, graphics driver, resolution, frame
 *    cap, FEXCore and Wine knobs. C# writes those straight into the same SharedPreferences, so their
 *    value lists live in C# only and are read back here as plain strings with a default.
 *
 * Launch-time settings are mirrored into files/xla-settings.sh as XLA_* assignments, which
 * runtime/run-wine.sh sources before starting Wine (and MainViewModel parses for the X screen size).
 * This class is the only writer of that file; C# calls xla.XServerHost.syncSettings to regenerate it.
 */
final class XlaSettings {
    private static final String TAG = "XlaSettings";

    /** Compositor upscaler. OFF keeps the zero-copy scanout path (the display hardware scales the game). */
    private static final String[] UPSCALERS = {"OFF", "SGSR", "FSR"};
    private static final String[] SCREEN_FITS = {"FIT", "FILL", "STRETCH"};
    /** On-screen pad: AUTO shows it only while no physical controller is attached. */
    private static final String[] TOUCH_CONTROLS = {"AUTO", "ON", "OFF"};
    /** TOUCH = GameNative's "FPS" sticks, raised under the thumb; FIXED = always drawn in place. */
    private static final String[] STICK_MODES = {"TOUCH", "FIXED"};
    /** Grab the mouse so Android hides its cursor and the game gets raw motion (needed to drag the camera). */
    private static final String[] MOUSE_MODES = {"CAPTURE", "CURSOR"};
    /**
     * Stick dead zone, percent of full deflection. Pads on this phone report flat=0, so without one
     * the sticks drift; 15 is GameNative's value and the default.
     */
    private static final int[] DEAD_ZONES = {15, 10, 20, 25};

    /** Defaults for the launcher-owned keys. Kept in step with SettingsViewModel's option lists. */
    static final String DEFAULT_GAME_PATH = "/storage/emulated/0/Emulation/windows/import/FFXIV";
    private static final String DEFAULT_RESOLUTION = "1280x720";
    private static final String DEFAULT_FPS_CAP = "30";
    private static final String DEFAULT_FEX_PRESET = "INTERMEDIATE";

    private final SharedPreferences prefs;
    private final File launchFile;
    /** Downloaded by the launcher (XlaBoot.Steam.LosslessFetch); frame generation stays off without it. */
    private final File losslessDll;

    XlaSettings(Context context) {
        prefs = context.getSharedPreferences("xla_settings", Context.MODE_PRIVATE);
        launchFile = new File(context.getFilesDir(), "xla-settings.sh");
        losslessDll = new File(context.getFilesDir(), "lsfg/Lossless.dll");
        writeLaunchFile();
    }

    // ---- In-game menu settings ---------------------------------------------------------------

    boolean isHudVisible() {
        return prefs.getBoolean("hud_visible", true);
    }

    void setHudVisible(boolean visible) {
        prefs.edit().putBoolean("hud_visible", visible).apply();
    }

    String getUpscaler() { return choice("upscaler", UPSCALERS); }

    void cycleUpscaler() { cycle("upscaler", UPSCALERS); }

    String getScreenFit() { return choice("screen_fit", SCREEN_FITS); }

    void cycleScreenFit() { cycle("screen_fit", SCREEN_FITS); }

    String getTouchControls() { return choice("touch_controls", TOUCH_CONTROLS); }

    void cycleTouchControls() { cycle("touch_controls", TOUCH_CONTROLS); }

    String getStickMode() { return choice("stick_mode", STICK_MODES); }

    void cycleStickMode() { cycle("stick_mode", STICK_MODES); }

    String getMouseMode() { return choice("mouse_mode", MOUSE_MODES); }

    void cycleMouseMode() { cycle("mouse_mode", MOUSE_MODES); }

    int getDeadZonePercent() {
        int value = prefs.getInt("dead_zone", DEAD_ZONES[0]);
        for (int v : DEAD_ZONES) if (v == value) return value;
        return DEAD_ZONES[0];
    }

    float getDeadZone() { return getDeadZonePercent() / 100f; }

    void cycleDeadZone() {
        int current = getDeadZonePercent();
        int next = DEAD_ZONES[0];
        for (int i = 0; i < DEAD_ZONES.length; i++) {
            if (DEAD_ZONES[i] == current) next = DEAD_ZONES[(i + 1) % DEAD_ZONES.length];
        }
        prefs.edit().putInt("dead_zone", next).apply();
    }

    // ---- Launcher settings (written by C#, read here) ----------------------------------------

    private String text(String key, String fallback) {
        try {
            String value = prefs.getString(key, fallback);
            return value == null || value.isEmpty() ? fallback : value;
        } catch (ClassCastException e) {
            // A key that used to hold another type (fps_cap was an int before the launcher screen
            // existed, which is why the cap now lives under fps_cap_hz). Fall back, never crash.
            Log.w(TAG, key + " has the wrong type in prefs; using " + fallback);
            return fallback;
        }
    }

    private String flag(String key, boolean defaultOn) {
        return "ON".equals(text(key, defaultOn ? "ON" : "OFF")) ? "1" : "0";
    }

    /** Single-quoted for sh: these come from a folder picker, so they can contain anything. */
    private static String quote(String value) {
        return "'" + value.replace("'", "'\\''") + "'";
    }

    void writeLaunchFile() {
        // WINEDEBUG channels. -all is silent; the other two are for diagnosing a device that will
        // not start the game at all, which is the whole point of exposing this in the launcher.
        String wineLog;
        switch (text("wine_log", "OFF")) {
            case "ERRORS": wineLog = "err+all"; break;
            case "FULL":   wineLog = "+all"; break;
            default:       wineLog = "-all"; break;
        }
        // Wrapper ICD BCn emulation: 3 = auto (GameNative's default), 1 = always, 0 = never.
        String bcn;
        switch (text("bcn_emulation", "AUTO")) {
            case "ON":  bcn = "1"; break;
            case "OFF": bcn = "0"; break;
            default:    bcn = "3"; break;
        }

        String content = "# Written by xla.XlaSettings (launcher settings screen + in-game menu).\n"
                + "# Sourced by run-wine.sh before Wine starts; MainViewModel parses XLA_RESOLUTION.\n"
                + "XLA_RESOLUTION=" + text("resolution", DEFAULT_RESOLUTION) + "\n"
                + "XLA_FPS_CAP=" + text("fps_cap_hz", DEFAULT_FPS_CAP) + "\n"
                + "XLA_FEX_PRESET=" + text("fex_preset", DEFAULT_FEX_PRESET) + "\n"
                + "XLA_FEX_MULTIBLOCK=" + flag("fex_multiblock", true) + "\n"
                + "XLA_PRESENT_MODE=" + text("present_mode", "MAILBOX").toLowerCase(Locale.US) + "\n"
                + "XLA_EMULATE_BCN=" + bcn + "\n"
                + "XLA_SHADER_CACHE=" + flag("shader_cache", true) + "\n"
                + "XLA_DXVK_ASYNC=" + flag("dxvk_async", true) + "\n"
                + "XLA_ESYNC=" + flag("esync", true) + "\n"
                + "XLA_WINE_LOG=" + wineLog + "\n"
                + "XLA_DRIVER_DIR=" + quote(text("driver_dir", "")) + "\n"
                + "XLA_GAME_DIR=" + quote(text("game_path", DEFAULT_GAME_PATH)) + "\n"
                + "XLA_DALAMUD=" + flag("dalamud_enabled", false) + "\n"
                + "XLA_LSFG=" + (losslessDll.isFile() ? flag("lsfg_enabled", false) : "0") + "\n"
                + "XLA_LSFG_MULTIPLIER=" + text("lsfg_multiplier", "2") + "\n"
                + "XLA_LSFG_FLOW_SCALE=" + text("lsfg_flow_scale", "0.80") + "\n"
                + "XLA_LSFG_PERFORMANCE=" + flag("lsfg_performance", true) + "\n";
        try (FileWriter w = new FileWriter(launchFile, false)) {
            w.write(content);
        } catch (IOException e) {
            Log.e(TAG, "could not write " + launchFile, e);
        }
    }

    static String label(String id) {
        switch (id) {
            case "OFF": return "Off";
            case "ON": return "On";
            case "AUTO": return "Auto";
            case "SGSR": return "Snapdragon GSR";
            case "FSR": return "AMD FSR 1";
            case "FIT": return "Fit";
            case "FILL": return "Fill (crop)";
            case "STRETCH": return "Stretch";
            case "TOUCH": return "Under thumb";
            case "FIXED": return "Fixed";
            case "CAPTURE": return "Capture (raw)";
            case "CURSOR": return "Cursor";
            default: return id;
        }
    }

    private String choice(String key, String[] values) {
        String value = text(key, values[0]);
        for (String v : values) if (v.equals(value)) return value;
        return values[0];
    }

    private void cycle(String key, String[] values) {
        String current = choice(key, values);
        for (int i = 0; i < values.length; i++) {
            if (values[i].equals(current)) {
                prefs.edit().putString(key, values[(i + 1) % values.length]).apply();
                return;
            }
        }
    }
}
