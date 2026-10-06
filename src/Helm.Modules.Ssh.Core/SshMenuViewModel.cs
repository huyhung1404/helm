using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
public sealed class MenuEntry(MenuItem item, string group, bool firstOfGroup)
{
    public MenuItem Item { get; } = item;

    public string Group { get; } = group;

    /// <summary>The first item of its group: the popup shows the group's title above it.</summary>
    public bool StartsGroup { get; } = firstOfGroup && group.Length > 0;

    public string Title => Item.Title;

    public string? Description => Item.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Item.Description);

    public bool IsDangerous => Item.Danger == MenuDanger.High;

    public bool HasParams => Item.Params.Count > 0;

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

    public override string ToString() => Title;
}

/// <summary>
/// A server's own menu (see <see cref="SshMenu"/>) as a popup over the terminal: the items it offers and, for one with
/// parameters, a small form. Choosing an item types its command into the terminal (with a leading space, which keeps
/// it out of the shell's history), so its output shows there and Ctrl+C works as usual; the menu prints text for
/// people when its output is a terminal. Helm checks every value against what the menu declared, quotes each argument
/// and asks before dangerous items. The menu is read over its own SSH channel (describe, choices), never through the
/// terminal. Members are used on the UI thread.
/// </summary>
public sealed partial class SshMenuViewModel : ObservableObject
{
    private readonly Func<string, string, string, Task<bool>> _ask;
    private readonly Action<string> _type;
    private IMenuRunner? _runner;
    private string _menuPath = SshMenu.DefaultPath;
    private string _server = "";
    private MenuDescription? _menu;

    [ObservableProperty] private MenuState _state;
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private string _title = "Menu";
    [ObservableProperty] private MenuEntry? _selected;

    /// <param name="ask">Asks a yes/no question (title, message, confirm text) with the terminal out of the way.</param>
    /// <param name="type">Types text into the terminal of the session shown.</param>
    public SshMenuViewModel(Func<string, string, string, Task<bool>> ask, Action<string> type)
    {
        _ask = ask;
        _type = type;
    }

    public ObservableCollection<MenuEntry> Entries { get; } = [];

    public ObservableCollection<MenuParamField> Fields { get; } = [];

    public bool IsReady => State == MenuState.Ready;

    public bool IsLoading => State == MenuState.Loading;

    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    /// <summary>The form of the item chosen is shown (it has parameters).</summary>
    public bool IsFormOpen => Selected is not null;

    public bool IsListOpen => IsReady && Selected is null;

    public bool CanOpen => _runner is not null;

    /// <summary>Raised after an item's command was typed into the terminal: the popup closes and the terminal gets the keys.</summary>
    public event EventHandler? Typed;

