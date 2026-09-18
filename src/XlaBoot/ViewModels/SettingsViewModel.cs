using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XlaBoot.Steam;

namespace XlaBoot.ViewModels;

/// <summary>
/// The launcher's settings screen, opened by the cog on the login page.
///
/// Everything here takes effect on the NEXT launch, which is exactly why it is not in the in-game
/// menu: showing a value mid-game that does not match what is running is worse than not showing it.
/// The in-game menu keeps only the settings that apply live (overlay, upscaler, screen fit,
/// on-screen pad, mouse, stick dead zone).
///
/// Values are stored in the same SharedPreferences as the in-game menu's, through AppHost; after
/// each write Java regenerates files/xla-settings.sh, which run-wine.sh sources. The option lists
/// below are the authority for the launch-time keys — xla.XlaSettings reads them back as plain
/// strings with a default and never validates them against a list of its own.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    // The first entry is the default, so a flag that ships off needs the reversed pair - otherwise
    // Cycle() starts from "ON" and the first tap moves a row that already reads "Off" to "Off".
    private static readonly string[] OnOff = { "ON", "OFF" };
    private static readonly string[] OffOn = { "OFF", "ON" };
    private static readonly string[] OnOffLabels = { "On", "Off" };
    private static readonly string[] OffOnLabels = { "Off", "On" };


    [ObservableProperty]
    private bool _isOpen;

    /// <summary>0 General, 1 Graphics, 2 Advanced, 3 Dalamud Repos.</summary>
    [ObservableProperty]
    private int _tabIndex;

    /// <summary>The last thing that happened: a picked folder rejected, a driver imported, an error.</summary>
    [ObservableProperty]
    private string _status = "";

    public ObservableCollection<SettingsItem> Rows { get; } = new();

    public bool IsGeneralTab => TabIndex == 0;
    public bool IsGraphicsTab => TabIndex == 1;
    public bool IsAdvancedTab => TabIndex == 2;
    public bool IsReposTab => TabIndex == 3;

    /// <summary>The Dalamud Repos tab only exists while Dalamud is on.</summary>
    public bool ShowReposTab => AppHost.Get("dalamud_enabled", OffOn[0]) == "ON";

    /// <summary>
    /// Signs in to Steam and fetches Lossless.dll, reporting progress through the callback. True when a
    /// new copy was downloaded. Set by MainViewModel, which owns the Steam sign-in overlays.
    /// </summary>
    public Func<Action<string>, Task<bool>>? FetchLossless { get; set; }

    private bool _fetchingLossless;

    /// <summary>Second tap confirms deleting an imported driver, like the in-game menu's Exit row.</summary>
    private bool _removeArmed;

    partial void OnTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsGeneralTab));
        OnPropertyChanged(nameof(IsGraphicsTab));
        OnPropertyChanged(nameof(IsAdvancedTab));
        OnPropertyChanged(nameof(IsReposTab));
        Rebuild();
    }

    public void Open()
    {
        Status = "";
        OnPropertyChanged(nameof(ShowReposTab));
        if (IsReposTab && !ShowReposTab)
            TabIndex = 0;
        Rebuild();
        IsOpen = true;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void SelectTab(string index) => TabIndex = int.TryParse(index, out var i) ? i : 0;

    // ---- Rows ---------------------------------------------------------------------------------

    private void Rebuild()
    {
        _removeArmed = false;
        Rows.Clear();
        switch (TabIndex)
        {
            case 1: BuildGraphics(); break;
            case 2: BuildAdvanced(); break;
            case 3: BuildRepos(); break;
            default: BuildGeneral(); break;
        }
        foreach (var row in Rows.OfType<SettingsRow>())
            row.Icon ??= Views.Icons.ForSetting(row.Title);
    }

    private void BuildGeneral()
    {
        Rows.Add(new SettingsHeader("Game"));

        var gamePath = MainViewModel.GamePath;
        // GameLocation opens a file rather than stat-ing one: without the storage permission the
        // metadata of an install reads back fine while nothing in it can be opened, so an existence
        // check here would report a healthy folder that cannot be launched.
        var game = GameLocation.Check(gamePath);
        var location = new SettingsRow("Game install location", PickGameFolder) { Value = "Change" };
        location.Detail = gamePath + "\n" + game.Message;
        location.IsWarning = !game.IsPresent;
        Rows.Add(location);

        Rows.Add(new SettingsHeader("Plugins"));
        // Like Choice(), but the Dalamud Repos tab appears and disappears with it.
        SettingsRow? dalamud = null;
        dalamud = new SettingsRow("Dalamud", () =>
        {
            dalamud!.Value = LabelOf(AppHost.Cycle("dalamud_enabled", OffOn), OffOn, OffOnLabels);
            OnPropertyChanged(nameof(ShowReposTab));
        })
        {
            Value = LabelOf(AppHost.Get("dalamud_enabled", OffOn[0]), OffOn, OffOnLabels),
            Detail = "Loads the Dalamud plugin framework into the game. Downloads Dalamud on first use; "
                     + "falls back to a plain launch if it doesn't support the installed game version yet.",
        };
        Rows.Add(dalamud);

        Rows.Add(Choice("helper_plugin", "In-game helper", OnOff, OnOffLabels,
            "Installs XIVLauncher Android Helper, a small plugin that shows this device's battery in the game's "
            + "server info bar. Updated through Dalamud like any other plugin."));

        SettingsRow? safeMode = null;
        safeMode = new SettingsRow("Run in safe mode", () =>
        {
            MainViewModel.SafeModeNextLaunch = !MainViewModel.SafeModeNextLaunch;
            ShowSafeMode(safeMode!);
        });
        ShowSafeMode(safeMode);
        Rows.Add(safeMode);
    }

    private static void ShowSafeMode(SettingsRow row)
    {
        var on = MainViewModel.SafeModeNextLaunch;
        row.Value = on ? "Next launch" : "Off";
        row.IsWarning = on;
        row.Detail = "Starts the game with Dalamud but every plugin disabled, for when a plugin makes the game crash. "
            + "Turn the bad plugin off in /xlplugins, then launch normally. Applies to the next launch only.";
    }

    private void BuildGraphics()
    {
        Rows.Add(new SettingsHeader("Vulkan driver"));

        var drivers = GraphicsDrivers.All();
        var selected = GraphicsDrivers.Selected(drivers);
        SettingsRow? driverRow = null;
        driverRow = new SettingsRow("Driver", () =>
        {
            var list = GraphicsDrivers.All();
            var index = list.FindIndex(d => d.Directory == GraphicsDrivers.Selected(list).Directory);
            var next = list[(index + 1) % list.Count];
            GraphicsDrivers.Select(next);
            Rebuild(); // the Remove row appears and disappears with the selection
        });
        driverRow.Value = selected.Label;
        driverRow.Detail = drivers.Count == 1
            ? selected.Detail + "\nImport another below if the game renders wrongly or will not start."
            : selected.Detail + $"\nTap to switch ({drivers.Count} installed).";
        Rows.Add(driverRow);

        Rows.Add(new SettingsRow("Import a driver", ImportDriver)
        {
            Value = "Pick file",
            Detail = "An AdrenoTools .zip (a Turnip or Mesa build, the same packages Winlator and GameNative take).",
        });

        if (!selected.IsBundled)
        {
            Rows.Add(new SettingsRow("Remove this driver", () =>
            {
                if (!_removeArmed)
                {
                    _removeArmed = true;
                    Status = $"Tap again to delete {selected.Label}.";
                    return;
                }
                try
                {
                    GraphicsDrivers.Remove(selected);
                    Status = $"Removed {selected.Label}; back on the bundled driver.";
                }
                catch (Exception ex)
                {
                    Status = $"Could not remove it: {ex.GetType().Name}: {ex.Message}";
                }
                Rebuild();
            })
            {
                Value = "Delete",
                Detail = "Deletes the imported package. The bundled driver cannot be removed.",
                IsWarning = true,
            });
        }

        Rows.Add(new SettingsHeader("Output"));
        Rows.Add(Choice("resolution", "Game resolution",
            new[] { "1280x720", "1560x720", "1170x540", "960x540" },
            new[] { "1280x720", "1560x720 (19.5:9)", "1170x540 (19.5:9)", "960x540" },
            "The Wine desktop the game renders into; the display scales it to the panel. Lower is faster and cooler."));
        Rows.Add(Choice("fps_cap_hz", "Frame rate cap",
            new[] { "30", "60", "0" }, new[] { "30 fps", "60 fps", "Uncapped" },
            "DXVK's limiter. A cap the device can actually hold beats an uncapped rate it cannot."));
        Rows.Add(Choice("renderer", "Renderer",
            new[] { "VULKAN", "GL" }, new[] { "Vulkan", "OpenGL (fallback)" },
            "How the app itself presents the game's window. Vulkan hands the buffer to the display with no copy; "
            + "OpenGL is the fallback for a device the Vulkan path cannot drive."));

        Rows.Add(new SettingsHeader("Compatibility"));
        Rows.Add(Choice("present_mode", "Present mode",
            new[] { "MAILBOX", "FIFO", "IMMEDIATE" },
            new[] { "Mailbox (low latency)", "FIFO (v-synced)", "Immediate (may tear)" },
            "Mailbox is the default. Try FIFO on a driver that stutters or tears."));
        Rows.Add(Choice("bcn_emulation", "Decompress BC textures",
            new[] { "AUTO", "ON", "OFF" }, new[] { "Auto", "Always", "Never" },
            "Transcodes the game's BC-compressed textures on the CPU. Needed on a GPU with no native BC support; "
            + "pure overhead on one that has it (Adreno does, so Auto skips it)."));
        Rows.Add(Choice("shader_cache", "Shader cache", OnOff, OnOffLabels,
            "Keeps compiled pipelines on disk. Leave on: a cold cache costs minutes of CPU and a lot of heat on every "
            + "launch. Turn it off only to rule the cache out after a driver change."));

        BuildFrameGeneration();

        // FFXIV.cfg only exists once the game has run, so the row shows greyed out until then.
        var config = GameSettingsPreset.ConfigPath(AppHost.FilesDir);
        Rows.Add(new SettingsHeader("Game settings"));
        SettingsRow? optimize = null;
        optimize = new SettingsRow("Optimize game settings", () =>
        {
            try
            {
                var changed = GameSettingsPreset.Apply(config);
                optimize!.Value = "Done";
                Status = changed == 0 ? "Game settings were already optimized." : $"Optimized {changed} game settings.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                optimize!.Value = "Failed";
                Status = $"Could not update FFXIV.cfg: {ex.Message}";
            }
        })
        {
            Value = "Apply",
            Detail = "Set FFXIV's graphic options to a preset tuned for 30 FPS with the least heat on a phone.",
            IsAvailable = File.Exists(config),
        };
        Rows.Add(optimize);
    }

    private void BuildFrameGeneration()
    {
        // lsfg-vk extracts its shaders from the player's own Lossless.dll, so nothing else here works
        // until that has been downloaded. xla.XlaSettings also checks for the file before arming the layer.
        Rows.Add(new SettingsHeader("Frame generation"));
        var installed = LosslessFetch.IsInstalled(AppHost.FilesDir);
        Rows.Add(new SettingsRow("Lossless Scaling", DownloadLossless)
        {
            Value = _fetchingLossless ? "Downloading" : installed ? "Update" : "Download",
            IsAvailable = !_fetchingLossless && FetchLossless != null,
        });
        if (!installed)
            return;

        Rows.Add(Choice("lsfg_enabled", "Frame generation", OffOn, OffOnLabels, ""));
        Rows.Add(Choice("lsfg_multiplier", "Multiplier",
            new[] { "2", "3", "4" }, new[] { "2x", "3x", "4x" }, ""));
        Rows.Add(Choice("lsfg_flow_scale", "Flow scale",
            new[] { "0.80", "1.00", "0.65", "0.50" }, new[] { "80%", "100%", "65%", "50%" }, ""));
        Rows.Add(Choice("lsfg_performance", "Performance mode", OnOff, OnOffLabels, ""));
    }

    private async Task DownloadLossless()
    {
        if (FetchLossless == null || _fetchingLossless)
            return;

        _fetchingLossless = true;
        Rebuild();
        try
        {
            // Progress arrives from SteamKit's threads.
            var downloaded = await FetchLossless(m => Dispatcher.UIThread.Post(() => Status = m));
            Status = downloaded ? "Lossless.dll downloaded." : "Lossless.dll is up to date.";
        }
        catch (OperationCanceledException)
        {
            Status = "Steam sign-in cancelled.";
        }
        catch (Exception ex)
        {
            // SteamSignInException carries wording meant for the player; anything else is a plain failure.
            Status = ex is SteamSignInException ? ex.Message : $"Download failed: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            _fetchingLossless = false;
            if (IsGraphicsTab)
                Rebuild();
        }
    }

    private void BuildAdvanced()
    {
        Rows.Add(new SettingsHeader("x86 emulation (FEXCore)"));
        Rows.Add(Choice("fex_preset", "Preset",
            new[] { "INTERMEDIATE", "PERFORMANCE", "COMPATIBILITY" },
            new[] { "Intermediate", "Performance", "Compatibility" },
            "How faithfully FEX emulates x86 memory ordering. Intermediate is what runs the game here; "
            + "Performance drops the remaining barriers (faster, can break); Compatibility keeps them all (slower, cooler code paths)."));
        Rows.Add(Choice("fex_multiblock", "Multi-block recompilation", OnOff, OnOffLabels,
            "Lets FEX compile past a branch into one block. Faster; off is a debugging step for a device that crashes early."));

        Rows.Add(new SettingsHeader("Wine"));
        Rows.Add(Choice("esync", "Esync", OnOff, OnOffLabels,
            "Eventfd-based synchronisation instead of the server round trip. Much faster; off if threads deadlock."));
        Rows.Add(Choice("dxvk_async", "DXVK async shaders", OnOff, OnOffLabels,
            "Draws with a stand-in pipeline while the real one compiles. Hides compile stutter, can briefly show "
            + "untextured geometry."));
        Rows.Add(Choice("wine_log", "Wine log",
            new[] { "OFF", "ERRORS", "FULL" }, new[] { "Off", "Errors only", "Everything" },
            "Enable wine logging (Slow). Turn it on when the game will not start at all to see why."));
    }

    private void BuildRepos()
    {
        var config = DalamudRepos.ConfigPath(AppHost.FilesDir);
        Rows.Add(new SettingsHeader("Custom plugin repos"));

        IReadOnlyList<(string Url, bool IsEnabled)> repos;
        try
        {
            repos = DalamudRepos.List(config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status = ex.Message;
            repos = Array.Empty<(string, bool)>();
        }

        foreach (var (url, enabled) in repos)
            Rows.Add(new RepoRow(url, enabled, () => RemoveRepo(config, url)));
        if (repos.Count == 0)
            Rows.Add(new SettingsRow("No custom repos yet", () => { }) { IsAvailable = false });

        SettingsRow? add = null;
        add = new SettingsRow("Add repo", () =>
        {
            if (Rows.OfType<RepoInputRow>().Any())
                return;
            Rows.Insert(Rows.IndexOf(add!) + 1, new RepoInputRow(input => AddRepo(config, input), Rebuild));
        })
        {
            Detail = "Paste the link to a plugin repo, the same one you'd add in Dalamud's settings.",
        };
        Rows.Add(add);

        Rows.Add(new SettingsHeader("Import"));
        Rows.Add(new SettingsRow("Import from dalamudConfig.json", () => ImportRepos(config))
        {
            Value = "Pick file",
            Detail = "Adds every repo from a dalamudConfig.json copied from your PC that isn't already here.",
        });
    }

    private void AddRepo(string config, RepoInputRow input)
    {
        try
        {
            switch (DalamudRepos.Add(config, input.Text))
            {
                case AddRepoResult.Invalid:
                    input.Error = "That isn't a web link.";
                    return;
                case AddRepoResult.Duplicate:
                    input.Error = "That repo is already added.";
                    return;
            }
            Status = "Repo added.";
            Rebuild();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            input.Error = ex.Message;
        }
    }

    private void RemoveRepo(string config, string url)
    {
        try
        {
            Status = DalamudRepos.Remove(config, url) ? "Repo removed." : "That repo was already gone.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status = ex.Message;
        }
        Rebuild();
    }

    private async Task ImportRepos(string config)
    {
        if (AppHost.PickFile == null)
        {
            Status = "No file picker on this platform.";
            return;
        }
        PickedFile? file = null;
        try
        {
            file = await AppHost.PickFile();
            if (file == null)
                return;
            var (added, skipped) = DalamudRepos.Import(config, file.Path);
            Status = added == 0
                ? $"Nothing new in {file.Name}; every repo is already here."
                : $"Added {added} repo{(added == 1 ? "" : "s")}" + (skipped > 0 ? $", {skipped} were already here." : ".");
            Rebuild();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status = ex.Message;
        }
        finally
        {
            // The picker copied the file out of the content provider; nothing needs the copy now.
            if (file != null)
            {
                try { File.Delete(file.Path); }
                catch (Exception) { /* the cache dir is Android's to reclaim */ }
            }
        }
    }

    /// <summary>One setting the row cycles through, with the value and its explanation shown in place.</summary>
    private static SettingsRow Choice(string key, string title, string[] options, string[] labels, string detail)
    {
        SettingsRow? row = null;
        row = new SettingsRow(title, () => row!.Value = LabelOf(AppHost.Cycle(key, options), options, labels));
        row.Value = LabelOf(AppHost.Get(key, options[0]), options, labels);
        row.Detail = detail;
        return row;
    }

    private static string LabelOf(string value, string[] options, string[] labels)
    {
        var index = Array.IndexOf(options, value);
        return index >= 0 && index < labels.Length ? labels[index] : value;
    }

    // ---- Pickers ------------------------------------------------------------------------------

    private async Task PickGameFolder()
    {
        if (AppHost.PickFolder == null)
        {
            Status = "No folder picker on this platform.";
            return;
        }
        try
        {
            var path = await AppHost.PickFolder();
            if (path == null)
                return;
            var picked = GameLocation.Check(path);
            if (picked.Status == GameStatus.Unreadable)
            {
                Status = picked.Message;
                return;
            }

            // An empty folder is a valid choice too: it is where the game will be installed. It only has to
            // be writable, which a removable SD card is not until the user allows All-files access.
            if (!picked.IsPresent && !GameLocation.CanWrite(path))
            {
                if (AppHost.RequestAllFilesAccess != null)
                {
                    Status = "To install to this folder, allow XIVLauncher to manage all files, then come back.";
                    await AppHost.RequestAllFilesAccess();
                }
                if (!GameLocation.CanWrite(path))
                {
                    Status = $"XIVLauncher cannot write to {path}. Allow \"All files access\" for XIVLauncher, "
                             + "or pick a folder on internal storage.";
                    return;
                }
            }

            GameLocation.Use(path);
            Status = picked.IsPresent
                ? "Game location saved."
                : "Game location saved. There is no game here yet - Log in & Play will offer to install it.";
            Rebuild();
        }
        catch (Exception ex)
        {
            // The picker throws with a sentence meant for the user when the folder is not on storage.
            Status = ex.Message;
        }
    }

    private async Task ImportDriver()
    {
        if (AppHost.PickFile == null)
        {
            Status = "No file picker on this platform.";
            return;
        }

        PickedFile? file = null;
        try
        {
            file = await AppHost.PickFile();
            if (file == null)
                return;
            Status = $"Unpacking {file.Name}...";
            var driver = await Task.Run(() => GraphicsDrivers.Import(file.Path));
            GraphicsDrivers.Select(driver);
            Status = $"Installed and selected {driver.Label}.";
            Rebuild();
        }
        catch (Exception ex)
        {
            // The message is written for the user: GraphicsDrivers.Import throws with the reason the
            // archive is not a driver package, and everything else is an IO or zip failure.
            Status = "Not installed - " + ex.Message;
        }
        finally
        {
            // The picker copied the archive out of the content provider to read it; nothing needs it now.
            if (file != null)
            {
                try { File.Delete(file.Path); }
                catch (Exception) { /* the cache dir is Android's to reclaim */ }
            }
        }
    }

    // ---- Game folder ---------------------------------------------------------------------------


}
