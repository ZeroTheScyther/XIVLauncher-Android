package xla;

import android.app.Activity;
import android.content.Context;
import android.os.Build;
import android.util.Log;
import android.view.Gravity;
import android.view.InputDevice;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.PointerIcon;
import android.view.View;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.widget.FrameLayout;

import com.winlator.alsaserver.ALSAClient;
import com.winlator.xconnector.UnixSocketConfig;
import com.winlator.xenvironment.components.ALSAServerComponent;
import com.winlator.xenvironment.components.SysVSharedMemoryComponent;
import com.winlator.xenvironment.components.XServerComponent;
import com.winlator.renderer.VulkanRenderer;
import com.winlator.renderer.XServerRenderer;
import com.winlator.winhandler.WinHandler;
import com.winlator.widget.XServerRendererView;
import com.winlator.widget.XServerView;
import com.winlator.widget.XServerViewGL;
import com.winlator.xserver.Keyboard;
import com.winlator.xserver.ScreenInfo;
import com.winlator.xserver.XKeycode;
import com.winlator.xserver.XServer;
import com.winlator.xserver.extensions.PresentExtension;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileReader;

/**
 * The one class C# talks to. Everything under com.winlator.** is vendored Winlator/GameNative
 * source compiled with Bind="false" — its Java default-interface and covariant-return members
 * do not survive the C#/Java binding generator, and none of them need to.
 *
 * Owns an X server listening on <rootPath>/tmp/.X11-unix/X0 (i.e. DISPLAY=:0 once the guest's
 * /tmp is pointed at rootPath), the SysV shared-memory server that backs MIT-SHM, the audio servers
 * (PulseAudio, ALSA as fallback), input (controller, keyboard, mouse, on-screen pad) and the in-game
 * overlay (performance HUD + side menu).
 */
public final class XServerHost {

    private static final String TAG = "XlaXServerHost";
    /** GameNative's DEFAULT_FPS_LIMITER_TARGET_HZ. */
    private static final int DISPLAY_FPS_LIMIT = 60;
    /**
     * Pointer capture only sticks once the window actually holds focus; GameNative posts its
     * requestPointerCapture behind the same delay for the same reason.
     */
    private static final long CAPTURE_DELAY_MS = 100;

    private static XServer xServer;
    private static XServerComponent xServerComponent;
    private static SysVSharedMemoryComponent shmComponent;
    private static ALSAServerComponent alsaComponent;
    private static View view;
    private static GamepadBridge gamepad;
    private static XTouchHandler touchHandler;
    private static WinHandler winHandler;
    private static TouchControls touchControls;
    private static Context appContext;
    private static Activity activity;
    private static PerfHud hud;
    private static GameMenu menu;
    private static XlaSettings settings;
    private static VulkanRenderer vulkanRenderer;

    private XServerHost() {}

