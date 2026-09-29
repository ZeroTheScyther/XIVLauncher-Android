package xla;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.os.Bundle;

/**
 * Brings the launcher back in a fresh process after a game session (ProcessPhoenix's approach).
 *
 * Runs in its own process (":restart" in the manifest), so it can kill the old app process first and only then
 * start the launcher. Starting the launcher from the dying process itself races its death.
 */
public final class RestartActivity extends Activity {
    private static final String EXTRA_PID = "xla.old_pid";

    /** Hands over to a new process; the caller's process is killed from the other side. */
    static void restart(Context context, int oldPid) {
        Intent intent = new Intent(context, RestartActivity.class)
                .putExtra(EXTRA_PID, oldPid)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        context.startActivity(intent);
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        int oldPid = getIntent().getIntExtra(EXTRA_PID, -1);
        if (oldPid > 0) android.os.Process.killProcess(oldPid);

        Intent launch = getPackageManager().getLaunchIntentForPackage(getPackageName());
        if (launch != null) {
            launch.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TASK);
            startActivity(launch);
        }
        finish();
        Runtime.getRuntime().exit(0);
    }
}
