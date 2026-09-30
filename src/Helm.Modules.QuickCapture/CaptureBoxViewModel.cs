using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Capture;
using Helm.Core.Settings;

namespace Helm.Modules.QuickCapture;

/// <summary>A target button in the capture box.</summary>
public sealed partial class CaptureTargetOption(ICaptureTarget target, int index) : ObservableObject
{
    [ObservableProperty] private bool _isActive;

    public ICaptureTarget Target { get; } = target;

    public string Name => Target.Name;

    /// <summary>"Ctrl+1 or /n", shown as the button's tooltip.</summary>
    public string Hint => index < 9 ? $"Ctrl+{index + 1} or /{Target.Prefix} at the start" : $"/{Target.Prefix} at the start";
}

/// <summary>
/// The capture box: the typed text, the target it goes to (a "/t" prefix or the selected button), a live preview of
/// what will be saved, and saving. UI thread only.
/// </summary>
public sealed partial class CaptureBoxViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<ICaptureTarget>> _available;
    private readonly ISettingsStore<QuickCaptureSettings> _settings;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private CaptureTargetOption? _selected;
    [ObservableProperty] private string _previewText = "";
    [ObservableProperty] private bool _canSave;
    [ObservableProperty] private string _example = "";
    [ObservableProperty] private string? _problem;

    public CaptureBoxViewModel(Func<IReadOnlyList<ICaptureTarget>> available, ISettingsStore<QuickCaptureSettings> settings)
    {
        _available = available;
        _settings = settings;
    }

    public ObservableCollection<CaptureTargetOption> Targets { get; } = [];

    public bool HasTargets => Targets.Count > 0;

    public bool HasNoTargets => Targets.Count == 0;

    /// <summary>Raised after a save that worked, with its message (the window shows it and closes).</summary>
    public event EventHandler<string>? Saved;

    /// <summary>
    /// Called each time the box opens: the targets of the tools that are on now. Text that was typed but not saved is
    /// kept; an empty box starts on the default target.
    /// </summary>
    /// <param name="text">Text to start with (e.g. from the command palette) instead of what was left in the box.</param>
    /// <param name="targetId">The target to start on instead of the default one.</param>
    public void Reset(string? text = null, string? targetId = null)
    {
        var targets = _available();
        var keep = Selected?.Target.Id;
        Targets.Clear();
        for (var i = 0; i < targets.Count; i++) Targets.Add(new CaptureTargetOption(targets[i], i));
        if (text is not null) Text = text;
        if (Text.Trim().Length == 0 || keep is null) keep = _settings.Current.DefaultTarget;
        if (targetId is not null) keep = targetId;
        // Text handed in (e.g. a video link from the palette) goes where it clearly belongs.
        else if (text is not null && CaptureRouter.Suggest(targets, text, null) is { } claimed) keep = claimed.Id;
        Selected = Targets.FirstOrDefault(t => t.Target.Id == keep) ?? Targets.FirstOrDefault();
        Problem = null;
        OnPropertyChanged(nameof(HasTargets));
        OnPropertyChanged(nameof(HasNoTargets));
        Update();
    }

    /// <summary>Tab / Shift+Tab: the next or previous target.</summary>
    public void Cycle(int delta)
    {
        if (Targets.Count == 0) return;
        var index = Selected is null ? 0 : Targets.IndexOf(Selected);
        Selected = Targets[((index + delta) % Targets.Count + Targets.Count) % Targets.Count];
    }

    /// <summary>Ctrl+1 … Ctrl+9.</summary>
    public void SelectIndex(int index)
    {
        if (index >= 0 && index < Targets.Count) Selected = Targets[index];
    }

    [RelayCommand]
    private void Pick(CaptureTargetOption? option)
    {
        if (option is not null) Selected = option;
    }

    /// <summary>Saves the text to its target. On success the box is cleared and <see cref="Saved"/> is raised.</summary>
    public bool Save()
    {
        var (target, text) = Resolve();
        if (target is null) return false;
        CaptureResult result;
        try
        {
            result = target.Capture(text);
        }
        catch (Exception ex)
        {
            // A target must not throw, but a crash here would lose the typed text: keep it and say what happened.
            result = new CaptureResult(false, $"Could not save: {ex.Message}");
        }
        if (!result.Saved)
        {
            Problem = result.Message;
            return false;
        }
        Problem = null;
        Text = "";
        Saved?.Invoke(this, result.Message);
        return true;
    }

    partial void OnTextChanged(string value)
    {
        Problem = null;
        Update();
    }

    partial void OnSelectedChanged(CaptureTargetOption? value) => Update();

    partial void OnProblemChanged(string? value) => OnPropertyChanged(nameof(HasProblem));

    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    private (ICaptureTarget? Target, string Text) Resolve() =>
        CaptureRouter.Resolve(Targets.Select(t => t.Target).ToList(), Selected?.Target, Text);

    private void Update()
    {
        var (target, text) = Resolve();
        foreach (var option in Targets) option.IsActive = ReferenceEquals(option.Target, target);
        Example = target?.Example ?? "";
        if (target is null)
        {
            PreviewText = "";
            CanSave = false;
            return;
        }
        CapturePreview preview;
        try
        {
            preview = text.Trim().Length == 0 ? new CapturePreview(false, "") : target.Preview(text);
        }
        catch (Exception ex)
        {
            preview = new CapturePreview(false, ex.Message);
        }
        PreviewText = preview.Text;
        CanSave = preview.CanSave;
    }
}
