package xla;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.Rect;
import android.graphics.Typeface;
import android.util.SparseArray;
import android.view.MotionEvent;
import android.view.View;

import java.util.ArrayList;

/**
 * On-screen gamepad, laid out to match GameNative's default profile (assets
 * inputcontrols/profiles/controls-0.icp) and drawn in its style, so muscle memory carries over.
 *
 * Geometry follows GameNative exactly (InputControlsView + ControlElement.computeBoundingBox):
 *   snapping   = view width / 100, and every element's half-size is a multiple of it times its scale
 *                (circle button 3, rect button 4x2, D-pad 7, stick 6)
 *   position   = the profile's normalised x times width, y times height
 *   appearance = stroke only, white at 40% opacity, stroke width snapping * 0.25
 * Verified against a screenshot of GameNative itself: every element lands within a few px.
 *
 * Input is fed straight into {@link GamepadBridge}'s virtual pad rather than synthesised as key
 * presses: FFXIV has first-class controller support, so the overlay only has to look like an Xbox
 * pad and the game's own cross hotbar and button prompts work as they do on a console.
 *
 * Touches that miss every control return false, so a FrameLayout keeps dispatching them to the
 * views behind (XServerHost disables tap-to-click while the pad is up, so they simply do nothing).
 */
final class TouchControls extends View {

    /** Stick behaviour. TOUCH is GameNative's "FPS" feel: no stick until a thumb lands. */
    static final String STICKS_TOUCH = "TOUCH";
    static final String STICKS_FIXED = "FIXED";

    /** GameNative's InputControlsView.DEFAULT_OVERLAY_OPACITY. */
    private static final float DEFAULT_OPACITY = 0.4f;
    /** GameNative's ControlElement.STICK_DEAD_ZONE. */
    private static final float STICK_DEAD_ZONE = 0.15f;

    /**
     * Taken from GamepadBridge rather than restated here: these index the bridge's D-pad directly,
     * and a local copy in GameNative's clockwise order (up, right, down, left) silently disagreed
     * with the bridge's (up, down, left, right), sending right as down and down as left.
     * The arrow paths below switch on these names, so the values may be in any order.
     */
    private static final int UP = GamepadBridge.UP, RIGHT = GamepadBridge.RIGHT,
            DOWN = GamepadBridge.DOWN, LEFT = GamepadBridge.LEFT;

    private final GamepadBridge gamepad;
    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Path path = new Path();
    private final ArrayList<Element> elements = new ArrayList<>();
    private final SparseArray<Element> captured = new SparseArray<>();

    /** Floating sticks, used in TOUCH mode; they own no fixed area until touched. */
    private final Stick floatingLeft = new Stick(false, 0f, 0f, 1f, true);
    private final Stick floatingRight = new Stick(true, 0f, 0f, 1f, true);

    private float opacity = DEFAULT_OPACITY;
    private String stickMode = STICKS_TOUCH;
    private int snapping = 1;

    TouchControls(Context context, GamepadBridge gamepad) {
        super(context);
        this.gamepad = gamepad;
        setFocusable(false);
        setClickable(false);
        paint.setTypeface(Typeface.DEFAULT);
        paint.setTextAlign(Paint.Align.CENTER);
    }

    void setOpacity(float value) {
        opacity = Math.max(0.05f, Math.min(1f, value));
        invalidate();
    }

    /** STICKS_TOUCH or STICKS_FIXED. */
    void setStickMode(String mode) {
        String next = STICKS_FIXED.equals(mode) ? STICKS_FIXED : STICKS_TOUCH;
        if (next.equals(stickMode)) return;
        stickMode = next;
        rebuild(getWidth(), getHeight());
        invalidate();
    }

    // ---- layout ---------------------------------------------------------------------------------

    @Override
    protected void onSizeChanged(int w, int h, int oldw, int oldh) {
        super.onSizeChanged(w, h, oldw, oldh);
        rebuild(w, h);
    }

