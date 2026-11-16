package xla;

import android.content.Context;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.ColorDrawable;
import android.graphics.drawable.StateListDrawable;
import android.os.Handler;
import android.os.Looper;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.View;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import java.util.Locale;

/**
 * In-game side menu, GameNative quick-menu style: slides in from the left over the game.
 * Opened by Back (gesture / nav button) or the controller's Guide/PS button. While open it takes
 * touch, D-pad + A, and B / Back / Esc closes it.
 *
 * Only settings that take effect immediately live here. Anything that needs a relaunch (game
 * location, graphics driver, resolution, frame cap, FEXCore and Wine knobs) is in the launcher's
 * settings screen instead, reached from the cog on the login page - changing it mid-game would
 * only have shown a value that did not match what was running.
 */
final class GameMenu extends FrameLayout {

    interface Actions {
        void setHudVisible(boolean visible);
        void applyDisplaySettings();
        void applyInputSettings();
        void editTouchControls();
        void menuClosed();
        void exitGame();
    }

    private static final int PANEL_WIDTH_DP = 300;
    private static final long SLIDE_MS = 150;
    private static final long EXIT_CONFIRM_MS = 3000;

    private final XlaSettings settings;
    private final Actions actions;
    private final ScrollView scroll;
    private final LinearLayout panel;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final TextView hudRow;
    private final TextView upscalerRow;
    private final TextView fitRow;
    private final TextView touchControlsRow;
    private final TextView keyboardButtonRow;
    private final TextView keyboardAutoOpenRow;
    private final TextView stickModeRow;
    private final TextView touchModeRow;
    private final TextView deadZoneRow;
    private final TextView exitRow;
    private boolean exitArmed;

    private final Runnable disarmExit = () -> {
        exitArmed = false;
        refresh();
    };

    GameMenu(Context context, XlaSettings settings, Actions actions) {
        super(context);
        this.settings = settings;
        this.actions = actions;
        setVisibility(GONE);

        View scrim = new View(context);
        scrim.setBackgroundColor(0x66000000);
        scrim.setOnClickListener(v -> hide());
        addView(scrim, new LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT));

        panel = new LinearLayout(context);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(0, dp(16), 0, dp(16));

