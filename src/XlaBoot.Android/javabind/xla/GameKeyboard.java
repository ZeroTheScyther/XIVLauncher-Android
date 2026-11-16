package xla;

import android.content.Context;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.ColorDrawable;
import android.graphics.drawable.StateListDrawable;
import android.os.Handler;
import android.os.Looper;
import android.text.Editable;
import android.text.InputType;
import android.text.TextWatcher;
import android.util.Log;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.InputDevice;
import android.view.KeyEvent;
import android.view.View;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputMethodManager;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;

import org.json.JSONObject;

import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.nio.charset.StandardCharsets;
import java.util.function.BooleanSupplier;

/**
 * The Android keyboard for the game's text fields.
 *
 * The game runs under Wine and has never heard of a touchscreen, so Android will not raise its keyboard for a field
 * it cannot see, and the player cannot type without a Bluetooth keyboard. The helper plugin reports a field taking
 * input, either one of the game's own or a Dalamud (ImGui) window's; this bar appears with a real Android field; the
 * finished line goes back to the plugin, which puts it into that field, and Enter is pressed as a normal key.
 *
 * The bar can also be opened by hand, from the on-screen keyboard button. If the plugin then reports no field, Enter
 * opens the chat first and the line goes in once the plugin sees it. Typed into nothing, the game took the first
 * keys while the chat was still opening and the line arrived scrambled ("Test" became "stTe").
 *
 * Without the plugin, or when it answers that it found no field, the line is typed as key presses instead
 * (TextTyper), which reaches whatever field has focus.
 *
 * The bar sits at the TOP of the screen. The game window uses adjustNothing - resizing it would change the X screen
 * size mid-session - so the IME overlays the game from the bottom and a bar docked down there would be behind it.
 *
 * Nothing is written to the game until Send: the game never sees a half-typed line.
 */
final class GameKeyboard extends LinearLayout {

    private static final String TAG = "XlaKeyboard";

    /** The plugin sends field state here. */
    static final int LISTEN_PORT = 7949;

    /** The plugin listens here, the same port the battery status goes out on: everything app-to-game arrives there. */
    static final int PLUGIN_PORT = 7948;

    /** FFXIV samples input once per frame, so an injected key has to be held. Same value as XTouchHandler. */
    private static final long KEY_HOLD_MS = 100;

    /** The plugin writes the text on its next frame, so Enter waits long enough to land after it. */
    private static final long SUBMIT_DELAY_MS = 220;

    /** After typed text, a couple of frames for the game to take the last characters before Enter. */
    private static final long SUBMIT_AFTER_TYPING_MS = 120;

    /**
     * Closing the bar hides the IME and returns to immersive full screen, and the game window gets focus and size
     * changes from that. Characters typed during it landed out of order ("Test" became "stTe": the text cursor moved
     * mid-line), so typing waits for the window to settle.
     */
    private static final long TYPE_AFTER_CLOSE_MS = 350;

    /** A version 2 plugin answers on its next frame or draw; past this, it is taken as not having delivered. */
    private static final long ACK_TIMEOUT_MS = 500;

    /** After the plugin confirms, a frame for the text to be in the field before Enter. */
    private static final long SUBMIT_AFTER_ACK_MS = 120;

    /** How long the chat gets to open after Enter before the line is handed back to the bar. */
    private static final long CHAT_OPEN_TIMEOUT_MS = 1000;

    /** Fallback length limit when the plugin could not read the field's own. */
    private static final int DEFAULT_MAX_CHAR = 500;

    private final Handler handler = new Handler(Looper.getMainLooper());
    private final EditText input;
    private final TextView notice;
    private final Runnable onClosed;
    private final BooleanSupplier autoOpen;

    private InetAddress loopback;
    private DatagramSocket listener;
    private DatagramSocket sender;
    private Thread receiver;
    private volatile boolean running = true;

    private int maxChar = DEFAULT_MAX_CHAR;
    private int maxByte;
    private int allow;
    private boolean multiline;
    private boolean open;
    private boolean filtering;

    /**
     * Whether the plugin reported the field this bar is for. Then the plugin writes the line into it. Opened by hand,
     * nothing in the game knows about the bar, so the line is typed instead (TextTyper), which reaches any field.
     */
    private boolean fromPlugin;

