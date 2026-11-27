using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;

namespace Helm.Modules.Vocab.Ui;

/// <summary>The Words tab's list (see the XAML); DataContext: <see cref="VocabViewModel"/>.</summary>
public partial class VocabWordsPanel : UserControl
{
    private VocabViewModel? _vm;
    private bool _narrow;

    public VocabWordsPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Watch();
    }

    /// <summary>Where else words are added on this platform (Quick Capture on the PC, a text selection on the phone).</summary>
    public string AddHintText
    {
        set
        {
            AddHint.Text = value;
            AddHint.IsVisible = value.Length > 0;
        }
    }

    /// <summary>The cursor in "Add a word" (Ctrl+N, the Learn tab's Add words, first run).</summary>
    public void FocusAdd()
    {
        NewWordBox.Focus();
        NewWordBox.SelectAll();
    }

    /// <summary>The cursor in the search box (Ctrl+F).</summary>
    public void FocusSearch() => SearchBox.Focus();

    /// <summary>
    /// Narrow: the search on its own line, the language and the order sharing the line under it; wide: all three in one
    /// line. The waiting card's buttons fill their row either way.
    /// </summary>
    public void SetNarrow(bool narrow)
    {
        _narrow = narrow;
        Grid.SetRow(SearchBox, narrow ? 0 : 0);
        Grid.SetColumn(SearchBox, narrow ? 0 : 1);
        Grid.SetColumnSpan(SearchBox, narrow ? 3 : 1);
        Grid.SetRow(LanguageSeg, narrow ? 1 : 0);
        Grid.SetRow(OrderBox, narrow ? 1 : 0);
        Grid.SetColumn(OrderBox, 2);
        Grid.SetColumnSpan(LanguageSeg, 1);
        ArrangeFind();
    }

    private void ArrangeFind()
    {
        // On a phone without Chinese words the order takes the whole line under the search.
        var language = LanguageSeg.IsVisible;
        Grid.SetColumn(OrderBox, _narrow && !language ? 0 : 2);
        Grid.SetColumnSpan(OrderBox, _narrow && !language ? 3 : 1);
        OrderBox.HorizontalAlignment = _narrow ? Avalonia.Layout.HorizontalAlignment.Stretch : Avalonia.Layout.HorizontalAlignment.Left;
    }

    private void Watch()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnChanged;
            _vm.List.PropertyChanged -= OnChanged;
        }
        _vm = DataContext as VocabViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnChanged;
        _vm.List.PropertyChanged += OnChanged;
        Update();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Update();

    /// <summary>The line under the title, and what else waits for the agent.</summary>
    private void Update()
    {
        if (_vm is not { } vm) return;
        var parts = new List<string> { vm.List.Count == 1 ? "1 word" : $"{vm.List.Count} words" };
        if (vm.PendingCount > 0) parts.Add($"{vm.PendingCount} waiting for an AI agent");
        if (vm.TextCount > 0) parts.Add(vm.TextCount == 1 ? "1 text to pick words from" : $"{vm.TextCount} texts to pick words from");
        Summary.Text = string.Join(" · ", parts);

        LanguageSeg.IsVisible = vm.List.ChineseCount > 0;
        var others = VocabWork.Others(vm);
        OtherWork.Text = others.Count == 0 ? "" : "Also on its list: " + string.Join(" · ", others) + ".";
        OtherWork.IsVisible = others.Count > 0;
        ArrangeFind();
    }
}
