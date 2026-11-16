package xla;

import android.util.Log;

import com.winlator.xserver.XKeycode;
import com.winlator.xserver.XServer;

/**
 * Types text into whatever has keyboard focus in the game, as key presses. Needs nothing inside the game, so it
 * reaches every field the game or Dalamud draws, with or without the helper plugin.
 *
 * Each character goes out on a spare keycode that is remapped to that character first: the X server sends
 * MappingNotify, Wine rereads the keymap, and the press arrives as an ordinary typed character. Latin-1 keysyms are
 * the code point itself; everything else is 0x01000000 + code point, the X convention for Unicode.
 *
 * Spare keycodes only, never a real key: a remapped key stays remapped, so typing through the A key would leave a
 * Bluetooth keyboard's A producing the last character typed here. Wine may translate a key press into a character
 * a frame or more after it arrives, using the keymap as it is then, so a spare is not remapped again until it has
 * been idle the longest of all of them (least recently used). Characters already on a spare reuse it as they are.
 */
final class TextTyper {

    private static final String TAG = "XlaTyper";

    /**
     * Keycodes no XKeycode uses, all at or below 127. The vendored server's GetKeyboardMapping reply only covers
     * keycodes 8 to 131, and it indexes its keymap with a signed byte, so 128 and up read as negative and throw.
     */
    private static final byte[] SPARES = {
        92, 93, 94, 97, 98, 99, 100, 101, 102, 103, 109,
        120, 121, 122, 123, 124, 125, 126, 127,
    };

    /** Held long enough for the press to register, short enough that a full chat line types in about a second. */
    private static final long PRESS_MS = 6;
    private static final long GAP_MS = 6;

    /** Enter is read by the game once per frame, like the other keys XServerHost presses, so it is held longer. */
    private static final long ENTER_HOLD_MS = 100;

    /**
     * After a character outside Latin-1. Wine has no key of its own for those, and the game turns a press into a
     * character only when it next reads its messages, through the keymap as it is by then. Typed at the Latin-1 pace,
     * the next character's remap got there first: "こんにちは" arrived as "んんにちは" and "漢字" as "字". So the game
     * gets a few frames to read each one before the keymap changes again.
     */
    private static final long UNICODE_GAP_MS = 80;

    private static final int[] keysymOnSpare = new int[SPARES.length];
    private static final long[] lastUsed = new long[SPARES.length];
    private static long clock;

    private TextTyper() {}

    /**
     * Types the text on a worker thread, then runs after (on that thread). A '\n' is pressed as Enter.
     */
    static void type(XServer xServer, String text, Runnable after) {
        new Thread(() -> {
            try {
                typeNow(xServer, text);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                return;
            } catch (Exception e) {
                Log.w(TAG, "typing stopped: " + e);
            }
            if (after != null) after.run();
        }, "XlaTyper").start();
    }

    private static void typeNow(XServer xServer, String text) throws InterruptedException {
        int count = 0;
        for (int i = 0; i < text.length(); ) {
            int cp = text.codePointAt(i);
            i += Character.charCount(cp);
            if (cp == '\n') {
                xServer.injectKeyPress(XKeycode.KEY_ENTER);
                Thread.sleep(ENTER_HOLD_MS);
                xServer.injectKeyRelease(XKeycode.KEY_ENTER);
            } else {
                byte keycode;
                int keysym = keysymFor(cp);
                synchronized (TextTyper.class) {
                    keycode = spareFor(keysym);
                }
                xServer.injectKeyPress(keycode, keysym);
                Thread.sleep(PRESS_MS);
                xServer.injectKeyRelease(keycode);
                if ((keysym & 0x01000000) != 0) Thread.sleep(UNICODE_GAP_MS);
            }
            Thread.sleep(GAP_MS);
            count++;
        }
        Log.i(TAG, "typed " + count + " characters");
    }

    static int keysymFor(int cp) {
        boolean latin1 = (cp >= 0x20 && cp <= 0x7E) || (cp >= 0xA0 && cp <= 0xFF);
        return latin1 ? cp : 0x01000000 | cp;
    }

    /** The spare already carrying this keysym, else the one idle longest, remapped to it on the press. */
    private static byte spareFor(int keysym) {
        int pick = -1;
        for (int i = 0; i < SPARES.length; i++) {
            if (keysymOnSpare[i] == keysym) { pick = i; break; }
        }
        if (pick < 0) {
            pick = 0;
            for (int i = 1; i < SPARES.length; i++) {
                if (lastUsed[i] < lastUsed[pick]) pick = i;
            }
            keysymOnSpare[pick] = keysym;
        }
        lastUsed[pick] = ++clock;
        return SPARES[pick];
    }
}
