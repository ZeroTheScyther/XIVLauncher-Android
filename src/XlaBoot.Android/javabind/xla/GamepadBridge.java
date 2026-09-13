package xla;

import android.content.Context;
import android.hardware.input.InputManager;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.system.Os;
import android.util.Log;
import android.view.InputDevice;
import android.view.KeyEvent;
import android.view.MotionEvent;

import com.winlator.winhandler.WinHandler;

import java.io.File;
import java.io.RandomAccessFile;
import java.nio.ByteOrder;
import java.nio.MappedByteBuffer;
import java.nio.channels.FileChannel;

/**
 * Physical controller and on-screen pad -> Wine, through GameNative's libevshim.
 *
 * libevshim.so is LD_PRELOADed into every Wine process (see run-wine.sh). Inside Wine it maps
 * files/gamepad_shm/gamepad.mem and exposes it as an SDL virtual "Xbox 360 Controller", which
 * winebus turns into an XInput device. This class is the writer on the Android side: it folds
 * gamepad KeyEvents/MotionEvents into that block and wakes the Wine reader with evshim's futex
 * (WinHandler.notifyStateChanged, implemented in libevshim.so).
 *
 * Block layout (little endian) - must match evshim.c:
 *   0 seq (u32, bumped by notifyStateChanged)   4 lx  6 ly  8 rx  10 ry  12 lt  14 rt (s16)
 *   16 btn[15] (u8, SDL GameController order)   31 hat   32/34 rumble   36 rumble_seq
 *   40 connected (u32)
 *
 * Three properties of that protocol drive the design here:
 *
 * 1. It carries CURRENT STATE, not events. evshim's reader (try_read_state) copies whatever the
 *    block holds when it wakes; a press written and then cleared before it wakes is never seen at
 *    all. FFXIV compounds this by sampling XInput once per frame (~33ms at 30fps). So every
 *    button and trigger is held for MIN_HOLD_MS after release.
 * 2. Nothing downstream applies a stick dead zone. Android's own MotionRange.getFlat() is 0 on
 *    this phone's pads (verified: a DualShock 4 reports flat=0.000 on all ten joystick axes), so
 *    gating on it alone let raw stick noise through as camera drift. A radial dead zone
 *    with a floor is applied instead, matching GameNative's ControlElement.STICK_DEAD_ZONE.
 * 3. Every control has TWO independent sources - real hardware and the on-screen pad - so each
 *    keeps its own "wanted" state and the published value is their union. Sharing one field let a
 *    hardware rescan zero a trigger the on-screen pad was still holding.
 */
final class GamepadBridge implements InputManager.InputDeviceListener {

    private static final String TAG = "XlaGamepad";
    /**
     * Per-transition logging: one line per press/release, the raw joystick axes as they arrive, and
     * the published stick values. Off in normal use (it would write a line per input during play);
     * flip it on to diagnose an input complaint, which is how the pad direction and trigger-hold bugs were pinned down.
     */
    private static final boolean LOG_TRANSITIONS = false;

    private static final int OFF_LX = 4, OFF_LY = 6, OFF_RX = 8, OFF_RY = 10, OFF_LT = 12, OFF_RT = 14;
    private static final int OFF_BTN = 16, OFF_HAT = 31, OFF_CONNECTED = 40;

    // SDL_GameControllerButton order - evshim's virtual joystick is declared as a game controller.
    private static final int SDL_A = 0, SDL_B = 1, SDL_X = 2, SDL_Y = 3, SDL_BACK = 4, SDL_GUIDE = 5,
            SDL_START = 6, SDL_LSTICK = 7, SDL_RSTICK = 8, SDL_LB = 9, SDL_RB = 10,
            SDL_DPAD_UP = 11, SDL_DPAD_DOWN = 12, SDL_DPAD_LEFT = 13, SDL_DPAD_RIGHT = 14;
    private static final int BUTTON_COUNT = 15;

    /**
     * D-pad indices for setVirtualDpad. Package-visible on purpose: xla.TouchControls indexes the
     * same D-pad, and when it kept its own copy of this order the two drifted apart and the
     * on-screen pad sent right as down, down as left and left as right. One definition.
     */
    static final int UP = 0, DOWN = 1, LEFT = 2, RIGHT = 3;

