#!/usr/bin/env bash
# Build the Wine the runtime ships: GameNative's Proton 11.0-2 (arm64ec, bionic) plus runtime/wine-patches.
#
# Output: work/wine-wcp/proton-11.0-2-xla-arm64ec.wcp, the default input of scripts/bundle-runtime.sh.
# The prefixPack and profile.json inside it come from GameNative's own release .wcp, which is also where
# bundle-runtime.sh reads the prefix from.
#
# Follows GameNative's CI (.github/workflows/build-proton.yml in their repo) with the host differences below.
# Their scripts hardcode these toolchain paths, so install them there first:
#   ~/Android/Sdk/ndk/27.3.13750724                                    Android NDK r27d
#   ~/toolchains/llvm-mingw-20250920-ucrt-ubuntu-22.04-x86_64          github.com/bylaws/llvm-mingw 20250920
#   ~/termuxfs/aarch64/data/data/com.termux/files/usr                  GameNative/termux-on-gha build-20260218
# Also needs flex, bison and m4 on PATH (BISON_PKGDATADIR set if bison is not system-wide) and a Rust toolchain
# with the aarch64-linux-android target for ntsync-android.
#
#   scripts/build-wine.sh
set -euo pipefail

P="$(cd "$(dirname "$0")/.." && pwd)"
WORK="${XLA_WINE_WORK:-$P/work/wine-build}"
GN_WCP="${XLA_GN_WCP:-$P/work/wine-wcp/proton-11.0-2-arm64ec.wcp}"
OUT="${XLA_WINE_OUT:-$P/work/wine-wcp/proton-11.0-2-xla-arm64ec.wcp}"
WINE_COMMIT=0971187883d7488b1686770015f6e6fd6bf342da     # GameNative/proton-wine, release proton-11.0-2
NTSYNC_COMMIT=7ce6435e5979b1cb5341aa4b299f31e8937fe121   # GameNative/ntsync-android, as in that release
INSTALLED="$HOME/compiled-files-aarch64"                  # where build-step-arm64ec.sh --install puts the tree

[[ -f "$GN_WCP" ]] || { echo "Missing $GN_WCP (GameNative's proton-11.0-2-arm64ec.wcp release asset)"; exit 1; }

fetch() {   # fetch <dir> <repo> <commit>
    [[ -d "$1/.git" ]] || git init -q "$1"
    git -C "$1" fetch -q --depth 1 "$2" "$3"
    git -C "$1" checkout -q --force FETCH_HEAD
    git -C "$1" clean -qfdx
}

echo "==> sources"
mkdir -p "$WORK"
fetch "$WORK/proton-wine" https://github.com/GameNative/proton-wine.git "$WINE_COMMIT"
git -C "$WORK/proton-wine" submodule update -q --init --recursive --depth 1
fetch "$WORK/ntsync-android" https://github.com/GameNative/ntsync-android.git "$NTSYNC_COMMIT"

cd "$WORK/proton-wine"
echo "==> autogen"
bash autogen.sh

# Their step 0 configures a 32-bit tools build that needs i386 freetype. The tools only run on the build host,
# so a 64-bit build is equivalent.
echo "==> wine-tools"
mkdir -p wine-tools
(cd wine-tools && ../configure --enable-win64 --without-x --without-gstreamer --without-vulkan --without-wayland \
    > configure.log 2>&1 && make -j"$(nproc)" __tooldeps__ nls/all > make.log 2>&1)

echo "==> sysvshm, ntsync-android"
bash build-scripts/build-step-arm64ec.sh --build-sysvshm
NTSYNC_ANDROID_DIR="$WORK/ntsync-android" bash build-scripts/build-step-arm64ec.sh --build-ntsync-android

# --configure applies GameNative's patch set. Ours go on top of it.
echo "==> configure"
bash build-scripts/build-step-arm64ec.sh --configure
for patch in "$P"/runtime/wine-patches/*.patch; do
    echo "    applying $(basename "$patch")"
    git apply "$patch"
done
# Their patches leave the generated protocol and spec files stale.
tools/make_requests
tools/make_specfiles

echo "==> build"
bash build-scripts/build-step-arm64ec.sh --build
# The build step exits 0 even when make fails, so check what it produced.
for f in dlls/ntdll/ntdll.so server/wineserver dlls/ntdll/aarch64-windows/ntdll.dll; do
    [[ -f "$f" ]] || { echo "Build failed: $f is missing"; exit 1; }
done

echo "==> install"
bash build-scripts/build-step-arm64ec.sh --install

echo "==> package"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
tar -I zstd -xf "$GN_WCP" -C "$TMP" prefixPack.txz profile.json
cp "$TMP/prefixPack.txz" "$TMP/profile.json" "$INSTALLED/"
mkdir -p "$(dirname "$OUT")"
(cd "$INSTALLED" && tar -I 'zstd -T0 -19' -cf "$OUT" bin lib share prefixPack.txz profile.json)
echo "==> $OUT"
