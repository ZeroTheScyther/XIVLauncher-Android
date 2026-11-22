using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XlaBoot;

/// <summary>
/// Dalamud's swapchain hook mode, matched to the DXVK build before each Dalamud launch.
///
/// Dalamud hooks IDXGISwapChain::Present by default by rewriting its first instructions as x86-64 code
/// (SwapChainHookMode 0, ByteCode). In an ARM64EC dxgi.dll those instructions are ARM64, so the hook fails with
/// "Can't encode an invalid instruction" and Dalamud's UI never comes up. VTable mode (1) swaps the function
/// pointer instead, which works whatever the code behind it is.
///
/// Only switches to VTable for an ARM64EC build, and only switches back if the launcher was the one that set it,
/// so a player who picked VTable in Dalamud's own settings keeps it. Must run while the game is closed: Dalamud
/// rewrites dalamudConfig.json on exit.
/// </summary>
public static class DalamudHookMode
{
    private const string Key = "SwapChainHookMode";
    private const int ByteCode = 0;
    private const int VTable = 1;

    /// <summary>Present while the VTable mode in dalamudConfig.json was set by the launcher, not the player.</summary>
    private static string MarkerPath(string filesDir) => Path.Combine(filesDir, "xlroaming", ".vtable-for-arm64ec");

    /// <summary>Returns a line for the launcher log when it changed anything, otherwise null.</summary>
    public static string? Apply(string filesDir)
    {
        var path = DalamudRepos.ConfigPath(filesDir);
        var marker = MarkerPath(filesDir);
        try
        {
            if (Imports.DxvkImport.SelectedIsArm64Ec(filesDir))
            {
                var config = DalamudRepos.LoadOrNew(path);
                if (Mode(config) == VTable)
                    return null;
                config[Key] = VTable;
                DalamudRepos.Save(path, config);
                File.WriteAllText(marker, "");
                return "Dalamud: VTable swapchain hook for the ARM64EC DXVK build";
            }

            if (!File.Exists(marker))
                return null;
            File.Delete(marker);
            if (!File.Exists(path))
                return null;
            var current = DalamudRepos.Load(path);
            if (Mode(current) != VTable)
                return null;
            current[Key] = ByteCode;
            DalamudRepos.Save(path, current);
            return "Dalamud: back to the default swapchain hook";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or JsonException or InvalidOperationException)
        {
            // Never stops a launch: the worst case is the Dalamud hook error this exists to avoid.
            return $"Dalamud: could not set the swapchain hook mode ({ex.GetType().Name})";
        }
    }

    private static int Mode(JsonObject config) =>
        config[Key] is JsonValue value && value.TryGetValue(out int mode) ? mode : ByteCode;
}