    /** Long enough for FFXIV to sample the state at least twice at 30fps. Same value as XTouchHandler. */
    private static final long MIN_HOLD_MS = 100;

    /** GameNative's ControlElement.STICK_DEAD_ZONE, used when the device reports no dead zone of its own. */
    static final float DEFAULT_DEAD_ZONE = 0.15f;

    /** Triggers rest at exactly 0 on every pad seen so far; this only rejects noise. */
    private static final float TRIGGER_THRESHOLD = 0.06f;

    interface ConnectionListener {
        void onControllerConnectedChanged(boolean connected);
    }

    private final MappedByteBuffer shm;
    private final InputManager inputManager;
    private final Handler handler = new Handler(Looper.getMainLooper());

    /** What each source wants right now; the published value is their union. */
    private final boolean[] physical = new boolean[BUTTON_COUNT];
    private final boolean[] virtualButtons = new boolean[BUTTON_COUNT];
    /** What Wine currently sees - a release lags the sources by up to MIN_HOLD_MS. */
    private final boolean[] published = new boolean[BUTTON_COUNT];
    private final long[] pressedAt = new long[BUTTON_COUNT];
    private final Runnable[] pendingRelease = new Runnable[BUTTON_COUNT];

    // The D-pad arrives as HAT axes on most pads and as DPAD key events on others. Track the two
    // separately so one source reporting "released" does not cancel the other.
    private final boolean[] hatDpad = new boolean[4];
    private final boolean[] keyDpad = new boolean[4];
    private final boolean[] virtualDpad = new boolean[4];

    private float lx, ly, rx, ry;
    private float vlx, vly, vrx, vry;

    /**
     * Triggers, split per source like the buttons and latched the same way: FFXIV's cross hotbar is
     * driven by holding them, and a hold that gets cut looks like a tap.
     */
    private float physLt, physRt, virtLt, virtRt;
    private float publishedLt, publishedRt;
    private long ltPressedAt, rtPressedAt;
    private Runnable ltPendingRelease, rtPendingRelease;

    /** Log cadence while a stick is deflected, so a held stick leaves a visible trail in logcat. */
    private static final long STICK_LOG_INTERVAL_MS = 500;
    private long lastStickLogAt;
    private long lastRawLogAt;
    private int rawEventCount;
    private boolean sticksDeflected;

    /**
     * Which physical pad currently owns the analog axes. Every connected pad feeds this one virtual
     * controller, and an idle pad reports its sticks centred on each event it sends, so without an
     * owner two pads fight and the idle one keeps zeroing the other's held stick.
     * -1 means unclaimed; ownership passes to whichever pad is actually being moved.
     */
    private int activeAxisDeviceId = -1;

    private float deadZone = DEFAULT_DEAD_ZONE;
    private volatile boolean physicalConnected;
    private boolean virtualPadActive;
    private ConnectionListener connectionListener;

    private GamepadBridge(Context context, MappedByteBuffer shm) {
        this.shm = shm;
        this.inputManager = (InputManager) context.getSystemService(Context.INPUT_SERVICE);
    }

    /**
     * Loads evshim into this process and maps player 1's block. Must run before any Wine process
     * starts: evshim's app-side constructor zeroes the block it creates.
     */
    static GamepadBridge start(Context context) throws Exception {
        File filesDir = context.getFilesDir();
        Os.setenv("EVSHIM_BASE_PATH", filesDir.getAbsolutePath(), true);
        Os.setenv("EVSHIM_MAX_PLAYERS", "1", true);
        System.loadLibrary("evshim");

        File dir = new File(filesDir, "gamepad_shm");
        dir.mkdirs();
        MappedByteBuffer buffer;
        try (RandomAccessFile raf = new RandomAccessFile(new File(dir, "gamepad.mem"), "rw")) {
            raf.setLength(64);
            buffer = raf.getChannel().map(FileChannel.MapMode.READ_WRITE, 0, 64);
        }
        buffer.order(ByteOrder.LITTLE_ENDIAN);

        GamepadBridge bridge = new GamepadBridge(context, buffer);
        bridge.inputManager.registerInputDeviceListener(bridge, new Handler(Looper.getMainLooper()));
        bridge.refreshConnected();
        return bridge;
    }

