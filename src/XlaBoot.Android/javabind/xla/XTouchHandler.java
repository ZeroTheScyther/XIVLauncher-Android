package xla;

import android.view.InputDevice;
import android.view.MotionEvent;
import android.view.View;

import com.winlator.renderer.VulkanRenderer;
import com.winlator.winhandler.WinHandler;
import com.winlator.xserver.Pointer;
import com.winlator.xserver.XServer;

/**
 * Touch and physical-mouse bridge to the X pointer.
 *
 * A tap lands the X pointer where you touched and clicks; a USB/Bluetooth mouse moves the pointer
 * absolutely, with its real buttons and scroll wheel. Winlator does this in TouchpadView with
 * several input modes (relative trackpad, gamepad overlays, ...); this is the direct-touch subset,
 * which is what a menu-driven game needs.
 *
 * Coordinates are mapped through whichever fit the renderer is currently using. That has to track
 * the menu's "Screen fit" setting: the previous version always assumed an aspect-preserving fit, so
 * under Stretch (the tuned default) every tap landed compressed toward the centre of the screen.
 *
 * Relative pointer motion is deliberately not used: XServer.injectPointerMoveDelta routes through
 * WinHandler when relativeMouseMovement is set, and our WinHandler is a stub whose mouseEvent drops
 * the call, so enabling it would silently lose all mouse movement.
 */
final class XTouchHandler implements View.OnTouchListener {

    /**
     * A quick tap delivers DOWN and UP a few ms apart. FFXIV samples mouse state once per frame,
     * so a press shorter than a frame is never seen (verified: a 4ms tap only moved the cursor,
     * a 250ms press clicked). Hold the button for at least this long, which covers ~3 frames at 30fps.
     */
    private static final long MIN_HOLD_MS = 100;

    private final XServer xServer;
    private final int screenWidth;
    private final int screenHeight;

    private int scaleMode = VulkanRenderer.SCALE_FIT;
    private boolean clicksEnabled = true;
    private long pressedAt;
    private Runnable pendingRelease;
    private Runnable uncapturedMouseListener;

    /** Android position of the previous uncaptured mouse event, for relative drags; NaN when unknown. */
    private float lastMouseX = Float.NaN, lastMouseY = Float.NaN;

    XTouchHandler(XServer xServer, int screenWidth, int screenHeight) {
        this.xServer = xServer;
        this.screenWidth = screenWidth;
        this.screenHeight = screenHeight;
    }

    /** Told about every mouse event that arrives uncaptured, so the host can retake the grab. */
    void setUncapturedMouseListener(Runnable listener) {
        uncapturedMouseListener = listener;
    }

    /** One of VulkanRenderer.SCALE_FIT / SCALE_FILL / SCALE_STRETCH, from the in-game menu. */
    void setScaleMode(int scaleMode) {
        this.scaleMode = scaleMode;
    }

    /**
     * Turns tap-to-click on and off. It is off while the on-screen pad is up: a thumb resting beside
     * a stick would otherwise click in the world, and the pad's own misses fall through to here.
     * A physical mouse is unaffected.
     */
    void setClicksEnabled(boolean enabled) {
        clicksEnabled = enabled;
    }

    @Override
    public boolean onTouch(View v, MotionEvent event) {
        // A mouse reports through the touch listener as well on some builds; it is handled as a mouse.
        if (isMouse(event))
            return onMouseEvent(v, event);

        if (!clicksEnabled) return false;

        int vw = v.getWidth(), vh = v.getHeight();
        if (vw == 0 || vh == 0) return false;

        int[] point = toScreen(vw, vh, event.getX(), event.getY());
        int x = point[0], y = point[1];

        switch (event.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                flushPendingRelease(v);
                xServer.injectPointerMove(x, y);
                xServer.injectPointerButtonPress(Pointer.Button.BUTTON_LEFT);
                pressedAt = event.getEventTime();
                return true;
            case MotionEvent.ACTION_MOVE:
                xServer.injectPointerMove(x, y);
                return true;
            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_CANCEL: {
                xServer.injectPointerMove(x, y);
                long remaining = MIN_HOLD_MS - (event.getEventTime() - pressedAt);
                if (remaining <= 0) {
                    xServer.injectPointerButtonRelease(Pointer.Button.BUTTON_LEFT);
                } else {
                    pendingRelease = () -> {
                        pendingRelease = null;
                        xServer.injectPointerButtonRelease(Pointer.Button.BUTTON_LEFT);
                    };
                    v.postDelayed(pendingRelease, remaining);
                }
                return true;
            }
            default:
                return false;
        }
    }

