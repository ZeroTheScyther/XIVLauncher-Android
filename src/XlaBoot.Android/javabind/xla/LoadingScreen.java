package xla;

import android.content.Context;
import android.graphics.Color;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.View;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ProgressBar;

/**
 * Covers the X screen from launch until the game draws its first frame, so the Wine desktop, the injector's
 * console and the helper processes starting up are never on screen. XServerHost lifts it from the present
 * listener once a frame comes from the game's own window.
 *
 * It lifts itself after TIMEOUT_MS regardless: a Wine error dialog or crash window must not stay hidden
 * behind it with no way to see it.
 */
final class LoadingScreen extends FrameLayout {
    private static final long TIMEOUT_MS = 3 * 60 * 1000;
    private static final long FADE_MS = 300;

    private final Handler ui = new Handler(Looper.getMainLooper());
    private volatile boolean dismissed;

    LoadingScreen(Context context) {
        super(context);
        setBackgroundColor(Color.BLACK);
        // Swallows touches so nothing lands on the Wine desktop underneath.
        setClickable(true);

        LinearLayout column = new LinearLayout(context);
        column.setOrientation(LinearLayout.VERTICAL);
        column.setGravity(Gravity.CENTER_HORIZONTAL);

        int logoId = context.getResources().getIdentifier("appicon_foreground", "mipmap", context.getPackageName());
        if (logoId != 0) {
            ImageView logo = new ImageView(context);
            logo.setImageResource(logoId);
            int size = PerfHud.dp(context, 160);
            column.addView(logo, new LinearLayout.LayoutParams(size, size));
        }

        ProgressBar spinner = new ProgressBar(context);
        spinner.setIndeterminate(true);
        int spinnerSize = PerfHud.dp(context, 36);
        LinearLayout.LayoutParams spinnerParams = new LinearLayout.LayoutParams(spinnerSize, spinnerSize);
        spinnerParams.topMargin = PerfHud.dp(context, 8);
        column.addView(spinner, spinnerParams);

        addView(column, new FrameLayout.LayoutParams(
                LayoutParams.WRAP_CONTENT, LayoutParams.WRAP_CONTENT, Gravity.CENTER));

        ui.postDelayed(this::dismiss, TIMEOUT_MS);
    }

    boolean isDismissed() {
        return dismissed;
    }

    /** Fades out and gets out of the way for good. Any thread; only the first call does anything. */
    void dismiss() {
        if (dismissed) return;
        dismissed = true;
        ui.removeCallbacksAndMessages(null);
        ui.post(() -> animate().alpha(0f).setDuration(FADE_MS).withEndAction(() -> setVisibility(View.GONE)));
    }
}
