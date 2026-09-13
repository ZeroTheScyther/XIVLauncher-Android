#!/system/bin/sh
# Runs inside the app process, after xla.XServerHost is already listening.
# Env contract mirrors GameNative's BionicProgramLauncherComponent so the bionic rootfs,
# the vendored X server and FEXCore all line up.
HOME=${HOME:-/data/user/0/uk.aetherworks.xivlauncher/files}
LOG="$HOME/wine-test.log"
: > "$LOG"

WINE="$HOME/wine"
ROOT="$HOME/rootfs"
NATIVE_LIB_DIR="${NATIVE_LIB_DIR:-}"

# Launcher settings screen + in-game menu (xla.XlaSettings writes this file, XLA_* only).
# Sourced here, before anything derived from it, so every tuning export below can take its value
# with ${XLA_...:-default}: the defaults in this script stay the tuned profile for this phone, and
# the settings screen is what moves them for a different device.
[ -f "$HOME/xla-settings.sh" ] && . "$HOME/xla-settings.sh"

export HOME
export USER=xuser
# libX11 here is the Termux-patched build: it looks for the X socket under $TMPDIR,
# not /tmp, which is what lets DISPLAY=:0 reach the in-app X server.
export TMPDIR="$ROOT/tmp"
export DISPLAY=:0
export PATH="$WINE/bin:$ROOT/usr/bin:/system/bin"
export LD_LIBRARY_PATH="$ROOT/usr/lib:/system/lib64:$WINE/lib"
export ANDROID_SYSVSHM_SERVER="$ROOT/tmp/.sysvshm/SM0"
export LD_PRELOAD="$ROOT/usr/lib/libandroid-sysvshm.so"
# Controller: GameNative's evshim turns files/gamepad_shm/gamepad.mem (written by the app's
# xla.GamepadBridge) into an SDL virtual Xbox 360 pad, which winebus exposes to the game as XInput.
# The preload is applied ONLY to the wine invocation below: evshim prints to stdout from its
# constructor, so preloading it for this whole script poisons every $(...) substitution
# (it broke ADRENOTOOLS_DRIVER_NAME and the log header).
EVSHIM_PRELOAD=
if [ -n "$NATIVE_LIB_DIR" ] && [ -f "$NATIVE_LIB_DIR/libevshim.so" ]; then
    EVSHIM_PRELOAD="$NATIVE_LIB_DIR/libevshim.so"
    export EVSHIM_WINE=1
    export EVSHIM_BASE_PATH="$HOME"
    export EVSHIM_MAX_PLAYERS=1
    export EVSHIM_DEBUG=0
fi
export PREFIX="$ROOT/usr"
export XDG_DATA_DIRS="$ROOT/usr/share"
export XDG_CONFIG_DIRS="$ROOT/usr/etc/xdg"
export REDIRECT_EXEC__PROC_SELF_EXE="$WINE/bin/wine"

# Audio: winepulse -> the app's PulseAudio daemon (xla.PulseAudioServer, AAudio sink) on PULSE_SERVER,
# GameNative's default route. PULSE_LATENCY_MSEC matches GameNative's FFXIV container.
# Fallback if PulseAudio is unreachable: winealsa -> bionic libasound -> android_aserver plugin -> the
# app's ALSA server -> AudioTrack. libasound has Winlator's config dir compiled in, so the config and
# plugin locations are given explicitly. (On this Wine build winealsa's open fails with -95.)
export PULSE_SERVER="$ROOT/tmp/.sound/PS0"
export PULSE_LATENCY_MSEC=144
export ANDROID_ALSA_SERVER="$ROOT/tmp/.sound/AS0"
export ALSA_CONFIG_PATH="$ROOT/usr/share/alsa/alsa.conf:$ROOT/usr/etc/alsa/conf.d/android_aserver.conf"
export ALSA_PLUGIN_DIR="$ROOT/usr/lib/alsa-lib"

export WINEPREFIX="$HOME/prefix/.wine"
export WINEDLLPATH="$WINE/lib/wine"
export HODLL=libwow64fex.dll
export WINE_NO_DUPLICATE_EXPLORER=1
# GameNative's nsiproxy: use its Android interface enumeration (getifaddrs). Without it the stock path wedges
# if_list_lock on the second enumeration, and every later nsiproxy request - including the game's data centre
# ping - hangs behind it. Dalamud's HTTP proxy auto-detect triggers that second call: "Connecting to the data
# center" forever. GameNative's launcher sets this too.
export WINE_NEW_NDIS=1
export WINE_DISABLE_FULLSCREEN_HACK=1
# Settings: Wine log = Off / Errors / Full. -all is silent and the normal case; the other two are
# for a device that will not start the game at all.
export WINEDEBUG="${XLA_WINE_LOG:--all}"
# Developer override, no UI: files/xla-winedebug holds a WINEDEBUG channel list for one investigation
# (e.g. +rawinput,+dinput,+cursor). Delete the file to go back to the setting above.
[ -f "$HOME/xla-winedebug" ] && export WINEDEBUG="$(cat "$HOME/xla-winedebug")"
export WINEESYNC="${XLA_ESYNC:-1}"