    /** Stick dead zone as a fraction of full deflection. The device's own "flat" is used when larger. */
    void setDeadZone(float fraction) {
        deadZone = Math.max(0f, Math.min(0.6f, fraction));
        applySticks();
    }

    void setConnectionListener(ConnectionListener listener) {
        connectionListener = listener;
        if (listener != null) listener.onControllerConnectedChanged(physicalConnected);
    }

    /** True while at least one real game controller is attached (the on-screen pad ignores this). */
    boolean isPhysicalControllerConnected() {
        return physicalConnected;
    }

    /**
     * Whether the on-screen pad is currently up. It counts as a controller for Wine, but only while
     * it is actually shown: claiming a pad exists with nothing attached and no overlay would leave
     * FFXIV in gamepad mode (and re-invite its "Calibrate controller?" prompt).
     */
    void setVirtualPadActive(boolean active) {
        if (virtualPadActive == active) return;
        virtualPadActive = active;
        if (!active) {
            java.util.Arrays.fill(virtualButtons, false);
            java.util.Arrays.fill(virtualDpad, false);
            vlx = vly = vrx = vry = 0f;
            virtLt = virtRt = 0f;
            applyDpad();
            for (int i = 0; i < BUTTON_COUNT; i++) applyButton(i);
            applyTrigger(false);
            applyTrigger(true);
            applySticks();
        }
        updateConnectedFlag();
    }

    /** Wine only creates its virtual pad while "connected" is set. */
    private void updateConnectedFlag() {
        synchronized (this) {
            shm.putInt(OFF_CONNECTED, (physicalConnected || virtualPadActive) ? 1 : 0);
        }
        write();
    }

    // ---- physical controller ---------------------------------------------------------------------

    /** Returns true when the key came from a controller and was forwarded to Wine. */
    boolean onKeyEvent(KeyEvent event) {
        if (!isControllerSource(event.getSource()))
            return false;
        int action = event.getAction();
        if (action != KeyEvent.ACTION_DOWN && action != KeyEvent.ACTION_UP)
            return false;
        // A held button repeats; the state is already published and must not be re-latched.
        if (action == KeyEvent.ACTION_DOWN && event.getRepeatCount() > 0)
            return true;
        boolean pressed = action == KeyEvent.ACTION_DOWN;

        switch (event.getKeyCode()) {
            case KeyEvent.KEYCODE_BUTTON_A: setPhysical(SDL_A, pressed); break;
            // Some pads report their east button as BACK instead of BUTTON_B.
            case KeyEvent.KEYCODE_BUTTON_B:
            case KeyEvent.KEYCODE_BACK: setPhysical(SDL_B, pressed); break;
            case KeyEvent.KEYCODE_BUTTON_X: setPhysical(SDL_X, pressed); break;
            case KeyEvent.KEYCODE_BUTTON_Y: setPhysical(SDL_Y, pressed); break;
            case KeyEvent.KEYCODE_BUTTON_L1: setPhysical(SDL_LB, pressed); break;
            case KeyEvent.KEYCODE_BUTTON_R1: setPhysical(SDL_RB, pressed); break;
            case KeyEvent.KEYCODE_BUTTON_SELECT: setPhysical(SDL_BACK, pressed); break;
            case KeyEvent.KEYCODE_BUTTON_START: setPhysical(SDL_START, pressed); break;
            case KeyEvent.KEYCODE_BUTTON_MODE: setPhysical(SDL_GUIDE, pressed); break;
            case KeyEvent.KEYCODE_BUTTON_THUMBL: setPhysical(SDL_LSTICK, pressed); break;
            case KeyEvent.KEYCODE_BUTTON_THUMBR: setPhysical(SDL_RSTICK, pressed); break;
            // Pads with analog triggers also send L2/R2 keys; the axis is authoritative for them.
            case KeyEvent.KEYCODE_BUTTON_L2:
                if (!hasAxis(event.getDevice(), MotionEvent.AXIS_LTRIGGER, MotionEvent.AXIS_BRAKE))
                    setPhysicalTrigger(false, pressed ? 1f : 0f);
                break;
            case KeyEvent.KEYCODE_BUTTON_R2:
                if (!hasAxis(event.getDevice(), MotionEvent.AXIS_RTRIGGER, MotionEvent.AXIS_GAS))
                    setPhysicalTrigger(true, pressed ? 1f : 0f);
                break;
            case KeyEvent.KEYCODE_DPAD_UP: keyDpad[UP] = pressed; applyDpad(); break;
            case KeyEvent.KEYCODE_DPAD_DOWN: keyDpad[DOWN] = pressed; applyDpad(); break;
            case KeyEvent.KEYCODE_DPAD_LEFT: keyDpad[LEFT] = pressed; applyDpad(); break;
            case KeyEvent.KEYCODE_DPAD_RIGHT: keyDpad[RIGHT] = pressed; applyDpad(); break;
            default:
                return false; // leave anything else (volume keys, ...) to Android
        }
        return true;
    }

