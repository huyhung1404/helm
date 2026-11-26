using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Helm.Core.Ui;

namespace Helm.Modules.Vocab.Ui;

/// <summary>
/// A section of a word's page or the Learn card (How it's built, Examples, Goes with…) whose title folds it away: the
/// arrow points right when folded and turns down when open (<see cref="Motion.Turn"/>). Folded once, a section stays
/// folded for every word on this device (<see cref="VocabSettingsViewModel.SetFolded"/>, by <see cref="Key"/>), so the
/// next card looks the same. Classes <c>memo</c>, <c>py</c> and <c>warn</c> colour the title.
/// </summary>
public sealed class FoldSection : ContentControl
{
    public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<FoldSection, string>(nameof(Title), "");

    public static readonly StyledProperty<string> KeyProperty = AvaloniaProperty.Register<FoldSection, string>(nameof(Key), "");

    public static readonly StyledProperty<bool> IsOpenProperty = AvaloniaProperty.Register<FoldSection, bool>(nameof(IsOpen), true);

    /// <summary>Where the folds are kept: set once on the page, inherited by every section under it.</summary>
    public static readonly AttachedProperty<VocabSettingsViewModel?> FoldsProperty =
        AvaloniaProperty.RegisterAttached<FoldSection, Control, VocabSettingsViewModel?>("Folds", inherits: true);

    private Control? _body;
    private Panel? _chevron;

    public static void SetFolds(Control control, VocabSettingsViewModel? value) => control.SetValue(FoldsProperty, value);

    public static VocabSettingsViewModel? GetFolds(Control control) => control.GetValue(FoldsProperty);

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Names the section where it is (learn.tree, word.examples…), so its folding is remembered.</summary>
    public string Key
    {
        get => GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (e.NameScope.Find<Button>("PART_Header") is { } header) header.Click += (_, _) => Toggle();
        _body = e.NameScope.Find<Control>("PART_Body");
        _chevron = e.NameScope.Find<Panel>("PART_Chevron");
        Load();
        Show(animate: false);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FoldsProperty || change.Property == KeyProperty) Load();
        else if (change.Property == IsOpenProperty) Show(animate: true);
    }

    private void Load()
    {
        if (Key.Length > 0 && GetFolds(this) is { } folds) IsOpen = !folds.IsFolded(Key);
    }

    private void Toggle()
    {
        IsOpen = !IsOpen;
        if (Key.Length > 0) GetFolds(this)?.SetFolded(Key, !IsOpen);
    }

    private void Show(bool animate)
    {
        if (_body is not null)
        {
            _body.IsVisible = IsOpen;
            if (animate && IsOpen)
            {
                Motion.SetFromTop(_body, true);
                Motion.Enter(_body);
            }
        }
        if (_chevron is not null) Motion.SetTurn(_chevron, IsOpen);
    }
}
