package com.winlator.xenvironment.components;

import com.winlator.renderer.Texture;

/**
 * Stub for Winlator's VortekRendererComponent. Only the static texture teardown helper is
 * reachable from the vendored X server; the Vortek renderer itself is not used here.
 */
public abstract class VortekRendererComponent {
    public static void destroyTexture(Texture texture) {
        if (texture != null) texture.destroy();
    }
}
