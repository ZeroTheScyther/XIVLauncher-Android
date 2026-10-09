package xla;

import android.os.SystemClock;
import android.view.HapticFeedbackConstants;
import android.view.InputDevice;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewConfiguration;

import com.winlator.renderer.VulkanRenderer;
import com.winlator.winhandler.WinHandler;
import com.winlator.xserver.Pointer;
import com.winlator.xserver.XServer;

/**
 * Touch and physical-mouse bridge to the X pointer.
 *
 * One finger is the left button and two are the right. A tap lands the X pointer where you touched
 * and clicks, a drag is a left drag, and a finger held still holds the button down. Two fingers
 * tapped are a right click, held still they hold the right button, and moved they scroll the wheel.
 * In touchpad mode the finger's position means nothing: a swipe moves the pointer by its travel,
 * taps and holds act wherever the pointer is, and a tap followed at once by a second touch is a
 * left drag without the wait.
 * A USB/Bluetooth mouse moves the pointer absolutely, with its real buttons and scroll wheel.
 *
 * Coordinates are mapped through whichever fit the renderer is currently using. That has to track
 * the menu's "Screen fit" setting: the previous version always assumed an aspect-preserving fit, so
 * under Stretch (the tuned default) every tap landed compressed toward the centre of the screen.
 */
final class XTouchHandler implements View.OnTouchListener {

    /**
     * A quick tap delivers DOWN and UP a few ms apart. FFXIV samples mouse state once per frame,
     * so a press shorter than a frame is never seen (verified: a 4ms tap only moved the cursor,
     * a 250ms press clicked). Hold the button for at least this long, which covers ~3 frames at 30fps.
     */
    private static final long MIN_HOLD_MS = 100;
    /** Two-finger travel per wheel notch, in dp. */
    private static final float SCROLL_STEP_DP = 24f;

    /**
     * Where a touch gesture has got to. Nothing is pressed at PENDING: the left button cannot go down
     * with the finger, or the first finger of a two-finger tap would left-click before the right click.
     * TWO is two fingers down and undecided between right button and wheel; DONE ignores what is left
     * of a touch whose click has already been sent.
     */
    private static final int IDLE = 0, PENDING = 1, LEFT = 2, RIGHT = 3, SCROLL = 4, MOVE = 5, TWO = 6, DONE = 7;
    /** Touchpad mode: pointer travel per unit of finger travel, both measured on the game's screen. */
    private static final float TOUCHPAD_SPEED = 1.5f;

    private final XServer xServer;
    private final int screenWidth;
    private final int screenHeight;