# Guest-side Vulkan: Mesa's wrapper ICD, which uses adrenotools to load the Turnip driver
# instead of the stock Adreno blob. The ICD manifest needs an ABSOLUTE library_path, and every
# copy that ships in a package hardcodes some app's data dir (GameNative's, and the bundle's own
# vkextra copy the pre-rename package id), so it is written here from the real install path on
# every launch. That keeps the runtime bundle independent of the package id and the Android user.
ICD="$ROOT/usr/share/vulkan/icd.d/xla_icd.aarch64.json"
printf '{\n    "ICD": {\n        "api_version": "1.3.289",\n        "library_path": "%s"\n    },\n    "file_format_version": "1.0.0"\n}\n' \
    "$ROOT/usr/lib/libvulkan_wrapper.so" > "$ICD"
export VK_ICD_FILENAMES="$ICD"
# Settings: the graphics driver. Empty XLA_DRIVER_DIR means the one shipped in the rootfs; a driver
# imported in the launcher lives in its own directory under files/drivers and is used as-is. Both
# layouts are an AdrenoTools package: meta.json, one vulkan.*.so, a driver-name file naming it,
# and a writable temp/ scratch dir.
DRIVER_DIR="$ROOT/usr/lib/turnip"
if [ -n "${XLA_DRIVER_DIR:-}" ] && [ -f "$XLA_DRIVER_DIR/driver-name" ]; then
    DRIVER_DIR="$XLA_DRIVER_DIR"
fi
export ADRENOTOOLS_DRIVER_PATH="$DRIVER_DIR/"
# The .so name comes from the installed package's meta.json (the bundled driver and the launcher's
# driver import both record it in driver-name).
if [ -f "$DRIVER_DIR/driver-name" ]; then
    # Shell builtin, no subprocess: nothing preloaded can leak into the value.
    IFS= read -r ADRENOTOOLS_DRIVER_NAME < "$DRIVER_DIR/driver-name" || [ -n "$ADRENOTOOLS_DRIVER_NAME" ]
    export ADRENOTOOLS_DRIVER_NAME
else
    export ADRENOTOOLS_DRIVER_NAME="vulkan.ad08XX.so"
fi
export ADRENOTOOLS_HOOKS_PATH="$ROOT/usr/lib"

# Turnip: skip the conformance-only paths, and keep Mesa quiet in a release build.
export TU_DEBUG=noconform
export MESA_DEBUG=silent
export MESA_NO_ERROR=1
# Settings: present mode. mailbox is lowest-latency and the default; fifo is the safe choice on a
# driver whose mailbox path stutters or tears.
export MESA_VK_WSI_PRESENT_MODE="${XLA_PRESENT_MODE:-mailbox}"

# DXVK 2.4.1-gplasync + vkd3d 2.14.1 are copied into the prefix, so Wine must prefer the
# native DLLs over its own builtins.
export WINEDLLOVERRIDES="d3d8,d3d9,d3d10,d3d10_1,d3d10core,d3d11,d3d12,d3d12core,dxgi=n"
export VKD3D_SHADER_MODEL=6_0

# Wrapper ICD settings, matched to what GameNative derives for the user's (clean-rendering) FFXIV
# container: graphicsDriver=Wrapper, bcnEmulation=auto, resourceType=auto, presentMode=mailbox.
# See XServerScreen.kt's Wrapper branch. BCn compute emulation is excluded on Adreno there.
export WRAPPER_VK_VERSION=1.3.284          # <vulkanVersion>.<Adreno driver patch>
export WRAPPER_EXTENSION_BLACKLIST=
export WRAPPER_RESOURCE_TYPE=auto
export WRAPPER_DISABLE_PRESENT_WAIT=0
export WRAPPER_MAX_IMAGE_COUNT=0
# Settings: 3 = auto (skipped on Adreno), 1 = always transcode BCn textures, 0 = never. A GPU with
# no native BC support needs this on; one with it loses performance for nothing.
export WRAPPER_EMULATE_BCN="${XLA_EMULATE_BCN:-3}"
export WRAPPER_USE_BCN_CACHE=0
export WRAPPER_BCN_GPU=0                   # CPU transcoder
export WRAPPER_ASTC_BLOCK=8x8
export vblank_mode=0

