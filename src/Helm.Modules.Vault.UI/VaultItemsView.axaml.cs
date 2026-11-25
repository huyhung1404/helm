using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Helm.Core.Ui;
using Helm.Modules.Vault.ViewModels;

namespace Helm.Modules.Vault.Ui;

/// <summary>
/// The unlocked vault: sections, search and add, the items, and the open item. Wide: the items on the left and the open
/// item beside them. Narrow (a phone): one at a time, the open item in place of the list.
/// </summary>
public partial class VaultItemsView : UserControl
{
    private bool _narrow;
    private ItemsViewModel? _items;

    public VaultItemsView() => AvaloniaXamlLoader.Load(this);

    /// <summary>A phone: one pane at a time. Set by the page from its width.</summary>
    public bool Narrow
    {
        get => _narrow;
        set
        {
            if (_narrow == value) return;
            _narrow = value;
            Apply();
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_items is not null) _items.PropertyChanged -= OnItemsChanged;
        _items = DataContext as ItemsViewModel;
        if (_items is not null) _items.PropertyChanged += OnItemsChanged;
        Apply();
    }

    private void OnItemsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ItemsViewModel.Detail)) Apply();
    }

    private Control Pane(string name) => this.FindControl<Control>(name)!;

    /// <summary>Wide: both panes side by side. Narrow: the list, or the open item with Back.</summary>
    private void Apply()
    {
        var panes = this.FindControl<Grid>("Panes");
        if (panes is null) return;
        var list = Pane("ListPane");
        var detail = Pane("DetailPane");
        var back = Pane("BackButton");
        var open = _items?.Detail is not null;
        // A phone shows the open item in place of the list; the PC picks the first item itself.
        if (_items is not null) _items.AutoSelectFirst = !_narrow;
        if (_narrow)
        {
            panes.ColumnDefinitions = ColumnDefinitions.Parse("*");
            Grid.SetColumn(detail, 0);
            var wasList = list.IsVisible;
            list.IsVisible = !open;
            detail.IsVisible = open;
            back.IsVisible = true;
            if (wasList == open) Motion.Enter(open ? detail : list);
        }
        else
        {
            panes.ColumnDefinitions = ColumnDefinitions.Parse("380,*");
            Grid.SetColumn(detail, 1);
            list.IsVisible = true;
            detail.IsVisible = true;
            back.IsVisible = false;
        }
    }

    /// <summary>Puts the caret in search, where the user types first.</summary>
    public void FocusSearch() => this.FindControl<TextBox>("SearchBox")?.Focus();
}