    /**
     * Positions are GameNative's controls-0.icp verbatim. Its x is a fraction of width and its y a
     * fraction of height (ControlsProfile multiplies by getMaxWidth/getMaxHeight).
     */
    private void rebuild(int w, int h) {
        // Release BEFORE dropping the elements: releaseAll walks the list to un-press whatever is
        // held, so clearing first would leave a button latched down in the bridge after a resize.
        releaseAll();
        elements.clear();
        if (w == 0 || h == 0) return;
        snapping = Math.max(1, w / 100);

        elements.add(new DPad(0.10784314f, 0.4f, 0.85f));

        elements.add(new CircleButton(0.8133170f, 0.4f, 1f, "X", GamepadBridge.sdlX()));
        elements.add(new CircleButton(0.8721405f, 0.26666667f, 1f, "Y", GamepadBridge.sdlY()));
        elements.add(new CircleButton(0.8721405f, 0.53333333f, 1f, "A", GamepadBridge.sdlA()));
        elements.add(new CircleButton(0.9309641f, 0.4f, 1f, "B", GamepadBridge.sdlB()));

        elements.add(new RectButton(0.07f, 0.07f, 2f, "LT", -1, false, false));
        elements.add(new RectButton(0.03f, 0.22222224f, 1f, "LB", GamepadBridge.sdlLb(), false, false));
        elements.add(new RectButton(0.93f, 0.07f, 2f, "RT", -1, true, false));
        elements.add(new RectButton(0.97f, 0.22222224f, 1f, "RB", GamepadBridge.sdlRb(), false, false));

        elements.add(new RectButton(0.46078432f, 0.9111111f, 0.85f, "SELECT", GamepadBridge.sdlBack(), false, true));
        elements.add(new RectButton(0.53880721f, 0.9111111f, 0.85f, "START", GamepadBridge.sdlStart(), false, true));

        elements.add(new CircleButton(0.05f, 0.73333335f, 0.85f, "L3", GamepadBridge.sdlLStick()));
        elements.add(new CircleButton(0.95f, 0.73333335f, 0.85f, "R3", GamepadBridge.sdlRStick()));

        if (STICKS_FIXED.equals(stickMode)) {
            elements.add(new Stick(false, 0.21568628f, 0.73333335f, 1f, false));
            elements.add(new Stick(true, 0.78431374f, 0.73333335f, 1f, false));
        }

        for (Element element : elements) element.layout(w, h);
        floatingLeft.layout(w, h);
        floatingRight.layout(w, h);
    }

    // ---- input ----------------------------------------------------------------------------------

    @Override
    public boolean onTouchEvent(MotionEvent event) {
        if (getVisibility() != VISIBLE) return false;

        int index = event.getActionIndex();
        int pointerId = event.getPointerId(index);

        switch (event.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
            case MotionEvent.ACTION_POINTER_DOWN: {
                float x = event.getX(index), y = event.getY(index);
                Element hit = findElement(x, y);
                if (hit == null) hit = floatingStickFor(x, y);
                if (hit == null) return false; // nothing here: let the touch through
                captured.put(pointerId, hit);
                hit.onDown(x, y);
                invalidate();
                return true;
            }
            case MotionEvent.ACTION_MOVE: {
                boolean handled = false;
                for (int i = 0; i < event.getPointerCount(); i++) {
                    Element element = captured.get(event.getPointerId(i));
                    if (element == null) continue;
                    element.onMove(event.getX(i), event.getY(i));
                    handled = true;
                }
                if (handled) invalidate();
                return handled;
            }
            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_POINTER_UP: {
                Element element = captured.get(pointerId);
                if (element == null) return false;
                captured.remove(pointerId);
                element.onUp();
                invalidate();
                return true;
            }
            case MotionEvent.ACTION_CANCEL:
                releaseAll();
                invalidate();
                return true;
            default:
                return false;
        }
    }

    /**
     * TOUCH mode: a thumb landing on empty space raises a stick there - left half drives movement,
     * right half the camera, which is what makes the "FPS" layout comfortable.
     */
    private Element floatingStickFor(float x, float y) {
        if (!STICKS_TOUCH.equals(stickMode)) return null;
        Stick stick = x < getWidth() * 0.5f ? floatingLeft : floatingRight;
        return stick.active ? null : stick; // one finger per stick
    }

