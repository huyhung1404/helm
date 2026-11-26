using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Helm.Core.Ui;
using Helm.Modules.NovelReader.Reports;

namespace Helm.Modules.NovelReader.Ui;

// The sheets over the page. Three follow the view model (the tapped word, the held sentence, the report form): they open
// when it opens them and close when it closes them, and closing the sheet (its X, a tap outside, Escape, Back) closes
// them there too. The others open from a button. The names and the wrong translations sit beside the text on a wide
// screen and are sheets on a phone.
public partial class NovelReaderView
{
    private SheetHandle? _word;
    private SheetHandle? _sentence;
    private SheetHandle? _report;
    private SheetHandle? _panelSheet;
    private int _heldParagraph = -1;

    /// <summary>The sheets open over the page, for the Gallery and the tests: the word's, the sentence's, the report form.</summary>
    public SheetHandle? WordSheet => _word;

    public SheetHandle? SentenceSheet => _sentence;

    public SheetHandle? ReportSheet => _report;

    /// <summary>After the page is up: the sheets the view model already has open.</summary>
    private void FollowViewModel()
    {
        if (_viewModel is null) return;
        if (_viewModel.Editor.IsOpen) ShowWord();
        if (_viewModel.HeldSentence.IsShown) ShowSentence();
        if (_viewModel.Reports?.Editor.IsOpen == true) ShowReportForm();
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WordEditorViewModel.IsOpen) || _viewModel is null) return;
        if (_viewModel.Editor.IsOpen) Dispatcher.UIThread.Post(ShowWord, DispatcherPriority.Loaded);
        else Close(ref _word);
    }

    private void OnHeldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HeldSentenceViewModel.IsShown) || _viewModel is null) return;
        if (_viewModel.HeldSentence.IsShown) Dispatcher.UIThread.Post(ShowSentence, DispatcherPriority.Loaded);
        else Close(ref _sentence);
    }

    private void OnReportEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ReportEditorViewModel.IsOpen) || _viewModel?.Reports is not { } reports) return;
        if (reports.Editor.IsOpen) Dispatcher.UIThread.Post(ShowReportForm, DispatcherPriority.Loaded);
        else Close(ref _report);
    }

    private void ShowWord()
    {
        if (_viewModel is not { Editor: { IsOpen: true } editor } || _word is { IsOpen: true } || VisualRoot is null) return;
        var handle = Sheet.Show(this, new NovelWordSheet(editor, () => editor.CloseCommand.Execute(null)), 440);
        if (handle is null) return;
        _word = handle;
        handle.Closed += () =>
        {
            if (ReferenceEquals(_word, handle)) _word = null;
            if (editor.IsOpen) editor.CloseCommand.Execute(null);
        };
    }

    private void ShowSentence()
    {
        if (_viewModel is not { HeldSentence: { IsShown: true } held } || _sentence is { IsOpen: true } || VisualRoot is null) return;
        var handle = Sheet.Show(this, new NovelSentenceSheet(held, () => held.DismissCommand.Execute(null)), 520);
        if (handle is null) return;
        _sentence = handle;
        // A phone's sheet covers the bottom of the text: the held line goes up into view.
        if (_narrow && _heldParagraph >= 0) Text.ShowHeld(_heldParagraph);
        handle.Closed += () =>
        {
            if (ReferenceEquals(_sentence, handle)) _sentence = null;
            if (held.IsShown) held.DismissCommand.Execute(null);
        };
    }

    private void ShowReportForm()
    {
        if (_viewModel?.Reports?.Editor is not { IsOpen: true } editor || _report is { IsOpen: true } || VisualRoot is null) return;
        var handle = Sheet.Show(this, new NovelReportForm(editor), 520);
        if (handle is null) return;
        _report = handle;
        handle.Closed += () =>
        {
            if (ReferenceEquals(_report, handle)) _report = null;
            // A tap outside, Escape or Back leaves the form without saving, like Cancel.
            if (editor.IsOpen) editor.CloseCommand.Execute(null);
        };
    }

    private static void Close(ref SheetHandle? handle)
    {
        var open = handle;
        handle = null;
        open?.Close();
    }

    private void CloseReaderSheets()
    {
        Close(ref _word);
        Close(ref _sentence);
        Close(ref _report);
        Close(ref _panelSheet);
    }

    private void CloseAllSheets()
    {
        CloseReaderSheets();
        Close(ref _other);
    }

    // ---- Sheets opened from a button ------------------------------------------------------------------------------------

    private SheetHandle? _other;

    private SheetHandle? ShowOther(Func<Action, Control> body, double width)
    {
        Close(ref _other);
        SheetHandle? sheet = null;
        sheet = Sheet.Show(this, body(() => sheet?.Close()), width);
        _other = sheet;
        return sheet;
    }

    /// <summary>The gear: Novel Reader's settings as a sheet over the page, not a page to navigate to.</summary>
    public SheetHandle? OpenSettings()
    {
        if (_viewModel is null || _module is null || _host is null) return null;
        return ShowOther(close => new NovelSettingsView(_viewModel, _module, _host, close), 520);
    }

    /// <summary>Voice, speed, sleep timer, what the page shows, text size.</summary>
    public SheetHandle? OpenOptions()
    {
        if (_viewModel is null) return null;
        return ShowOther(close => new NovelOptionsSheet(_viewModel, _host?.HasPointer != false && !_narrow, close, () => Dispatcher.UIThread.Post(() => OpenSettings())), 460);
    }

    /// <summary>The open novel's chapters.</summary>
    public SheetHandle? OpenChapters()
    {
        if (_viewModel is not { IsBookOpen: true }) return null;
        var height = (TopLevel.GetTopLevel(this)?.Bounds.Height ?? 800) - 300;
        return ShowOther(close => new NovelChaptersSheet(_viewModel, close, height), 480);
    }

    /// <summary>A novel's sheet: title, cover, web source, delete.</summary>
    public SheetHandle? OpenBook(BookCardViewModel card)
    {
        if (_viewModel is null || _host is null) return null;
        return ShowOther(close => new NovelBookSheet(_viewModel, card, _host, close), 480);
    }

    private void Chapters_Click(object? sender, RoutedEventArgs e) => OpenChapters();

    private void Options_Click(object? sender, RoutedEventArgs e) => OpenOptions();

    /// <summary>A paragraph right-clicked: read from it, copy it, or report it.</summary>
    private void OpenParagraphMenu(ParagraphViewModel paragraph)
    {
        if (_viewModel is not { } vm) return;
        ShowOther(close =>
        {
            Button Row(string text, Symbol icon, Action act)
            {
                var button = new Button
                {
                    Classes = { "ghost" },
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children = { new SymbolIcon { Symbol = icon, FontSize = 15 }, new TextBlock { Text = text } },
                    },
                };
                button.Click += (_, _) =>
                {
                    close();
                    act();
                };
                return button;
            }
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Classes = { "eyebrow" }, Text = "PARAGRAPH" });
            panel.Children.Add(new TextBlock { Text = paragraph.Text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxLines = 3, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, Margin = new Avalonia.Thickness(0, 0, 0, 6) });
            panel.Children.Add(Row("Read aloud from here", Symbol.Play, () => vm.ReadFrom(paragraph)));
            panel.Children.Add(Row("Copy the translation", Symbol.Copy, () => _clipboard?.SetText(paragraph.Text)));
            panel.Children.Add(Row("Copy the Chinese", Symbol.Copy, () => _clipboard?.SetText(paragraph.Chinese)));
            if (vm.Reports is { } reports && paragraph.Line is { } line)
                panel.Children.Add(Row("Report a wrong translation", Symbol.Flag, () => reports.Start(line, 0, 0)));
            return panel;
        }, 400);
    }

    // ---- Names and wrong translations: beside the text, or a sheet --------------------------------------------------------

    private void Names_Click(object? sender, RoutedEventArgs e) => OpenNames();

    private void Reports_Click(object? sender, RoutedEventArgs e) => OpenReports();

    /// <summary>Names: on a wide screen the panel beside the text opens or closes; on a phone a sheet.</summary>
    public void OpenNames()
    {
        if (_viewModel is not { } vm || _host is null) return;
        if (!_narrow)
        {
            vm.ShowNamesPanel = !vm.ShowNamesPanel;
            return;
        }
        // The list loads while the panel is "open".
        vm.ShowNamesPanel = true;
        Close(ref _panelSheet);
        SheetHandle? sheet = null;
        sheet = Sheet.Show(this, new NovelNamesView(vm, _host, () => sheet?.Close()), 440);
        _panelSheet = sheet;
        if (sheet is not null) sheet.Closed += () => vm.ShowNamesPanel = false;
    }

    public void OpenReports()
    {
        if (_viewModel?.Reports is not { } reports || _host is null) return;
        if (!_narrow)
        {
            reports.IsPanelOpen = !reports.IsPanelOpen;
            return;
        }
        reports.IsPanelOpen = true;
        Close(ref _panelSheet);
        SheetHandle? sheet = null;
        sheet = Sheet.Show(this, new NovelReportsView(reports, _host, () => sheet?.Close()), 480);
        _panelSheet = sheet;
        if (sheet is not null) sheet.Closed += () => reports.IsPanelOpen = false;
    }

    /// <summary>The panel beside the text shows what the view model has open, on a wide screen only.</summary>
    private void UpdateSidePanel()
    {
        if (_viewModel is not { } vm || _host is null) return;
        var names = vm.ShowNamesPanel && vm.IsBookOpen && !_narrow;
        var reports = vm.Reports is { IsPanelOpen: true } && vm.IsBookOpen && !_narrow;
        NamesButton.Classes.Set("selected", names);
        ReportsButton.Classes.Set("selected", reports);
        Control? content = names ? new NovelNamesView(vm, _host, () => vm.ShowNamesPanel = false)
            : reports && vm.Reports is { } r ? new NovelReportsView(r, _host, () => r.IsPanelOpen = false)
            : null;
        var kind = content?.GetType();
        if (SidePanel.Child is ScrollViewer { Content: { } shown } && shown.GetType() == kind) return;
        SidePanel.Child = content is null ? null : new ScrollViewer
        {
            Content = content,
            Padding = new Avalonia.Thickness(0, 0, 14, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        if (SidePanel.Child is ScrollViewer scroller) ScrollFade.SetEnabled(scroller, true);
        SidePanel.IsVisible = content is not null;
    }
}
