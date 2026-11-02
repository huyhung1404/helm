using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Helm.Modules.Vocab;

/// <summary>The sheets only the page opens (the others follow the view model: Add words, Analysis, Edit, new analysis).</summary>
public enum VocabSheet
{
    None,
    CardMenu,
    WordMenu,
    Order,
}

/// <summary>
/// The bottom sheets over the Vocabulary page; DataContext: <see cref="VocabViewModel"/>. The menus and the order are
/// opened by the page's buttons (<see cref="Show(Control, VocabSheet)"/>); Android's back closes any sheet first.
/// </summary>
public partial class VocabSheets : UserControl
{
    private VocabSheet _shown;

    public VocabSheets()
    {
        InitializeComponent();
    }

    private VocabViewModel? ViewModel => DataContext as VocabViewModel;

    /// <summary>Opens one of the page's sheets from a button anywhere on the Vocabulary page.</summary>
    public static void Show(Control from, VocabSheet sheet) => from.FindAncestorOfType<VocabContentPage>()?.Sheets.Show(sheet);

    public void Show(VocabSheet sheet)
    {
        _shown = sheet;
        CardMenu.IsVisible = sheet == VocabSheet.CardMenu;
        WordMenu.IsVisible = sheet == VocabSheet.WordMenu;
        Order.IsVisible = sheet == VocabSheet.Order;
    }

    /// <summary>Back: closes the sheet on top, if any.</summary>
    public bool HandleBack()
    {
        if (_shown != VocabSheet.None)
        {
            Show(VocabSheet.None);
            return true;
        }
        if (ViewModel is not { } vm) return false;
        if (vm.Detail is { } detail)
        {
            if (detail.Editor.IsOpen)
            {
                detail.Editor.CancelCommand.Execute(null);
                return true;
            }
            if (detail.IsAskingRedo)
            {
                detail.IsAskingRedo = false;
                return true;
            }
        }
        if (vm.IsAddOpen)
        {
            vm.CloseAddCommand.Execute(null);
            return true;
        }
        if (vm.IsAgentOpen)
        {
            vm.CloseAgentCommand.Execute(null);
            return true;
        }
        return false;
    }

    // A tap on the dimmed page closes the sheet over it (Edit has no such tap: typing would be lost).
    private void Scrim_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        HandleBack();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Show(VocabSheet.None);

    private void Order_Tapped(object? sender, TappedEventArgs e) => Show(VocabSheet.None);

    private void CloseAdd_Click(object? sender, RoutedEventArgs e) => ViewModel?.CloseAddCommand.Execute(null);

    /// <summary>The card's word on its own page (the Words tab).</summary>
    private void OpenCardWord_Click(object? sender, RoutedEventArgs e)
    {
        Show(VocabSheet.None);
        if (ViewModel is { Card.WordId: { } id } vm) vm.Open(id);
    }

    /// <summary>The card's word opened on its page, with the editor over it.</summary>
    private void EditCardWord_Click(object? sender, RoutedEventArgs e)
    {
        Show(VocabSheet.None);
        if (ViewModel is not { Card.WordId: { } id } vm) return;
        vm.Open(id);
        vm.Detail?.Editor.BeginCommand.Execute(null);
    }
}