    private Element findElement(float x, float y) {
        // Reverse order: the last added (smallest, most specific) wins an overlap.
        for (int i = elements.size() - 1; i >= 0; i--) {
            Element element = elements.get(i);
            if (element.contains(x, y)) return element;
        }
        return null;
    }

    /** Releases every held control, so nothing sticks when the overlay is hidden or a gesture cancels. */
    void releaseAll() {
        captured.clear();
        for (Element element : elements) element.onUp();
        floatingLeft.onUp();
        floatingRight.onUp();
    }

    @Override
    public void setVisibility(int visibility) {
        if (visibility != VISIBLE) releaseAll();
        super.setVisibility(visibility);
    }

    // ---- drawing --------------------------------------------------------------------------------

    @Override
    protected void onDraw(Canvas canvas) {
        float strokeWidth = snapping * 0.25f;
        for (Element element : elements) element.draw(canvas, strokeWidth);
        if (STICKS_TOUCH.equals(stickMode)) {
            if (floatingLeft.active) floatingLeft.draw(canvas, strokeWidth);
            if (floatingRight.active) floatingRight.draw(canvas, strokeWidth);
        }
    }

    /** GameNative's InputControlsView.getPrimaryColor: white at the overlay opacity. */
    private int primaryColor() {
        return Color.argb((int) (opacity * 255), 255, 255, 255);
    }

    private void strokePaint(float strokeWidth) {
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(strokeWidth);
        paint.setColor(primaryColor());
    }

    private void drawLabel(Canvas canvas, String text, float cx, float cy, float maxWidth, float scale) {
        paint.setStyle(Paint.Style.FILL);
        paint.setColor(primaryColor());
        float size = Math.min(snapping * 2f * scale, textSizeForWidth(text, maxWidth));
        paint.setTextSize(size);
        canvas.drawText(text, cx, cy - (paint.descent() + paint.ascent()) * 0.5f, paint);
    }

    /** Largest text size whose string still fits the given width, like GameNative's helper. */
    private float textSizeForWidth(String text, float width) {
        final float probe = 48f;
        paint.setTextSize(probe);
        float measured = paint.measureText(text);
        return measured <= 0f ? probe : probe * width / measured;
    }

    // ---- elements -------------------------------------------------------------------------------

    private abstract class Element {
        final float nx, ny, scale;
        final Rect bounds = new Rect();

        Element(float nx, float ny, float scale) {
            this.nx = nx;
            this.ny = ny;
            this.scale = scale;
        }

        /** Half-size in snapping units, before scale. */
        abstract int halfUnitsX();
        abstract int halfUnitsY();

        void layout(int w, int h) {
            int cx = (int) (nx * w), cy = (int) (ny * h);
            int halfW = (int) (halfUnitsX() * snapping * scale);
            int halfH = (int) (halfUnitsY() * snapping * scale);
            bounds.set(cx - halfW, cy - halfH, cx + halfW, cy + halfH);
        }

        boolean contains(float x, float y) {
            return bounds.contains((int) x, (int) y);
        }

        void onDown(float x, float y) { onMove(x, y); }
        abstract void onMove(float x, float y);
        abstract void onUp();
        abstract void draw(Canvas canvas, float strokeWidth);
    }

    /** Round face button / stick click. GameNative: half-size 3 units, drawn as an outlined circle. */
    private final class CircleButton extends Element {
        private final String text;
        private final int sdlButton;
        private boolean down;

        CircleButton(float nx, float ny, float scale, String text, int sdlButton) {
            super(nx, ny, scale);
            this.text = text;
            this.sdlButton = sdlButton;
        }

        @Override int halfUnitsX() { return 3; }
        @Override int halfUnitsY() { return 3; }

        @Override boolean contains(float x, float y) {
            float cx = bounds.centerX(), cy = bounds.centerY();
            return Math.hypot(x - cx, y - cy) <= bounds.width() * 0.5f;
        }

        @Override void onMove(float x, float y) {
            if (down) return;
            down = true;
            gamepad.setVirtualButton(sdlButton, true);
        }

        @Override void onUp() {
            if (!down) return;
            down = false;
            gamepad.setVirtualButton(sdlButton, false);
        }

