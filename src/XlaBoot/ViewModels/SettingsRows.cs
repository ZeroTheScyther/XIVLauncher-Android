using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace XlaBoot.ViewModels;

/// <summary>Anything that can appear in the settings list. SettingsView has a template per subclass.</summary>
public abstract partial class SettingsItem : ObservableObject
{
    [ObservableProperty]
    private string _title = "";
}

/// <summary>A section heading.</summary>
public sealed class SettingsHeader : SettingsItem
{
    public SettingsHeader(string title) => Title = title;
}

/// <summary>
/// A tappable row: a title, the current value on the right, and one line under it saying what the
/// setting is for. Tapping runs <see cref="Tap"/> — cycling to the next value, or opening a picker.
/// Everything is a row of this one kind on purpose: no ComboBox popups or CheckBoxes, which is both
/// thumb-friendly and how the in-game menu already behaves (and avoids an Avalonia.Android accessibility crash with toggle controls).
/// </summary>
public sealed partial class SettingsRow : SettingsItem
{
    [ObservableProperty]
    private string _value = "";

    [ObservableProperty]
    private string _detail = "";

    /// <summary>The icon in the card's left tile; SettingsViewModel assigns it by title (Views.Icons).</summary>
    [ObservableProperty]
    private Geometry? _icon;

    /// <summary>Draws the row in red: a destructive action, or a value that will stop the game working.</summary>
    [ObservableProperty]
    private bool _isWarning;

    /// <summary>False greys the row out and ignores taps, for an action with nothing to act on yet.</summary>
    [ObservableProperty]
    private bool _isAvailable = true;

    public ICommand Tap { get; }

    public SettingsRow(string title, Action tap)
    {
        Title = title;
        Tap = new RelayCommand(tap);
    }

    public SettingsRow(string title, Func<Task> tap)
    {
        Title = title;
        Tap = new AsyncRelayCommand(tap);
    }
}

/// <summary>One custom Dalamud plugin repo: its link, and a red trash button that removes it.</summary>
public sealed class RepoRow : SettingsItem
{
    public string Url { get; }
    public bool IsEnabled { get; }
    public ICommand Delete { get; }

    public RepoRow(string url, bool isEnabled, Action delete)
    {
        Url = url;
        IsEnabled = isEnabled;
        Title = isEnabled ? "" : "Disabled in Dalamud";
        Delete = new RelayCommand(delete);
    }
}

/// <summary>
/// The field "Add repo" opens for pasting a repo link. The one text field in settings, because a link
/// can't be picked from a list. A refused link keeps the field open with the reason under it.
/// </summary>
public sealed partial class RepoInputRow : SettingsItem
{
    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private string _error = "";

    public ICommand Confirm { get; }
    public ICommand Cancel { get; }

    public RepoInputRow(Action<RepoInputRow> confirm, Action cancel)
    {
        Confirm = new RelayCommand(() => confirm(this));
        Cancel = new RelayCommand(cancel);
    }
}
