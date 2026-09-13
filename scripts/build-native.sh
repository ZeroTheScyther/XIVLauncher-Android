#!/usr/bin/env bash
# Build the vendored native renderer (src/native/vulkan_renderer) with the NDK.
#   scripts/build-native.sh            build into build/vulkan_renderer and compare with the packaged .so
#   scripts/build-native.sh --install  also replace src/native/arm64-v8a/libvulkan_renderer.so
set -euo pipefail

P="$(cd "$(dirname "$0")/.." && pwd)"
NDK="${ANDROID_NDK:-${ANDROID_SDK_ROOT:-$HOME/Android/Sdk}/ndk/27.0.12077973}"
SRC="$P/src/native/vulkan_renderer"
BUILD="$P/build/vulkan_renderer"
PACKAGED="$P/src/native/arm64-v8a/libvulkan_renderer.so"
STRIP="$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin/llvm-strip"

cmake -S "$SRC" -B "$BUILD" -G Ninja \
    -DCMAKE_TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake" \
    -DANDROID_ABI=arm64-v8a \
    -DANDROID_PLATFORM=android-26 \
    -DANDROID_STL=c++_static \
    -DCMAKE_BUILD_TYPE=Release >/dev/null
cmake --build "$BUILD" --target vulkan_renderer

OUT="$BUILD/libvulkan_renderer.stripped.so"
"$STRIP" --strip-unneeded -o "$OUT" "$BUILD/libvulkan_renderer.so"

exports() { nm -D --defined-only "$1" | awk '{print $3}' | grep '^Java_' | sort; }
needed()  { readelf -d "$1" | sed -nE 's/.*NEEDED.*\[(.*)\]/\1/p' | sort | tr '\n' ' '; }
echo "built:    $(exports "$OUT" | wc -l) JNI exports, needs: $(needed "$OUT")"
echo "packaged: $(exports "$PACKAGED" | wc -l) JNI exports, needs: $(needed "$PACKAGED")"
if ! diff <(exports "$PACKAGED") <(exports "$OUT"); then
    echo "JNI exports differ from the packaged renderer (see above)"
fi

if [[ "${1:-}" == "--install" ]]; then
    cp "$OUT" "$PACKAGED"
    echo "installed -> $PACKAGED"
fi
