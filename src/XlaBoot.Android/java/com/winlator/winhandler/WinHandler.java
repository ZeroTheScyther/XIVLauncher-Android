package com.winlator.winhandler;

import android.os.Handler;
import android.os.HandlerThread;
import android.util.Log;

import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/**
 * UDP bridge to winhandler.exe, the helper that runs inside the Wine prefix.
 *
 * Only the mouse half of Winlator's WinHandler is implemented. It exists because of how Proton 10's winex11 treats
 * input: ordinary X pointer events are always sent to wineserver with SEND_HWMSG_NO_RAW, so they never become raw
 * input, and the Wine build we ship has XInput2 compiled out, so nothing else produces raw mouse input either.
 * DirectInput is fed by raw input - and FFXIV reads its camera through DirectInput - so X11 mouse motion alone can
 * never turn the camera. winhandler.exe replays motion with mouse_event(), which is injected Windows input:
 * that DOES produce raw input. GameNative does the same for its "Win32 relative mouse input" mode.
 *
 * Protocol (winhandler.c): the helper binds 127.0.0.1:7946 and sends INIT to 7947, which is us. A mouse packet is
 * [7][int size][int flags][short dx][short dy][short wheel][byte cursor-feedback], little-endian; with feedback set
 * the helper answers [13][short x][short y], the Windows cursor position after the move.
 *
 * Gamepads do not go through here: evshim's shared memory handles them (see xla.GamepadBridge).
 */
public class WinHandler {
    private static final String TAG = "XlaWinHandler";
    private static final int HELPER_PORT = 7946;
    private static final int OUR_PORT = 7947;
    private static final byte RC_INIT = 1;
    private static final byte RC_MOUSE_EVENT = 7;
    private static final byte RC_CURSOR_POS_FEEDBACK = 13;

    /** Receives the Windows cursor position reported back after a move. Called on the receive thread. */
    public interface CursorFeedbackListener {
        void onCursorPosition(short x, short y);
    }

    private final Object sendLock = new Object();
    /**
     * Mouse events arrive on the UI thread, and Android forbids socket I/O there (NetworkOnMainThreadException - a
     * RuntimeException, so it took the app down on the first captured move). One sender thread keeps them in order.
     */
    private HandlerThread senderThread;
    private volatile Handler sender;
    private final ByteBuffer sendData = ByteBuffer.allocate(16).order(ByteOrder.LITTLE_ENDIAN);
    private final DatagramPacket sendPacket = new DatagramPacket(sendData.array(), 16);

    private volatile DatagramSocket socket;
    private volatile boolean ready;
    private volatile boolean running;
    private volatile CursorFeedbackListener cursorFeedbackListener;
    private InetAddress localhost;

    public void setCursorFeedbackListener(CursorFeedbackListener listener) {
        cursorFeedbackListener = listener;
    }

    /** True once the helper inside Wine has said hello; until then there is nobody to send to. */
    public boolean isReady() {
        return ready;
    }

    /** Opens the socket and waits for the helper. Safe to call before Wine starts. */
    public void start() {
        if (running) return;
        running = true;
        senderThread = new HandlerThread("WinHandlerSend");
        senderThread.start();
        sender = new Handler(senderThread.getLooper());
        Thread receiver = new Thread(this::receiveLoop, "WinHandler");
        receiver.setDaemon(true);
        receiver.start();
    }

    private void receiveLoop() {
        try {
            localhost = InetAddress.getByName("127.0.0.1");
            DatagramSocket s = new DatagramSocket(null);
            s.setReuseAddress(true);
            s.bind(new InetSocketAddress(localhost, OUR_PORT));
            socket = s;

            byte[] buffer = new byte[64];
            DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
            ByteBuffer data = ByteBuffer.wrap(buffer).order(ByteOrder.LITTLE_ENDIAN);
            while (running) {
                packet.setLength(buffer.length);
                s.receive(packet);
                if (packet.getLength() < 1) continue;
                switch (buffer[0]) {
                    case RC_INIT:
                        if (!ready) Log.i(TAG, "winhandler.exe connected");
                        ready = true;
                        break;
                    case RC_CURSOR_POS_FEEDBACK: {
                        CursorFeedbackListener l = cursorFeedbackListener;
                        if (l != null && packet.getLength() >= 5)
                            l.onCursorPosition(data.getShort(1), data.getShort(3));
                        break;
                    }
                    default:
                        break;
                }
            }
        } catch (IOException e) {
            if (running) Log.w(TAG, "winhandler bridge stopped: " + e.getMessage());
        }
    }

    public void mouseEvent(int flags, int dx, int dy, int wheelDelta) {
        Handler h = sender;
        if (!ready || h == null) return;
        h.post(() -> send(flags, dx, dy, wheelDelta));
    }

    private void send(int flags, int dx, int dy, int wheelDelta) {
        DatagramSocket s = socket;
        if (s == null) return;
        if (flags != MouseEventFlags.MOVE)
            xla.MouseTrace.log("winhandler send flags=0x" + Integer.toHexString(flags) + " wheel=" + wheelDelta);
        synchronized (sendLock) {
            sendData.clear();
            sendData.put(RC_MOUSE_EVENT);
            sendData.putInt(10);
            sendData.putInt(flags);
            sendData.putShort((short) dx);
            sendData.putShort((short) dy);
            sendData.putShort((short) wheelDelta);
            sendData.put((byte) ((flags & MouseEventFlags.MOVE) != 0 ? 1 : 0));
            try {
                sendPacket.setAddress(localhost);
                sendPacket.setPort(HELPER_PORT);
                sendPacket.setLength(sendData.position());
                s.send(sendPacket);
            } catch (IOException e) {
                // A dropped mouse packet is a lost frame of motion, not worth tearing anything down for.
            }
        }
    }

    /** Raising windows by class name is not wired up; the desktop helper calls this on every map. */
    public void bringToFront(String className, long handle) {}

    public void stop() {
        running = false;
        ready = false;
        DatagramSocket s = socket;
        socket = null;
        if (s != null) s.close();
        HandlerThread t = senderThread;
        sender = null;
        if (t != null) t.quitSafely();
    }

    /**
     * Implemented in libevshim.so (JNI name is tied to this class): bumps player N's shared-memory
     * sequence word and futex-wakes the Wine side. xla.GamepadBridge loads the library first.
     */
    public static native void notifyStateChanged(int playerIndex);
}
