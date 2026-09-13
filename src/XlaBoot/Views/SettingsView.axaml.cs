using System.Collections.Specialized;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XlaBoot.ViewModels;

namespace XlaBoot.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is SettingsViewModel vm)
            vm.Rows.CollectionChanged += FocusNewRepoInput;
    }

    /// <summary>"Add repo" opens a paste field; focus it so the keyboard (and its paste button) comes up at once.</summary>
    private void FocusNewRepoInput(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems?.OfType<RepoInputRow>().Any() != true)
            return;
        Dispatcher.UIThread.Post(() =>
            this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Classes.Contains("repoInput"))?.Focus(),
            DispatcherPriority.Loaded);
    }
}
