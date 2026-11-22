#!/usr/bin/env bash
# Build DXVK 2.4.1-gplasync as ARM64EC: the same DXVK the overlay ships, compiled to native ARM code that the
# x86-64 game can still call. Only the game's own code then runs under FEX.
#
# Output: work/dxvk-arm64ec/out/x64/{dxgi,d3d11,d3d10core}.dll and work/dxvk-arm64ec/dxvk-2.4.1-gplasync-arm64ec.tar.gz,
# which the app's DXVK importer accepts.
#
# d3d9 and d3d8 are left out: DXVK 2.4.1's d3d9 headers clash with current mingw headers, and the game only
# uses dxgi and d3d11. Those two stay the overlay's x86-64 builds.
#
# Needs meson, ninja, glslang and the llvm-mingw toolchain build-wine.sh uses:
#   ~/toolchains/llvm-mingw-20250920-ucrt-ubuntu-22.04-x86_64          github.com/bylaws/llvm-mingw 20250920
#
#   scripts/build-dxvk-arm64ec.sh
set -euo pipefail

P="$(cd "$(dirname "$0")/.." && pwd)"
WORK="${XLA_DXVK_WORK:-$P/work/dxvk-arm64ec}"
TOOLCHAIN="${XLA_LLVM_MINGW:-$HOME/toolchains/llvm-mingw-20250920-ucrt-ubuntu-22.04-x86_64}/bin"
DXVK_TAG=v2.4.1
GPLASYNC_COMMIT=04e50ed2c4dee43e64d30119b2c5b26a23e2b6a4   # gitlab.com/Ph42oN/dxvk-gplasync, carries every version's patch
GPLASYNC_PATCH=dxvk-gplasync-2.4-1.patch   # the patch the overlay's 2.4.1-gplasync build was made with
NAME=dxvk-2.4.1-gplasync-arm64ec
DLLS=(dxgi/dxgi d3d11/d3d11 d3d10/d3d10core)

[[ -x "$TOOLCHAIN/arm64ec-w64-mingw32-clang++" ]] || { echo "Missing llvm-mingw with arm64ec in $TOOLCHAIN"; exit 1; }

echo "==> sources"
mkdir -p "$WORK"
rm -rf "$WORK/dxvk"
git -c advice.detachedHead=false clone -q --depth 1 --branch "$DXVK_TAG" --recurse-submodules \
    --shallow-submodules https://github.com/doitsujin/dxvk.git "$WORK/dxvk"
[[ -d "$WORK/gplasync/.git" ]] || git init -q "$WORK/gplasync"
git -C "$WORK/gplasync" fetch -q --depth 1 https://gitlab.com/Ph42oN/dxvk-gplasync.git "$GPLASYNC_COMMIT"
git -C "$WORK/gplasync" checkout -q --force FETCH_HEAD
git -C "$WORK/dxvk" apply --whitespace=nowarn "$WORK/gplasync/patches/$GPLASYNC_PATCH"

echo "==> configure"
# meson has no arm64ec cpu family. aarch64 keeps DXVK's x86-only flags and paths off, and DXVK's own
# __arm64ec__ checks pick the ARM code paths. No link timestamp, so a rebuild gives the same bytes.
cat > "$WORK/dxvk/build-arm64ec.txt" <<EOF
[binaries]
c = '$TOOLCHAIN/arm64ec-w64-mingw32-clang'
cpp = '$TOOLCHAIN/arm64ec-w64-mingw32-clang++'
ar = '$TOOLCHAIN/llvm-ar'
strip = '$TOOLCHAIN/llvm-strip'
windres = '$TOOLCHAIN/arm64ec-w64-mingw32-windres'
widl = '$TOOLCHAIN/arm64ec-w64-mingw32-widl'

[built-in options]
c_link_args = ['-Wl,--no-insert-timestamp']
cpp_link_args = ['-Wl,--no-insert-timestamp']

[properties]
needs_exe_wrapper = true

[host_machine]
system = 'windows'
cpu_family = 'aarch64'
cpu = 'arm64ec'
endian = 'little'
EOF
meson setup --cross-file "$WORK/dxvk/build-arm64ec.txt" --buildtype release --strip -Dbuild_id=false \
    -Denable_d3d9=false -Denable_d3d8=false "$WORK/dxvk/build.arm64ec" "$WORK/dxvk" >/dev/null

echo "==> build"
ninja -C "$WORK/dxvk/build.arm64ec" >/dev/null

echo "==> check"
# file(1) reports ARM64EC images as x86-64: they keep the AMD64 machine field so x64 code can load them. The
# load config's code map is what says the code inside is ARM.
for dll in "${DLLS[@]}"; do
    f="$WORK/dxvk/build.arm64ec/src/$dll.dll"
    "$TOOLCHAIN/llvm-readobj" --coff-load-config "$f" | grep -q 'ARM64EC' \
        || { echo "$f has no ARM64EC code map"; exit 1; }
done

echo "==> package"
rm -rf "$WORK/out" "$WORK/pkg"
mkdir -p "$WORK/out/x64" "$WORK/pkg/$NAME/x64"
for dll in "${DLLS[@]}"; do
    cp "$WORK/dxvk/build.arm64ec/src/$dll.dll" "$WORK/out/x64/"
    cp "$WORK/dxvk/build.arm64ec/src/$dll.dll" "$WORK/pkg/$NAME/x64/"
done
tar -C "$WORK/pkg" -czf "$WORK/$NAME.tar.gz" "$NAME"
ls -la "$WORK/out/x64"
echo "==> done: $WORK/out"