        @Override void draw(Canvas canvas, float strokeWidth) {
            float cx = bounds.centerX(), cy = bounds.centerY();
            strokePaint(down ? strokeWidth * 2f : strokeWidth);
            canvas.drawCircle(cx, cy, bounds.width() * 0.5f, paint);
            drawLabel(canvas, text, cx, cy, bounds.width() - strokeWidth * 4f, scale);
        }
    }

    /** Shoulder, trigger, Start/Select. GameNative: half-size 4x2 units. */
    private final class RectButton extends Element {
        private final String text;
        private final int sdlButton;
        private final boolean rightTrigger;
        private final boolean rounded;
        private boolean down;

        RectButton(float nx, float ny, float scale, String text, int sdlButton,
                   boolean rightTrigger, boolean rounded) {
            super(nx, ny, scale);
            this.text = text;
            this.sdlButton = sdlButton;
            this.rightTrigger = rightTrigger;
            this.rounded = rounded;
        }

        @Override int halfUnitsX() { return 4; }
        @Override int halfUnitsY() { return 2; }

        @Override void onMove(float x, float y) {
            if (down) return;
            down = true;
            if (sdlButton >= 0) gamepad.setVirtualButton(sdlButton, true);
            else gamepad.setVirtualTrigger(rightTrigger, true);
        }

        @Override void onUp() {
            if (!down) return;
            down = false;
            if (sdlButton >= 0) gamepad.setVirtualButton(sdlButton, false);
            else gamepad.setVirtualTrigger(rightTrigger, false);
        }

        @Override void draw(Canvas canvas, float strokeWidth) {
            strokePaint(down ? strokeWidth * 2f : strokeWidth);
            if (rounded) {
                float radius = bounds.height() * 0.5f;
                canvas.drawRoundRect(bounds.left, bounds.top, bounds.right, bounds.bottom, radius, radius, paint);
            } else {
                canvas.drawRect(bounds, paint);
            }
            drawLabel(canvas, text, bounds.centerX(), bounds.centerY(),
                    bounds.width() - strokeWidth * 4f, scale);
        }
    }

    /** Four-arrow cross, ported from GameNative's ControlElement.setDPadDirectionPath. */
    private final class DPad extends Element {
        private final boolean[] pressed = new boolean[4];

        DPad(float nx, float ny, float scale) {
            super(nx, ny, scale);
        }

        @Override int halfUnitsX() { return 7; }
        @Override int halfUnitsY() { return 7; }

        @Override void onMove(float x, float y) {
            float cx = bounds.centerX(), cy = bounds.centerY();
            float dx = x - cx, dy = y - cy;
            float threshold = bounds.width() * 0.5f * 0.22f;
            set(UP, dy < -threshold);
            set(DOWN, dy > threshold);
            set(LEFT, dx < -threshold);
            set(RIGHT, dx > threshold);
        }

        @Override void onUp() {
            for (int i = 0; i < 4; i++) set(i, false);
        }

        private void set(int direction, boolean down) {
            if (pressed[direction] == down) return;
            pressed[direction] = down;
            gamepad.setVirtualDpad(direction, down);
        }

        @Override void draw(Canvas canvas, float strokeWidth) {
            float cx = bounds.centerX(), cy = bounds.centerY();
            float offsetX = snapping * 2 * scale;
            float offsetY = snapping * 3 * scale;
            float start = snapping * scale;
            for (int i = 0; i < 4; i++) {
                buildArrow(i, cx, cy, offsetX, offsetY, start);
                strokePaint(pressed[i] ? strokeWidth * 2f : strokeWidth);
                canvas.drawPath(path, paint);
            }
        }

