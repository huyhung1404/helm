using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Ai;

namespace Helm.App.Android.Views;

/// <summary>General › AI features; its DataContext is the shared <see cref="Helm.Ai.AiFeaturesViewModel"/>.</summary>
public partial class AiFeaturesView : UserControl
{
    private AiFeaturesViewModel? _viewModel;

    public AiFeaturesView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel = DataContext as AiFeaturesViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelChanged;
    }

    /// <summary>A feature's detail, and the list again, start at the top (the page's scroller kept the list's place).</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AiFeaturesViewModel.Selected)) return;
        if (this.FindAncestorOfType<ScrollViewer>() is not { } scroller) return;
        scroller.Offset = default;
        // Again once the other half is laid out: its height changed what the offset may be.
        Dispatcher.UIThread.Post(() => scroller.Offset = default(Vector), DispatcherPriority.Loaded);
    }
}
