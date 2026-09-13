package com.winlator.xenvironment;

/**
 * Winlator's component base, minus its back-reference to XEnvironment. This app starts and stops
 * the two components it needs (X server, SysV shared memory) directly from xla.XServerHost, so
 * the whole XEnvironment orchestration layer is not vendored.
 */
public abstract class EnvironmentComponent {
    public abstract void start();

    public abstract void stop();
}