    private int scaleMode = VulkanRenderer.SCALE_FIT;
    private boolean clicksEnabled = true;
    private long pressedAt;
    private Runnable pendingRelease;
    private int gesture = IDLE;
    private Runnable pendingHold;
    /** Mean view position of the fingers when the last one landed, for the slop test. */
    private float downX, downY;
    private boolean touchpad;
    /** Mean view position of the fingers at the last move, and the travel not yet sent. */
    private float padX, padY, padRestX, padRestY;
    /** Touchpad mode: when the last tap lifted, for tap-and-drag. */
    private long tapUpAt;
    private boolean afterTap;
    /** Mean finger height at the last scroll step; NaN after the finger count changed. */
    private float scrollY = Float.NaN;
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
        if (!enabled) endGesture();
    }

    /** Touchpad mode instead of tap-to-click, from the in-game menu. */
    void setTouchpad(boolean enabled) {
        if (touchpad == enabled) return;
        touchpad = enabled;
        endGesture();
    }

    /** Drops a gesture in flight: its remaining events will not arrive, or would be read the other way. */
    private void endGesture() {
        pendingHold = null;
        tapUpAt = 0;
        if (gesture == LEFT) xServer.injectPointerButtonRelease(Pointer.Button.BUTTON_LEFT);
        if (gesture == RIGHT) xServer.injectPointerButtonRelease(Pointer.Button.BUTTON_RIGHT);
        gesture = IDLE;
    }

    @Override
    public boolean onTouch(View v, MotionEvent event) {
        // A mouse reports through the touch listener as well on some builds; it is handled as a mouse.
        if (isMouse(event))
            return onMouseEvent(v, event);

        if (!clicksEnabled) return false;

        int vw = v.getWidth(), vh = v.getHeight();
        if (vw == 0 || vh == 0) return false;

        int action = event.getActionMasked();
        // Mean of the fingers that are still down after this event.
        float mx = 0f, my = 0f;
        int fingers = 0;
        for (int i = 0; i < event.getPointerCount(); i++) {
            if (action == MotionEvent.ACTION_POINTER_UP && i == event.getActionIndex()) continue;
            mx += event.getX(i);
            my += event.getY(i);
            fingers++;
        }
        mx /= fingers;
        my /= fingers;

        switch (action) {
            case MotionEvent.ACTION_DOWN: {
                flushPendingRelease(v);
                cancelHold(v);
                anchor(mx, my);
                downX = mx;
                downY = my;
                // Touchpad: a touch straight after a tap drags with the left button as soon as it moves,
                // without waiting out the hold. The press waits for the move because the tap's own
                // release has only just gone out, and the game would miss a gap that short.
                afterTap = touchpad && event.getEventTime() - tapUpAt <= ViewConfiguration.getDoubleTapTimeout();
                tapUpAt = 0;
                if (!touchpad) {
                    int[] point = toScreen(vw, vh, mx, my);
                    xServer.injectPointerMove(point[0], point[1]);
                }
                gesture = PENDING;
                startHold(v, Pointer.Button.BUTTON_LEFT, LEFT);
                return true;
            }
            case MotionEvent.ACTION_POINTER_DOWN:
                MouseTrace.log("touch finger " + fingers + " down, gesture " + gesture
                        + ", " + (event.getEventTime() - event.getDownTime()) + " ms after the first");
                anchor(mx, my);
                scrollY = Float.NaN;
                if (fingers != 2 || gesture == SCROLL || gesture == RIGHT || gesture == DONE) return true;
                // Two fingers are the right button or the wheel; what they do next says which.
                cancelHold(v);
                if (gesture == LEFT) release(v, Pointer.Button.BUTTON_LEFT, event.getEventTime());
                downX = mx;
                downY = my;
                gesture = TWO;
                startHold(v, Pointer.Button.BUTTON_RIGHT, RIGHT);
                return true;
            case MotionEvent.ACTION_POINTER_UP:
                anchor(mx, my);
                scrollY = Float.NaN;
                if (gesture == TWO) {
                    // Lifted without moving or waiting: a right click.
                    cancelHold(v);
                    press(Pointer.Button.BUTTON_RIGHT, RIGHT, event.getEventTime());
                }
                if (gesture == RIGHT) {
                    release(v, Pointer.Button.BUTTON_RIGHT, event.getEventTime());
                    gesture = DONE;
                }
                return true;
            case MotionEvent.ACTION_MOVE:
                if (gesture == SCROLL) {
                    scroll(v, my);
                } else if (gesture == PENDING || gesture == TWO) {
                    int slop = ViewConfiguration.get(v.getContext()).getScaledTouchSlop();
                    if (Math.hypot(mx - downX, my - downY) <= slop) return true;
                    cancelHold(v);
                    if (gesture == TWO) {
                        gesture = SCROLL;
                        scrollY = Float.NaN;
                        return true;
                    }
                    // Tap-to-click presses where the finger landed, so a drag starts on what was touched.
                    if (!touchpad || afterTap) press(Pointer.Button.BUTTON_LEFT, LEFT, event.getEventTime());
                    else gesture = MOVE;
                    glide(v, mx, my);
                } else if (gesture == LEFT || gesture == RIGHT || gesture == MOVE) {
                    glide(v, mx, my);
                }
                return true;
            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_CANCEL:
                MouseTrace.log("touch ended, gesture " + gesture + ", lasted "
                        + (event.getEventTime() - event.getDownTime()) + " ms, action " + action);
                cancelHold(v);
                if (gesture == PENDING && action == MotionEvent.ACTION_UP) {
                    press(Pointer.Button.BUTTON_LEFT, LEFT, event.getEventTime());
                    tapUpAt = event.getEventTime();
                }
                if (gesture == LEFT) release(v, Pointer.Button.BUTTON_LEFT, event.getEventTime());
                if (gesture == RIGHT) release(v, Pointer.Button.BUTTON_RIGHT, event.getEventTime());
                gesture = IDLE;
                return true;
            default:
                return false;
        }
    }

    /** Fingers that stay put for the long-press time hold a button: one finger the left, two the right. */
    private void startHold(View v, Pointer.Button button, int next) {
        final int from = gesture;
        pendingHold = new Runnable() {
            @Override public void run() {
                if (pendingHold != this || gesture != from) return;
                pendingHold = null;
                press(button, next, SystemClock.uptimeMillis());
                v.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS);
            }
        };
        v.postDelayed(pendingHold, ViewConfiguration.getLongPressTimeout());
    }

    private void press(Pointer.Button button, int next, long time) {
        MouseTrace.log("touch press " + button + " from gesture " + gesture);
        xServer.injectPointerButtonPress(button);
        pressedAt = time;
        gesture = next;
    }

    /** Releases now, or once the press has lasted MIN_HOLD_MS. */
    private void release(View v, Pointer.Button button, long time) {
        flushPendingRelease(v);
        long remaining = MIN_HOLD_MS - (time - pressedAt);
        if (remaining <= 0) {
            xServer.injectPointerButtonRelease(button);
            return;
        }
        pendingRelease = () -> {
            pendingRelease = null;
            xServer.injectPointerButtonRelease(button);
        };
        v.postDelayed(pendingRelease, remaining);
    }

    /** Restarts motion tracking from here, whenever a finger lands or lifts and the mean jumps. */
    private void anchor(float x, float y) {
        padX = x;
        padY = y;
        padRestX = padRestY = 0f;
    }

    /**
     * Pointer motion: the fingers' travel since the last event, scaled to the game's screen, and faster
     * than the finger in touchpad mode. The fraction of a pixel left over is carried, or a slow swipe
     * would round to nothing every event.
     *
     * Always relative, like a held mouse button in onMouseEvent: FFXIV drags the camera by warping the
     * cursor back to where the drag began, and an absolute position would overwrite each warp.
     */
    private void glide(View v, float x, float y) {
        float[] fit = fit(v.getWidth(), v.getHeight());
        float speed = touchpad ? TOUCHPAD_SPEED : 1f;
        padRestX += (x - padX) / fit[0] * speed;
        padRestY += (y - padY) / fit[1] * speed;
        padX = x;
        padY = y;
        int dx = (int) padRestX, dy = (int) padRestY;
        if (dx == 0 && dy == 0) return;
        padRestX -= dx;
        padRestY -= dy;
        xServer.injectPointerMoveDelta(dx, dy);
    }

    /** Fingers moving up scroll down, as a touchscreen list does. One wheel notch per SCROLL_STEP_DP. */
    private void scroll(View v, float y) {
        if (Float.isNaN(scrollY)) {
            scrollY = y;
            return;
        }
        float step = SCROLL_STEP_DP * v.getResources().getDisplayMetrics().density;
        while (Math.abs(y - scrollY) >= step) {
            boolean up = y > scrollY;
            Pointer.Button button = up ? Pointer.Button.BUTTON_SCROLL_UP : Pointer.Button.BUTTON_SCROLL_DOWN;
            xServer.injectPointerButtonPress(button);
            xServer.injectPointerButtonRelease(button);
            scrollY += up ? step : -step;
        }
    }

    private void cancelHold(View v) {
        if (pendingHold == null) return;
        v.removeCallbacks(pendingHold);
        pendingHold = null;
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
     * A mouse or trackpad event, and not a stick or pad button. This is the event's own source, so a
     * DualShock 4's touchpad counts: it arrives as plain SOURCE_MOUSE and moves the cursor through the
     * uncaptured path. XServerHost.hasExternalMouse goes by the device and never captures for it.
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
        float[] fit = fit(vw, vh);
        int x = (int) ((ex - fit[2]) / fit[0]);
        int y = (int) ((ey - fit[3]) / fit[1]);
        return new int[]{
                Math.max(0, Math.min(screenWidth - 1, x)),
                Math.max(0, Math.min(screenHeight - 1, y)),
        };
    }

    /** View pixels per screen pixel in x and y, then the screen's offset inside the view. */
    private float[] fit(int vw, int vh) {
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
        return new float[]{scaleX, scaleY, offsetX, offsetY};
    }

    /** A new touch arrived before the previous tap's delayed release: release it now, in order. */
    private void flushPendingRelease(View v) {
        if (pendingRelease == null) return;
        Runnable r = pendingRelease;
        v.removeCallbacks(r);
        r.run();
    }
}
