# Third-party software

XIVLauncher Android is built on the work of many projects. This file lists what the app includes or downloads, where
it comes from, and under which licence. The app itself is licensed under the GNU General Public License v3.0 (see
`LICENSE`), as required by the GPL-3.0 components it incorporates.

## Built into the app (APK)

| Component | Upstream | Licence | Used as |
|---|---|---|---|
| XIVLauncher.Common | [goatcorp/FFXIVQuickLauncher](https://github.com/goatcorp/FFXIVQuickLauncher) @ `40ed6e9` | GPL-3.0 | Square Enix login, patching, Dalamud launch. Fetched at build time (`scripts/fetch-deps.sh`). |
| Winlator X server, input and audio (Java) | [utkarshdalal/GameNative](https://github.com/utkarshdalal/GameNative) @ `cd5ecbc8e`, derived from [brunodev85/winlator](https://github.com/brunodev85/winlator) | GPL-3.0 | Vendored in `src/XlaBoot.Android/java/com/winlator`, modified. |
| Winlator native libraries (`libwinlator*.so`, `libxconnectorpatch.so`, `libahbimage.so`, `libasurface_renderer.so`, `libextras.so`, `libevshim.so`) | GameNative / Winlator | GPL-3.0 | Prebuilt, `src/native/arm64-v8a`. |
| Vulkan renderer (`libvulkan_renderer.so`) | GameNative | GPL-3.0 | Source in `src/native/vulkan_renderer`, built by `scripts/build-native.sh`. |
| Snapdragon Game Super Resolution shader | [SnapdragonStudios/snapdragon-gsr](https://github.com/SnapdragonStudios/snapdragon-gsr) | BSD-3-Clause | Upscaler pass in the renderer. |
| `winhandler.exe` | GameNative imagefs (Winlator) | GPL-3.0 | `runtime/winhandler.exe`, mouse input helper inside Wine. |
| PulseAudio, libsndfile, libltdl | [PulseAudio](https://www.freedesktop.org/wiki/Software/PulseAudio/), [libsndfile](https://github.com/libsndfile/libsndfile), [libtool](https://www.gnu.org/software/libtool/), as built by GameNative | LGPL-2.1+ | Audio server, `src/native/arm64-v8a`. |
| LLVM libc++ (`libc++_shared.so`) | [LLVM](https://llvm.org) via the Android NDK | Apache-2.0 WITH LLVM-exception | C++ runtime. |
| Avalonia UI | [AvaloniaUI/Avalonia](https://github.com/AvaloniaUI/Avalonia) | MIT | Launcher UI. |
| CommunityToolkit.Mvvm | [CommunityToolkit/dotnet](https://github.com/CommunityToolkit/dotnet) | MIT | View models. |
| ZstdSharp | [oleg-st/ZstdSharp](https://github.com/oleg-st/ZstdSharp) | MIT | Unpacking the runtime. |
| SteamKit2 | [SteamRE/SteamKit](https://github.com/SteamRE/SteamKit) | LGPL-2.1 | Steam sign-in and auth session tickets, for Steam service accounts. |
| protobuf-net | [protobuf-net/protobuf-net](https://github.com/protobuf-net/protobuf-net) | Apache-2.0 | Steam message serialisation (SteamKit2 dependency). |
| AndroidX libraries | [Android Jetpack](https://developer.android.com/jetpack) | Apache-2.0 | Splash screen, collections. |
| Material Design icons | [google/material-design-icons](https://github.com/google/material-design-icons) | Apache-2.0 | Launcher icons. |
| Inter font | [rsms/inter](https://github.com/rsms/inter) (via Avalonia.Fonts.Inter) | OFL-1.1 | Launcher typeface. |

## Downloaded on first run (Wine runtime)

The runtime is hosted at `xivlauncher.aetherworks.uk` and described by `runtime-manifest.json`. It is assembled by
`scripts/bundle-runtime.sh` from the following:

| Component | Upstream | Licence |
|---|---|---|
| Wine / Proton 10.0 (ARM64EC build) | [ValveSoftware/Proton](https://github.com/ValveSoftware/Proton), [Wine](https://gitlab.winehq.org/wine/wine), ARM64EC build as distributed by GameNative | LGPL-2.1+ (Wine); Proton components under their own licences |
| FEX-Emu (FEXCore ARM64EC DLLs) | [FEX-Emu/FEX](https://github.com/FEX-Emu/FEX) | MIT |
| DXVK 2.4.1 (gplasync) | [doitsujin/dxvk](https://github.com/doitsujin/dxvk), [gplasync patches](https://gitlab.com/Ph42oN/dxvk-gplasync) | zlib |
| vkd3d-proton 2.14.1 | [HansKristian-Work/vkd3d-proton](https://github.com/HansKristian-Work/vkd3d-proton) | LGPL-2.1 |
| Mesa Turnip (Adreno Vulkan driver) | [Mesa](https://gitlab.freedesktop.org/mesa/mesa) | MIT |
| libadrenotools | [bylaws/libadrenotools](https://github.com/bylaws/libadrenotools) @ `8fae8ce`, GameNative fork [Pipetto-crypto/libadrenotools](https://github.com/Pipetto-crypto/libadrenotools) @ `8483dfd` | BSD-2-Clause |
| Bionic userspace libraries (X11, Vulkan loader, etc.) | As packaged by GameNative | Various (MIT/X11, Apache-2.0) |
| ALSA configuration | [alsa-project](https://www.alsa-project.org) | LGPL-2.1 |
| PulseAudio modules and `pactl` | PulseAudio | LGPL-2.1+ |

## Used at runtime, not distributed

- **Dalamud** ([goatcorp/Dalamud](https://github.com/goatcorp/Dalamud), AGPL-3.0) and its .NET runtime are downloaded
  by XIVLauncher.Common from goatcorp's servers when the player enables it.
- **FINAL FANTASY XIV** is downloaded from Square Enix's servers, or copied by the player. This project is not
  affiliated with or endorsed by Square Enix. FINAL FANTASY is a registered trademark of Square Enix Holdings Co., Ltd.

## Source code offer

The complete source of this app is in this repository. Source for the third-party GPL and LGPL components listed
above is available from the linked upstream projects at the versions given. If you cannot obtain the source of any
component we distribute, open an issue in this repository and we will provide it, for at least three years after the
last release that included that component.
