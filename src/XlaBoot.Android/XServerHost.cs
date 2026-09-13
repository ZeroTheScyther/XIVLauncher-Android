using System;
using Android.Content;
using Android.Runtime;
using Android.Views;
using Java.Interop;

namespace XlaBoot.Android;

/// <summary>
/// JNI shim over the vendored Java class <c>xla.XServerHost</c>.
/// The Winlator sources are compiled with Bind="false", so there is no generated binding to
/// call; these four entry points are the entire C#-to-X-server surface.
/// </summary>
internal static class XServerHost
{
    private static IntPtr _class;
    private static IntPtr _handleKeyEvent;
    private static IntPtr _handleGenericMotionEvent;

    private static IntPtr Class => _class != IntPtr.Zero
        ? _class
        : _class = JNIEnv.FindClass("xla/XServerHost");

    private static IntPtr Method(string name, string signature) =>
        JNIEnv.GetStaticMethodID(Class, name, signature);

    /// <summary>Builds the X server and its renderer view. Must run on the UI thread.</summary>
    public static View Create(Context context, int width, int height, bool useGlRenderer)
    {
        var id = Method("create", "(Landroid/content/Context;IIZ)Landroid/view/View;");
        var handle = JNIEnv.CallStaticObjectMethod(Class, id,
            new JValue(context),
            new JValue(width),
            new JValue(height),
            new JValue(useGlRenderer));
        return Java.Lang.Object.GetObject<View>(handle, JniHandleOwnership.TransferLocalRef)!;
    }

    /// <summary>Starts listening. <paramref name="rootPath"/> is the guest root holding /tmp.</summary>
    public static void Start(string rootPath)
    {
        var id = Method("start", "(Ljava/lang/String;)V");
        var s = JNIEnv.NewString(rootPath);
        try { JNIEnv.CallStaticVoidMethod(Class, id, new JValue(s)); }
        finally { JNIEnv.DeleteLocalRef(s); }
    }

    /// <summary>
    /// Kills guest processes left over from a previous session (wineserver above all) and returns how many.
    /// Must be called before a game starts, never while one is running.
    /// </summary>
    public static int ClearLeftovers() =>
        JNIEnv.CallStaticIntMethod(Class, Method("clearLeftovers", "()I"));

    public static void Stop() =>
        JNIEnv.CallStaticVoidMethod(Class, Method("stop", "()V"));

    /// <summary>
    /// Regenerates files/xla-settings.sh from the stored settings. The launcher's settings screen
    /// writes the launch-time settings into SharedPreferences from C#; the Java side stays the only
    /// writer of the shell file that run-wine.sh sources, so this is called after every change.
    /// </summary>
    public static void SyncSettings(Context context)
    {
        var id = Method("syncSettings", "(Landroid/content/Context;)V");
        JNIEnv.CallStaticVoidMethod(Class, id, new JValue(context));
    }

    /// <summary>Presses (<paramref name="down"/>) or releases one X key by its XKeycode name, e.g. "KEY_ESC".</summary>
    public static void InjectKey(string xKeycodeName, bool down)
    {
        var id = Method("injectKey", "(Ljava/lang/String;Z)V");
        var s = JNIEnv.NewString(xKeycodeName);
        try { JNIEnv.CallStaticVoidMethod(Class, id, new JValue(s), new JValue(down)); }
        finally { JNIEnv.DeleteLocalRef(s); }
    }

    /// <summary>Controller button -> Wine virtual pad. True when consumed.</summary>
    public static bool HandleKeyEvent(KeyEvent e)
    {
        if (_handleKeyEvent == IntPtr.Zero)
            _handleKeyEvent = Method("handleKeyEvent", "(Landroid/view/KeyEvent;)Z");
        return JNIEnv.CallStaticBooleanMethod(Class, _handleKeyEvent, new JValue(e));
    }

    /// <summary>Controller sticks/triggers/hat -> Wine virtual pad. True when consumed.</summary>
    public static bool HandleGenericMotionEvent(MotionEvent e)
    {
        if (_handleGenericMotionEvent == IntPtr.Zero)
            _handleGenericMotionEvent = Method("handleGenericMotionEvent", "(Landroid/view/MotionEvent;)Z");
        return JNIEnv.CallStaticBooleanMethod(Class, _handleGenericMotionEvent, new JValue(e));
    }

    /// <summary>Re-enters immersive full screen over the game. Must run on the UI thread.</summary>
    public static void HideSystemBars() =>
        JNIEnv.CallStaticVoidMethod(Class, Method("hideSystemBars", "()V"));

    public static bool IsRunning =>
        JNIEnv.CallStaticBooleanMethod(Class, Method("isRunning", "()Z"));

    public static string SocketPath(string rootPath)
    {
        var id = Method("socketPath", "(Ljava/lang/String;)Ljava/lang/String;");
        var s = JNIEnv.NewString(rootPath);
        try
        {
            var handle = JNIEnv.CallStaticObjectMethod(Class, id, new JValue(s));
            return JNIEnv.GetString(handle, JniHandleOwnership.TransferLocalRef);
        }
        finally { JNIEnv.DeleteLocalRef(s); }
    }
}
