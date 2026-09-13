#!/usr/bin/env bash
# Fetch the upstream sources the build references but this repo doesn't carry.
#   XIVLauncher.Common (goatcorp/FFXIVQuickLauncher), pinned to the commit the app is built and tested against.
# Keep the pin in sync with .github/workflows/release.yml and THIRD_PARTY.md.
set -euo pipefail

P="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$P/external/FFXIVQuickLauncher"
REPO="https://github.com/goatcorp/FFXIVQuickLauncher.git"
COMMIT="40ed6e93e7eb73e1c18f4d4871e05f32ab5fd2c6"

if [[ -e "$DEST" ]]; then
    echo "external/FFXIVQuickLauncher already present"
    exit 0
fi
mkdir -p "$P/external"
git clone --filter=blob:none "$REPO" "$DEST"
git -C "$DEST" checkout --quiet "$COMMIT"
echo "==> XIVLauncher.Common at $(git -C "$DEST" rev-parse --short HEAD)"
