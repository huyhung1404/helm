using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Ssh;

public enum MenuState
{
    /// <summary>Not connected, or not opened yet.</summary>
    Idle,

    Loading,

    Ready,

    /// <summary>The server has no menu at the path (or it cannot run).</summary>
    Missing,

    /// <summary>The menu answered something Helm cannot use.</summary>
    Failed,
}

/// <summary>An item in the menu's list.</summary>
public sealed class MenuEntry(MenuItem item, string group)
{
    public MenuItem Item { get; } = item;

    public string Group { get; } = group;

    public string Title => Item.Title;

    public bool IsView => Item.Kind == MenuItemKind.View;

    public bool IsDangerous => Item.Danger == MenuDanger.High;

    /// <summary>What screen readers and the list's automation read.</summary>
    public override string ToString() => Title;
}

/// <summary>One parameter's field in the form.</summary>
public sealed partial class MenuParamField : ObservableObject
{
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string? _error;

    public MenuParamField(MenuParam param)
    {
        Param = param;
        foreach (var choice in param.Choices) Choices.Add(choice);
        _value = param.Default ?? (param.Type == MenuParamType.Bool ? "false" : "");
    }

    public MenuParam Param { get; }

    public string Title => Param.Title;

    public ObservableCollection<string> Choices { get; } = [];

    public bool IsChoice => Param.Type == MenuParamType.Choice;

    public bool IsBool => Param.Type == MenuParamType.Bool;

    public bool IsText => Param.Type is MenuParamType.Text or MenuParamType.Number;

    public bool BoolValue
    {
        get => Value == "true";
        set => Value = value ? "true" : "false";
    }

    partial void OnValueChanged(string value)
    {
        Error = null;
        OnPropertyChanged(nameof(BoolValue));
    }
}

/// <summary>A button on a table row.</summary>
public sealed record MenuRowAction(string Id, string Title);

/// <summary>A table row as the page shows it: its cells by column id and its actions.</summary>
public sealed class MenuTableRow(MenuRow row, IReadOnlyList<MenuRowAction> actions)
{
    public MenuRow Row { get; } = row;

    public IReadOnlyList<MenuRowAction> Actions { get; } = actions;

    public string this[string column] => Row.Cells.GetValueOrDefault(column, "");

    public override string ToString() => Row.Key ?? string.Join(" ", Row.Cells.Values);
}

/// <summary>
/// A server's own menu (see <see cref="SshMenu"/>), for the session shown: its items, the form of the one chosen and
/// what it printed. The menu runs on the server; Helm only shows it, checks what goes in and asks before dangerous items.
/// One item runs at a time. Members are used on the UI thread; output arrives on background threads and is posted.
/// </summary>
public sealed partial class SshMenuViewModel : ObservableObject, IDisposable
{
    /// <summary>The most output kept on screen; older text goes first.</summary>
    public const int MaxOutput = 512 * 1024;

    private readonly IUiDispatcher _ui;
    private readonly Func<string, string, string, Task<bool>> _ask;
    private readonly ILogger _logger;
    private readonly StringBuilder _buffer = new();
    private readonly object _outputGate = new();
    private IMenuRunner? _runner;
    private string _menuPath = SshMenu.DefaultPath;
    private string _server = "";
    private MenuDescription? _menu;
    private CancellationTokenSource? _run;
    private Timer? _refresh;
    private int _outputQueued;

    [ObservableProperty] private MenuState _state;
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private string _title = "Menu";
    [ObservableProperty] private MenuEntry? _selected;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _output = "";
    [ObservableProperty] private MenuTable? _table;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool? _succeeded;
    [ObservableProperty] private bool _isVisible;

    /// <param name="ask">Asks a yes/no question (title, message, confirm text) with the terminal out of the way.</param>
    public SshMenuViewModel(IUiDispatcher ui, Func<string, string, string, Task<bool>> ask, ILogger logger)
    {
        _ui = ui;
        _ask = ask;
        _logger = logger;
    }

    public ObservableCollection<MenuEntry> Entries { get; } = [];

    public ObservableCollection<MenuParamField> Fields { get; } = [];

    public ObservableCollection<MenuTableRow> Rows { get; } = [];