        private void buildArrow(int direction, float cx, float cy, float offsetX, float offsetY, float start) {
            path.reset();
            switch (direction) {
                case UP:
                    path.moveTo(cx, cy - start);
                    path.lineTo(cx - offsetX, cy - offsetY);
                    path.lineTo(cx - offsetX, bounds.top);
                    path.lineTo(cx + offsetX, bounds.top);
                    path.lineTo(cx + offsetX, cy - offsetY);
                    break;
                case RIGHT:
                    path.moveTo(cx + start, cy);
                    path.lineTo(cx + offsetY, cy - offsetX);
                    path.lineTo(bounds.right, cy - offsetX);
                    path.lineTo(bounds.right, cy + offsetX);
                    path.lineTo(cx + offsetY, cy + offsetX);
                    break;
                case DOWN:
                    path.moveTo(cx, cy + start);
                    path.lineTo(cx - offsetX, cy + offsetY);
                    path.lineTo(cx - offsetX, bounds.bottom);
                    path.lineTo(cx + offsetX, bounds.bottom);
                    path.lineTo(cx + offsetX, cy + offsetY);
                    break;
                case LEFT:
                    path.moveTo(cx - start, cy);
                    path.lineTo(cx - offsetY, cy - offsetX);
                    path.lineTo(bounds.left, cy - offsetX);
                    path.lineTo(bounds.left, cy + offsetX);
                    path.lineTo(cx - offsetY, cy + offsetX);
                    break;
            }
        }
    }

    /**
     * Thumbstick. GameNative: outer circle half-size 6 units, thumb radius 3.5 units.
     * A floating stick has no home position - it appears wherever the thumb lands and vanishes on release.
     */
    private final class Stick extends Element {
        private final boolean right;
        private final boolean floating;
        private float centreX, centreY, thumbX, thumbY;
        boolean active;

        Stick(boolean right, float nx, float ny, float scale, boolean floating) {
            super(nx, ny, scale);
            this.right = right;
            this.floating = floating;
        }

        @Override int halfUnitsX() { return 6; }
        @Override int halfUnitsY() { return 6; }

        @Override void layout(int w, int h) {
            super.layout(w, h);
            centreX = thumbX = bounds.centerX();
            centreY = thumbY = bounds.centerY();
        }

        private float radius() {
            return 6 * snapping * scale;
        }

        @Override boolean contains(float x, float y) {
            if (floating) return false; // claimed explicitly by floatingStickFor
            float cx = bounds.centerX(), cy = bounds.centerY();
            return Math.hypot(x - cx, y - cy) <= radius();
        }

        @Override void onDown(float x, float y) {
            active = true;
            // Both kinds recentre under the thumb, so the stick never fights where the finger landed.
            centreX = x;
            centreY = y;
            onMove(x, y);
        }

        @Override void onMove(float x, float y) {
            float r = radius();
            float dx = x - centreX, dy = y - centreY;
            float distance = (float) Math.hypot(dx, dy);
            if (distance > r) {
                dx *= r / distance;
                dy *= r / distance;
            }
            thumbX = centreX + dx;
            thumbY = centreY + dy;

            float nxv = dx / r, nyv = dy / r;
            float magnitude = (float) Math.hypot(nxv, nyv);
            if (magnitude <= STICK_DEAD_ZONE) {
                gamepad.setVirtualStick(right, 0f, 0f);
                return;
            }
            // Rescale past the dead zone so full deflection stays reachable.
            float factor = ((magnitude - STICK_DEAD_ZONE) / (1f - STICK_DEAD_ZONE)) / magnitude;
            gamepad.setVirtualStick(right, nxv * factor, nyv * factor);
        }

        @Override void onUp() {
            if (!active) return;
            active = false;
            centreX = thumbX = bounds.centerX();
            centreY = thumbY = bounds.centerY();
            gamepad.setVirtualStick(right, 0f, 0f);
        }

        @Override void draw(Canvas canvas, float strokeWidth) {
            float cx = active ? centreX : bounds.centerX();
            float cy = active ? centreY : bounds.centerY();
            strokePaint(strokeWidth);
            canvas.drawCircle(cx, cy, radius(), paint);

            float thumbRadius = snapping * 3.5f * scale;
            float tx = active ? thumbX : cx, ty = active ? thumbY : cy;
            paint.setStyle(Paint.Style.FILL);
            paint.setColor(Color.argb((int) (opacity * 50), 255, 255, 255));
            canvas.drawCircle(tx, ty, thumbRadius, paint);
            strokePaint(strokeWidth);
            canvas.drawCircle(tx, ty, thumbRadius + strokeWidth * 0.5f, paint);
        }
    }
}
