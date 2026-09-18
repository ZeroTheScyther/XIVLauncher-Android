#!/usr/bin/env bash
# Build the lsfg-vk Vulkan layer (GameNative's Android/Wine branch of lsfg-vk 1.0.0, MIT) with the NDK.
#   scripts/build-lsfg.sh            build into build/lsfg-vk-android
#   scripts/build-lsfg.sh --install  also replace src/native/arm64-v8a/liblsfg-vk-layer.so
# Keep the pin in sync with THIRD_PARTY.md. lsfg-vk 2.x is CC BY-NC-ND, so stay on this 1.0.x branch.
set -euo pipefail

P="$(cd "$(dirname "$0")/.." && pwd)"
NDK="${ANDROID_NDK:-${ANDROID_SDK_ROOT:-$HOME/Android/Sdk}/ndk/27.3.13750724}"
REPO="https://github.com/GameNative/lsfg-vk-android.git"
TAG="v1.0.4-android"
COMMIT="ea1e3f9cf1196cfda7d75fadfdd51616b388e021"
SRC="$P/build/lsfg-vk-android"
PACKAGED="$P/src/native/arm64-v8a/liblsfg-vk-layer.so"

if [[ ! -d "$SRC" ]]; then
    git clone --quiet --depth 1 --branch "$TAG" --recurse-submodules --shallow-submodules "$REPO" "$SRC"
fi
if [[ "$(git -C "$SRC" rev-parse HEAD)" != "$COMMIT" ]]; then
    echo "build/lsfg-vk-android is not at $TAG ($COMMIT); delete it and rerun" >&2
    exit 1
fi

# The flags of the branch's own scripts/build/android.sh, plus --as-needed. That script links
# libandroid.so without using a symbol from it, and libandroid pulls in the system libsqlite, which then
# resolves libcrypto.so from LD_LIBRARY_PATH. Inside Wine that is the rootfs OpenSSL, so dlopen fails
# on a missing OpenSSL_add_all_algorithms and the loader silently drops the layer.
BUILD="$SRC/build-xla"
cmake -S "$SRC" -B "$BUILD" -G Ninja \
    -DCMAKE_TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake" \
    -DANDROID_ABI=arm64-v8a \
    -DANDROID_PLATFORM=android-28 \
    -DCMAKE_BUILD_TYPE=Release \
    -DLSFGVK_ANDROID_WINE=ON \
    -DVOLK_STATIC_DEFINES=VK_USE_PLATFORM_ANDROID_KHR \
    -DCMAKE_CXX_FLAGS="-DVK_USE_PLATFORM_ANDROID_KHR" \
    -DCMAKE_C_FLAGS="-DVK_USE_PLATFORM_ANDROID_KHR" \
    -DCMAKE_SHARED_LINKER_FLAGS="-Wl,-z,max-page-size=16384 -Wl,--as-needed" >/dev/null
cmake --build "$BUILD" --parallel >/dev/null
OUT="$BUILD/liblsfg-vk.stripped.so"
"$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin/llvm-strip" --strip-unneeded -o "$OUT" "$BUILD/liblsfg-vk.so"

needed() { readelf -d "$1" | sed -nE 's/.*NEEDED.*\[(.*)\]/\1/p' | sort | tr '\n' ' '; }
echo "built: $(stat -c %s "$OUT") bytes, needs: $(needed "$OUT")"
if needed "$OUT" | grep -q libandroid; then
    echo "the layer still links libandroid.so; it will not load inside Wine" >&2
    exit 1
fi

if [[ "${1:-}" == "--install" ]]; then
    cp "$OUT" "$PACKAGED"
    echo "installed -> $PACKAGED"
fi
