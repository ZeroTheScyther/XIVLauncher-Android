package xla;

import android.os.SystemClock;
import android.util.Log;

import java.io.File;

/**
 * Diagnostic trace of the whole mouse chain: Android capture state, the deltas and buttons we inject, and
 * what the X client (Wine) does with the pointer in return - warps, grabs, XInput2 selections.
 *
 * Off unless files/xla-mouse-trace exists (create it with run-as + touch; read when the X server starts),
 * because a held drag produces tens of lines a second. Motion is summarised every 250 ms; state changes and
 * client requests are logged as they happen. Tag: XlaMouse.
 */
public final class MouseTrace {
    private static final String TAG = "XlaMouse";
    private static final long SUMMARY_MS = 250;

    public static volatile boolean enabled;

    private static long windowStart;
    private static int moves, sumDx, sumDy, warps, warpsIgnored;
    private static String lastPath = "";

    private MouseTrace() {}

    static void init(File filesDir) {
        enabled = new File(filesDir, "xla-mouse-trace").exists();
        if (enabled) Log.i(TAG, "mouse trace enabled");
    }

    public static void log(String line) {
        if (enabled) Log.i(TAG, line);
    }

    /** One injected relative motion (captured) or absolute move (uncaptured), summarised. */
    public static synchronized void motion(String path, int dx, int dy, int ptrX, int ptrY, int buttons) {
        if (!enabled) return;
        if (!path.equals(lastPath)) {
            Log.i(TAG, "input path -> " + path);
            lastPath = path;
        }
        moves++;
        sumDx += dx;
        sumDy += dy;
        flush(ptrX, ptrY, buttons);
    }

    /** An XWarpPointer from the client: where it asked for, and whether the server honoured it. */
    public static synchronized void warp(boolean honoured, int fromX, int fromY, int toX, int toY) {
        if (!enabled) return;
        if (honoured) warps++;
        else warpsIgnored++;
        if (warps + warpsIgnored <= 3)
            Log.i(TAG, "warp " + (honoured ? "" : "IGNORED ") + "(" + fromX + "," + fromY + ") -> (" + toX + "," + toY + ")");
    }

    private static void flush(int ptrX, int ptrY, int buttons) {
        long now = SystemClock.uptimeMillis();
        if (windowStart == 0) windowStart = now;
        if (now - windowStart < SUMMARY_MS) return;
        Log.i(TAG, "moves=" + moves + " d=(" + sumDx + "," + sumDy + ") warps=" + warps
                + (warpsIgnored > 0 ? " warpsIgnored=" + warpsIgnored : "")
                + " ptr=(" + ptrX + "," + ptrY + ") buttons=0x" + Integer.toHexString(buttons));
        windowStart = now;
        moves = sumDx = sumDy = warps = warpsIgnored = 0;
    }
}