    partial void OnStateChanged(MenuState value)
    {
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(IsListOpen));
    }

    partial void OnProblemChanged(string? value) => OnPropertyChanged(nameof(HasProblem));

    partial void OnSelectedChanged(MenuEntry? value)
    {
        OnPropertyChanged(nameof(IsFormOpen));
        OnPropertyChanged(nameof(IsListOpen));
        Fields.Clear();
        if (value is null) return;
        foreach (var param in value.Item.Params) Fields.Add(new MenuParamField(param));
        _ = LoadChoicesAsync(value);
    }

    /// <summary>The page shows another session (or none): the menu is read again from that server when next opened.</summary>
    public void Attach(IMenuRunner? runner, string? menuPath, string server)
    {
        if (ReferenceEquals(runner, _runner) && (menuPath ?? SshMenu.DefaultPath) == _menuPath) return;
        _runner = runner;
        _menuPath = menuPath ?? SshMenu.DefaultPath;
        _server = server;
        _menu = null;
        Entries.Clear();
        Selected = null;
        Problem = null;
        Title = "Menu";
        State = MenuState.Idle;
        OnPropertyChanged(nameof(CanOpen));
    }

    /// <summary>The popup opens: the list shows, and the menu is read the first time.</summary>
    public async Task OpenAsync()
    {
        Selected = null;
        if (State is MenuState.Idle or MenuState.Failed or MenuState.Missing) await LoadAsync().ConfigureAwait(true);
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
        State = MenuState.Loading;
        Problem = null;
        Selected = null;
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
            Fail(MenuState.Failed, Clip($"The menu failed (exit {result.ExitCode?.ToString() ?? "-"}). {result.Error}".Trim()));
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
            for (var i = 0; i < group.Items.Count; i++) Entries.Add(new MenuEntry(group.Items[i], group.Title, i == 0));
        State = MenuState.Ready;
    }

    /// <summary>An item was chosen: one without parameters runs now; one with parameters opens its form.</summary>
    public async Task ChooseAsync(MenuEntry entry)
    {
        if (!IsReady) return;
        if (entry.HasParams)
        {
            Selected = entry;
            return;
        }
        await TypeAsync(entry.Item, new Dictionary<string, string>()).ConfigureAwait(true);
    }

    /// <summary>The form's Run: checks the values, then runs the item in the terminal.</summary>
    [RelayCommand]
    private async Task RunAsync()
    {
        if (Selected is not { } entry) return;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var ok = true;
        foreach (var field in Fields)
        {
            field.Error = SshMenu.Validate(field.Param, field.Value, field.Choices);
            ok &= field.Error is null;
            if (field.Value.Length > 0) values[field.Param.Id] = field.Value;
        }
        if (ok) await TypeAsync(entry.Item, values).ConfigureAwait(true);
    }

    /// <summary>The form's Back: the list again.</summary>
    [RelayCommand]
    private void Back() => Selected = null;

    /// <summary>The command line typed for an item (what a dangerous item's question shows).</summary>
    public string CommandFor(MenuItem item, IReadOnlyDictionary<string, string> values) => SshMenu.Command(_menuPath, SshMenu.RunArguments(item, values));

    private async Task TypeAsync(MenuItem item, IReadOnlyDictionary<string, string> values)
    {
        var command = CommandFor(item, values);
        if (item.Danger != MenuDanger.None)
        {
            var message = item.Danger == MenuDanger.High
                ? $"{item.Confirm ?? $"Run {item.Title} on {_server}?"}\n\nHelm types this into the terminal of {_server}:\n{command}"
                : item.Confirm ?? $"Run {item.Title} on {_server}?";
            if (!await _ask(item.Title, message, "Run").ConfigureAwait(true)) return;
        }
        // A leading space keeps the line out of bash's history (HISTCONTROL=ignorespace, Ubuntu's default).
        _type(" " + command + "\r");
        Selected = null;
        Typed?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadChoicesAsync(MenuEntry entry)
    {
        if (_runner is not { } runner) return;
        foreach (var field in Fields.ToList())
        {
            if (!field.Param.DynamicChoices) continue;
            var text = new StringBuilder();
            var result = await runner.RunStreamingAsync(SshMenu.Command(_menuPath, "choices", entry.Item.Id, field.Param.Id), s => { lock (text) text.Append(s); },
                CancellationToken.None).ConfigureAwait(true);
            if (Selected != entry) return;
            IReadOnlyList<string> choices;
            try
            {
                lock (text) choices = result.Succeeded ? SshMenu.ParseChoices(text.ToString()) : [];
            }
            catch (MenuFormatException)
            {
                choices = [];
            }
            field.Choices.Clear();
            foreach (var choice in choices) field.Choices.Add(choice);
            if (choices.Count == 0) field.Error = "The server offered nothing to choose.";
            else if (field.Value.Length > 0 && !choices.Contains(field.Value)) field.Value = "";
        }
    }

    private void Fail(MenuState state, string problem)
    {
        _menu = null;
        Entries.Clear();
        Selected = null;
        Problem = problem;
        State = state;
    }

    private static string Clip(string text) => text.Length <= 600 ? text : text[..600] + "…";
}
