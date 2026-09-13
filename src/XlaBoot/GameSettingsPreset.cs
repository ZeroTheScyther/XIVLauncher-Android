using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace XlaBoot;

/// <summary>
/// "Optimize game settings": puts FFXIV's own graphics options back to the profile measured to hold 30 fps with the
/// least heat on a phone (from an on-device optimization pass on a Snapdragon 8 Elite). The game keeps these in FFXIV.cfg inside the Wine prefix, so a fresh prefix, or the
/// player changing things in game, loses them - this is the one-tap way back.
///
/// Only graphics keys are touched. Screen mode and window size stay with the launcher's resolution setting, and the
/// rest of the file (gamepad, UI, quest progress, accessibility) is the player's. Values of EXISTING lines are
/// rewritten in place, so section layout, CRLF line ends and the trailing NUL the game writes are preserved.
/// The game rewrites the file when it exits, so this only sticks while the game is closed.
/// </summary>
public static class GameSettingsPreset
{
    public const string BackupSuffix = ".pre-optimize";

    /// <summary>Key -> value. Keys are unique across FFXIV.cfg's sections (the DX11 twins carry a suffix).</summary>
    public static readonly IReadOnlyDictionary<string, string> Values = new Dictionary<string, string>
    {
        ["Fps"] = "3",
        ["AntiAliasing"] = "1",
        ["TextureFilterQuality"] = "1",
        ["TextureAnisotropicQuality"] = "0",
        ["SSAO"] = "0",
        ["Glare"] = "0",
        ["DistortionWater"] = "0",
        ["DepthOfField"] = "0",
        ["RadialBlur"] = "0",
        ["Vignetting"] = "1",
        ["GrassQuality"] = "0",
        ["TranslucentQuality"] = "0",
        ["ShadowVisibilityType"] = "2",
        ["ShadowSoftShadowType"] = "0",
        ["ShadowTextureSizeType"] = "0",
        ["ShadowCascadeCountType"] = "0",
        ["LodType"] = "1",
        ["StreamingType"] = "0",
        ["GeneralQuality"] = "1",
        ["OcclusionCulling"] = "1",
        ["ShadowLOD"] = "1",
        ["PhysicsType"] = "1",
        ["MapResolution"] = "1",
        ["ShadowVisibilityTypeSelf"] = "1",
        ["ShadowVisibilityTypeParty"] = "0",
        ["ShadowVisibilityTypeOther"] = "0",
        ["ShadowVisibilityTypeEnemy"] = "0",
        ["PhysicsTypeSelf"] = "2",
        ["PhysicsTypeParty"] = "1",
        ["PhysicsTypeOther"] = "0",
        ["PhysicsTypeEnemy"] = "1",
        ["ReflectionType"] = "0",
        ["AntiAliasing_DX11"] = "1",
        ["TextureFilterQuality_DX11"] = "1",
        ["TextureAnisotropicQuality_DX11"] = "0",
        ["SSAO_DX11"] = "0",
        ["Glare_DX11"] = "2",
        ["DistortionWater_DX11"] = "0",
        ["DepthOfField_DX11"] = "0",
        ["RadialBlur_DX11"] = "0",
        ["Vignetting_DX11"] = "1",
        ["GrassQuality_DX11"] = "0",
        ["TranslucentQuality_DX11"] = "0",
        ["ShadowSoftShadowType_DX11"] = "0",
        ["ShadowTextureSizeType_DX11"] = "0",
        ["ShadowCascadeCountType_DX11"] = "0",
        ["LodType_DX11"] = "1",
        ["OcclusionCulling_DX11"] = "1",
        ["ShadowLOD_DX11"] = "1",
        ["MapResolution_DX11"] = "1",
        ["ShadowVisibilityTypeSelf_DX11"] = "1",
        ["ShadowVisibilityTypeParty_DX11"] = "0",
        ["ShadowVisibilityTypeOther_DX11"] = "0",
        ["ShadowVisibilityTypeEnemy_DX11"] = "0",
        ["PhysicsTypeSelf_DX11"] = "2",
        ["PhysicsTypeParty_DX11"] = "0",
        ["PhysicsTypeOther_DX11"] = "0",
        ["PhysicsTypeEnemy_DX11"] = "0",
        ["ReflectionType_DX11"] = "0",
        ["WaterWet_DX11"] = "0",
        ["ParallaxOcclusion_DX11"] = "0",
        ["Tessellation_DX11"] = "0",
        ["GlareRepresentation_DX11"] = "0",
        ["DynamicRezoThreshold"] = "2",
        ["GraphicsRezoScale"] = "80",
        ["GraphicsRezoUpscaleType"] = "0",
        ["GrassEnableDynamicInterference"] = "0",
        ["ShadowBgLOD"] = "1",
        ["TextureRezoType"] = "0",
        ["ShadowLightValidType"] = "0",
        ["DynamicRezoEnableCutScene"] = "1",
    };

    /// <summary>FFXIV.cfg in the Wine prefix's Documents, where the game reads it.</summary>
    public static string ConfigPath(string filesDir) => Path.Combine(filesDir, "prefix", ".wine", "drive_c", "users",
        "xuser", "Documents", "My Games", "FINAL FANTASY XIV - A Realm Reborn", "FFXIV.cfg");

    /// <summary>
    /// Applies the preset to <paramref name="path"/> and returns how many values changed. The previous file is kept
    /// beside it as FFXIV.cfg.pre-optimize when anything changes. Throws on I/O errors.
    /// </summary>
    public static int Apply(string path) => Set(path, Values, backup: true);

    /// <summary>
    /// Run before every launch: the game always starts in Full Screen at the launcher's resolution. Windowed mode shows
    /// Wine's window frame, which a thumb near the screen edge grabs and resizes the game by accident. A player who
    /// picks windowed in game gets Full Screen again next launch - that is the point. Returns the values changed.
    /// </summary>
    public static int ForceFullScreen(string path, int width, int height) => Set(path, new Dictionary<string, string>
    {
        ["ScreenMode"] = "1",
        ["FullScreenWidth"] = width.ToString(),
        ["FullScreenHeight"] = height.ToString(),
    }, backup: false);

    /// <summary>Rewrites the values of existing keys in place. Missing keys are not added.</summary>
    public static int Set(string path, IReadOnlyDictionary<string, string> values, bool backup)
    {
        var original = File.ReadAllBytes(path);
        // Latin-1 maps every byte to one char and back, so anything we don't rewrite round-trips exactly.
        var latin1 = Encoding.Latin1;
        var lines = latin1.GetString(original).Split("\r\n");
        var changed = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var tab = lines[i].IndexOf('\t');
            if (tab <= 0)
                continue;
            if (!values.TryGetValue(lines[i][..tab], out var value))
                continue;
            var current = lines[i][(tab + 1)..];
            // The last line can carry the NUL terminator the game writes; keep it after the value.
            var nul = current.IndexOf('\0');
            var tail = nul >= 0 ? current[nul..] : "";
            if (nul >= 0)
                current = current[..nul];
            if (current == value)
                continue;
            lines[i] = lines[i][..(tab + 1)] + value + tail;
            changed++;
        }

        if (changed == 0)
            return 0;

        if (backup)
            File.WriteAllBytes(path + BackupSuffix, original);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, latin1.GetBytes(string.Join("\r\n", lines)));
        File.Move(temp, path, overwrite: true);
        return changed;
    }
}
