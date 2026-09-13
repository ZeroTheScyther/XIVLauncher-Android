# XIVLauncher Android

Play FINAL FANTASY XIV on an Android phone. This is a port of [XIVLauncher](https://github.com/goatcorp/FFXIVQuickLauncher):
it logs in to your Square Enix account, installs or updates the game, and runs the Windows client under Wine with
FEX, DXVK and the Turnip Vulkan driver, all inside one app. [Dalamud](https://github.com/goatcorp/Dalamud) plugins
are supported.

> [!CAUTION]
> **This repository is the only official source of XIVLauncher Android.** The APK is published only on this
> repository's [Releases page](https://github.com/ZeroTheScyther/XIVLauncher-Android/releases). It is not
> distributed through any website, app store, mirror, Discord server or file host.
>
> The app handles your Square Enix login. An APK from anywhere else may be modified to **steal your FFXIV account**
> or **install malware on your device**. If you got it somewhere else, uninstall it, change your Square Enix
> password and enable the one-time password.

> [!NOTE]
> **Alpha.** Expect rough edges. Please send logs with bug reports (Logs tab → download button).

## Requirements

- An arm64 Android phone with a **Snapdragon** SoC (Adreno GPU). Developed on a Snapdragon 8 Elite; other
  recent Adreno 7xx/8xx devices may work.
- Android 8.0 or newer.
- About 2.5 GB for the runtime, plus the game (~130 GB), or copy your existing PC install onto the phone.
- A FINAL FANTASY XIV account with an active subscription (standalone Square Enix accounts).
- A controller, or the on-screen controls, keyboard and mouse.

## Install

1. Download `XIVLauncher.apk` from the [latest release](https://github.com/ZeroTheScyther/XIVLauncher-Android/releases/latest).
2. Open it on your phone and allow installing from your browser or file manager.
3. On first launch the app downloads its runtime (about 400 MB). Keep the app open until setup finishes.
4. Tap **Play**, log in, and either let the app install the game or point **Settings → Game install location** at a
   copy you already have.

The app checks for new releases on launch and offers the download from this repository.

### Verifying the APK

Every official APK is signed with the same key. You can check a download with `apksigner` from the Android SDK
build tools:

```sh
apksigner verify --print-certs XIVLauncher.apk
```

The certificate SHA-256 digest must be:

```
ec57f9d5961252b5c09b973c6cd746ba126f1165179b9ee9be4cad419851d943
```

Each release also lists the APK's SHA-256 checksum in `XIVLauncher.apk.sha256`. Android itself refuses to update an
official install with an APK signed by a different key.

## Building

You need the .NET 10 SDK with the Android workload (`dotnet workload install android`), the Android SDK, and
Java 17.

```sh
scripts/fetch-deps.sh     # XIVLauncher.Common, pinned
scripts/build.sh          # Debug build, installed on the connected device with adb
```

Release builds are produced by GitHub Actions for every `v*` tag (`.github/workflows/release.yml`).

Repository layout:

| Path | Contents |
|---|---|
| `src/XlaBoot` | Launcher UI and logic (Avalonia): login, patching, runtime setup, settings |
| `src/XlaBoot.Android` | Android app: X server, input, audio, Wine process management |
| `src/XlaBoot.Desktop` | Desktop preview of the launcher UI |
| `src/native` | Native libraries and the Vulkan renderer source |
| `runtime` | `run-wine.sh` launch script and in-prefix helpers shipped with the app |
| `scripts` | Build, native build and runtime bundling scripts |

## Support

If this project helps you, you can [buy me a coffee](https://ko-fi.com/zerothescyther).

## Licence

GPL-3.0. See [LICENSE](LICENSE) and [THIRD_PARTY.md](THIRD_PARTY.md) for the components this project builds on.

Not affiliated with or endorsed by Square Enix. FINAL FANTASY is a registered trademark of Square Enix Holdings Co.,
Ltd.
