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
    /** Process state summary set by the menu's Exit game; MainActivity.RecentExits matches on it. */
    private static final String EXIT_GAME_SUMMARY = "exit-game";
    /** GameNative's DEFAULT_FPS_LIMITER_TARGET_HZ. */
    private static final int DISPLAY_FPS_LIMIT = 60;
    /**
     * Pointer capture only sticks once the window actually holds focus; GameNative posts its
     * requestPointerCapture behind the same delay for the same reason.
     */
    private static final long CAPTURE_DELAY_MS = 100;
    /** WM_CLASS Wine gives the game's windows (the exe name). */
    private static final String GAME_WM_CLASS = "ffxiv_dx11.exe";
    /** How often the game-exit watcher looks for the game window. */
    private static final long GAME_WATCH_MS = 1000;

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
    private static LoadingScreen loading;
    private static long launchMillis;
    private static GameMenu menu;
    private static GameKeyboard keyboard;
    private static XlaSettings settings;
    private static VulkanRenderer vulkanRenderer;
    private static android.hardware.input.InputManager inputManager;
    private static Thread gameWatcher;
    private static boolean returning;

    /** A mouse plugged in or unplugged mid-game: the cursor and the grab follow it without opening the menu. */
    private static final android.hardware.input.InputManager.InputDeviceListener mouseListener =
            new android.hardware.input.InputManager.InputDeviceListener() {
                @Override public void onInputDeviceAdded(int deviceId) { onDevicesChanged(); }
                @Override public void onInputDeviceRemoved(int deviceId) { onDevicesChanged(); }
                @Override public void onInputDeviceChanged(int deviceId) { onDevicesChanged(); }
            };

    private static void onDevicesChanged() {
        updateCursorVisibility();
        updateMouseCapture();
    }

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
        loading = new LoadingScreen(context);
        launchMillis = android.os.SystemClock.uptimeMillis();
        PresentExtension.presentListener = windowId -> {
            PerfHud h = hud;
            if (h != null) h.onPresent(windowId);
            LoadingScreen l = loading;
            if (l != null && !l.isDismissed() && isGameWindow(windowId)) {
                Log.i(TAG, "game window up after " + (android.os.SystemClock.uptimeMillis() - launchMillis) + " ms");
                l.dismiss();
                // Until the X pointer moves inside the game window once, the Windows cursor stays pinned at (0,0)
                // and ignores winhandler's relative moves, so the cursor sits unseen in the corner until the first
                // tap. Doing the move half of that tap here (no click) unpins it.
                View v = view;
                if (v != null) v.post(() -> {
                    XServer s = xServer;
                    if (s != null) s.injectPointerMove(s.screenInfo.width / 2, s.screenInfo.height / 2);
                });
            }
        };

        menu = new GameMenu(context, settings, new GameMenu.Actions() {
            @Override public void setHudVisible(boolean visible) { if (hud != null) hud.setVisible(visible); }
            @Override public void applyDisplaySettings() { XServerHost.applyDisplaySettings(); }
            @Override public void applyInputSettings() { XServerHost.applyInputSettings(); }
            @Override public void editTouchControls() { XServerHost.editTouchControls(); }
            @Override public void menuClosed() { hideSystemBars(); }
            @Override public void exitGame() { XServerHost.exitGame(context); }
            @Override public void showKeyboard() { if (keyboard != null) keyboard.openManually(); }
        });

        // Opened by the helper plugin when the game focuses a text field, or by hand from the menu.
        keyboard = new GameKeyboard(context, XServerHost::hideSystemBars);

        FrameLayout root = new FrameLayout(context);
        root.addView(view, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));

        // The on-screen pad sits over the game but under the HUD and the menu. It returns false for
        // touches that miss a control, and a FrameLayout then keeps dispatching to the views behind
        // it, so the pad never blocks the rest of the overlay.
        if (gamepad != null) {
            touchControls = new TouchControls(context, gamepad, settings);
            root.addView(touchControls, new FrameLayout.LayoutParams(
                    FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));
            gamepad.setConnectionListener(connected -> updateTouchControls());
        }

        FrameLayout.LayoutParams hudParams = new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.TOP | Gravity.START);
        int margin = PerfHud.dp(context, 8);
        hudParams.setMargins(margin, margin, margin, margin);
        root.addView(hud.getView(), hudParams);
        // Over the game, pad and HUD until the first frame; under the menu, so Exit game stays reachable.
        root.addView(loading, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));
        // Top of the screen, above everything but the menu: the IME covers the bottom, and the game window is
        // adjustNothing so nothing moves out of its way.
        root.addView(keyboard, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.TOP));
        root.addView(menu, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));

        applyInputSettings();
        hideSystemBars();

        inputManager = (android.hardware.input.InputManager) context.getSystemService(Context.INPUT_SERVICE);
        if (inputManager != null)
            inputManager.registerInputDeviceListener(mouseListener, new android.os.Handler(android.os.Looper.getMainLooper()));

        Log.i(TAG, "created " + width + "x" + height
                + " renderer=" + (renderer == null ? "null" : renderer.getClass().getSimpleName()));
        return root;
    }

    /**
     * True when the window, or one of its ancestors, belongs to the game. Inside Wine's virtual desktop the
     * swapchain is a child window, so the WM_CLASS Wine sets from the exe name may sit a level or two up.
     */
    private static boolean isGameWindow(int windowId) {
        XServer server = xServer;
        if (server == null) return false;
        com.winlator.xserver.Window root = server.windowManager.rootWindow;
        for (com.winlator.xserver.Window w = server.windowManager.getWindow(windowId); w != null && w != root; w = w.getParent()) {
            if (w.getClassName().toLowerCase(java.util.Locale.ROOT).contains(GAME_WM_CLASS)) return true;
        }
        return false;
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
            case "FIT": fit = VulkanRenderer.SCALE_FIT; break;
            default:    fit = VulkanRenderer.SCALE_STRETCH; break;
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

    /** From the menu. The pad is put up for the edit whatever its setting, and goes back to it afterwards. */
    private static void editTouchControls() {
        if (touchControls == null) return;
        touchControls.startEditing(XServerHost::updateTouchControls);
        updateTouchControls();
    }

    /** AUTO hides the on-screen pad whenever real hardware is attached. */
    private static void updateTouchControls() {
        if (touchControls == null || settings == null) return;
        String mode = settings.getTouchControls();
        boolean show = touchControls.isEditing() || "ON".equals(mode)
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
     * Draws the game's own pointer. VulkanRenderer starts with cursorVisible = false, so without this
     * the pointer moves around invisibly - hover tooltips fire but there is no cursor to see.
     *
     * Hidden while the on-screen pad is up and no mouse is attached, where play is gamepad-only and a
     * parked cursor is just clutter. The pad being up does not mean there is no mouse: AUTO shows it
     * whenever no controller is attached, so a mouse-only player had no cursor at all. A client that
     * hides its own cursor is still respected - sendCursorToNative clears visibility whenever the
     * current X Cursor reports itself invisible.
     */
    private static void updateCursorVisibility() {
        if (vulkanRenderer == null) return;
        boolean padUp = touchControls != null && touchControls.getVisibility() == View.VISIBLE;
        boolean visible = !padUp || hasExternalMouse();
        MouseTrace.log("cursor visible=" + visible + " padUp=" + padUp);
        vulkanRenderer.setCursorVisible(visible);
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

        boolean want = hasExternalMouse() && (menu == null || !menu.isOpen());
        MouseTrace.log("capture wanted=" + want
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
        if (menu != null && menu.isOpen()) return;
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
        startGameWatcher();

        Log.i(TAG, "listening on " + rootPath + UnixSocketConfig.XSERVER_PATH);
    }

    public static void stop() {
        Thread watcher = gameWatcher;
        gameWatcher = null;
        if (watcher != null) watcher.interrupt();
        if (inputManager != null) {
            inputManager.unregisterInputDeviceListener(mouseListener);
            inputManager = null;
        }
        if (keyboard != null) {
            keyboard.dispose();
            keyboard = null;
        }
        if (hud != null) hud.stop();
        loading = null;
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
     * - Editing the on-screen pad: Back / Esc / B / Guide end the edit.
 * - Guide/PS button: toggles the menu.
     * - Controller buttons: the Wine virtual pad (so B/Circle never turns into Back).
     * - A real keyboard: straight through to the X keyboard, so FFXIV sees a PC keyboard.
     * - Remaining Back (gesture, nav button): opens the menu. It must never reach Activity.finish().
     */
    public static boolean handleKeyEvent(KeyEvent event) {
        if (xServerComponent == null) return false;
        if (menu != null && menu.isOpen()) return menu.handleKey(event);
        // The on-screen keyboard is an Android text field: its keys must reach it, not the X server, or every
        // character would be typed into the game as well.
        if (keyboard != null && keyboard.isOpen()) return keyboard.handleKey(event);

        int code = event.getKeyCode();
        // Whatever would open or close the menu ends a layout edit instead.
        if (touchControls != null && touchControls.isEditing()
                && (code == KeyEvent.KEYCODE_BACK || code == KeyEvent.KEYCODE_ESCAPE
                || code == KeyEvent.KEYCODE_BUTTON_B || code == KeyEvent.KEYCODE_BUTTON_MODE)) {
            if (event.getAction() == KeyEvent.ACTION_UP) touchControls.stopEditing();
            return true;
        }
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
     * Shuts the game down and brings the launcher back. Kills every other process of this app's uid (wineserver,
     * the game, FEX helpers), then has RestartActivity replace this process with a fresh one. The X server,
     * renderer and audio were built to live for one session, so a new process is the clean way back to the
     * launcher. /proc only shows our own uid's processes to an app, and killProcess is allowed for them.
     */
    private static void exitGame(Context context) {
        if (returning) return;
        returning = true;
        // Android records killProcess(myPid) as a SIGKILL, the same as any outside kill. The summary is stored
        // with the exit record, which is how the next launch (MainActivity.RecentExits) tells the two apart.
        if (Build.VERSION.SDK_INT >= 30) {
            android.app.ActivityManager am =
                (android.app.ActivityManager) context.getSystemService(Context.ACTIVITY_SERVICE);
            if (am != null) am.setProcessStateSummary(EXIT_GAME_SUMMARY.getBytes());
        }
        killOtherProcesses(false);
        RestartActivity.restart(context, myPid());
    }

    /**
     * Returns to the launcher once the game has closed by itself (the game's own Exit, or a crash). Wine's virtual
     * desktop and helpers outlive the game, so without this the player is left looking at an empty desktop.
     *
     * Watches the game's WINDOW, not its process: after the in-game Exit, ffxiv_dx11.exe tears its window down and
     * Dalamud unloads, but the process itself can hang in exit under Wine for good. exitGame kills it anyway.
     * The game counts as gone after three checks in a row without a mapped window (a brief unmap, e.g. a mode
     * switch, is not an exit), and not while Dalamud's crash handler has a window up, so a crash report stays readable until the player closes it. (The crash handler
     * PROCESS runs for the whole session, so only its window means anything.)
     */
    private static void startGameWatcher() {
        final Activity a = activity;
        if (a == null) return;
        Thread t = new Thread(() -> {
            boolean seen = false;
            int missing = 0;
            while (gameWatcher == Thread.currentThread()) {
                try { Thread.sleep(GAME_WATCH_MS); }
                catch (InterruptedException e) { return; }
                XServer server = xServer;
                if (server == null) return;
                boolean game, crashHandler;
                try (com.winlator.xserver.XLock lock = server.lock(XServer.Lockable.WINDOW_MANAGER)) {
                    com.winlator.xserver.Window root = server.windowManager.rootWindow;
                    game = hasMappedWindow(root, GAME_WM_CLASS);
                    crashHandler = hasMappedWindow(root, "dalamudcrashhandler.exe");
                }
                if (game) { seen = true; missing = 0; continue; }
                if (!seen || crashHandler || ++missing < 3) continue;
                Log.i(TAG, "game window gone, returning to launcher");
                a.runOnUiThread(() -> exitGame(a));
                return;
            }
        }, "xla-game-watcher");
        t.setDaemon(true);
        gameWatcher = t;
        t.start();
    }

    /** True when a mapped window under parent has a WM_CLASS containing the given exe name. Hold the window lock. */
    private static boolean hasMappedWindow(com.winlator.xserver.Window parent, String exe) {
        for (com.winlator.xserver.Window w : parent.getChildren()) {
            if (!w.attributes.isMapped()) continue;
            if (w.getClassName().toLowerCase(java.util.Locale.ROOT).contains(exe)) return true;
            if (hasMappedWindow(w, exe)) return true;
        }
        return false;
    }

    /**
     * Kills anything left running from a previous game session, and returns how many there were.
     *
     * Only the menu's "Exit game" shuts the guest down. Swiping the app out of recents - or the app being killed
     * for any other reason - takes the app process and leaves wineserver, the game and the Wine services behind,
     * because they are separate processes that merely share our uid. The next launch would then start a fresh Wine
     * against that stale wineserver, which holds the same prefix, and hang before the game ever drew a frame.
     *
     * Call this before starting a game, never during one.
     */
    public static int clearLeftovers() {
        int killed = killOtherProcesses(true);
        if (killed > 0) Log.i(TAG, "killed " + killed + " process(es) left over from a previous session");
        return killed;
    }

    /**
     * Kills every other process sharing our uid. With <paramref>keepAppProcesses</paramref> the app's own
     * processes are spared (their cmdline is the package name) and only the guest's are killed, which is what
     * makes this safe to call before a launch.
     */
    private static int killOtherProcesses(boolean keepAppProcesses) {
        int myPid = myPid();
        int myUid = android.os.Process.myUid();
        String ours = cmdlineOf(myPid);
        File[] entries = new File("/proc").listFiles();
        if (entries == null) return 0;

        int killed = 0;
        for (File entry : entries) {
            int pid;
            try { pid = Integer.parseInt(entry.getName()); }
            catch (NumberFormatException e) { continue; }
            if (pid == myPid || uidOf(pid) != myUid) continue;
            String cmdline = cmdlineOf(pid);
            if (keepAppProcesses && !ours.isEmpty() && cmdline.startsWith(ours)) continue;
            if (keepAppProcesses) Log.i(TAG, "killing leftover pid " + pid + " (" + cmdline + ")");
            android.os.Process.killProcess(pid);
            killed++;
        }
        return killed;
    }

    private static int myPid() {
        return android.os.Process.myPid();
    }

    /** A process's command line with the NUL separators flattened, or "" if it is already gone. */
    private static String cmdlineOf(int pid) {
        try (BufferedReader reader = new BufferedReader(new FileReader("/proc/" + pid + "/cmdline"))) {
            String line = reader.readLine();
            return line == null ? "" : line.replace('\0', ' ').trim();
        }
        catch (Exception e) { return ""; }
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