    /**
     * Physical mouse: absolute motion, real buttons, scroll wheel. Hover events carry the position
     * while no button is down, which is how a mouse drives a cursor over the game.
     *
     * Buttons are synchronised from getButtonState() on every event rather than driven by
     * ACTION_BUTTON_PRESS alone: a plain ACTION_DOWN would otherwise move the cursor without
     * clicking, and which of the two Android delivers varies. Pointer.setButton only fires on a
     * real change, so re-asserting the same state costs nothing.
     */
    boolean onMouseEvent(View v, MotionEvent event) {
        int vw = v.getWidth(), vh = v.getHeight();
        if (vw == 0 || vh == 0) return false;
        if (xServer.isRelativeMouseMovement()) {
            releaseMouseButtons();
            xServer.setRelativeMouseMovement(false); // uncaptured: absolute X11 positions, as before
        }
        if (uncapturedMouseListener != null) uncapturedMouseListener.run();

        switch (event.getActionMasked()) {
            case MotionEvent.ACTION_HOVER_MOVE:
            case MotionEvent.ACTION_MOVE:
            case MotionEvent.ACTION_DOWN:
            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_BUTTON_PRESS:
            case MotionEvent.ACTION_BUTTON_RELEASE: {
                int buttons = event.getButtonState();
                boolean held = (buttons & (MotionEvent.BUTTON_PRIMARY | MotionEvent.BUTTON_SECONDARY
                        | MotionEvent.BUTTON_TERTIARY)) != 0;
                int[] point = toScreen(vw, vh, event.getX(), event.getY());
                if (held && !Float.isNaN(lastMouseX)) {
                    // A held button is a drag, and FFXIV drags the camera by warping the cursor back to
                    // where the drag began every frame. Absolute positions would overwrite each warp with
                    // the mouse's screen position; relative motion from the last event works with it.
                    // (Only the fallback: with capture held, onCapturedPointer gets raw deltas instead.)
                    int[] last = toScreen(vw, vh, lastMouseX, lastMouseY);
                    int dx = point[0] - last[0], dy = point[1] - last[1];
                    if (MouseTrace.enabled)
                        MouseTrace.motion("absolute-drag", dx, dy, xServer.pointer.getX(), xServer.pointer.getY(), buttons);
                    xServer.injectPointerMoveDelta(dx, dy);
                } else {
                    if (MouseTrace.enabled)
                        MouseTrace.motion("absolute", point[0] - xServer.pointer.getX(), point[1] - xServer.pointer.getY(),
                                point[0], point[1], buttons);
                    xServer.injectPointerMove(point[0], point[1]);
                }
                lastMouseX = event.getX();
                lastMouseY = event.getY();
                syncButtons(buttons);
                return true;
            }
            case MotionEvent.ACTION_HOVER_EXIT:
            case MotionEvent.ACTION_CANCEL:
                syncButtons(0);
                return true;
            case MotionEvent.ACTION_SCROLL: {
                float scroll = event.getAxisValue(MotionEvent.AXIS_VSCROLL);
                if (scroll == 0f) return false;
                Pointer.Button button = scroll > 0f
                        ? Pointer.Button.BUTTON_SCROLL_UP : Pointer.Button.BUTTON_SCROLL_DOWN;
                xServer.injectPointerButtonPress(button);
                xServer.injectPointerButtonRelease(button);
                return true;
            }
            default:
                return false;
        }
    }

    /**
     * Captured mouse: Android hides its own cursor and hands us raw relative motion, which is what
     * lets a held right-click drag the FFXIV camera instead of the pointer stopping at a screen edge.
     *
     * The deltas go through XServer.injectPointerMoveDelta with relativeMouseMovement left FALSE, so
     * they take the X path (moving the X pointer and emitting an XInput2 raw motion). Setting that
     * flag would route them to WinHandler, which is a stub here and would drop them silently.
     */
    boolean onCapturedPointer(View v, MotionEvent event) {
        // Captured motion and buttons go to Windows through winhandler.exe once it is connected: the X server
        // routes injectPointerMoveDelta and button events to WinHandler while relativeMouseMovement is set, and
        // only injected Windows input reaches DirectInput (see WinHandler). Until the helper connects, the X
        // path below still moves the cursor so the launcher-to-game handoff is never mouse-dead.
        WinHandler handler = xServer.getWinHandler();
        boolean viaWindows = handler != null && handler.isReady();
        if (viaWindows != xServer.isRelativeMouseMovement()) {
            releaseMouseButtons(); // a button held across the switch would be released on the wrong path
            xServer.setRelativeMouseMovement(viaWindows);
            MouseTrace.log("mouse route -> " + (viaWindows ? "winhandler (SendInput)" : "X11"));
        }
        switch (event.getActionMasked()) {
            case MotionEvent.ACTION_MOVE:
            case MotionEvent.ACTION_HOVER_MOVE: {
                float dx = 0f, dy = 0f;
                for (int i = 0; i < event.getHistorySize(); i++) {
                    dx += event.getHistoricalAxisValue(MotionEvent.AXIS_RELATIVE_X, i);
                    dy += event.getHistoricalAxisValue(MotionEvent.AXIS_RELATIVE_Y, i);
                }
                dx += event.getAxisValue(MotionEvent.AXIS_RELATIVE_X);
                dy += event.getAxisValue(MotionEvent.AXIS_RELATIVE_Y);
                xServer.injectPointerMoveDelta(Math.round(dx), Math.round(dy));
                syncButtons(event.getButtonState());
                if (MouseTrace.enabled)
                    MouseTrace.motion(viaWindows ? "captured-winhandler" : "captured-x11", Math.round(dx), Math.round(dy),
                            xServer.pointer.getX(), xServer.pointer.getY(), event.getButtonState());
                return true;
            }
            case MotionEvent.ACTION_DOWN:
            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_BUTTON_PRESS:
            case MotionEvent.ACTION_BUTTON_RELEASE:
                syncButtons(event.getButtonState());
                return true;
            case MotionEvent.ACTION_SCROLL: {
                float scroll = event.getAxisValue(MotionEvent.AXIS_VSCROLL);
                MouseTrace.log("captured scroll v=" + scroll + " relative=" + xServer.isRelativeMouseMovement());
                if (scroll == 0f) return false;
                Pointer.Button button = scroll > 0f
                        ? Pointer.Button.BUTTON_SCROLL_UP : Pointer.Button.BUTTON_SCROLL_DOWN;
                xServer.injectPointerButtonPress(button);
                xServer.injectPointerButtonRelease(button);
                return true;
            }
            default:
                MouseTrace.log("captured event not handled: action=" + event.getActionMasked()
                        + " vscroll=" + event.getAxisValue(MotionEvent.AXIS_VSCROLL));
                return false;
        }
    }

