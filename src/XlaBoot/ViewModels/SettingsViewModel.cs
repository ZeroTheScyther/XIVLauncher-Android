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
using XlaBoot.Imports;
using XlaBoot.Localization;
using XlaBoot.Provisioning;
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
    // Labels are read on every rebuild, so they follow a launcher language change.
    private static string[] OnOffLabels => new[] { Strings.Common_On, Strings.Common_Off };
    private static string[] OffOnLabels => new[] { Strings.Common_Off, Strings.Common_On };
    private static readonly string[] PresentModes = { "MAILBOX", "FIFO", "IMMEDIATE" };
    private static string[] PresentModeLabels =>
        new[] { Strings.Settings_PresentMailbox, Strings.Settings_PresentFifo, Strings.Settings_PresentImmediate };

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
    /// <summary>The component whose "Remove this" row was tapped once, waiting for the second tap.</summary>
    private string? _removeComponentArmed;

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
        _removeComponentArmed = null;
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
        Rows.Add(new SettingsHeader(Strings.Settings_HeaderGame));

        var gamePath = MainViewModel.GamePath;
        // GameLocation opens a file rather than stat-ing one: without the storage permission the
        // metadata of an install reads back fine while nothing in it can be opened, so an existence
        // check here would report a healthy folder that cannot be launched.
        var game = GameLocation.Check(gamePath);
        var location = new SettingsRow(Strings.Settings_GameLocation, PickGameFolder) { Value = Strings.Settings_Change };
        location.Detail = gamePath + "\n" + game.Message;
        location.IsWarning = !game.IsPresent;
        Rows.Add(location);

        // Text language only; voice language is the game's own setting (System Configuration > Sound).
        Rows.Add(Choice("game_language", Strings.Settings_GameLanguage,
            new[] { "English", "French", "German", "Japanese" },
            new[] { "English", "Français", "Deutsch", "日本語" }, ""));

        // The language names stay in their own language, so a player stuck in the wrong one can still find theirs.
        var launcherLanguages = new[] { Strings.Settings_SystemDefault, "English", "Français", "Deutsch", "日本語" };
        Rows.Add(Dropdown(Strings.Settings_LauncherLanguage, launcherLanguages,
            () => Array.IndexOf(Loc.Options, AppHost.Get("launcher_language", "system")),
            index =>
            {
                Loc.Select(Loc.Options[index]);
                Rebuild(); // these rows, in the new language; the rest of the app follows through Loc.Changed
            }));

        Rows.Add(new SettingsHeader(Strings.Settings_HeaderPlugins));
        // Like Choice(), but the Dalamud Repos tab appears and disappears with it.
        SettingsRow? dalamud = null;
        dalamud = new SettingsRow("Dalamud", () =>
        {
            dalamud!.Value = LabelOf(AppHost.Cycle("dalamud_enabled", OffOn), OffOn, OffOnLabels);
            OnPropertyChanged(nameof(ShowReposTab));
        })
        {
            Value = LabelOf(AppHost.Get("dalamud_enabled", OffOn[0]), OffOn, OffOnLabels),
            Detail = Strings.Settings_DalamudDetail,
        };
        Rows.Add(dalamud);
        BuildDalamudBranch();

        Rows.Add(Choice("helper_plugin", Strings.Settings_Helper, OnOff, OnOffLabels, Strings.Settings_HelperDetail));

        SettingsRow? safeMode = null;
        safeMode = new SettingsRow(Strings.Settings_SafeMode, () =>
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
            Rows.Add(new SettingsRow(Strings.Settings_DalamudBranch, FetchBranches)
            {
                Value = _fetchingBranches ? Strings.Settings_Loading : track,
            });
            return;
        }

        var row = Dropdown(Strings.Settings_DalamudBranch, branches.Select(b => b.DisplayNameWithAvailability).ToArray(),
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
            Status = Loc.F(Strings.Status_BranchesFailed, ex.Message);
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
        row.Value = on ? Strings.Settings_NextLaunch : Strings.Common_Off;
        row.IsWarning = on;
        row.Detail = Strings.Settings_SafeModeDetail;
    }

    private void BuildGraphics()
    {
        Rows.Add(new SettingsHeader(Strings.Settings_HeaderDriver));

        var drivers = GraphicsDrivers.All();
        var selected = GraphicsDrivers.Selected(drivers);
        var driverRow = Dropdown(Strings.Settings_Driver, drivers.Select(d => d.Label).ToArray(),
            () => drivers.FindIndex(d => d.Directory == selected.Directory),
            index =>
            {
                GraphicsDrivers.Select(drivers[index]);
                Rebuild(); // the Remove row appears and disappears with the selection
            });
        driverRow.Detail = drivers.Count == 1
            ? selected.Detail + "\n" + Strings.Settings_DriverImportHint
            : selected.Detail;
        Rows.Add(driverRow);

        Rows.Add(new SettingsRow(Strings.Settings_ImportDriver, ImportDriver)
        {
            Value = Strings.Settings_PickFile,
            Detail = Strings.Settings_ImportDriverDetail,
        });

        if (!selected.IsBundled)
        {
            Rows.Add(new SettingsRow(Strings.Settings_RemoveDriver, () =>
            {
                if (!_removeArmed)
                {
                    _removeArmed = true;
                    Status = Loc.F(Strings.Status_TapAgainDelete, selected.Label);
                    return;
                }
                try
                {
                    GraphicsDrivers.Remove(selected);
                    Status = Loc.F(Strings.Status_DriverRemoved, selected.Label);
                }
                catch (Exception ex)
                {
                    Status = Loc.F(Strings.Status_RemoveFailed, $"{ex.GetType().Name}: {ex.Message}");
                }
                Rebuild();
            })
            {
                Value = Strings.Settings_Delete,
                Detail = Strings.Settings_RemoveDriverDetail,
                IsWarning = true,
            });
        }

        Rows.Add(new SettingsHeader(Strings.Settings_HeaderOutput));
        Rows.Add(Choice("resolution", Strings.Settings_Resolution,
            new[] { "1280x720", "1560x720", "1170x540", "960x540", "1280x800", "1152x720", "864x540" },
            new[] { "1280x720", "1560x720 (19.5:9)", "1170x540 (19.5:9)", "960x540",
                "1280x800 (16:10)", "1152x720 (16:10)", "864x540 (16:10)" },
            Strings.Settings_ResolutionDetail));
        Rows.Add(Choice("fps_cap_hz", Strings.Settings_FpsCap,
            new[] { "30", "40", "60" }, new[] { "30 fps", "40 fps", "60 fps" }, ""));
        Rows.Add(Choice("renderer", Strings.Settings_Renderer,
            new[] { "VULKAN", "GL" }, new[] { "Vulkan", Strings.Settings_OpenGlFallback },
            Strings.Settings_RendererDetail));

        Rows.Add(new SettingsHeader(Strings.Settings_HeaderCompat));
        var presentMode = Choice("present_mode", Strings.Settings_PresentMode, PresentModes, PresentModeLabels,
            Strings.Settings_PresentModeDetail);
        Rows.Add(presentMode);
        Rows.Add(Choice("bcn_emulation", Strings.Settings_Bcn,
            new[] { "AUTO", "ON", "OFF" }, new[] { Strings.Common_Auto, Strings.Settings_Always, Strings.Settings_Never },
            Strings.Settings_BcnDetail));
        Rows.Add(Choice("shader_cache", Strings.Settings_ShaderCache, OnOff, OnOffLabels, Strings.Settings_ShaderCacheDetail));

        BuildFrameGeneration(presentMode);

        // FFXIV.cfg only exists once the game has run, so the row shows greyed out until then.
        var config = GameSettingsPreset.ConfigPath(AppHost.FilesDir);
        Rows.Add(new SettingsHeader(Strings.Settings_HeaderGameSettings));
        SettingsRow? optimize = null;
        optimize = new SettingsRow(Strings.Settings_Optimize, () =>
        {
            try
            {
                var changed = GameSettingsPreset.Apply(config);
                optimize!.Value = Strings.Settings_Done;
                Status = changed == 0 ? Strings.Status_AlreadyOptimized : Loc.F(Strings.Status_Optimized, changed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                optimize!.Value = Strings.Settings_Failed;
                Status = Loc.F(Strings.Status_CfgFailed, ex.Message);
            }
        })
        {
            Value = Strings.Settings_Apply,
            Detail = Strings.Settings_OptimizeDetail,
            IsAvailable = File.Exists(config),
        };
        Rows.Add(optimize);
    }

    private void BuildFrameGeneration(SettingsRow presentMode)
    {
        // lsfg-vk extracts its shaders from the player's own Lossless.dll, so nothing else here works
        // until that has been downloaded. xla.XlaSettings also checks for the file before arming the layer.
        Rows.Add(new SettingsHeader(Strings.Settings_FrameGen));
        var installed = LosslessFetch.IsInstalled(AppHost.FilesDir);
        Rows.Add(new SettingsRow("Lossless Scaling", DownloadLossless)
        {
            Value = _fetchingLossless ? Strings.Settings_Downloading : installed ? Strings.Settings_Update : Strings.Settings_Download,
            IsAvailable = !_fetchingLossless && FetchLossless != null,
        });
        if (!installed)
            return;

        // lsfg-vk judders on FIFO and holds steady on Mailbox, so turning it on moves FIFO over. The player
        // can still pick FIFO again afterwards.
        SettingsRow? enabled = null;
        enabled = new SettingsRow(Strings.Settings_FrameGen, () =>
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
        Rows.Add(Choice("lsfg_flow_scale", Strings.Settings_FlowScale,
            new[] { "0.80", "1.00", "0.65", "0.50" }, new[] { "80%", "100%", "65%", "50%" }, ""));
        Rows.Add(Choice("lsfg_performance", Strings.Settings_PerformanceMode, OnOff, OnOffLabels, ""));
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
            Status = downloaded ? Strings.Status_LosslessDownloaded : Strings.Status_LosslessUpToDate;
        }
        catch (OperationCanceledException)
        {
            Status = Strings.Steam_Cancelled;
        }
        catch (Exception ex)
        {
            // SteamSignInException carries wording meant for the player; anything else is a plain failure.
            Status = ex is SteamSignInException ? ex.Message : Loc.F(Strings.Status_DownloadFailed, $"{ex.GetType().Name}: {ex.Message}");
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
        Rows.Add(new SettingsHeader(Strings.Settings_HeaderFex));
        Rows.Add(Choice("fex_preset", Strings.Settings_Preset,
            new[] { "INTERMEDIATE", "PERFORMANCE", "COMPATIBILITY" },
            new[] { Strings.Settings_FexIntermediate, Strings.Settings_FexPerformance, Strings.Settings_HeaderCompat },
            Strings.Settings_PresetDetail));
        Rows.Add(Choice("fex_multiblock", Strings.Settings_Multiblock, OnOff, OnOffLabels, Strings.Settings_MultiblockDetail));
        Rows.Add(Choice("fex_diskcache", Strings.Settings_CodeCache, OnOff, OnOffLabels, Strings.Settings_CodeCacheDetail));
        BuildComponentRows("FEXCore", FexImport.Store(AppHost.FilesDir), RuntimeInstaller.StockFexVersion,
            Strings.Settings_FexSource, FexImport.Import);

        Rows.Add(new SettingsHeader(Strings.Settings_HeaderDxvk));
        BuildComponentRows("DXVK", DxvkImport.Store(AppHost.FilesDir), RuntimeInstaller.StockDxvkVersion,
            Strings.Settings_DxvkSource, DxvkImport.Import);

        Rows.Add(new SettingsHeader("Wine"));
        Rows.Add(Choice("esync", "Esync", OnOff, OnOffLabels, Strings.Settings_EsyncDetail));
        Rows.Add(Choice("dxvk_async", Strings.Settings_DxvkAsync, OnOff, OnOffLabels, Strings.Settings_DxvkAsyncDetail));
        Rows.Add(Choice("wine_log", Strings.Settings_WineLog,
            new[] { "OFF", "ERRORS", "FULL" }, new[] { Strings.Common_Off, Strings.Settings_ErrorsOnly, Strings.Settings_Everything },
            Strings.Settings_WineLogDetail));
    }

    private void BuildRepos()
    {
        var config = DalamudRepos.ConfigPath(AppHost.FilesDir);
        Rows.Add(new SettingsHeader(Strings.Settings_HeaderRepos));

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
            Rows.Add(new SettingsRow(Strings.Settings_NoRepos, () => { }) { IsAvailable = false });

        SettingsRow? add = null;
        add = new SettingsRow(Strings.Settings_AddRepo, () =>
        {
            if (Rows.OfType<RepoInputRow>().Any())
                return;
            Rows.Insert(Rows.IndexOf(add!) + 1, new RepoInputRow(input => AddRepo(config, input), Rebuild));
        })
        {
            Detail = Strings.Settings_AddRepoDetail,
        };
        Rows.Add(add);

        Rows.Add(new SettingsHeader(Strings.Settings_HeaderImport));
        Rows.Add(new SettingsRow(Strings.Settings_ImportRepos, () => ImportRepos(config))
        {
            Value = Strings.Settings_PickFile,
            Detail = Strings.Settings_ImportReposDetail,
        });
        Rows.Add(new SettingsRow(Strings.Settings_ImportPluginConfigs, ImportPluginConfigs) { Value = Strings.Settings_PickZip });
    }

    private void AddRepo(string config, RepoInputRow input)
    {
        try
        {
            switch (DalamudRepos.Add(config, input.Text))
            {
                case AddRepoResult.Invalid:
                    input.Error = Strings.Repo_NotALink;
                    return;
                case AddRepoResult.Duplicate:
                    input.Error = Strings.Repo_Duplicate;
                    return;
            }
            Status = Strings.Status_RepoAdded;
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
            Status = DalamudRepos.Remove(config, url) ? Strings.Status_RepoRemoved : Strings.Status_RepoGone;
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
            Status = Strings.Status_NoFilePicker;
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
                ? Loc.F(Strings.Status_ReposNothingNew, file.Name)
                : skipped > 0
                    ? Loc.F(Strings.Status_ReposAddedSkipped, added, skipped)
                    : Loc.F(Strings.Status_ReposAdded, added);
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
            Status = Strings.Status_NoFilePicker;
            return;
        }
        PickedFile? file = null;
        try
        {
            file = await AppHost.PickFile();
            if (file == null)
                return;
            Status = Strings.Status_ImportingConfigs;
            var path = file.Path;
            var count = await Task.Run(() => PluginConfigs.Import(path, AppHost.FilesDir));
            Status = Loc.F(Strings.Status_ConfigsImported, count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status = ex is InvalidDataException ? ex.Message : Loc.F(Strings.Status_ImportFailed, ex.Message);
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
        if (options == OnOff || options == OffOn)
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
            Status = Strings.Status_NoFolderPicker;
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
                    Status = Strings.Status_AllFilesAccess;
                    await AppHost.RequestAllFilesAccess();
                }
                if (!GameLocation.CanWrite(path))
                {
                    Status = Loc.F(Strings.Status_CannotWrite, path);
                    return;
                }
            }

            GameLocation.Use(path);
            Status = picked.IsPresent
                ? Strings.Status_LocationSaved
                : Strings.Status_LocationSavedEmpty;
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
            Status = Strings.Status_NoFilePicker;
            return;
        }

        PickedFile? file = null;
        try
        {
            file = await AppHost.PickFile();
            if (file == null)
                return;
            Status = Loc.F(Strings.Status_Unpacking, file.Name);
            var driver = await Task.Run(() => GraphicsDrivers.Import(file.Path));
            GraphicsDrivers.Select(driver);
            Status = Loc.F(Strings.Status_DriverInstalled, driver.Label);
            Rebuild();
        }
        catch (Exception ex)
        {
            // The message is written for the user: GraphicsDrivers.Import throws with the reason the
            // archive is not a driver package, and everything else is an IO or zip failure.
            Status = Loc.F(Strings.Status_NotInstalled, ex.Message);
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
    /// <summary>
    /// The version dropdown, import row and (for an imported version) double-tap remove row of a swappable
    /// runtime component. The runtime's own build is listed first as "&lt;version&gt; (default)".
    /// </summary>
    private void BuildComponentRows(string name, ComponentStore store, string defaultVersion, string source,
        Func<string, string?, string, string> import)
    {
        var versions = store.Versions();
        var selected = store.Selected();
        Rows.Add(Dropdown(Loc.F(Strings.Settings_ComponentVersion, name),
            versions.Select(v => v == selected ? Loc.F(Strings.Settings_ActiveSuffix, v) : v)
                .Prepend(Loc.F(Strings.Settings_DefaultSuffix, defaultVersion)).ToArray(),
            () => selected == ComponentStore.Stock ? 0 : Array.IndexOf(versions, selected) + 1,
            index =>
            {
                var version = index == 0 ? ComponentStore.Stock : versions[index - 1];
                store.Select(version);
                Status = version == ComponentStore.Stock
                    ? Loc.F(Strings.Status_ComponentDefault, name, defaultVersion)
                    : Loc.F(Strings.Status_ComponentUsing, name, version);
                Rebuild();
            }));
        Rows.Add(new SettingsRow(Loc.F(Strings.Settings_ImportComponent, name), () => ImportComponent(name, import))
        {
            Value = Strings.Settings_PickArchive,
            Detail = source,
        });
        if (selected == ComponentStore.Stock)
            return;
        Rows.Add(new SettingsRow(Loc.F(Strings.Settings_RemoveComponent, name), () =>
        {
            if (_removeComponentArmed != name)
            {
                _removeComponentArmed = name;
                Status = Loc.F(Strings.Status_TapAgainDelete, $"{name} {selected}");
                return;
            }
            try
            {
                store.Remove(selected);
                Status = Loc.F(Strings.Status_ComponentRemoved, name, selected, defaultVersion);
            }
            catch (Exception ex)
            {
                Status = Loc.F(Strings.Status_RemoveFailed, $"{ex.GetType().Name}: {ex.Message}");
            }
            Rebuild();
        })
        {
            Value = Strings.Settings_Delete,
            Detail = Loc.F(Strings.Settings_RemoveComponentDetail, name, selected),
            IsWarning = true,
        });
    }

    private async Task ImportComponent(string name, Func<string, string?, string, string> import)
    {
        if (AppHost.PickFile == null)
        {
            Status = Strings.Status_NoFilePicker;
            return;
        }
        PickedFile? file = null;
        try
        {
            file = await AppHost.PickFile();
            if (file == null)
                return;
            Status = Loc.F(Strings.Status_ImportingComponent, name);
            var version = await Task.Run(() => import(file.Path, file.Name, AppHost.FilesDir));
            Status = Loc.F(Strings.Status_ComponentImported, name, version);
            Rebuild();
        }
        catch (Exception ex)
        {
            Status = Loc.F(Strings.Status_ImportFailedReason, ex.Message);
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

    // ---- Game folder ---------------------------------------------------------------------------


}
