using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;

namespace Helm.Modules.Vocab.Ui;

/// <summary>The Roots tab (see the XAML); DataContext: <see cref="VocabRootsViewModel"/>.</summary>
public partial class VocabRootsPanel : UserControl
{
    private VocabRootsViewModel? _vm;

    public VocabRootsPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Watch();
    }

    private void Watch()
    {
        if (_vm is not null)
        {
            _vm.Roots.CollectionChanged -= OnRootsChanged;
            _vm.PropertyChanged -= OnChanged;
        }
        _vm = DataContext as VocabRootsViewModel;
        if (_vm is null) return;
        _vm.Roots.CollectionChanged += OnRootsChanged;
        _vm.PropertyChanged += OnChanged;
        Group();
    }

    private void OnRootsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Group();

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VocabRootsViewModel.ChineseCount) or nameof(VocabRootsViewModel.EnglishCount)) Group();
    }

    /// <summary>The page's families as cards in columns, the parts used once in their own group under them.</summary>
    private void Group()
    {
        if (_vm is null) return;
        var families = _vm.Roots.Where(r => r.Words.Count > 1).ToList();
        var singles = _vm.Roots.Where(r => r.Words.Count <= 1).ToList();
        FamiliesList.ItemsSource = families;
        SinglesList.ItemsSource = singles;
        FamiliesHead.IsVisible = families.Count > 0;
        SinglesHead.IsVisible = singles.Count > 0;
        SinglesHead.Margin = new Avalonia.Thickness(2, families.Count > 0 ? 22 : 0, 0, 10);
        LanguageSeg.IsVisible = _vm.ChineseCount > 0;
    }
}