# DXVK settings from the same container's dxwrapperConfig: framerate=30, asyncCache=1.
# Settings: frame cap (0 = uncapped) and async pipeline compilation. Async hides shader-compile
# stutter but can briefly draw untextured geometry, so it is a switch rather than a given.
export DXVK_FRAME_RATE="${XLA_FPS_CAP:-30}"
export DXVK_ASYNC="${XLA_DXVK_ASYNC:-1}"
export DXVK_GPLASYNCCACHE="${XLA_DXVK_ASYNC:-1}"

# Turnip's shader/pipeline disk cache. Mesa leaves it off unless asked (GameNative's FFXIV container sets the same
# variables). Without it every launch recompiled every pipeline: ~5 CPU cores of dxvk-shader threads for minutes,
# CPU throttled to half, battery +2C/min. With a warm cache the title screen needs ~1.4 cores in total.
# Settings: off only to rule the cache out when a device renders wrongly or refuses to start.
if [ "${XLA_SHADER_CACHE:-1}" = "1" ]; then
    export MESA_SHADER_CACHE_DISABLE=false
else
    export MESA_SHADER_CACHE_DISABLE=true
fi
export MESA_SHADER_CACHE_MAX_SIZE=512MB
export MESA_SHADER_CACHE_DIR="$HOME/.cache"

# FEXCore presets, same variables as GameNative's FEXCorePresetManager. Its FFXIV container runs
# INTERMEDIATE; FEX's own defaults are closer to COMPATIBILITY, which costs CPU (and heat).
XLA_FEX_PRESET="${XLA_FEX_PRESET:-INTERMEDIATE}"
case "$XLA_FEX_PRESET" in
    PERFORMANCE)   set -- 0 0 0 0 1 ;;
    COMPATIBILITY) set -- 1 1 1 1 0 ;;
    *)             XLA_FEX_PRESET=INTERMEDIATE; set -- 1 0 0 1 1 ;;
esac
export FEX_TSOENABLED=$1 FEX_VECTORTSOENABLED=$2 FEX_MEMCPYSETTSOENABLED=$3
export FEX_HALFBARRIERTSOENABLED=$4 FEX_X87REDUCEDPRECISION=$5
export FEX_MULTIBLOCK="${XLA_FEX_MULTIBLOCK:-1}"

GAME="${XLA_GAME_DIR:-/storage/emulated/0/Emulation/windows/import/FFXIV}"
TARGET="winecfg /?"

# Written by the launcher immediately before this script runs: TARGET only. Every tuning variable
# lives above, so a setting can never be silently overridden by a stale wine-cmd (it used to be).
[ -f "$HOME/wine-cmd" ] && . "$HOME/wine-cmd"

# Local experiment overrides (absent in normal use). Sourced last so they win.
XLA_OVERRIDES=
if [ -f "$HOME/xla-env.sh" ]; then
    . "$HOME/xla-env.sh"
    while IFS= read -r line; do XLA_OVERRIDES="$XLA_OVERRIDES$line; "; done < "$HOME/xla-env.sh"
fi

NOW="$(date)"
XSOCK="$(ls -la "$TMPDIR/.X11-unix/" 2>&1)"
WINE_PRELOAD="$LD_PRELOAD${EVSHIM_PRELOAD:+:$EVSHIM_PRELOAD}"
{
  echo "=== $NOW ==="
  echo "TARGET=$TARGET WINEDEBUG=$WINEDEBUG DISPLAY=$DISPLAY TMPDIR=$TMPDIR"
  echo "ADRENOTOOLS_DRIVER_PATH=$ADRENOTOOLS_DRIVER_PATH ADRENOTOOLS_DRIVER_NAME=$ADRENOTOOLS_DRIVER_NAME"
  echo "FEX preset=$XLA_FEX_PRESET multiblock=$FEX_MULTIBLOCK DXVK_FRAME_RATE=$DXVK_FRAME_RATE async=$DXVK_ASYNC"
  echo "present=$MESA_VK_WSI_PRESENT_MODE bcn=$WRAPPER_EMULATE_BCN shaderCacheDisable=$MESA_SHADER_CACHE_DISABLE esync=$WINEESYNC"
  echo "game=$GAME dalamud=${XLA_DALAMUD:-0}"
  echo "overrides: ${XLA_OVERRIDES:-none}"
  echo "xsock: $XSOCK"
  echo "--- run ---"
  eval "LD_PRELOAD=\"\$WINE_PRELOAD\" \"\$WINE/bin/wine\" $TARGET"
  echo "--- exit=$? ---"
} >>"$LOG" 2>&1

echo "log: $(wc -l < "$LOG") lines"
tail -4 "$LOG"