    /** Returns true when the motion came from a controller's sticks/triggers/hat. */
    boolean onGenericMotionEvent(MotionEvent event) {
        if ((event.getSource() & InputDevice.SOURCE_JOYSTICK) != InputDevice.SOURCE_JOYSTICK
                || event.getAction() != MotionEvent.ACTION_MOVE)
            return false;

        InputDevice device = event.getDevice();
        boolean hasLeftTrigger = hasAxis(device, MotionEvent.AXIS_LTRIGGER, MotionEvent.AXIS_BRAKE);
        boolean hasRightTrigger = hasAxis(device, MotionEvent.AXIS_RTRIGGER, MotionEvent.AXIS_GAS);

        float nlx = rawAxis(event, device, MotionEvent.AXIS_X);
        float nly = rawAxis(event, device, MotionEvent.AXIS_Y);
        float nrx = rawAxis(event, device, MotionEvent.AXIS_Z);
        float nry = rawAxis(event, device, MotionEvent.AXIS_RZ);
        float nlt = hasLeftTrigger ? triggerAxis(event, MotionEvent.AXIS_LTRIGGER, MotionEvent.AXIS_BRAKE) : 0f;
        float nrt = hasRightTrigger ? triggerAxis(event, MotionEvent.AXIS_RTRIGGER, MotionEvent.AXIS_GAS) : 0f;
        float hatX = event.getAxisValue(MotionEvent.AXIS_HAT_X);
        float hatY = event.getAxisValue(MotionEvent.AXIS_HAT_Y);

        // Ownership: a second pad sitting idle still reports centred sticks on every event it emits,
        // which would wipe the stick this one is holding. Only a pad that is actually being used
        // takes the axes; events from any other pad are swallowed without touching the state.
        boolean inUse = deadZoned(nlx, nly, deadZone) != null || deadZoned(nrx, nry, deadZone) != null
                || nlt > 0f || nrt > 0f || Math.abs(hatX) >= 0.5f || Math.abs(hatY) >= 0.5f;
        int deviceId = event.getDeviceId();
        if (deviceId != activeAxisDeviceId) {
            if (!inUse) return true;
            Log.i(TAG, "axis owner -> device " + deviceId
                    + (device != null ? " (" + device.getName() + ")" : ""));
            activeAxisDeviceId = deviceId;
        }

        lx = nlx;
        ly = nly;
        rx = nrx;
        ry = nry;
        if (hasLeftTrigger) setPhysicalTrigger(false, nlt);
        if (hasRightTrigger) setPhysicalTrigger(true, nrt);

        if (LOG_TRANSITIONS) logRawAxes(event);

        hatDpad[UP] = hatY <= -0.5f;
        hatDpad[DOWN] = hatY >= 0.5f;
        hatDpad[LEFT] = hatX <= -0.5f;
        hatDpad[RIGHT] = hatX >= 0.5f;

        applyDpad();
        applySticks();
        return true;
    }