    /** Builds the X server, its renderer view and the overlay on top. Call from the UI thread. */
    public static View create(Context context, int width, int height, boolean useGlRenderer) {
        appContext = context.getApplicationContext();
        activity = context instanceof Activity ? (Activity) context : null;
        xServer = new XServer(new ScreenInfo(width, height), false, false);
        // The bridge to winhandler.exe inside Wine. Captured mouse input goes through it rather than X11: see
        // WinHandler for why X11 motion can never reach DirectInput (and so FFXIV's camera) in this Wine build.
        // It also has to exist regardless - DesktopHelper calls into it on every window map.
        winHandler = new WinHandler();
        winHandler.setCursorFeedbackListener((x, y) -> {
            // The Windows cursor after an injected move: mirror it into the X pointer WITHOUT emitting X events.
            // A MotionNotify from a stale feedback packet would drag the Windows cursor back behind the mouse.
            xServer.pointer.setX(x);
            xServer.pointer.setY(y);
            VulkanRenderer r = vulkanRenderer;
            if (r != null) r.onPointerMove(x, y);
        });
        xServer.setWinHandler(winHandler);
        winHandler.start();

        XServerRendererView rendererView = useGlRenderer
                ? new XServerViewGL(context, xServer)
                : new XServerView(context, xServer, "vulkan");

        // Winlator's views build their renderer but never register it back on the XServer -
        // GameNative's Compose screen does that. Without it the server NPEs the first time a
        // client frees a pixmap (DrawableManager.removeDrawable -> getRenderer().getRendererView()).
        XServerRenderer renderer = rendererView.getRenderer();
        xServer.setRenderer(renderer);

        // GameNative's default frame limiter (XServerScreen.applyFpsLimiterToEngines, 60): the Vulkan renderer
        // hints SurfaceControl so the 120 Hz panel can drop to the game's rate, and PresentExtension paces
        // idle notifies on vsync. Without it the display composes at 120 Hz for a 30 fps game.
        if (rendererView instanceof XServerView) ((XServerView) rendererView).setFrameRateLimit(DISPLAY_FPS_LIMIT);
        PresentExtension present = xServer.getExtension(PresentExtension.MAJOR_OPCODE);
        if (present != null) present.setFrameRateLimit(DISPLAY_FPS_LIMIT);

        view = (View) rendererView;
        touchHandler = new XTouchHandler(xServer, width, height);
        touchHandler.setUncapturedMouseListener(XServerHost::onUncapturedMouse);
        view.setOnTouchListener(touchHandler);

        // Mouse capture: the view has to be focusable to hold it, and Android's own arrow is hidden
        // so the game's cursor is the only one on screen.
        view.setFocusable(true);
        view.setFocusableInTouchMode(true);
        view.setOnCapturedPointerListener((v, event) ->
                touchHandler != null && touchHandler.onCapturedPointer(v, event));
        view.setPointerIcon(PointerIcon.getSystemIcon(context, PointerIcon.TYPE_NULL));

        // Controller bridge has to exist before Wine starts (evshim resets the block on load here).
        if (gamepad == null) {
            try { gamepad = GamepadBridge.start(context); }
            catch (Throwable t) { Log.e(TAG, "controller bridge unavailable", t); }
        }

        vulkanRenderer = renderer instanceof VulkanRenderer ? (VulkanRenderer) renderer : null;
        settings = new XlaSettings(context);
        applyDisplaySettings();
        hud = new PerfHud(context);
        hud.setVisible(settings.isHudVisible());
        PresentExtension.presentListener = windowId -> {
            PerfHud h = hud;
            if (h != null) h.onPresent(windowId);
        };

        menu = new GameMenu(context, settings, new GameMenu.Actions() {
            @Override public void setHudVisible(boolean visible) { if (hud != null) hud.setVisible(visible); }
            @Override public void applyDisplaySettings() { XServerHost.applyDisplaySettings(); }
            @Override public void applyInputSettings() { XServerHost.applyInputSettings(); }
            @Override public void menuClosed() { hideSystemBars(); }
            @Override public void exitGame() { XServerHost.exitGame(context); }
        });

        FrameLayout root = new FrameLayout(context);
        root.addView(view, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));

        // The on-screen pad sits over the game but under the HUD and the menu. It returns false for
        // touches that miss a control, and a FrameLayout then keeps dispatching to the views behind
        // it, so the pad never blocks the rest of the overlay.
        if (gamepad != null) {
            touchControls = new TouchControls(context, gamepad);
            root.addView(touchControls, new FrameLayout.LayoutParams(
                    FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));
            gamepad.setConnectionListener(connected -> updateTouchControls());
        }

