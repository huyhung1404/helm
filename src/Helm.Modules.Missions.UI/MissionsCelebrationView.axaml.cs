using System.ComponentModel;
using Avalonia.Controls;

namespace Helm.Modules.Missions.Ui;

/// <summary>
/// The celebration of a phase or a whole mission (see <see cref="MissionsViewModel.Celebration"/>), as a sheet that
/// <see cref="MissionsView"/> opens and closes with it.
/// </summary>
public partial class MissionsCelebrationView : UserControl
{
    private readonly MissionsViewModel? _viewModel;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public MissionsCelebrationView() => InitializeComponent();

    public MissionsCelebrationView(MissionsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            viewModel.PropertyChanged += OnChanged;
            Arrange();
        };
        DetachedFromVisualTree += (_, _) => viewModel.PropertyChanged -= OnChanged;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MissionsViewModel.Celebration) or nameof(MissionsViewModel.CanSaveToNotes)) Arrange();
    }

    /// <summary>
    /// The summary's buttons fill their row (one alone across it); with no reward to take, Close is the one main
    /// action.
    /// </summary>
    private void Arrange()
    {
        if (_viewModel is null) return;
        Grid.SetColumnSpan(CopyButton, _viewModel.CanSaveToNotes ? 1 : 2);
        var reward = _viewModel.Celebration?.HasReward == true;
        CloseButton.Classes.Set("pill", !reward);
        CloseButton.Classes.Set("ghost", reward);
    }
}
