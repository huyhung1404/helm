using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core.Links;

namespace Helm.Core.Ui;

/// <summary>
/// The links of one thing, for any tool's page: bind DataContext to a <see cref="LinksViewModel"/> (the panel hides
/// itself while it is null or has nothing to show). The search box takes the focus when picking starts.
/// </summary>
public partial class LinksPanel : UserControl
{
    public static readonly StyledProperty<bool> ShowActionsProperty = AvaloniaProperty.Register<LinksPanel, bool>(nameof(ShowActions));

    private LinksViewModel? _viewModel;

    public LinksPanel()
    {
        InitializeComponent();
        IsVisible = false;
    }

    /// <summary>Shows "Link…" and "New note" under the chips (otherwise the page starts picking from its own button).</summary>
    public bool ShowActions
    {
        get => GetValue(ShowActionsProperty);
        set => SetValue(ShowActionsProperty, value);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel = DataContext as LinksViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelChanged;
        UpdateVisibility();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowActionsProperty) UpdateVisibility();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LinksViewModel.HasItems) or nameof(LinksViewModel.IsPicking)) UpdateVisibility();
        if (e.PropertyName == nameof(LinksViewModel.IsPicking) && _viewModel is { IsPicking: true })
            Dispatcher.UIThread.Post(() => QueryBox.Focus(), DispatcherPriority.Input);
    }

    private void UpdateVisibility() =>
        IsVisible = _viewModel is { } links && (links.HasItems || links.IsPicking || ShowActions);
}