    /** Releases any mouse button still held, e.g. when capture is dropped mid-drag. */
    void releaseMouseButtons() {
        syncButtons(0);
    }

    /** Presses/releases the X buttons to match Android's current mouse button mask. */
    private void syncButtons(int buttonState) {
        syncButton(Pointer.Button.BUTTON_LEFT, (buttonState & MotionEvent.BUTTON_PRIMARY) != 0);
        syncButton(Pointer.Button.BUTTON_RIGHT, (buttonState & MotionEvent.BUTTON_SECONDARY) != 0);
        syncButton(Pointer.Button.BUTTON_MIDDLE, (buttonState & MotionEvent.BUTTON_TERTIARY) != 0);
    }

    private void syncButton(Pointer.Button button, boolean pressed) {
        if (pressed == xServer.pointer.isButtonPressed(button)) return;
        MouseTrace.log("button " + button + (pressed ? " DOWN" : " UP")
                + " at (" + xServer.pointer.getX() + "," + xServer.pointer.getY() + ")");
        if (pressed) xServer.injectPointerButtonPress(button);
        else xServer.injectPointerButtonRelease(button);
    }

    /**
     * A real mouse or trackpad, and not a game controller. A DualShock 4 reports SOURCE_MOUSE and
     * SOURCE_TOUCHPAD on the same device as its sticks, so its touchpad must not drive the cursor
     * while it is being used as a pad.
     */
    static boolean isMouse(MotionEvent event) {
        int source = event.getSource();
        boolean mouseLike = (source & InputDevice.SOURCE_MOUSE) == InputDevice.SOURCE_MOUSE
                || (source & InputDevice.SOURCE_MOUSE_RELATIVE) == InputDevice.SOURCE_MOUSE_RELATIVE
                || (source & InputDevice.SOURCE_TOUCHPAD) == InputDevice.SOURCE_TOUCHPAD;
        if (!mouseLike) return false;
        return (source & InputDevice.SOURCE_JOYSTICK) != InputDevice.SOURCE_JOYSTICK
                && (source & InputDevice.SOURCE_GAMEPAD) != InputDevice.SOURCE_GAMEPAD;
    }

    /**
     * View coordinates -> X screen coordinates, mirroring VulkanRenderer.updateTransform:
     * Stretch fills the surface, Fill scales by the larger ratio and centre-crops, Fit by the
     * smaller ratio and letterboxes.
     */
    private int[] toScreen(int vw, int vh, float ex, float ey) {
        float scaleX, scaleY, offsetX = 0f, offsetY = 0f;
        if (scaleMode == VulkanRenderer.SCALE_STRETCH) {
            scaleX = (float) vw / screenWidth;
            scaleY = (float) vh / screenHeight;
        } else {
            float scale = scaleMode == VulkanRenderer.SCALE_FILL
                    ? Math.max((float) vw / screenWidth, (float) vh / screenHeight)
                    : Math.min((float) vw / screenWidth, (float) vh / screenHeight);
            scaleX = scaleY = scale;
            offsetX = (vw - screenWidth * scale) / 2f;
            offsetY = (vh - screenHeight * scale) / 2f;
        }

        int x = (int) ((ex - offsetX) / scaleX);
        int y = (int) ((ey - offsetY) / scaleY);
        return new int[]{
                Math.max(0, Math.min(screenWidth - 1, x)),
                Math.max(0, Math.min(screenHeight - 1, y)),
        };
    }

    /** A new touch arrived before the previous tap's delayed release: release it now, in order. */
    private void flushPendingRelease(View v) {
        if (pendingRelease == null) return;
        Runnable r = pendingRelease;
        v.removeCallbacks(r);
        r.run();
    }
}