    // ---- on-screen pad (xla.TouchControls) -------------------------------------------------------

    /** x/y in -1..1, already dead-zoned by the overlay (a finger does not drift). */
    void setVirtualStick(boolean rightStick, float x, float y) {
        if (rightStick) { vrx = x; vry = y; } else { vlx = x; vly = y; }
        applySticks();
    }

    void setVirtualButton(int sdlButton, boolean pressed) {
        if (sdlButton < 0 || sdlButton >= BUTTON_COUNT) return;
        virtualButtons[sdlButton] = pressed;
        applyButton(sdlButton);
    }

    void setVirtualDpad(int direction, boolean pressed) {
        if (direction < 0 || direction > 3) return;
        virtualDpad[direction] = pressed;
        applyDpad();
    }

    void setVirtualTrigger(boolean rightTrigger, boolean pressed) {
        if (rightTrigger) virtRt = pressed ? 1f : 0f;
        else virtLt = pressed ? 1f : 0f;
        applyTrigger(rightTrigger);
    }

    /** SDL button indices, for TouchControls' layout table. */
    static int sdlA() { return SDL_A; }
    static int sdlB() { return SDL_B; }
    static int sdlX() { return SDL_X; }
    static int sdlY() { return SDL_Y; }
    static int sdlLb() { return SDL_LB; }
    static int sdlRb() { return SDL_RB; }
    static int sdlBack() { return SDL_BACK; }
    static int sdlStart() { return SDL_START; }
    static int sdlLStick() { return SDL_LSTICK; }
    static int sdlRStick() { return SDL_RSTICK; }

    // ---- connection ------------------------------------------------------------------------------

    @Override public void onInputDeviceAdded(int deviceId) { refreshConnected(); }
    @Override public void onInputDeviceRemoved(int deviceId) { refreshConnected(); }
    @Override public void onInputDeviceChanged(int deviceId) { refreshConnected(); }

    /**
     * Rescans for real controllers; the on-screen pad is tracked separately.
     *
     * This runs on every onInputDeviceChanged, which Android fires for unrelated reasons (battery
     * reports, a mouse waking) while a finger is down. It must therefore only ever clear the
     * PHYSICAL half of the state and let the normal latch publish the result - an earlier version
     * zeroed the published triggers outright here and cut on-screen trigger holds short.
     */
    private void refreshConnected() {
        boolean any = false;
        for (int id : InputDevice.getDeviceIds()) {
            if (isGameController(InputDevice.getDevice(id))) {
                any = true;
                break;
            }
        }

        boolean changed = any != physicalConnected;
        physicalConnected = any;

        // The pad holding the axes just went away: drop them so its last values cannot stick, and
        // let the next pad that is actually used take over.
        if (activeAxisDeviceId != -1 && InputDevice.getDevice(activeAxisDeviceId) == null) {
            activeAxisDeviceId = -1;
            lx = ly = rx = ry = 0f;
            physLt = physRt = 0f;
            java.util.Arrays.fill(hatDpad, false);
            applyDpad();
            applyTrigger(false);
            applyTrigger(true);
            applySticks();
        }

        if (!any) {
            java.util.Arrays.fill(physical, false);
            java.util.Arrays.fill(hatDpad, false);
            java.util.Arrays.fill(keyDpad, false);
            lx = ly = rx = ry = 0f;
            physLt = physRt = 0f;
            applyDpad();
            for (int i = 0; i < BUTTON_COUNT; i++) applyButton(i);
            applyTrigger(false);
            applyTrigger(true);
            applySticks();
        }

        updateConnectedFlag();
        if (changed) Log.i(TAG, "physical controller connected=" + any);

        ConnectionListener listener = connectionListener;
        if (changed && listener != null) listener.onControllerConnectedChanged(any);
    }

    // ---- state publication ----------------------------------------------------------------------

    private void setPhysical(int sdlButton, boolean pressed) {
        physical[sdlButton] = pressed;
        applyButton(sdlButton);
    }

