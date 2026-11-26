using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Helm.Modules.Missions.Ui;

/// <summary>
/// A new mission or a new plan (see <see cref="MissionsViewModel.IsNewOpen"/>): the prompt, then the import with its
/// preview. <see cref="MissionsView"/> opens it in a <see cref="Helm.Core.Ui.Sheet"/> and closes it when the view
/// model closes the flow (created, or Close).
/// </summary>
public partial class MissionsNewView : UserControl
{
    private const double TwoColumns = 440;
    private readonly MissionsViewModel? _viewModel;
    private bool _narrow;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public MissionsNewView() => InitializeComponent();

    public MissionsNewView(MissionsViewModel viewModel)
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
        switch (e.PropertyName)
        {
            case nameof(MissionsViewModel.IsPromptOpen) or nameof(MissionsViewModel.IsReplanOpen) or nameof(MissionsViewModel.IsImportOpen):
                Dispatcher.UIThread.Post(FocusContent, DispatcherPriority.Input);
                break;
            case nameof(MissionsViewModel.IsReplan):
                Arrange();
                break;
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _narrow = e.NewSize.Width < TwoColumns;
        Arrange();
    }

    /// <summary>Fields fill their row: two equal columns on a wide sheet, one under the other on a phone.</summary>
    private void Arrange()
    {
        Place(LevelField, 0, 0);
        Place(TimeField, _narrow ? 1 : 0, _narrow ? 0 : 1);
        Place(DeadlineField, _narrow ? 2 : 1, 0);
        Place(NotesField, _narrow ? 3 : 1, _narrow ? 0 : 1);
        PromptGrid.RowDefinitions = new RowDefinitions(_narrow ? "Auto,Auto,Auto,Auto" : "Auto,Auto");
        PromptGrid.ColumnDefinitions = new ColumnDefinitions(_narrow ? "*" : "*,*");

        // A new plan keeps the mission's reward: the deadline then fills the row alone.
        var reward = _viewModel?.IsNewMission != false;
        var oneColumn = _narrow || !reward;
        Place(RewardField, 0, 0);
        Place(PreviewDeadlineField, oneColumn ? (reward ? 1 : 0) : 0, oneColumn ? 0 : 1);
        PreviewGrid.RowDefinitions = new RowDefinitions(oneColumn ? "Auto,Auto" : "Auto");
        PreviewGrid.ColumnDefinitions = new ColumnDefinitions(oneColumn ? "*" : "*,*");
        PreviewGrid.RowSpacing = oneColumn && reward ? 12 : 0;
    }

    private static void Place(Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
    }

    /// <summary>The cursor where the step wants it: the goal, what changed, or the box for the AI's answer.</summary>
    private void FocusContent()
    {
        if (_viewModel is null) return;
        if (_viewModel.IsImportOpen) ImportBox.Focus();
        else if (_viewModel.IsReplanOpen) ReasonBox.Focus();
        else if (_viewModel.IsPromptOpen && string.IsNullOrEmpty(_viewModel.PromptGoal)) GoalBox.Focus();
    }
}
