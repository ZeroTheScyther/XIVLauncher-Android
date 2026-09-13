#!/usr/bin/env bash
# Build the runtime packages the app downloads on first run, plus the manifest that describes them.
#
# This is the build half of in-app provisioning: the app fetches these files over HTTP on first run. Upload the
# whole output directory to the runtime host; runtime-manifest.json references its siblings by name.
#
# Inputs are the staged runtime trees under $XLA_RUNTIME_SRC (default: work/ beside this repo): the Proton
# arm64ec .wcp, the prepared Wine prefix, the bionic rootfs stage, the overlay (fonts, DXVK, vkd3d), the Vulkan
# wrapper/Turnip files, ALSA config and PulseAudio. Their upstream sources are listed in THIRD_PARTY.md.
#
# Everything is .tar.zst (zstd -19 --long): measured smallest AND fastest to decode of the candidates on a
# phone - 207 MB and ~15 s for the Wine tree, against xz's 251 MB and ~131 s.
#
# Layout is built for a CDN-fronted object store (Cloudflare R2 behind a custom domain):
#   runtime-manifest.json          stable URL, SHORT cache TTL  - replace it to roll out a change
#   pkg/<name>-<sha8>.tar.zst      content-addressed, IMMUTABLE - cache forever
# Because a package's name contains its own hash, a component that did not change keeps its URL and
# stays cached at the edge, and a changed one can never be served stale. Only the manifest is mutable.
#
#   usage: scripts/bundle-runtime.sh [--out DIR] [NAME ...]     (no NAME = all components)
set -euo pipefail

P="$(cd "$(dirname "$0")/.." && pwd)"
SRCROOT="${XLA_RUNTIME_SRC:-$P/work}"
OUT="$SRCROOT/bundle"
RUNTIME_VERSION=1
ZSTD_ARGS=(-19 --long=27 -T0 -q)

MANIFEST_ONLY=0
while [[ $# -gt 0 && "$1" == --* ]]; do
    case "$1" in
        --out) OUT="$2"; shift 2 ;;
        --manifest-only) MANIFEST_ONLY=1; shift ;;   # rebuild runtime-manifest.json from existing packages
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
done
WANTED=("$@")

mkdir -p "$OUT/pkg"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# Directories that a tar archive does not list explicitly are created by extraction with the CURRENT
# time (alsa.tgz has no "usr/" entry, so usr and usr/etc were freshly stamped on every run). That alone
# made each rebuild produce a different archive. Only directories are touched; files keep their times.
normalise_dir_times() {
    find "$1" -type d -exec touch -d @1 {} +
}