    /**
     * Publishes one button, holding a release until it has been visible for MIN_HOLD_MS.
     * A press arriving while a release is still pending simply extends the press: the game has not
     * seen the release yet, so extending is coherent and never drops the new tap.
     */
    private void applyButton(int i) {
        boolean wanted = physical[i] || virtualButtons[i];
        if (wanted == published[i] && pendingRelease[i] == null) return;

        if (wanted) {
            if (pendingRelease[i] != null) {
                handler.removeCallbacks(pendingRelease[i]);
                pendingRelease[i] = null;
            }
            if (!published[i]) {
                published[i] = true;
                pressedAt[i] = SystemClock.uptimeMillis();
                if (LOG_TRANSITIONS) Log.i(TAG, "btn " + i + " DOWN");
                write();
            }
            return;
        }

        if (pendingRelease[i] != null) return; // already scheduled

        long held = SystemClock.uptimeMillis() - pressedAt[i];
        long remaining = MIN_HOLD_MS - held;
        if (remaining <= 0) {
            published[i] = false;
            if (LOG_TRANSITIONS) Log.i(TAG, "btn " + i + " UP after " + held + "ms");
            write();
            return;
        }
        final int index = i;
        pendingRelease[i] = () -> {
            pendingRelease[index] = null;
            if (!(physical[index] || virtualButtons[index])) {
                published[index] = false;
                if (LOG_TRANSITIONS) Log.i(TAG, "btn " + index + " UP after " + MIN_HOLD_MS + "ms (held short)");
                write();
            }
        };
        handler.postDelayed(pendingRelease[i], remaining);
    }

    private void applyDpad() {
        setPhysicalDpad(SDL_DPAD_UP, UP);
        setPhysicalDpad(SDL_DPAD_DOWN, DOWN);
        setPhysicalDpad(SDL_DPAD_LEFT, LEFT);
        setPhysicalDpad(SDL_DPAD_RIGHT, RIGHT);
    }

    private void setPhysicalDpad(int sdlButton, int direction) {
        physical[sdlButton] = hatDpad[direction] || keyDpad[direction];
        virtualButtons[sdlButton] = virtualDpad[direction];
        applyButton(sdlButton);
    }

    private void setPhysicalTrigger(boolean right, float value) {
        float clamped = value <= TRIGGER_THRESHOLD ? 0f : Math.min(1f, value);
        if (right) physRt = clamped; else physLt = clamped;
        applyTrigger(right);
    }

    /**
     * Publishes one trigger from the union of its sources, with the same minimum hold as a button.
     * The value is analog, so a change in magnitude while already pressed is published immediately
     * and only the fall to zero is latched.
     */
    private void applyTrigger(boolean right) {
        float wanted = right ? Math.max(physRt, virtRt) : Math.max(physLt, virtLt);
        float published = right ? publishedRt : publishedLt;

        if (wanted > 0f) {
            Runnable pending = right ? rtPendingRelease : ltPendingRelease;
            if (pending != null) {
                handler.removeCallbacks(pending);
                if (right) rtPendingRelease = null; else ltPendingRelease = null;
            }
            if (published == 0f) {
                if (right) rtPressedAt = SystemClock.uptimeMillis();
                else ltPressedAt = SystemClock.uptimeMillis();
                if (LOG_TRANSITIONS) Log.i(TAG, (right ? "RT" : "LT") + " DOWN");
            }
            if (published != wanted) {
                if (right) publishedRt = wanted; else publishedLt = wanted;
                write();
            }
            return;
        }

        if (published == 0f) return;
        if ((right ? rtPendingRelease : ltPendingRelease) != null) return;

        long pressed = right ? rtPressedAt : ltPressedAt;
        long held = SystemClock.uptimeMillis() - pressed;
        long remaining = MIN_HOLD_MS - held;
        if (remaining <= 0) {
            if (right) publishedRt = 0f; else publishedLt = 0f;
            if (LOG_TRANSITIONS) Log.i(TAG, (right ? "RT" : "LT") + " UP after " + held + "ms");
            write();
            return;
        }
        Runnable release = () -> {
            if (right) {
                rtPendingRelease = null;
                if (Math.max(physRt, virtRt) == 0f) { publishedRt = 0f; write(); }
            } else {
                ltPendingRelease = null;
                if (Math.max(physLt, virtLt) == 0f) { publishedLt = 0f; write(); }
            }
            if (LOG_TRANSITIONS) Log.i(TAG, (right ? "RT" : "LT") + " UP after " + MIN_HOLD_MS + "ms (held short)");
        };
        if (right) rtPendingRelease = release; else ltPendingRelease = release;
        handler.postDelayed(release, remaining);
    }

