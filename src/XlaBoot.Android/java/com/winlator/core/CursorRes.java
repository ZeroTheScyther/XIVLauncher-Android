package com.winlator.core;

import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.util.Base64;

/**
 * The X server's default pointer bitmap. Winlator loads this from R.drawable.cursor; this app
 * has no Java-side R class, so the same PNG is embedded here instead.
 */
public final class CursorRes {
    private CursorRes() {}

    private static final String CURSOR_PNG =
            "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAdElEQVQ4T53TYQrAIAgGUL2gh9UDbioVi1apglA/vleBIQA82qQt"
            + "2unCBliwhDigBYi2zCMDsHQFmYAKsgBZ5BfIIFsgihyBCHIFbkgIOCELICJAZEO5LZ+4XhPwCYfHegCVsD/N/gIz92uHT56e0Dbp"
            + "sOVe9hhPEcxOLeQAAAAASUVORK5CYII=";

    public static Bitmap decode(BitmapFactory.Options options) {
        byte[] data = Base64.decode(CURSOR_PNG, Base64.DEFAULT);
        return BitmapFactory.decodeByteArray(data, 0, data.length, options);
    }
}
