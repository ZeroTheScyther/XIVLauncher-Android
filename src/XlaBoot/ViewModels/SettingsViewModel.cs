using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XIVLauncher.Common.Dalamud;
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
    private static readonly string[] PresentModes = { "MAILBOX", "FIFO", "IMMEDIATE" };
    private static readonly string[] PresentModeLabels = { "Mailbox (low latency)", "FIFO (v-synced)", "Immediate (may tear)" };

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

    /// <summary>Dalamud's branches as of the last fetch (Open refreshes them), for the branch dropdown. Null until the first one lands.</summary>
    private static IReadOnlyList<DalamudBranchMeta.Branch>? _branches;

    private bool _fetchingBranches;

    /// <summary>Second tap confirms deleting an imported driver, like the in-game menu's Exit row.</summary>
    private bool _removeArmed;
    private bool _removeDxvkArmed;

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
        _ = FetchBranches();
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void SelectTab(string index) => TabIndex = int.TryParse(index, out var i) ? i : 0;

    // ---- Rows ---------------------------------------------------------------------------------

    private void Rebuild()
    {
        _removeArmed = false;
        _removeDxvkArmed = false;
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
        BuildDalamudBranch();

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

    /// <summary>
    /// Dalamud's branch switcher, as in XIVLauncher on PC: the branches Dalamud lists publicly, "(unavailable)" on
    /// one that does not support the installed game yet. Only the track is stored; the launch looks its key up.
    /// </summary>
    private void BuildDalamudBranch()
    {
        var track = AppHost.Get("dalamud_branch", "release");
        var branches = _branches?.Where(b => !b.Hidden).ToList();
        if (branches == null || branches.Count == 0)
        {
            Rows.Add(new SettingsRow("Dalamud branch", FetchBranches)
            {
                Value = _fetchingBranches ? "Loading" : track,
            });
            return;
        }

        var row = Dropdown("Dalamud branch", branches.Select(b => b.DisplayNameWithAvailability).ToArray(),
            () => branches.FindIndex(b => b.Track == track),
            index =>
            {
                AppHost.Set("dalamud_branch", branches[index].Track);
                Rebuild(); // the description follows the branch
            });
        row.Detail = branches.FirstOrDefault(b => b.Track == track)?.Description ?? "";
        Rows.Add(row);
    }

    private async Task FetchBranches()
    {
        if (_fetchingBranches)
            return;
        _fetchingBranches = true;
        // Only the first load rebuilds: a refresh would snap shut a dropdown the player already opened.
        var first = _branches == null;
        try
        {
            _branches = (await DalamudBranchMeta.FetchBranchesAsync(MainViewModel.DalamudHttp)).ToList();
        }
        catch (Exception ex)
        {
            Status = $"Could not load Dalamud's branches: {ex.Message}";
        }
        finally
        {
            _fetchingBranches = false;
            if (first && IsOpen && IsGeneralTab)
                Rebuild();
        }
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
        var driverRow = Dropdown("Driver", drivers.Select(d => d.Label).ToArray(),
            () => drivers.FindIndex(d => d.Directory == selected.Directory),
            index =>
            {
                GraphicsDrivers.Select(drivers[index]);
                Rebuild(); // the Remove row appears and disappears with the selection
            });
        driverRow.Detail = drivers.Count == 1
            ? selected.Detail + "\nImport another below if the game renders wrongly or will not start."
            : selected.Detail;
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
            new[] { "30", "60" }, new[] { "30 fps", "60 fps" }, ""));
        Rows.Add(Choice("renderer", "Renderer",
            new[] { "VULKAN", "GL" }, new[] { "Vulkan", "OpenGL (fallback)" },
            "How the app itself presents the game's window. Vulkan hands the buffer to the display with no copy; "
            + "OpenGL is the fallback for a device the Vulkan path cannot drive."));

        Rows.Add(new SettingsHeader("Compatibility"));
        var presentMode = Choice("present_mode", "Present mode", PresentModes, PresentModeLabels,
            "Mailbox is the default. Try FIFO on a driver that stutters or tears.");
        Rows.Add(presentMode);
        Rows.Add(Choice("bcn_emulation", "Decompress BC textures",
            new[] { "AUTO", "ON", "OFF" }, new[] { "Auto", "Always", "Never" },
            "Transcodes the game's BC-compressed textures on the CPU. Needed on a GPU with no native BC support; "
            + "pure overhead on one that has it (Adreno does, so Auto skips it)."));
        Rows.Add(Choice("shader_cache", "Shader cache", OnOff, OnOffLabels,
            "Keeps compiled pipelines on disk. Leave on: a cold cache costs minutes of CPU and a lot of heat on every "
            + "launch. Turn it off only to rule the cache out after a driver change."));

        BuildFrameGeneration(presentMode);

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

    private void BuildFrameGeneration(SettingsRow presentMode)
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

        // lsfg-vk judders on FIFO and holds steady on Mailbox, so turning it on moves FIFO over. The player
        // can still pick FIFO again afterwards.
        SettingsRow? enabled = null;
        enabled = new SettingsRow("Frame generation", () =>
        {
            var value = AppHost.Cycle("lsfg_enabled", OffOn);
            enabled!.Value = LabelOf(value, OffOn, OffOnLabels);
            if (value == "ON" && AppHost.Get("present_mode", PresentModes[0]) == "FIFO")
            {
                AppHost.Set("present_mode", "MAILBOX");
                presentMode.Value = LabelOf("MAILBOX", PresentModes, PresentModeLabels);
            }
        })
        {
            Value = LabelOf(AppHost.Get("lsfg_enabled", OffOn[0]), OffOn, OffOnLabels),
        };
        Rows.Add(enabled);
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
        Rows.Add(Choice("fex_diskcache", "Code cache", OnOff, OnOffLabels,
            "Keeps translated code between launches. Faster startup; off if the game misbehaves after a cached launch."));

        Rows.Add(new SettingsHeader("DXVK"));
        var dxvkVersions = ComponentImporter.DxvkVersions(AppHost.FilesDir);
        var selected = ComponentImporter.SelectedVersion(AppHost.FilesDir);
        var dxvkLabels = dxvkVersions
            .Select(v => v == selected ? v + " (active)" : v)
            .Prepend("Stock (runtime default)")
            .ToArray();
        var selectedIndex = selected == "stock" ? 0 : dxvkVersions.ToList().IndexOf(selected) + 1;

        Rows.Add(Dropdown("DXVK version", dxvkLabels,
            () => selectedIndex,
            index =>
            {
                var version = index == 0 ? "stock" : dxvkVersions[index - 1];
                ComponentImporter.SelectVersion(AppHost.FilesDir, version);
                Status = version == "stock" ? "Using runtime default." : $"Using {version}.";
                Rebuild();
            }));
        Rows.Add(new SettingsRow("Import DXVK", ImportDxvk)
        {
            Value = "Pick zip/tar.gz",
            Detail = selected == "stock" ? "Using the runtime's built-in DXVK." : $"Active: {selected}.",
        });

        if (selected != "stock")
        {
            Rows.Add(new SettingsRow("Remove this DXVK", () =>
            {
                if (!_removeDxvkArmed)
                {
                    _removeDxvkArmed = true;
                    Status = $"Tap again to delete {selected}.";
                    return;
                }
                try
                {
                    ComponentImporter.RemoveDxvk(AppHost.FilesDir, selected);
                    Status = $"Removed {selected}; back on the runtime's DXVK.";
                }
                catch (Exception ex)
                {
                    Status = $"Could not remove it: {ex.GetType().Name}: {ex.Message}";
                }
                Rebuild();
            })
            {
                Value = "Delete",
                Detail = $"Deletes the imported DXVK {selected}.",
                IsWarning = true,
            });
        }

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
        Rows.Add(new SettingsRow("Import plugin configs", ImportPluginConfigs) { Value = "Pick zip" });
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

    private async Task ImportPluginConfigs()
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
            Status = "Importing plugin configs...";
            var path = file.Path;
            var count = await Task.Run(() => PluginConfigs.Import(path, AppHost.FilesDir));
            Status = $"Imported configs for {count} plugin{(count == 1 ? "" : "s")}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status = ex is InvalidDataException ? ex.Message : "Could not import: " + ex.Message;
        }
        finally
        {
            if (file != null)
            {
                try { File.Delete(file.Path); }
                catch (Exception) { /* the cache dir is Android's to reclaim */ }
            }
        }
    }

    /// <summary>
    /// One setting, with the value and its explanation shown in place. An on/off pair flips with a tap;
    /// anything else is a dropdown.
    /// </summary>
    private SettingsRow Choice(string key, string title, string[] options, string[] labels, string detail)
    {
        SettingsRow row;
        if (labels == OnOffLabels || labels == OffOnLabels)
        {
            SettingsRow? flip = null;
            flip = new SettingsRow(title, () => flip!.Value = LabelOf(AppHost.Cycle(key, options), options, labels));
            flip.Value = LabelOf(AppHost.Get(key, options[0]), options, labels);
            row = flip;
        }
        else
        {
            row = Dropdown(title, labels, () => Array.IndexOf(options, AppHost.Get(key, options[0])),
                index => AppHost.Set(key, options[index]));
        }
        row.Detail = detail;
        return row;
    }

    /// <summary>
    /// A row whose tap lists its values as cards right under it, the current one ticked; picking one stores it
    /// through <paramref name="pick"/> and closes the list. <paramref name="selected"/> is -1 for a stored value
    /// that is no longer an option, which launches as the first one, so the first is shown.
    /// </summary>
    private SettingsRow Dropdown(string title, IReadOnlyList<string> labels, Func<int> selected, Action<int> pick)
    {
        SettingsRow? row = null;
        row = new SettingsRow(title, () =>
        {
            var wasOpen = row!.IsExpanded;
            CloseDropdowns();
            if (wasOpen)
                return;
            row.IsExpanded = true;
            var at = Rows.IndexOf(row) + 1;
            var current = Math.Max(selected(), 0);
            for (var i = 0; i < labels.Count; i++)
            {
                var index = i;
                Rows.Insert(at + i, new SettingsOption(labels[i], i == current, () =>
                {
                    CloseDropdowns();
                    row.Value = labels[index];
                    pick(index);
                }));
            }
        })
        {
            IsDropdown = true,
            Value = labels[Math.Max(selected(), 0)],
        };
        return row;
    }

    private void CloseDropdowns()
    {
        foreach (var option in Rows.OfType<SettingsOption>().ToList())
            Rows.Remove(option);
        foreach (var row in Rows.OfType<SettingsRow>())
            row.IsExpanded = false;
    }

    private static string LabelOf(string value, string[] options, string[] labels)
    {
        // A stored value that is no longer an option (a cap of 120 from an older build) launches as the
        // first one, so show that.
        var index = Array.IndexOf(options, value);
        return index >= 0 && index < labels.Length ? labels[index] : labels[0];
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
    private async Task ImportDxvk()
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
            Status = "Importing DXVK...";
            await Task.Run(() => ComponentImporter.ImportDxvk(file.Path, AppHost.FilesDir));
            Status = "Imported. Next launch uses the new DLLs.";
            Rebuild();
        }
        catch (Exception ex)
        {
            Status = "Import failed: " + ex.Message;
        }
        finally
        {
            if (file != null)
            {
                try { File.Delete(file.Path); }
                catch (Exception) { }
            }
        }
    }

    // ---- Game folder ---------------------------------------------------------------------------


}