    /**
     * Dead-zones both sticks radially and writes them. The physical stick wins while it is outside
     * its dead zone, so a connected pad is never fought by a stale on-screen stick.
     */
    private void applySticks() {
        float[] left = deadZoned(lx, ly, deadZone);
        if (left == null) left = deadZoned(vlx, vly, 0f);
        float[] right = deadZoned(rx, ry, deadZone);
        if (right == null) right = deadZoned(vrx, vry, 0f);

        short slx = stickValue(left == null ? 0f : left[0]);
        short sly = stickValue(left == null ? 0f : left[1]);
        short srx = stickValue(right == null ? 0f : right[0]);
        short sry = stickValue(right == null ? 0f : right[1]);

        synchronized (this) {
            shm.putShort(OFF_LX, slx);
            shm.putShort(OFF_LY, sly);
            shm.putShort(OFF_RX, srx);
            shm.putShort(OFF_RY, sry);
        }
        boolean deflected = slx != 0 || sly != 0 || srx != 0 || sry != 0;
        if (LOG_TRANSITIONS) logSticks(deflected, slx, sly, srx, sry);
        sticksDeflected = deflected;
        write();
    }

    /**
     * Logs the published stick values on every deflect/centre transition, plus a heartbeat while
     * deflected. A held stick that reaches the game as a single nudge is either us publishing once
     * and stopping, or us publishing continuously and something downstream dropping it - and these
     * lines are what tells the two apart.
     */
    /**
     * Logs the raw joystick axes as Android delivers them, with the event count since the last
     * line. That rate is the thing worth seeing: it separates "Android stopped sending events while
     * the stick was held" from "events arrive carrying zeros" from "values are fine and the loss is
     * downstream in Wine".
     */
    private void logRawAxes(MotionEvent event) {
        rawEventCount++;
        long now = SystemClock.uptimeMillis();
        if (now - lastRawLogAt < STICK_LOG_INTERVAL_MS) return;
        long span = now - lastRawLogAt;
        lastRawLogAt = now;
        Log.i(TAG, String.format(java.util.Locale.US,
                "raw joystick x=%.3f y=%.3f z=%.3f rz=%.3f  (%d events in %dms)",
                event.getAxisValue(MotionEvent.AXIS_X), event.getAxisValue(MotionEvent.AXIS_Y),
                event.getAxisValue(MotionEvent.AXIS_Z), event.getAxisValue(MotionEvent.AXIS_RZ),
                rawEventCount, span));
        rawEventCount = 0;
    }

    private void logSticks(boolean deflected, short slx, short sly, short srx, short sry) {
        long now = SystemClock.uptimeMillis();
        if (deflected != sticksDeflected) {
            lastStickLogAt = now;
            Log.i(TAG, (deflected ? "stick DEFLECT " : "stick CENTRE ")
                    + "L(" + slx + "," + sly + ") R(" + srx + "," + sry + ")");
            return;
        }
        if (deflected && now - lastStickLogAt >= STICK_LOG_INTERVAL_MS) {
            lastStickLogAt = now;
            Log.i(TAG, "stick HELD L(" + slx + "," + sly + ") R(" + srx + "," + sry + ")");
        }
    }

    /**
     * Radial dead zone with the remainder rescaled back to 0..1, so full deflection is still
     * reachable and a diagonal is not clipped into an axis. Returns null inside the dead zone.
     *
     * Per-axis gating (what this used to do) is what made walking forward wobble: holding the stick
     * up leaves a little X noise, which survives an X-only test and steers the character.
     */
    private static float[] deadZoned(float x, float y, float dz) {
        float magnitude = (float) Math.hypot(x, y);
        if (magnitude <= dz || magnitude <= 0f) return null;
        float clamped = Math.min(1f, magnitude);
        float scale = ((clamped - dz) / (1f - dz)) / magnitude;
        return new float[]{x * scale, y * scale};
    }