    public ObservableCollection<MenuStat> Stats { get; } = [];

    public bool IsReady => State == MenuState.Ready;

    public bool IsLoading => State == MenuState.Loading;

    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    public bool HasFields => Fields.Count > 0;

    public bool ShowsText => Selected?.Item.Output is MenuOutput.Stream or MenuOutput.Text || Output.Length > 0 && Table is null && Stats.Count == 0;

    public bool ShowsTable => Table is not null;

    public bool ShowsStats => Stats.Count > 0;

    public bool CanRun => IsReady && Selected is not null && !IsRunning;

    /// <summary>The table's columns, for the page to build its grid from.</summary>
    public IReadOnlyList<MenuColumn> Columns => Table?.Columns ?? [];

    partial void OnStateChanged(MenuState value)
    {
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(CanRun));
    }

    partial void OnProblemChanged(string? value) => OnPropertyChanged(nameof(HasProblem));

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanRun));

    partial void OnOutputChanged(string value) => OnPropertyChanged(nameof(ShowsText));

    partial void OnTableChanged(MenuTable? value)
    {
        OnPropertyChanged(nameof(ShowsTable));
        OnPropertyChanged(nameof(Columns));
        OnPropertyChanged(nameof(ShowsText));
    }

    partial void OnIsVisibleChanged(bool value)
    {
        if (value && State == MenuState.Idle && _runner is not null) _ = LoadAsync();
        UpdateRefresh();
    }

    partial void OnSelectedChanged(MenuEntry? value)
    {
        OnPropertyChanged(nameof(CanRun));
        ClearResult();
        Fields.Clear();
        if (value is not null)
            foreach (var param in value.Item.Params) Fields.Add(new MenuParamField(param));
        OnPropertyChanged(nameof(HasFields));
        OnPropertyChanged(nameof(ShowsText));
        if (value is null) return;
        _ = LoadChoicesAsync(value);
        // A view shows itself; an action waits for Run.
        if (value.IsView && value.Item.Params.Count == 0) _ = RunItemAsync(value.Item, new Dictionary<string, string>(), quiet: false);
        UpdateRefresh();
    }

    /// <summary>The page shows another session (or none): the menu is read again from that server when opened.</summary>
    public void Attach(IMenuRunner? runner, string? menuPath, string server)
    {
        if (ReferenceEquals(runner, _runner) && (menuPath ?? SshMenu.DefaultPath) == _menuPath) return;
        _run?.Cancel();
        _runner = runner;
        _menuPath = menuPath ?? SshMenu.DefaultPath;
        _server = server;
        _menu = null;
        Entries.Clear();
        Selected = null;
        Problem = null;
        Title = "Menu";
        State = MenuState.Idle;
        if (runner is not null && IsVisible) _ = LoadAsync();
    }

    /// <summary>Reads the menu from the server (menu describe).</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        if (_runner is not { } runner || State == MenuState.Loading) return;
        if (!SshMenu.IsValidPath(_menuPath))
        {
            Fail(MenuState.Failed, $"The menu path \"{_menuPath}\" is not a plain path. Change it in SSH settings.");
            return;
        }
        var keep = Selected?.Item.Id;
        State = MenuState.Loading;
        Problem = null;
        var text = new StringBuilder();
        var result = await runner.RunStreamingAsync(SshMenu.Command(_menuPath, "describe"), s => { lock (text) text.Append(s); }, CancellationToken.None)
            .ConfigureAwait(true);
        if (!ReferenceEquals(runner, _runner)) return;
        if (result.ExitCode is 126 or 127 || result.Error.Contains("No such file", StringComparison.Ordinal))
        {
            Fail(MenuState.Missing, result.ExitCode == 126
                ? $"{_menuPath} on this server cannot run. Make it executable: chmod +x {_menuPath}"
                : $"This server has no menu at {_menuPath}. A menu is a program there that lists what Helm can run (see Helm's docs/ssh-menu.md).");
            return;
        }
        if (!result.Succeeded)
        {
            Fail(MenuState.Failed, $"The menu failed (exit {result.ExitCode?.ToString() ?? "-"}). {result.Error}".Trim());
            return;
        }
        try
        {
            string json;
            lock (text) json = text.ToString();
            _menu = SshMenu.ParseDescription(json);
        }
        catch (MenuFormatException ex)
        {
            Fail(MenuState.Failed, ex.Message);
            return;
        }
        Title = _menu.Title;
        Entries.Clear();
        foreach (var group in _menu.Groups)
            foreach (var item in group.Items) Entries.Add(new MenuEntry(item, group.Title));
        State = MenuState.Ready;
        Selected = Entries.FirstOrDefault(e => e.Item.Id == keep);
    }

    /// <summary>Runs the chosen item with the form's values.</summary>
    [RelayCommand]
    private async Task RunAsync()
    {
        if (!CanRun || Selected is not { } entry) return;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var ok = true;
        foreach (var field in Fields)
        {
            field.Error = SshMenu.Validate(field.Param, field.Value, field.Choices);
            ok &= field.Error is null;
            if (field.Value.Length > 0) values[field.Param.Id] = field.Value;
        }
        if (!ok) return;
        await RunItemAsync(entry.Item, values, quiet: false).ConfigureAwait(true);
    }

    /// <summary>A row's button (e.g. Restart on a process): runs that item with the row's values, then shows the view again.</summary>
    public async Task RunRowActionAsync(MenuTableRow row, string actionId)
    {
        if (_menu?.Find(actionId) is not { } action || !CanRun || Selected is not { } view) return;
        var values = SshMenu.RowValues(action, row.Row);
        foreach (var param in action.Params)
        {
            var choices = param.DynamicChoices ? await FetchChoicesAsync(action, param).ConfigureAwait(true) : param.Choices;
            if (SshMenu.Validate(param, values.GetValueOrDefault(param.Id), choices) is { } error)
            {
                Status = error;
                Succeeded = false;
                return;
            }
        }
        var done = await RunItemAsync(action, values, quiet: false).ConfigureAwait(true);
        var status = Status;
        if (done && Selected == view && view.IsView)
        {
            await RunItemAsync(view.Item, new Dictionary<string, string>(), quiet: true, keepOutput: true).ConfigureAwait(true);
            Status = $"{action.Title} ({row.Row.Key ?? "row"}): {status}";
        }
    }

    [RelayCommand]
    private void Stop() => _run?.Cancel();

    /// <returns>True when the item ran and exited with 0.</returns>
    private async Task<bool> RunItemAsync(MenuItem item, IReadOnlyDictionary<string, string> values, bool quiet, bool keepOutput = false)
    {
        if (_runner is not { } runner || IsRunning) return false;
        var command = SshMenu.Command(_menuPath, SshMenu.RunArguments(item, values));
        if (!quiet && item.Danger != MenuDanger.None)
        {
            var message = item.Danger == MenuDanger.High
                ? $"{item.Confirm ?? $"Run {item.Title} on {_server}?"}\n\nOn {_server} Helm runs:\n{command}"
                : item.Confirm ?? $"Run {item.Title} on {_server}?";
            if (!await _ask(item.Title, message, "Run").ConfigureAwait(true)) return false;
        }
        var cts = _run = new CancellationTokenSource();
        IsRunning = true;
        if (!keepOutput || item.Output is MenuOutput.Stream or MenuOutput.Text) ClearResult();
        Status = quiet ? Status : "Running…";
        Succeeded = null;
        var live = item.Output is MenuOutput.Stream or MenuOutput.Text;
        var collected = new StringBuilder();
        try
        {
            var result = await runner.RunStreamingAsync(command, text =>
            {
                if (live) Append(text);
                else lock (collected) collected.Append(text);
            }, cts.Token).ConfigureAwait(true);
            FlushOutput();
            if (!ReferenceEquals(runner, _runner)) return false;
            if (result.Succeeded && !live)
            {
                string text;
                lock (collected) text = collected.ToString();
                ShowStructured(item, text);
            }
            if (!result.Succeeded && result.Error.Length > 0 && live) Append((Output.Length > 0 ? "\n" : "") + result.Error + "\n");
            FlushOutput();
            Succeeded = result.Succeeded;
            Status = result.Succeeded ? quiet ? $"Updated {DateTime.Now:HH:mm:ss}" : "Done"
                : result.ExitCode is { } code ? $"Failed (exit {code}). {(live ? "" : result.Error)}".Trim()
                : result.Error.Length > 0 ? result.Error : "Stopped.";
            return result.Succeeded;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("A menu item failed to run: {Type}", ex.GetType().Name);
            Succeeded = false;
            Status = "Could not run: " + ex.Message;
            return false;
        }
        finally
        {
            if (ReferenceEquals(_run, cts)) _run = null;
            cts.Dispose();
            IsRunning = false;
        }
    }

    private void ShowStructured(MenuItem item, string text)
    {
        try
        {
            if (item.Output == MenuOutput.Table)
            {
                var table = SshMenu.ParseTable(text);
                var actions = item.RowActions.Select(id => _menu?.Find(id)).OfType<MenuItem>().Select(a => new MenuRowAction(a.Id, a.Title)).ToList();
                Rows.Clear();
                foreach (var row in table.Rows) Rows.Add(new MenuTableRow(row, actions));
                Table = table;
            }
            else
            {
                Stats.Clear();
                foreach (var stat in SshMenu.ParseStats(text)) Stats.Add(stat);
                OnPropertyChanged(nameof(ShowsStats));
            }
        }
        catch (MenuFormatException ex)
        {
            Output = ex.Message + "\n\n" + (text.Length > 4000 ? text[..4000] : text);
        }
    }

    private async Task LoadChoicesAsync(MenuEntry entry)
    {
        foreach (var field in Fields.ToList())
        {
            if (!field.Param.DynamicChoices) continue;
            var choices = await FetchChoicesAsync(entry.Item, field.Param).ConfigureAwait(true);
            if (Selected != entry) return;
            field.Choices.Clear();
            foreach (var choice in choices) field.Choices.Add(choice);
            if (field.Value.Length > 0 && !choices.Contains(field.Value)) field.Value = "";
        }
    }

    private async Task<IReadOnlyList<string>> FetchChoicesAsync(MenuItem item, MenuParam param)
    {
        if (_runner is not { } runner) return [];
        var text = new StringBuilder();
        var result = await runner.RunStreamingAsync(SshMenu.Command(_menuPath, "choices", item.Id, param.Id), s => { lock (text) text.Append(s); }, CancellationToken.None)
            .ConfigureAwait(true);
        if (!result.Succeeded) return [];
        try
        {
            lock (text) return SshMenu.ParseChoices(text.ToString());
        }
        catch (MenuFormatException)
        {
            return [];
        }
    }

    private void ClearResult()
    {
        lock (_outputGate) _buffer.Clear();
        Output = "";
        Table = null;
        Rows.Clear();
        Stats.Clear();
        OnPropertyChanged(nameof(ShowsStats));
        Status = "";
        Succeeded = null;
    }

    /// <summary>Output arrives on a background thread; the page is updated at most once per UI turn.</summary>
    private void Append(string text)
    {
        lock (_outputGate)
        {
            _buffer.Append(text);
            if (_buffer.Length > MaxOutput) _buffer.Remove(0, _buffer.Length - MaxOutput);
        }
        if (Interlocked.Exchange(ref _outputQueued, 1) == 0) _ui.Post(FlushOutput);
    }

    private void FlushOutput()
    {
        Interlocked.Exchange(ref _outputQueued, 0);
        lock (_outputGate) Output = _buffer.ToString();
    }

    private void Fail(MenuState state, string problem)
    {
        _menu = null;
        Entries.Clear();
        Selected = null;
        Problem = problem;
        State = state;
    }

    /// <summary>A view with a refresh interval is read again while it is shown and nothing else runs.</summary>
    private void UpdateRefresh()
    {
        _refresh?.Dispose();
        _refresh = null;
        if (!IsVisible || Selected is not { IsView: true, Item.Refresh: > 0 } entry) return;
        var period = TimeSpan.FromSeconds(entry.Item.Refresh);
        _refresh = new Timer(_ => _ui.Post(() =>
        {
            if (Selected == entry && IsVisible && !IsRunning && IsReady) _ = RunItemAsync(entry.Item, new Dictionary<string, string>(), quiet: true, keepOutput: true);
        }), null, period, period);
    }

    public void Dispose()
    {
        _refresh?.Dispose();
        _run?.Cancel();
    }
}