    /** The plugin's protocol version, from its field reports. Version 1 never acknowledges, so it is never second-guessed. */
    private int pluginVersion = 1;

    /** "game" or "imgui": which kind of field the plugin reported, and so where it should put the text. */
    private String fieldKind = "game";

    /** Whether a field report has arrived, and so whether the plugin is running at all. */
    private boolean pluginSeen;

    /** The field as last reported, so a bar opened by hand knows about a field that was already open. */
    private boolean fieldActive;
    private int fieldMax, fieldBytes, fieldAllow;
    private boolean fieldLines;

    /** A line waiting for the chat to open (Enter was pressed for it), or null. */
    private String waitingForField;
    private final Runnable chatOpenTimeout = this::chatDidNotOpen;

    private int nextId;

    /** The delivery waiting for the plugin's answer, or 0. */
    private int awaitingId;
    private String awaitingText = "";
    private final Runnable ackTimeout = () -> {
        if (awaitingId == 0) return;
        Log.i(TAG, "no answer from the plugin; typing the text instead");
        awaitingId = 0;
        typeInstead(awaitingText, 0);
    };

    /**
     * @param onClosed run after the bar closes, so the caller can go back to immersive full screen (showing the IME
     *                 brings the system bars back).
     * @param autoOpen whether a field the plugin reports opens the bar. Off, the bar only opens by hand.
     */
    GameKeyboard(Context context, Runnable onClosed, BooleanSupplier autoOpen) {
        super(context);
        this.onClosed = onClosed;
        this.autoOpen = autoOpen;
        setOrientation(VERTICAL);
        setVisibility(GONE);
        setBackgroundColor(0xF2202127);
        setPadding(dp(8), dp(8), dp(8), dp(8));
        setClickable(true);   // taps on the bar must not fall through to the game

        LinearLayout row = new LinearLayout(context);
        row.setOrientation(HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        addView(row, new LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT));

        input = new EditText(context);
        input.setTextColor(Color.WHITE);
        input.setHintTextColor(0xFF9AA0A6);
        input.setHint(UiText.get("message"));
        input.setTextSize(TypedValue.COMPLEX_UNIT_SP, 16);
        input.setBackgroundColor(0xFF2B2D33);
        input.setPadding(dp(12), dp(10), dp(12), dp(10));
        applyInputType();
        input.setOnEditorActionListener((v, actionId, event) -> {
            if (actionId == EditorInfo.IME_ACTION_SEND
                    || (event != null && event.getKeyCode() == KeyEvent.KEYCODE_ENTER
                        && event.getAction() == KeyEvent.ACTION_DOWN)) {
                send();
                return true;
            }
            return false;
        });
        input.addTextChangedListener(new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int start, int count, int after) {}
            @Override public void onTextChanged(CharSequence s, int start, int before, int count) {}
            @Override public void afterTextChanged(Editable s) { applyFilter(); }
        });
        row.addView(input, new LayoutParams(0, LayoutParams.WRAP_CONTENT, 1f));

        row.addView(button(UiText.get("send"), v -> send()),
                new LayoutParams(LayoutParams.WRAP_CONTENT, LayoutParams.WRAP_CONTENT));
        row.addView(button(UiText.get("close"), v -> close()),
                new LayoutParams(LayoutParams.WRAP_CONTENT, LayoutParams.WRAP_CONTENT));

        notice = new TextView(context);
        notice.setTextSize(TypedValue.COMPLEX_UNIT_SP, 12);
        notice.setTextColor(0xFFFFB74D);
        notice.setPadding(dp(4), dp(6), dp(4), 0);
        notice.setVisibility(GONE);
        addView(notice, new LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT));

        startListening();
    }

    boolean isOpen() {
        return open;
    }

    /** Opens the bar by hand, for a field the plugin did not report (or with no plugin at all). */
    void openManually() {
        if (fieldActive) show("", fieldMax, fieldBytes, fieldAllow, fieldLines, true);
        else show("", 0, 0, 0, false, false);
    }

    /**
     * Keys while the bar is open. Back and Escape close it; everything else belongs to the Android field, so this
     * returns false and lets normal view dispatch deliver it.
     */
    boolean handleKey(KeyEvent event) {
        int code = event.getKeyCode();
        if (code == KeyEvent.KEYCODE_BACK || code == KeyEvent.KEYCODE_ESCAPE) {
            if (event.getAction() == KeyEvent.ACTION_UP) close();
            return true;
        }
        return false;
    }

    void close() {
        if (!open) return;
        open = false;
        setVisibility(GONE);
        notice.setVisibility(GONE);
        filtering = true;
        input.setText("");
        filtering = false;
        InputMethodManager imm = imm();
        if (imm != null) imm.hideSoftInputFromWindow(input.getWindowToken(), 0);
        input.clearFocus();
        if (onClosed != null) onClosed.run();
    }

    /** Stops the socket. Called when the X server shuts down. */
    void dispose() {
        running = false;
        if (listener != null) listener.close();
        if (sender != null) sender.close();
        if (receiver != null) receiver.interrupt();
    }

    // ---- the plugin's side -----------------------------------------------------------------------

    private void startListening() {
        logKeyboards();
        try {
            loopback = InetAddress.getByName("127.0.0.1");
            listener = new DatagramSocket(LISTEN_PORT, loopback);
            sender = new DatagramSocket();
        } catch (Exception e) {
            // No keyboard from the plugin, but the menu entry still opens the bar by hand.
            Log.w(TAG, "no socket for the helper plugin: " + e);
            return;
        }
        receiver = new Thread(this::receive, "XlaKeyboard");
        receiver.setDaemon(true);
        receiver.start();
    }

    private void receive() {
        byte[] buffer = new byte[4096];
        while (running) {
            try {
                DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
                listener.receive(packet);
                JSONObject json = new JSONObject(new String(packet.getData(), 0, packet.getLength(), StandardCharsets.UTF_8));
                if (json.has("ack")) {
                    int id = json.optInt("ack", 0);
                    boolean ok = json.optBoolean("ok", false);
                    handler.post(() -> onAck(id, ok));
                    continue;
                }
                int version = json.optInt("v", 1);
                String kind = json.optString("kind", "game");
                boolean active = json.optBoolean("input", false);
                String text = json.optString("text", "");
                int max = json.optInt("maxChar", 0);
                int bytes = json.optInt("maxByte", 0);
                int allowFlags = json.optInt("allow", 0);
                boolean lines = json.optBoolean("multiline", false);
                handler.post(() -> {
                    pluginVersion = version;
                    pluginSeen = true;
                    fieldActive = active;
                    if (active) {
                        fieldKind = kind;
                        fieldMax = max;
                        fieldBytes = bytes;
                        fieldAllow = allowFlags;
                        fieldLines = lines;
                    }
                    apply(active, text, max, bytes, allowFlags, lines);
                });
            } catch (IOException e) {
                return;   // closed by dispose()
            } catch (Exception e) {
                // A datagram we cannot read is not a reason to stop listening.
                Log.w(TAG, "unreadable field state: " + e);
            }
        }
    }

    /** Field state from the plugin, on the UI thread. */
    private void apply(boolean active, String text, int max, int bytes, int allowFlags, boolean lines) {
        if (waitingForField != null) {
            if (!active) return;
            handler.removeCallbacks(chatOpenTimeout);
            String line = GameText.forField(waitingForField, allowFlags, lines,
                    max > 0 ? max : DEFAULT_MAX_CHAR, bytes);
            waitingForField = null;
            if (!line.isEmpty()) sendToPlugin(line);
            return;
        }
        if (!active) {
            close();
            return;
        }
        if (!open && !autoOpen.getAsBoolean()) return;
        if (!open && hardwareKeyboard()) {
            Log.i(TAG, "text field active, but a keyboard is attached; not opening the bar");
            return;
        }
        show(text, max, bytes, allowFlags, lines, true);
    }

    private void show(String text, int max, int bytes, int allowFlags, boolean lines, boolean reported) {
        maxChar = max > 0 ? max : DEFAULT_MAX_CHAR;
        maxByte = bytes;
        allow = allowFlags;
        if (multiline != lines) {
            multiline = lines;
            applyInputType();
        }
        if (open) {
            // Opened by hand, then the plugin saw a field: it can write the line in.
            if (reported) fromPlugin = true;
            return;
        }

        open = true;
        fromPlugin = reported;
        filtering = true;
        String existing = GameText.forField(text, allow, multiline, maxChar, maxByte);
        input.setText(existing);
        input.setSelection(existing.length());
        filtering = false;
        notice.setVisibility(GONE);
        setVisibility(VISIBLE);
        input.requestFocus();
        // The IME will not come up for a view that is not laid out yet.
        handler.post(() -> {
            InputMethodManager m = imm();
            if (m != null) m.showSoftInput(input, InputMethodManager.SHOW_IMPLICIT);
        });
    }

    /**
     * Input type and IME options in one place. setSingleLine() and setInputType() each rewrite the other's flags,
     * so a single-line field set up piecemeal loses either the Send action or the suggestion bar.
     */
    private void applyInputType() {
        int type = InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS;
        if (multiline) type |= InputType.TYPE_TEXT_FLAG_MULTI_LINE;
        input.setInputType(type);
        input.setMaxLines(multiline ? 4 : 1);
        input.setHorizontallyScrolling(!multiline);
        // No fullscreen extract editor: in landscape an IME would otherwise cover the game with its own text field.
        // No suggestions either, because the game's vocabulary is not the dictionary's.
        input.setImeOptions(EditorInfo.IME_ACTION_SEND
                | EditorInfo.IME_FLAG_NO_EXTRACT_UI
                | EditorInfo.IME_FLAG_NO_FULLSCREEN);
    }

    /**
     * Whether a real keyboard is attached. Someone playing with a Bluetooth keyboard is already typing into the
     * game directly, and a bar appearing over it every time they open chat would be in the way. They can still
     * open it by hand from the menu.
     */
    private static boolean hardwareKeyboard() {
        for (int id : InputDevice.getDeviceIds()) {
            InputDevice device = InputDevice.getDevice(id);
            if (device != null && !device.isVirtual()
                    && device.getKeyboardType() == InputDevice.KEYBOARD_TYPE_ALPHABETIC)
                return true;
        }
        return false;
    }

    /** Which input devices claim to be keyboards, logged once so a wrong suppression can be spotted in the log. */
    private static void logKeyboards() {
        StringBuilder found = new StringBuilder();
        for (int id : InputDevice.getDeviceIds()) {
            InputDevice device = InputDevice.getDevice(id);
            if (device != null && device.getKeyboardType() == InputDevice.KEYBOARD_TYPE_ALPHABETIC)
                found.append(found.length() > 0 ? ", " : "")
                     .append(device.getName())
                     .append(device.isVirtual() ? " (virtual)" : "");
        }
        Log.i(TAG, "alphabetic keyboards: " + (found.length() > 0 ? found : "none"));
    }

    private void send() {
        String text = GameText.forField(input.getText().toString(), allow, multiline, maxChar, maxByte);
        if (text.isEmpty()) {
            warn(UiText.get("nothing_to_send"));
            return;
        }
        boolean viaPlugin = fromPlugin && sender != null;
        close();
        if (viaPlugin) {
            sendToPlugin(text);
        } else if (pluginSeen && pluginVersion >= 2 && sender != null) {
            // No field open: Enter opens the chat, and the plugin's report of it sends the line (apply).
            waitingForField = text;
            handler.postDelayed(this::pressEnter, TYPE_AFTER_CLOSE_MS);
            handler.postDelayed(chatOpenTimeout, TYPE_AFTER_CLOSE_MS + CHAT_OPEN_TIMEOUT_MS);
        } else {
            typeInstead(text, TYPE_AFTER_CLOSE_MS);
        }
    }

    /** Hands the line to the plugin for the field it reported, then presses Enter once it is in. */
    private void sendToPlugin(String text) {
        // A version 2 plugin says whether the text arrived; anything it could not place is typed instead.
        int id = pluginVersion >= 2 ? ++nextId : 0;
        String target = fieldKind;
        // Android forbids socket work on the UI thread (NetworkOnMainThreadException), so the datagram goes out on
        // its own thread.
        new Thread(() -> {
            boolean sent = deliver(text, target, id);
            handler.post(() -> {
                if (!sent) {
                    typeInstead(text, TYPE_AFTER_CLOSE_MS);
                    return;
                }
                if (id == 0) {
                    // Version 1: no answer is coming. Enter once the plugin has had a frame to write the line.
                    handler.postDelayed(this::pressEnter, SUBMIT_DELAY_MS);
                    return;
                }
                awaitingId = id;
                awaitingText = text;
                handler.postDelayed(ackTimeout, ACK_TIMEOUT_MS);
            });
        }, "XlaKeyboardSend").start();
    }

    /** Enter did not bring up a field: the line goes back into the bar rather than being lost. */
    private void chatDidNotOpen() {
        if (waitingForField == null) return;
        Log.i(TAG, "the chat did not open; back to the bar");
        String text = waitingForField;
        waitingForField = null;
        openManually();
        filtering = true;
        input.setText(text);
        input.setSelection(text.length());
        filtering = false;
    }

    /** The plugin's answer, on the UI thread. */
    private void onAck(int id, boolean ok) {
        if (id != awaitingId) return;   // late, after the timeout already typed it
        awaitingId = 0;
        handler.removeCallbacks(ackTimeout);
        if (ok) {
            handler.postDelayed(this::pressEnter, SUBMIT_AFTER_ACK_MS);
        } else {
            Log.i(TAG, "the plugin found no field for the text; typing it instead");
            typeInstead(awaitingText, TYPE_AFTER_CLOSE_MS);
        }
    }

    /** Types the line as key presses (TextTyper), then presses Enter. */
    private void typeInstead(String text, long delayMs) {
        handler.postDelayed(() -> XServerHost.typeText(text,
                () -> handler.postDelayed(this::pressEnter, SUBMIT_AFTER_TYPING_MS)), delayMs);
    }

    private void pressEnter() {
        XServerHost.injectKey("KEY_ENTER", true);
        handler.postDelayed(() -> XServerHost.injectKey("KEY_ENTER", false), KEY_HOLD_MS);
    }

    /** Never call from the UI thread. */
    private boolean deliver(String text, String target, int id) {
        try {
            JSONObject json = new JSONObject();
            json.put("v", 2);
            json.put("text", text);
            json.put("target", target);
            if (id > 0) json.put("id", id);
            byte[] datagram = json.toString().getBytes(StandardCharsets.UTF_8);
            sender.send(new DatagramPacket(datagram, datagram.length, loopback, PLUGIN_PORT));
            return true;
        } catch (Exception e) {
            Log.w(TAG, "could not send text to the plugin: " + e);
            return false;
        }
    }

    private void warn(String message) {
        notice.setText(message);
        notice.setVisibility(VISIBLE);
    }

    // ---- what the game will accept ---------------------------------------------------------------

    /**
     * Strips what the game cannot take as it is typed, so the bar always shows what the game will get rather than
     * dropping it silently on send.
     */
    private void applyFilter() {
        if (filtering) return;
        String typed = input.getText().toString();
        String allowed = GameText.forField(typed, allow, multiline, maxChar, maxByte);
        if (typed.equals(allowed)) {
            if (notice.getVisibility() == VISIBLE) notice.setVisibility(GONE);
            return;
        }
        filtering = true;
        int cursor = Math.min(input.getSelectionStart(), allowed.length());
        input.setText(allowed);
        input.setSelection(Math.max(0, cursor));
        filtering = false;
        notice.setText(allowed.length() >= maxChar
                ? UiText.get("field_full")
                : UiText.get("symbols_removed"));
        notice.setVisibility(VISIBLE);
    }

    // ---- plumbing --------------------------------------------------------------------------------

    private TextView button(String label, OnClickListener onClick) {
        TextView t = new TextView(getContext());
        t.setText(label);
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, 15);
        t.setTextColor(Color.WHITE);
        t.setTypeface(Typeface.DEFAULT_BOLD);
        t.setPadding(dp(16), dp(12), dp(16), dp(12));
        t.setClickable(true);
        t.setFocusable(true);
        StateListDrawable background = new StateListDrawable();
        background.addState(new int[]{android.R.attr.state_pressed}, new ColorDrawable(0x33FFFFFF));
        background.addState(new int[]{android.R.attr.state_focused}, new ColorDrawable(0x33FFFFFF));
        background.addState(new int[0], new ColorDrawable(Color.TRANSPARENT));
        t.setBackground(background);
        t.setOnClickListener(onClick);
        return t;
    }

    private InputMethodManager imm() {
        return (InputMethodManager) getContext().getSystemService(Context.INPUT_METHOD_SERVICE);
    }

    private int dp(int dp) {
        return PerfHud.dp(getContext(), dp);
    }
}
