package timber.log;
import android.util.Log;
/** Minimal stand-in for Jake Wharton's Timber, forwarding to android.util.Log. */
public final class Timber {
    private static final String TAG = "XlaXServer";
    private Timber() {}
    private static String fmt(String m, Object... a) {
        try { return (a == null || a.length == 0) ? m : String.format(m, a); }
        catch (Exception e) { return m; }
    }
    public static void v(String m, Object... a) { Log.v(TAG, fmt(m, a)); }
    public static void d(String m, Object... a) { Log.d(TAG, fmt(m, a)); }
    public static void i(String m, Object... a) { Log.i(TAG, fmt(m, a)); }
    public static void w(String m, Object... a) { Log.w(TAG, fmt(m, a)); }
    public static void e(String m, Object... a) { Log.e(TAG, fmt(m, a)); }
    public static void v(Throwable t, String m, Object... a) { Log.v(TAG, fmt(m, a), t); }
    public static void d(Throwable t, String m, Object... a) { Log.d(TAG, fmt(m, a), t); }
    public static void i(Throwable t, String m, Object... a) { Log.i(TAG, fmt(m, a), t); }
    public static void w(Throwable t, String m, Object... a) { Log.w(TAG, fmt(m, a), t); }
    public static void e(Throwable t, String m, Object... a) { Log.e(TAG, fmt(m, a), t); }
    public static void w(Throwable t) { Log.w(TAG, "", t); }
    public static void e(Throwable t) { Log.e(TAG, "", t); }
}
