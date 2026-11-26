using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Threading;

namespace Helm.Modules.Habits.Ui;

/// <summary>
/// What the Habits sheet shows (see <see cref="HabitsViewModel.HasPopup"/>): marking a day, the celebration, or the
/// reason for skipped days. <see cref="HabitsView"/> opens it in a <see cref="Helm.Core.Ui.Sheet"/> and keeps it open
/// while the reason must be written.
/// </summary>
public partial class HabitsPopupView : UserControl
{
    /// <summary>True for a count above zero (the celebration's lines).</summary>
    public static IValueConverter Any { get; } = new FuncValueConverter<int, bool>(n => n > 0);

    private readonly HabitsViewModel? _viewModel;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public HabitsPopupView() => InitializeComponent();

    public HabitsPopupView(HabitsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            viewModel.PropertyChanged += OnChanged;
            Arrange();
            Dispatcher.UIThread.Post(FocusContent, DispatcherPriority.Input);
        };
        DetachedFromVisualTree += (_, _) => viewModel.PropertyChanged -= OnChanged;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(HabitsViewModel.Mark) or nameof(HabitsViewModel.ShowLapse) or nameof(HabitsViewModel.ShowMark))) return;
        Arrange();
        Dispatcher.UIThread.Post(FocusContent, DispatcherPriority.Input);
    }

    /// <summary>The day's other actions fill their row: two side by side, one alone across it, none: no row.</summary>
    private void Arrange()
    {
        var mark = _viewModel?.Mark;
        var slip = mark?.CanSlip == true;
        var unmark = mark?.WasDone == true;
        OtherActions.IsVisible = slip || unmark;
        Grid.SetColumnSpan(SlipButton, unmark ? 1 : 2);
        Grid.SetColumn(UnmarkButton, slip ? 1 : 0);
        Grid.SetColumnSpan(UnmarkButton, slip ? 1 : 2);
    }

    /// <summary>The keyboard goes where the sheet wants it: the reason box, or the slider of a day being marked.</summary>
    private void FocusContent()
    {
        if (_viewModel is null) return;
        if (_viewModel.ShowLapse) ReasonBox.Focus();
        else if (_viewModel.Mark is { AsksPercent: true }) PercentSlider.Focus();
    }
}
