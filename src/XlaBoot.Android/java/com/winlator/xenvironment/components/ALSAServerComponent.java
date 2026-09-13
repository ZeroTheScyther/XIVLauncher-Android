package com.winlator.xenvironment.components;

import android.content.Context;

import com.winlator.alsaserver.ALSAClient;
import com.winlator.alsaserver.ALSAClientConnectionHandler;
import com.winlator.alsaserver.ALSARequestHandler;
import com.winlator.xconnector.Client;
import com.winlator.xconnector.UnixSocketConfig;
import com.winlator.xconnector.XConnectorEpoll;
import com.winlator.xenvironment.EnvironmentComponent;

/**
 * Winlator's ALSA server: the guest's libasound "android_aserver" PCM plugin connects to this socket
 * and every stream is played through an Android AudioTrack.
 *
 * Trimmed from GameNative: it takes the Context directly (there is no XEnvironment here) and the
 * rootfs is always the bionic variant.
 */
public class ALSAServerComponent extends EnvironmentComponent {
    private XConnectorEpoll connector;
    private final Context context;
    private final ALSAClient.Options options;
    private final UnixSocketConfig socketConfig;

    public ALSAServerComponent(Context context, UnixSocketConfig socketConfig, ALSAClient.Options options) {
        this.context = context;
        this.socketConfig = socketConfig;
        this.options = options;
    }

    @Override
    public void start() {
        if (connector != null) return;
        ALSAClient.assignFramesPerBuffer(context);
        connector = new XConnectorEpoll(socketConfig,
                new ALSAClientConnectionHandler(options, "bionic"), new ALSARequestHandler());
        connector.setMultithreadedClients(true);
        connector.start();
    }

    @Override
    public void stop() {
        XConnectorEpoll c = connector;
        if (c == null) return;
        for (int i = 0; i < c.getConnectedClientsCount(); i++) {
            Client client = c.getConnectedClientAt(i);
            if (client != null && client.getTag() instanceof ALSAClient) {
                ((ALSAClient) client.getTag()).stop();
            }
        }
        c.stop();
        connector = null;
    }

    /** Pauses every open stream, e.g. while the game menu is up or the app is in the background. */
    public void pause() {
        forEachClient(false);
    }

    public void resume() {
        forEachClient(true);
    }

    private void forEachClient(boolean play) {
        XConnectorEpoll c = connector;
        if (c == null) return;
        for (int i = 0; i < c.getConnectedClientsCount(); i++) {
            Client client = c.getConnectedClientAt(i);
            if (client != null && client.getTag() instanceof ALSAClient) {
                if (play) ((ALSAClient) client.getTag()).start();
                else ((ALSAClient) client.getTag()).pause();
            }
        }
    }
}
