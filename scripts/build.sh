#!/usr/bin/env bash
# Build the XIVLauncher Android APK and install it on the connected device.
#
#   scripts/build.sh                 Debug build, installed with adb
#   XLA_CONFIG=Release scripts/build.sh
#
# Needs the .NET 10 SDK with the android workload, the Android SDK, and XIVLauncher.Common fetched by
# scripts/fetch-deps.sh. Set ANDROID_SDK_ROOT if the SDK isn't in ~/Android/Sdk.
#
# Debug builds default to .NET "Fast Deployment", which pushes assemblies separately from the APK; a plain
# `adb install` of such an APK aborts at startup with
#   "No assemblies found in .__override__/arm64-v8a ... Fast Deployment. Exiting"
# hence EmbedAssembliesIntoApk=true.
set -euo pipefail

P="$(cd "$(dirname "$0")/.." && pwd)"
SDK="${ANDROID_SDK_ROOT:-$HOME/Android/Sdk}"
ADB="$(command -v adb || echo "$SDK/platform-tools/adb")"
PKG="uk.aetherworks.xivlauncher"
PROJ="$P/src/XlaBoot.Android/XlaBoot.Android.csproj"
CFG="${XLA_CONFIG:-Debug}"

[[ -d "$P/external/FFXIVQuickLauncher" ]] || { echo "Run scripts/fetch-deps.sh first."; exit 1; }

echo "==> building ($CFG)"
dotnet build "$PROJ" -c "$CFG" \
    -p:AndroidSdkDirectory="$SDK" \
    -p:EmbedAssembliesIntoApk=true \
    -p:AndroidUseSharedRuntime=false \
    -p:AndroidPackageFormat=apk \
    | tail -5

APK=$(find "$P/src/XlaBoot.Android/bin/$CFG" -name "$PKG-Signed.apk" | head -1)
[[ -n "$APK" ]] || { echo "APK not found"; exit 1; }
echo "==> installing $APK"
# --no-incremental: on Android 11+ adb defaults to an incremental (streamed) install. If the connection drops
# mid-stream - routine on wireless adb - PackageManager marks the package unhealthy and deletes it, data
# included, before adb's fallback install reports "Success" on a blank app.
"$ADB" install --no-incremental -r -t "$APK" | tail -2