        scroll = new ScrollView(context);
        scroll.setBackgroundColor(0xF2202127);
        scroll.setClickable(true); // taps on the panel's empty space must not reach the scrim
        scroll.addView(panel, new ScrollView.LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT));
        addView(scroll, new LayoutParams(dp(PANEL_WIDTH_DP), LayoutParams.MATCH_PARENT, Gravity.START));

        TextView title = text("XIVLauncher", 20, Color.WHITE);
        title.setTypeface(Typeface.DEFAULT_BOLD);
        title.setPadding(dp(20), dp(4), dp(20), dp(8));
        panel.addView(title);

        header(UiText.get("display"));
        hudRow = row(v -> {
            boolean visible = !settings.isHudVisible();
            settings.setHudVisible(visible);
            actions.setHudVisible(visible);
            refresh();
        });

        upscalerRow = row(v -> {
            settings.cycleUpscaler();
            actions.applyDisplaySettings();
            refresh();
        });
        fitRow = row(v -> {
            settings.cycleScreenFit();
            actions.applyDisplaySettings();
            refresh();
        });

        header(UiText.get("controls"));
        touchControlsRow = row(v -> {
            settings.cycleTouchControls();
            actions.applyInputSettings();
            refresh();
        });
        // Shown apart from the pad, so the keyboard stays one tap away with the pad hidden.
        keyboardButtonRow = row(v -> {
            settings.setKeyboardButtonVisible(!settings.isKeyboardButtonVisible());
            actions.applyInputSettings();
            refresh();
        });
        keyboardAutoOpenRow = row(v -> {
            settings.setKeyboardAutoOpen(!settings.isKeyboardAutoOpen());
            refresh();
        });
        stickModeRow = row(v -> {
            settings.cycleStickMode();
            actions.applyInputSettings();
            refresh();
        });
        TextView editRow = row(v -> {
            hide();
            actions.editTouchControls();
        });
        editRow.setText(UiText.get("edit_controls"));
        touchModeRow = row(v -> {
            settings.cycleTouchMode();
            actions.applyInputSettings();
            refresh();
        });
        deadZoneRow = row(v -> {
            settings.cycleDeadZone();
            actions.applyInputSettings();
            refresh();
        });

        header(UiText.get("game"));
        TextView back = row(v -> hide());
        back.setText(UiText.get("back_to_game"));
        exitRow = row(v -> {
            if (exitArmed) {
                actions.exitGame();
                return;
            }
            exitArmed = true;
            refresh();
            handler.removeCallbacks(disarmExit);
            handler.postDelayed(disarmExit, EXIT_CONFIRM_MS);
        });
        exitRow.setTextColor(0xFFFF8A80);

        refresh();
    }

    /**
     * Logical state, not visibility: hide() slides the panel out and only goes GONE when the animation
     * ends, but everything reacting to the close (the mouse grab above all) runs immediately. Reading
     * visibility there saw the menu as still open, so the mouse was never re-captured after the menu
     * had been opened once.
     */
    private boolean open;

    boolean isOpen() {
        return open;
    }

    void toggle() {
        if (isOpen()) hide();
        else show();
    }

    void show() {
        if (isOpen()) return;
        exitArmed = false;
        refresh();
        open = true;
        setVisibility(VISIBLE);
        scroll.setTranslationX(-dp(PANEL_WIDTH_DP));
        scroll.animate().translationX(0).setDuration(SLIDE_MS).start();
    }

    void hide() {
        if (!isOpen()) return;
        open = false;
        handler.removeCallbacks(disarmExit);
        scroll.animate().translationX(-dp(PANEL_WIDTH_DP)).setDuration(SLIDE_MS)
                .withEndAction(() -> setVisibility(GONE)).start();
        actions.menuClosed();
    }

    /**
     * Keys while the menu is open. Returns false for navigation keys so Android's normal focus
     * handling moves between rows.
     */
    boolean handleKey(KeyEvent event) {
        boolean up = event.getAction() == KeyEvent.ACTION_UP;
        switch (event.getKeyCode()) {
            case KeyEvent.KEYCODE_BACK:
            case KeyEvent.KEYCODE_ESCAPE:
            case KeyEvent.KEYCODE_BUTTON_B:
            case KeyEvent.KEYCODE_BUTTON_MODE:
                if (up) hide();
                return true;
            case KeyEvent.KEYCODE_BUTTON_A:
                if (up) {
                    View focused = findFocus();
                    if (focused != null) focused.performClick();
                    else hudRow.requestFocus();
                }
                return true;
            default:
                return false;
        }
    }

    private void refresh() {
        hudRow.setText(UiText.row("performance_overlay", UiText.onOff(settings.isHudVisible())));
        upscalerRow.setText(UiText.row("upscaler", XlaSettings.label(settings.getUpscaler())));
        fitRow.setText(UiText.row("screen_fit", XlaSettings.label(settings.getScreenFit())));
        touchControlsRow.setText(UiText.row("onscreen_pad", XlaSettings.label(settings.getTouchControls())));
        keyboardButtonRow.setText(UiText.row("keyboard_button", UiText.onOff(settings.isKeyboardButtonVisible())));
        keyboardAutoOpenRow.setText(UiText.row("keyboard_auto_open", UiText.onOff(settings.isKeyboardAutoOpen())));
        stickModeRow.setText(UiText.row("onscreen_sticks", XlaSettings.label(settings.getStickMode())));
        touchModeRow.setText(UiText.row("touchscreen", XlaSettings.label(settings.getTouchMode())));
        deadZoneRow.setText(UiText.row("dead_zone", settings.getDeadZonePercent() + "%"));
        exitRow.setText(UiText.get(exitArmed ? "exit_confirm" : "exit_game"));
    }

    private void header(String label) {
        TextView h = text(label.toUpperCase(Locale.US), 12, 0xFF9AA0A6);
        h.setPadding(dp(20), dp(16), dp(20), dp(4));
        panel.addView(h);
    }

    private TextView row(View.OnClickListener onClick) {
        TextView t = text("", 16, Color.WHITE);
        t.setPadding(dp(20), dp(14), dp(20), dp(14));
        t.setFocusable(true);
        t.setClickable(true);
        StateListDrawable background = new StateListDrawable();
        background.addState(new int[]{android.R.attr.state_pressed}, new ColorDrawable(0x33FFFFFF));
        background.addState(new int[]{android.R.attr.state_focused}, new ColorDrawable(0x33FFFFFF));
        background.addState(new int[0], new ColorDrawable(Color.TRANSPARENT));
        t.setBackground(background);
        t.setOnClickListener(onClick);
        panel.addView(t, new LinearLayout.LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT));
        return t;
    }

    private TextView text(String label, int sp, int color) {
        TextView t = new TextView(getContext());
        t.setText(label);
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
        t.setTextColor(color);
        return t;
    }

    private int dp(int dp) {
        return PerfHud.dp(getContext(), dp);
    }
}
