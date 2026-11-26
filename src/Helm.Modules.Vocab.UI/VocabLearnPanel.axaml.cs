using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core.Ui;

namespace Helm.Modules.Vocab.Ui;

/// <summary>The Learn tab (see the XAML); DataContext: <see cref="VocabViewModel"/>. Its keys are the page's (<see cref="VocabView"/>).</summary>
public partial class VocabLearnPanel : UserControl
{
    private VocabCardViewModel? _card;

    public VocabLearnPanel()
    {
        InitializeComponent();
        CardMenuButton.Click += (_, _) => OpenCardMenu();
        AddWordsButton.Click += (_, _) => (DataContext as VocabViewModel)?.OpenAddCommand.Execute(null);
        AddMoreButton.Click += (_, _) => (DataContext as VocabViewModel)?.OpenAddCommand.Execute(null);
        DataContextChanged += (_, _) => Watch();
    }

    /// <summary>"More for this word": I know it, hide it, open its page, edit it.</summary>
    public SheetHandle? OpenCardMenu() => DataContext is VocabViewModel vm ? VocabCardMenu.Show(this, vm) : null;

    /// <summary>The page is narrow: the kind of card goes under the title, across the row.</summary>
    public void SetNarrow(bool narrow)
    {
        Grid.SetColumn(KindBox, narrow ? 0 : 1);
        Grid.SetRow(KindBox, narrow ? 1 : 0);
        Grid.SetColumnSpan(KindBox, narrow ? 2 : 1);
        KindBox.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(16, 0, 0, 0);
    }

    private void Watch()
    {
        if (_card is not null) _card.PropertyChanged -= OnCardChanged;
        _card = (DataContext as VocabViewModel)?.Card;
        if (_card is not null) _card.PropertyChanged += OnCardChanged;
    }

    // A new card asking to type: the cursor in its box.
    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VocabCardViewModel.Text) or nameof(VocabCardViewModel.Mode))
            Dispatcher.UIThread.Post(FocusTyping, DispatcherPriority.Input);
    }

    /// <summary>The cursor in the card's box when it asks to type (a Type card, a pinyin card).</summary>
    public void FocusTyping()
    {
        if (_card is { IsTyping: true } && IsEffectivelyVisible) TypeBox.Focus();
    }
}
