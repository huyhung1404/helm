using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Helm.Shell.Tabs;

/// <summary>
/// One window's tabs, in order, and the one it shows. Opening, closing and moving tabs between windows goes through
/// <see cref="TabSet"/> (a page is open once across all windows); reordering and switching happen here.
/// </summary>
public sealed class TabStrip : ObservableObject
{
    private readonly ObservableCollection<ShellTab> _tabs = [];
    private ShellTab? _active;

    internal TabStrip(TabSet owner, bool isMain)
    {
        Owner = owner;
        IsMain = isMain;
        Tabs = new ReadOnlyObservableCollection<ShellTab>(_tabs);
    }

    public TabSet Owner { get; }

    /// <summary>The main window's strip: it never stays empty (Home comes back) and its tabs are remembered.</summary>
    public bool IsMain { get; }

    public ReadOnlyObservableCollection<ShellTab> Tabs { get; }

    public ShellTab? Active
    {
        get => _active;
        private set => SetProperty(ref _active, value);
    }

    /// <summary>A tab was added, removed, moved or shown.</summary>
    public event EventHandler? Changed;

    public void Activate(ShellTab tab)
    {
        if (ReferenceEquals(_active, tab) || !_tabs.Contains(tab)) return;
        if (_active is not null) _active.IsActive = false;
        Active = tab;
        tab.IsActive = true;
        Owner.Touch(this);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drags along the strip: the tab goes to <paramref name="index"/> (clamped).</summary>
    public void MoveTo(ShellTab tab, int index)
    {
        var from = _tabs.IndexOf(tab);
        if (from < 0) return;
        index = Math.Clamp(index, 0, _tabs.Count - 1);
        if (from == index) return;
        _tabs.Move(from, index);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab: the next or previous tab, wrapping around.</summary>
    public void SelectNext(int step)
    {
        if (_tabs.Count < 2 || _active is null) return;
        var index = ((_tabs.IndexOf(_active) + step) % _tabs.Count + _tabs.Count) % _tabs.Count;
        Activate(_tabs[index]);
    }

    internal void Insert(ShellTab tab, int index)
    {
        index = Math.Clamp(index, 0, _tabs.Count);
        tab.Strip = this;
        tab.IsActive = false;
        _tabs.Insert(index, tab);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes the tab off; if it was shown, the one after it (or before) is shown instead.</summary>
    internal void Remove(ShellTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;
        _tabs.RemoveAt(index);
        tab.Strip = null;
        tab.IsActive = false;
        if (ReferenceEquals(_active, tab))
        {
            Active = null;
            if (_tabs.Count > 0) Activate(_tabs[Math.Min(index, _tabs.Count - 1)]);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
