# Changelog

## 0.2.5-alpha.1

- You can now edit the position of the on-screen touch controls.
- Touch-to-mouse translation improvements.
- Added a nice small loading screen on app transition.

## 0.2.6-alpha.1

- Implemented touchpad mode for mouse in the back menu.

## 0.2.7-alpha.1

- Fixed DS4 touchpad compatibility.

## 0.2.8-alpha.1

- Auto-driver detection for Adreno 830 / 840+ devices.
- Fixed an android focus bug on external input detection that caused the screen to wash out.

## 0.2.9-alpha.1

- Fixed an issue with folder path parsing that caused the game to not launch with Dalamud off.
- Added process snapshots and log header fix for better debugging.
- Exporting logs also exports a copy of FFXIV.cfg (game settings) for better debugging.
- You can now opt in the dalamud beta branch. Sadly, I won't offer support for any issues caused by Dalamud or Dalamud plugins.
- Added drop-downs to some of the settings that have multiple options so you don't have to keep manually cycling through them.

## 0.2.10-alpha.1

- Fixed heavy screen glitching on Samsung Snapdragon 8 Gen 2 devices (S23, Fold5, Flip5) when using the hardware cursor.

## 0.2.11-alpha

- Updated the download and patching logic of the game for better in-app game install speed.

## 0.2.12-alpha

- Added FEX code cache for faster game startup.

## 0.3.0-alpha

- Added DXVK importer (by K1r0h)
- Added language picker in Settings for both the game and the launcher. The launcher translations are a first pass, so corrections are welcome as issues or PRs (src/XlaBoot/Localization/Strings.*.resx).
- Keyboard typing now works without Dalamud on.
- Keyboard typing now supports typing in Dalamud windows.
- Added a new on-screen button that opens the keyboard so you don't have to open the back-menu every time. While it works similarly to the on-screen gamepad, it's controlled by a toggle in the back-menu so it remains active while you have a controller connected.
- Fixed a window focus bug along the way which caused inputs to not be sent to the game unless you tapped on it in either touchscreen mode or tap-to-click mode.
- Added an option in the back-menu to turn off the auto-keyboard opening when selecting an input field.

## 0.3.1-alpha

- You can now use the android keyboard to write in the character creation screen. (Update the Dalamud plugin to 1.1.1 if you have Dalamud on. Without Dalamud it works just fine.) (Thanks Marv-Ahock)
- The back-menu is also translated now. (Ooops. My bad. I forgot about that one.)

## 0.3.2-alpha

- DXVK now uses a native ARM64EC build by default, so graphics translation no longer runs under emulation. The old x86-64 build is still selectable in the DXVK setting.
- You can now import FEXCore builds, and DXVK accepts .wcp packages too (by K1r0h).
- The Vulkan version now follows the selected driver, so 1.4 drivers get Vulkan 1.4 (by K1r0h).
- The game stays connected while you're in another app (by K1r0h).
- Added 16:10 resolutions and a 40 fps cap (by K1r0h).

## 0.3.3-alpha

- Added a D: drive that points at your phone's storage, so plugins like Penumbra can keep their files there.
- Import plugin configs now says how to pack the zip.