want() {
    [[ $MANIFEST_ONLY -eq 1 ]] && return 1
    [[ ${#WANTED[@]} -eq 0 ]] && return 0
    local name
    for name in "${WANTED[@]}"; do [[ "$name" == "$1" ]] && return 0; done
    return 1
}

# Entries that must never reach a device. The .wcp's inner tar was written by libarchive on macOS and
# carries 1761 AppleDouble sidecars; they are pure noise and confuse a strict tar reader.
EXCLUDES=(--exclude='._*' --exclude='.DS_Store')

# Deterministic packing, so a rebuild of unchanged content yields the same bytes and therefore the same
# content-addressed URL - otherwise every rebuild looks like a new component and users re-download it.
# Two things were needed:
#   --sort=name, because tar otherwise follows readdir order, which varies per filesystem;
#   and flat DIRECTORY mtimes in any tree we stage in $TMP - see normalise_dir_times. Ownership is
#   flattened too, being meaningless on Android where everything lands under the app's uid.
# FILE mtimes are deliberately preserved: Wine compares its prefix's .update-timestamp against the
# mtime of share/wine/wine.inf to decide whether to re-run wineboot, so flattening those would make
# every first launch think the prefix is stale.
DETERMINISTIC=(--sort=name --owner=0 --group=0 --numeric-owner)

# pack <name> <source dir> <member...>  -> $OUT/<name>.tar.zst
pack() {
    local name="$1" src="$2"; shift 2
    local tarball="$TMP/$name.tar" staged="$TMP/$name.tar.zst"
    echo "==> $name  (from $src)"
    tar -c "${EXCLUDES[@]}" "${DETERMINISTIC[@]}" ${PACK_EXTRA[@]+"${PACK_EXTRA[@]}"} \
        -f "$tarball" -C "$src" "$@"
    zstd "${ZSTD_ARGS[@]}" -o "$staged" -f -- "$tarball"
    rm -f "$tarball"
    unset PACK_EXTRA

    # Content-addressed final name, so the URL changes exactly when the bytes do.
    local sha8
    sha8="$(sha256sum "$staged" | cut -c1-8)"
    rm -f "$OUT/pkg/$name"-*.tar.zst          # drop this component's previous build
    local dest="$OUT/pkg/$name-$sha8.tar.zst"
    mv "$staged" "$dest"
    printf '    %s  %s bytes\n' "pkg/$(basename "$dest")" "$(stat -c%s "$dest")"
}

# --- wine: the ARM64EC Wine tree, extracted from the upstream .wcp so the pipeline is reproducible
#     rather than depending on a hand-extracted directory of unknown provenance.
if want wine; then
    echo "==> wine  (unpacking proton-10.0-arm64ec.wcp)"
    mkdir -p "$TMP/wine"
    # prefixPack.tzst and profile.json are Winlator container metadata we do not use; the prefix
    # ships as its own component, already populated with FEXCore and the registry.
    xz -dc "$SRCROOT/wine-wcp/proton-10.0-arm64ec.wcp" \
        | tar -x "${EXCLUDES[@]}" -C "$TMP/wine" bin lib share
    normalise_dir_times "$TMP/wine"
    pack wine "$TMP/wine" bin lib share
fi

# --- prefix: the Wine prefix, minus every dosdevices link. PrefixSetup recreates c:, z: and a: from
#     the real FilesDir and the chosen game folder; the image's own links are stale Winlator paths
#     (z: and e: point into com.winlator.cmod) and must not ship.
if want prefix; then
    # Packed from INSIDE .wine, so the entries are relative to the component's dest (prefix/.wine) -
    # the same invariant every other package follows. Packing the .wine directory itself instead put
    # everything in prefix/.wine/.wine/, which silently lost the registry and the FEXCore DLLs while
    # still looking healthy, because the builtin links and the overlay recreate the paths around them.
    PACK_EXTRA=(--exclude='./dosdevices')
    pack prefix "$SRCROOT/prefix-build/prefix/.wine" .
fi

# --- rootfs: the bionic userspace (libX11, libvulkan, libandroid-sysvshm, ...). 417 relative symlinks.
want rootfs && pack rootfs "$SRCROOT/rootfs-build/stage" usr

# --- overlay: Windows/Japanese fonts + DXVK 2.4.1-gplasync + vkd3d 2.14.1, extracted over the prefix.
want overlay && pack overlay "$SRCROOT/rootfs-build/overlay" drive_c

# --- vkextra: the Vulkan wrapper ICD, adrenotools hook libs and the Turnip driver package.
want vkextra && pack vkextra "$SRCROOT/rootfs-build/vkextra" usr

# --- alsa: config only, and the one component with no source tree - transcode the existing archive.
if want alsa; then
    echo "==> alsa  (transcoding alsa.tgz)"
    mkdir -p "$TMP/alsa"
    tar -xzf "$SRCROOT/rootfs-build/alsa.tgz" -C "$TMP/alsa"
    normalise_dir_times "$TMP/alsa"
    pack alsa "$TMP/alsa" usr
fi

# --- pulseaudio: the modules and pactl that xla.PulseAudioServer drives.
want pulseaudio && pack pulseaudio "$SRCROOT/rootfs-build/pulseaudio" modules pactl

# --- manifest -------------------------------------------------------------------------------------
# "sequence" is advisory: the app owns the real ordering, because two steps between components are
# code, not data (linking Wine's builtin PEs into system32, and deleting the d3d DLLs those links
# create before the overlay is unpacked over them).
echo "==> runtime-manifest.json"
python3 - "$OUT" "$RUNTIME_VERSION" <<'PY'
import glob, hashlib, json, os, shlex, subprocess, sys

out, version = sys.argv[1], int(sys.argv[2])
# Where each package's contents land, relative to the app's private files dir.
dest = {
    "wine": "wine",
    "prefix": "prefix/.wine",
    "rootfs": "rootfs",
    "overlay": "prefix/.wine",
    "vkextra": "rootfs",
    "alsa": "rootfs",
    "pulseaudio": "pulseaudio",
}
# ORDER IS LOAD-BEARING. rootfs and vkextra both write usr/lib, and they overlap on five adrenotools
# hook libs (libadrenotools, libmain_hook, libhook_impl, libgsl_alloc_hook, libfile_redirect_hook) with
# very different contents - rootfs carries the old build, vkextra the GameNative wrapper that actually
# renders correctly. vkextra MUST come after rootfs, or provisioning silently restores the wrapper that
# caused the texture garbage.
sequence = ["wine", "prefix", "overlay", "rootfs", "vkextra", "alsa", "pulseaudio"]

path = os.path.join(out, "runtime-manifest.json")
existing = {}
if os.path.exists(path):
    with open(path) as f:
        existing = {c["name"]: c for c in json.load(f).get("components", [])}

components = []
for name in dest:
    found = glob.glob(os.path.join(out, "pkg", f"{name}-*.tar.zst"))
    if not found:
        if name in existing:
            components.append(existing[name])   # not rebuilt this run; keep the previous entry
        continue
    archive = found[0]
    digest = hashlib.sha256()
    with open(archive, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    # Uncompressed size, so the app can check free space before it starts. Counted rather than read
    # from the frame header, which is absent for a stream zstd did not size up front.
    counted = subprocess.run(f"zstd -dc -- {shlex.quote(archive)} | wc -c",
                             shell=True, capture_output=True, text=True, check=True)
    extracted = int(counted.stdout.strip())
    components.append({
        "name": name,
        "file": f"pkg/{os.path.basename(archive)}",
        "bytes": os.path.getsize(archive),
        "sha256": digest.hexdigest(),
        "extractedBytes": extracted,
        "dest": dest[name],
    })

components.sort(key=lambda c: sequence.index(c["name"]))
with open(path, "w") as f:
    json.dump({"schema": 1, "runtimeVersion": version, "sequence": sequence,
               "components": components}, f, indent=2)
    f.write("\n")

total_dl = sum(c["bytes"] for c in components)
total_ex = sum(c["extractedBytes"] for c in components)
print(f"    {len(components)} components, {total_dl / 2**20:.0f} MB to download,"
      f" {total_ex / 2**30:.2f} GiB extracted")
PY

echo "==> done: $OUT"
ls -la "$OUT"