    private synchronized void write() {
        for (int i = 0; i < BUTTON_COUNT; i++)
            shm.put(OFF_BTN + i, published[i] ? (byte) 1 : (byte) 0);
        shm.putShort(OFF_LT, triggerValue(publishedLt));
        shm.putShort(OFF_RT, triggerValue(publishedRt));
        shm.put(OFF_HAT, (byte) 0); // D-pad is reported as buttons 11-14, hat stays centered

        // No periodic republish: evshim copies the whole state block whenever the sequence word
        // moves, and the block keeps its last value in between, so a held control stays held with
        // no traffic at all. Measured: a trigger held 3510ms was published once and survived. An
        // earlier draft added a 60Hz heartbeat on the theory that a real HID pad's continuous
        // reports were required - they are not, and it would have burned CPU on a thermally tight
        // device for nothing.
        WinHandler.notifyStateChanged(0);
    }

    private static boolean isControllerSource(int source) {
        return (source & InputDevice.SOURCE_GAMEPAD) == InputDevice.SOURCE_GAMEPAD
                || (source & InputDevice.SOURCE_JOYSTICK) == InputDevice.SOURCE_JOYSTICK;
    }

    /**
     * GameNative's ExternalController.isGameController: a source bit alone is not enough. Composite
     * HID devices (a keyboard with a touchpad, a pad's own touchpad node) can claim JOYSTICK while
     * exposing X/Y only through SOURCE_MOUSE, which would make them look like pads.
     */
    private static boolean isGameController(InputDevice device) {
        if (device == null || device.isVirtual()) return false;

        boolean hasAxes = hasControllerAxis(device, MotionEvent.AXIS_X)
                || hasControllerAxis(device, MotionEvent.AXIS_Y);
        boolean hasPadKeys = false;
        for (boolean key : device.hasKeys(KeyEvent.KEYCODE_BUTTON_A, KeyEvent.KEYCODE_BUTTON_B,
                KeyEvent.KEYCODE_BUTTON_X, KeyEvent.KEYCODE_BUTTON_Y)) {
            if (key) { hasPadKeys = true; break; }
        }

        return (device.supportsSource(InputDevice.SOURCE_GAMEPAD) && hasPadKeys)
                || (device.supportsSource(InputDevice.SOURCE_JOYSTICK) && hasAxes);
    }

    private static boolean hasControllerAxis(InputDevice device, int axis) {
        return device.getMotionRange(axis, InputDevice.SOURCE_JOYSTICK) != null
                || device.getMotionRange(axis, InputDevice.SOURCE_GAMEPAD) != null;
    }

    private static boolean hasAxis(InputDevice device, int primary, int fallback) {
        return device != null
                && (device.getMotionRange(primary) != null || device.getMotionRange(fallback) != null);
    }

    /**
     * Raw axis value, gated only by the device's own dead zone when it declares a useful one.
     * The real dead zone is applied radially in {@link #deadZoned}; most pads report flat=0.
     */
    private static float rawAxis(MotionEvent event, InputDevice device, int axis) {
        InputDevice.MotionRange range = device == null ? null : device.getMotionRange(axis, event.getSource());
        if (range == null)
            return 0f;
        float value = event.getAxisValue(axis);
        return Math.abs(value) > range.getFlat() ? value : 0f;
    }

    private static float triggerAxis(MotionEvent event, int primary, int fallback) {
        float value = event.getAxisValue(primary);
        if (value == 0f)
            value = event.getAxisValue(fallback);
        return value <= 0.01f ? 0f : Math.min(1f, value);
    }

    private static short stickValue(float v) {
        return (short) Math.round(Math.max(-1f, Math.min(1f, v)) * 32767f);
    }

    /** 0..1 -> -32767..32767, the full-range trigger axis SDL expects. */
    private static short triggerValue(float v) {
        return (short) (Math.round(Math.max(0f, Math.min(1f, v)) * 65534f) - 32767);
    }
}