        FrameLayout.LayoutParams hudParams = new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.TOP | Gravity.START);
        int margin = PerfHud.dp(context, 8);
        hudParams.setMargins(margin, margin, margin, margin);
        root.addView(hud.getView(), hudParams);
        root.addView(menu, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));

        applyInputSettings();
        hideSystemBars();

        Log.i(TAG, "created " + width + "x" + height
                + " renderer=" + (renderer == null ? "null" : renderer.getClass().getSimpleName()));
        return root;
    }

    /**
     * Upscaler + screen fit from the menu. OFF keeps VulkanRenderer's zero-copy scanout (the display
     * hardware scales the game buffer, no GPU pass); an upscaler moves presentation to the compositor,
     * whose window.frag runs it at output resolution.
     */
    private static void applyDisplaySettings() {
        if (vulkanRenderer == null || settings == null) return;
        int effect;
        float sharpness;
        switch (settings.getUpscaler()) {
            case "SGSR": effect = VulkanRenderer.EFFECT_SGSR; sharpness = 1.0f; break; // edge sharpness 2.0 (SGSR default)
            case "FSR":  effect = VulkanRenderer.EFFECT_FSR;  sharpness = 0.5f; break;
            default:     effect = VulkanRenderer.EFFECT_NONE; sharpness = 0.0f; break;
        }
        int fit;
        switch (settings.getScreenFit()) {
            case "FILL":    fit = VulkanRenderer.SCALE_FILL; break;
            case "STRETCH": fit = VulkanRenderer.SCALE_STRETCH; break;
            default:        fit = VulkanRenderer.SCALE_FIT; break;
        }
        vulkanRenderer.setEffect(effect, sharpness, fit);
        // Touch and mouse coordinates have to use the same mapping the renderer just took.
        if (touchHandler != null) touchHandler.setScaleMode(fit);
    }

    /** Stick dead zone, on-screen pad visibility and style, cursor and mouse capture, from the menu. */
    private static void applyInputSettings() {
        if (settings == null) return;
        if (gamepad != null) gamepad.setDeadZone(settings.getDeadZone());
        if (touchControls != null) touchControls.setStickMode(settings.getStickMode());
        updateTouchControls();
        updateCursorVisibility();
        updateMouseCapture();
    }

    /** AUTO hides the on-screen pad whenever real hardware is attached. */
    private static void updateTouchControls() {
        if (touchControls == null || settings == null) return;
        String mode = settings.getTouchControls();
        boolean show = "ON".equals(mode)
                || ("AUTO".equals(mode) && (gamepad == null || !gamepad.isPhysicalControllerConnected()));
        touchControls.setVisibility(show ? View.VISIBLE : View.GONE);
        // Only claim a pad exists while one is really usable, so FFXIV drops back to
        // keyboard/mouse prompts when there is neither hardware nor an overlay.
        if (gamepad != null) gamepad.setVirtualPadActive(show);
        // A thumb resting beside a stick must not click in the world.
        if (touchHandler != null) touchHandler.setClicksEnabled(!show);
        updateCursorVisibility();
    }

    /**
     * Draws the game's own pointer. VulkanRenderer starts with cursorVisible = false and nothing
     * here ever turned it on, so the pointer moved around invisibly - hover tooltips fired but there
     * was no cursor to see. Note this is a RENDERER setting, which is why it looked
     * identical in both Mouse modes: those only route input.
     *
     * Hidden again while the on-screen pad is up, where play is gamepad-only and a parked cursor is
     * just clutter. A client that hides its own cursor is still respected - sendCursorToNative
     * clears visibility whenever the current X Cursor reports itself invisible.
     */
    private static void updateCursorVisibility() {
        if (vulkanRenderer == null) return;
        boolean padUp = touchControls != null && touchControls.getVisibility() == View.VISIBLE;
        vulkanRenderer.setCursorVisible(!padUp);
    }

    /**
     * Grabs the mouse while one is attached and the menu is closed. Capture is what hides Android's
     * own cursor and delivers raw relative motion, which is what lets a held right-click drag the
     * FFXIV camera; without it the pointer just stops at the screen edge. Released while the menu is
     * open so its rows stay clickable.
     */
    private static void updateMouseCapture() {
        final View v = view;
        if (v == null || settings == null || Build.VERSION.SDK_INT < 26) return;

        boolean want = "CAPTURE".equals(settings.getMouseMode())
                && hasExternalMouse()
                && (menu == null || !menu.isOpen());
        MouseTrace.log("capture wanted=" + want + " mode=" + settings.getMouseMode()
                + " externalMouse=" + hasExternalMouse() + " menuOpen=" + (menu != null && menu.isOpen()));

        if (!want) {
            v.releasePointerCapture();
            if (touchHandler != null) touchHandler.releaseMouseButtons();
            xServer.setRelativeMouseMovement(false);
            return;
        }
        v.postDelayed(() -> {
            if (view != v || (menu != null && menu.isOpen())) return;
            v.requestFocus();
            v.requestPointerCapture();
            if (MouseTrace.enabled)
                v.postDelayed(() -> MouseTrace.log("capture held=" + v.hasPointerCapture()
                        + " focused=" + v.hasFocus()), 500);
        }, CAPTURE_DELAY_MS);
    }

    private static long lastCaptureRetry;

    /**
     * A mouse event arrived through the absolute path. If the grab should be held, it was lost or never
     * taken - a request made before the view was attached and focused is refused silently, and Android
     * drops capture on some focus changes - so ask again. Throttled; requests are cheap but not free.
     */
    private static void onUncapturedMouse() {
        View v = view;
        if (v == null || settings == null || Build.VERSION.SDK_INT < 26) return;
        if (!"CAPTURE".equals(settings.getMouseMode()) || (menu != null && menu.isOpen())) return;
        long now = android.os.SystemClock.uptimeMillis();
        if (now - lastCaptureRetry < 500) return;
        lastCaptureRetry = now;
        MouseTrace.log("uncaptured mouse event while capture wanted: re-requesting");
        v.requestFocus();
        v.requestPointerCapture();
    }

    /**
     * A real mouse or trackpad. Devices that also claim JOYSTICK/GAMEPAD are excluded: a DualShock 4
     * reports SOURCE_MOUSE and SOURCE_TOUCHPAD on the very same device as its sticks (its touchpad),
     * and capturing for that would hide the cursor whenever a pad is merely connected.
     */
    private static boolean hasExternalMouse() {
        for (int id : InputDevice.getDeviceIds()) {
            InputDevice device = InputDevice.getDevice(id);
            if (device == null || device.isVirtual()) continue;
            if (Build.VERSION.SDK_INT >= 29 && !device.isExternal()) continue;
            boolean mouseLike = device.supportsSource(InputDevice.SOURCE_MOUSE)
                    || device.supportsSource(InputDevice.SOURCE_MOUSE_RELATIVE)
                    || device.supportsSource(InputDevice.SOURCE_TOUCHPAD);
            boolean padLike = device.supportsSource(InputDevice.SOURCE_JOYSTICK)
                    || device.supportsSource(InputDevice.SOURCE_GAMEPAD);
            if (mouseLike && !padLike) return true;
        }
        return false;
    }

    /**
     * Immersive full screen over the game. A swipe (e.g. the Back gesture that opens the menu) only shows
     * the bars transiently; the menu closing and the window regaining focus hide them again, so they never
     * stay over the performance overlay. Those are also exactly the moments the mouse grab has to be
     * re-evaluated, so it is refreshed here. Call on the UI thread.
     */
    public static void hideSystemBars() {
        Activity a = activity;
        if (a == null || Build.VERSION.SDK_INT < 30) return;
        android.view.Window window = a.getWindow();
        window.setDecorFitsSystemWindows(false);
        WindowInsetsController controller = window.getInsetsController();
        if (controller != null) {
            controller.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
            controller.hide(WindowInsets.Type.systemBars());
        }
        updateMouseCapture();
    }

    /** Opens the side menu, dropping the mouse grab so its rows can be clicked. */
    private static void openMenu() {
        if (menu == null) return;
        menu.show();
        updateMouseCapture();
    }

    /** Starts listening. rootPath is the guest root whose /tmp the Wine process will see. */
    public static void start(String rootPath) {
        MouseTrace.init(appContext.getFilesDir());
        new File(rootPath, "tmp").mkdirs();

        shmComponent = new SysVSharedMemoryComponent(
                xServer, UnixSocketConfig.createSocket(rootPath, UnixSocketConfig.SYSVSHM_SERVER_PATH));
        shmComponent.start();

        xServerComponent = new XServerComponent(
                xServer, UnixSocketConfig.createSocket(rootPath, UnixSocketConfig.XSERVER_PATH));
        xServerComponent.start();

        // Audio is optional: a failure here must not keep the game from starting.
        try {
            alsaComponent = new ALSAServerComponent(appContext,
                    UnixSocketConfig.createSocket(rootPath, UnixSocketConfig.ALSA_SERVER_PATH), new ALSAClient.Options());
            alsaComponent.start();
        } catch (Throwable t) {
            Log.e(TAG, "ALSA server unavailable", t);
            alsaComponent = null;
        }

        // PulseAudio is what Wine's winepulse plays through (the ALSA server above is the fallback driver).
        final String pulseSocket = UnixSocketConfig.createSocket(rootPath, UnixSocketConfig.PULSE_SERVER_PATH).path;
        final String tmpDir = new File(rootPath, "tmp").getPath();
        new Thread(() -> PulseAudioServer.start(appContext, pulseSocket, tmpDir), "xla-pulseaudio").start();

        if (hud != null) hud.start(new File(appContext.getFilesDir(), "perf.csv"));

        Log.i(TAG, "listening on " + rootPath + UnixSocketConfig.XSERVER_PATH);
    }

    public static void stop() {
        if (hud != null) hud.stop();
        if (touchControls != null) touchControls.releaseAll();
        if (view != null && Build.VERSION.SDK_INT >= 26) view.releasePointerCapture();
        if (alsaComponent != null) { alsaComponent.stop(); alsaComponent = null; }
        PulseAudioServer.stop();
        if (xServerComponent != null) { xServerComponent.stop(); xServerComponent = null; }
        if (shmComponent != null) { shmComponent.stop(); shmComponent = null; }
    }

    /**
     * Regenerates files/xla-settings.sh from the stored settings.
     *
     * The launcher's settings screen writes the launch-time settings straight into the same
     * SharedPreferences from C#; this class stays the only writer of the shell file, so the
     * launcher calls here once at startup and after every change. Safe before create().
     */
    public static void syncSettings(Context context) {
        XlaSettings s = settings;
        if (s != null) s.writeLaunchFile();
        else settings = new XlaSettings(context); // its constructor writes the file
    }

    /** Absolute path of the X11 socket, for sanity-checking from the caller. */
    public static String socketPath(String rootPath) {
        return new File(rootPath, UnixSocketConfig.XSERVER_PATH).getPath();
    }

    /** Presses (down=true) or releases one X key by XKeycode name, e.g. "KEY_ESC". No-op before create(). */
    public static void injectKey(String xKeycodeName, boolean down) {
        if (xServer == null) return;
        XKeycode keycode = XKeycode.valueOf(xKeycodeName);
        if (down) xServer.injectKeyPress(keycode);
        else xServer.injectKeyRelease(keycode);
    }

    /**
     * Keys while the game is up. True when consumed.
     * - Menu open: the menu gets them (navigation keys fall through to Android focus handling).
     * - Guide/PS button: toggles the menu.
     * - Controller buttons: the Wine virtual pad (so B/Circle never turns into Back).
     * - A real keyboard: straight through to the X keyboard, so FFXIV sees a PC keyboard.
     * - Remaining Back (gesture, nav button): opens the menu. It must never reach Activity.finish().
     */
    public static boolean handleKeyEvent(KeyEvent event) {
        if (xServerComponent == null) return false;
        if (menu != null && menu.isOpen()) return menu.handleKey(event);

        int code = event.getKeyCode();
        if (menu != null && code == KeyEvent.KEYCODE_BUTTON_MODE) {
            if (event.getAction() == KeyEvent.ACTION_UP) openMenu();
            return true;
        }
        if (gamepad != null && gamepad.onKeyEvent(event)) return true;

        // Keyboard.isKeyboardDevice requires an ALPHABETIC keyboard, so a controller's own keyboard
        // node never lands here (a DualShock 4 reports KeyboardType 1, non-alphabetic). The X keymap
        // carries the keysyms, so Wine turns these into characters itself.
        if (xServer != null && Keyboard.isKeyboardDevice(event.getDevice())
                && xServer.keyboard.onKeyEvent(event))
            return true;

        if (menu != null && code == KeyEvent.KEYCODE_BACK) {
            if (event.getAction() == KeyEvent.ACTION_UP) openMenu();
            return true;
        }
        return false;
    }

    /** Controller sticks/triggers/hat -> Wine virtual pad; a real mouse -> the X pointer. True when consumed. */
    public static boolean handleGenericMotionEvent(MotionEvent event) {
        if (xServerComponent == null) return false;
        if (menu != null && menu.isOpen()) return false; // hat -> D-pad focus navigation in the menu

        if (XTouchHandler.isMouse(event) && touchHandler != null && view != null)
            return touchHandler.onMouseEvent(view, event);

        return gamepad != null && gamepad.onGenericMotionEvent(event);
    }

    public static boolean isRunning() {
        return xServerComponent != null;
    }

    /**
     * Kills every other process of this app's uid (wineserver, the game, FEX helpers), then the app.
     * /proc only shows our own uid's processes to an app, and killProcess is allowed for them.
     */
    private static void exitGame(Context context) {
        int myPid = android.os.Process.myPid();
        int myUid = android.os.Process.myUid();
        File[] entries = new File("/proc").listFiles();
        if (entries != null) {
            for (File entry : entries) {
                int pid;
                try { pid = Integer.parseInt(entry.getName()); }
                catch (NumberFormatException e) { continue; }
                if (pid != myPid && uidOf(pid) == myUid) android.os.Process.killProcess(pid);
            }
        }
        if (context instanceof Activity) ((Activity) context).finishAndRemoveTask();
        android.os.Process.killProcess(myPid);
    }

    static int uidOf(int pid) {
        try (BufferedReader r = new BufferedReader(new FileReader("/proc/" + pid + "/status"))) {
            String line;
            while ((line = r.readLine()) != null) {
                if (line.startsWith("Uid:")) return Integer.parseInt(line.substring(4).trim().split("\\s+")[0]);
            }
        } catch (Exception ignored) {
            // Process exited while we looked.
        }
        return -1;
    }
}
