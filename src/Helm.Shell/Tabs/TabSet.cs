namespace Helm.Shell.Tabs;

/// <summary>
/// The tabs of every Helm window, like a browser's: each page opens at most once, in one tab of one window; opening it
/// again goes to that tab. A tab can move along its strip, out into a window of its own (<see cref="TearOff"/>) and onto
/// another window's strip. The main window keeps its tabs across restarts (<see cref="Save"/> / <see cref="Restore"/>)
/// and never stays empty; another window closes with its last tab. No UI here: each window's view shows its
/// <see cref="TabStrip"/> and opens or closes windows when <see cref="StripOpened"/> / <see cref="StripClosed"/> fire.
/// </summary>
public sealed class TabSet
{
    private readonly Func<string, string> _title;
    private readonly List<TabStrip> _strips = [];
    private readonly Dictionary<string, ShellTab> _tabs = [];
    private TabStrip? _lastActive;

    /// <param name="title">A page key's tab title.</param>
    /// <param name="homeKey">The page the main window falls back to.</param>
    public TabSet(Func<string, string> title, string homeKey = ShellPages.Home)
    {
        _title = title;
        HomeKey = homeKey;
        Main = new TabStrip(this, isMain: true);
        _strips.Add(Main);
    }

    public TabStrip Main { get; }

    public string HomeKey { get; }

    public IReadOnlyList<TabStrip> Strips => _strips;

    /// <summary>The strip of the window used last (new tabs from search or a link go there).</summary>
    public TabStrip LastActive => _lastActive is { } last && _strips.Contains(last) ? last : Main;

    /// <summary>The title of the page shown in the window used last (the black box's "Page").</summary>
    public string? ActivePageTitle => LastActive.Active?.Title;

    /// <summary>A tab was torn off: open a window for this new strip.</summary>
    public event EventHandler<TabStrip>? StripOpened;

    /// <summary>A window other than the main one lost its last tab: close it.</summary>
    public event EventHandler<TabStrip>? StripClosed;

    /// <summary>A tab was opened or chosen from outside its window (menu, search): bring its window to the front.</summary>
    public event EventHandler<ShellTab>? ShowRequested;

    public ShellTab? Find(string key) => _tabs.TryGetValue(key, out var tab) && tab.Strip is not null ? tab : null;

    /// <summary>
    /// Shows the page <paramref name="key"/>: its tab wherever it is, else a new tab at the end of
    /// <paramref name="target"/> (by default the window used last), and asks for its window to come to the front
    /// unless <paramref name="show"/> is false.
    /// </summary>
    public ShellTab Open(string key, TabStrip? target = null, bool show = true)
    {
        if (!_tabs.TryGetValue(key, out var tab)) tab = _tabs[key] = new ShellTab(key, _title(key));
        if (tab.Strip is null)
        {
            target ??= LastActive;
            target.Insert(tab, target.Tabs.Count);
        }
        tab.Strip!.Activate(tab);
        if (show) ShowRequested?.Invoke(this, tab);
        return tab;
    }

    public void Close(ShellTab tab)
    {
        if (tab.Strip is not { } strip) return;
        strip.Remove(tab);
        AfterRemoved(strip);
    }

    public void CloseOthers(ShellTab keep)
    {
        if (keep.Strip is not { } strip) return;
        foreach (var tab in strip.Tabs.Where(t => !ReferenceEquals(t, keep)).ToList()) strip.Remove(tab);
        strip.Activate(keep);
    }

    /// <summary>Moves a tab to <paramref name="target"/> at <paramref name="index"/> (dropped on its strip, or "Move to main window").</summary>
    public void Move(ShellTab tab, TabStrip target, int index)
    {
        if (tab.Strip is not { } source) return;
        if (ReferenceEquals(source, target))
        {
            source.MoveTo(tab, index);
            return;
        }
        source.Remove(tab);
        target.Insert(tab, index);
        target.Activate(tab);
        ShowRequested?.Invoke(this, tab);
        AfterRemoved(source);
    }

    public void MoveToMain(ShellTab tab) => Move(tab, Main, Main.Tabs.Count);

    /// <summary>Whether <paramref name="tab"/> can leave for a window of its own: the main window keeps at least one tab.</summary>
    public bool CanTearOff(ShellTab tab) => tab.Strip is { } strip && strip.Tabs.Count > 1;

    /// <summary>
    /// Takes the tab out of its window onto a new strip and raises <see cref="StripOpened"/> for it (the view opens a
    /// window under the cursor). Null when the tab cannot leave (<see cref="CanTearOff"/>).
    /// </summary>
    public TabStrip? TearOff(ShellTab tab)
    {
        if (!CanTearOff(tab)) return null;
        var source = tab.Strip!;
        var strip = new TabStrip(this, isMain: false);
        _strips.Add(strip);
        source.Remove(tab);
        strip.Insert(tab, 0);
        strip.Activate(tab);
        StripOpened?.Invoke(this, strip);
        AfterRemoved(source);
        return strip;
    }

    /// <summary>Puts every tab of <paramref name="from"/> onto <paramref name="target"/> (a window dropped on a strip) and closes it.</summary>
    public void Merge(TabStrip from, TabStrip target, int index)
    {
        if (ReferenceEquals(from, target) || from.IsMain) return;
        foreach (var tab in from.Tabs.ToList())
        {
            from.Remove(tab);
            target.Insert(tab, index++);
            target.Activate(tab);
        }
        AfterRemoved(from);
    }

    /// <summary>A window was closed by the user: its tabs close with it (their pages wait, as they were, for the next open).</summary>
    public void WindowClosed(TabStrip strip)
    {
        if (strip.IsMain || !_strips.Remove(strip)) return;
        foreach (var tab in strip.Tabs.ToList()) strip.Remove(tab);
        if (ReferenceEquals(_lastActive, strip)) _lastActive = null;
    }

    /// <summary>The user works in this strip's window (it was activated, a tab was shown).</summary>
    public void Touch(TabStrip strip) => _lastActive = strip;

    /// <summary>The main window's tabs and its shown one; other windows are not restored.</summary>
    public TabsState Save() => new(Main.Tabs.Select(t => t.Key).ToList(), Main.Active?.Key);

    /// <summary>
    /// Brings back the main window's tabs from the last session (keys <paramref name="known"/> does not know are
    /// skipped), showing <paramref name="start"/> if given, else the one shown last, else the first, else Home.
    /// </summary>
    public void Restore(TabsState? saved, Func<string, bool> known, string? start = null)
    {
        foreach (var key in saved?.Keys ?? [])
            if (known(key)) Open(key, Main, show: false);
        var shown = start is not null && known(start) ? start : saved?.Active is { } active && known(active) ? active : null;
        if (shown is not null) Open(shown, Main, show: false);
        else if (Main.Tabs.Count > 0) Main.Activate(Main.Tabs[0]);
        else Open(HomeKey, Main, show: false);
    }

    private void AfterRemoved(TabStrip strip)
    {
        if (strip.Tabs.Count > 0) return;
        if (strip.IsMain)
        {
            Open(HomeKey, strip, show: false);
            return;
        }
        if (!_strips.Remove(strip)) return;
        if (ReferenceEquals(_lastActive, strip)) _lastActive = null;
        StripClosed?.Invoke(this, strip);
    }
}
