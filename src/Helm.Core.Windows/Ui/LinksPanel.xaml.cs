using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Helm.Core.Links;

namespace Helm.Core.Ui;

/// <summary>
/// The links of one thing, for any tool's page: bind <see cref="FrameworkElement.DataContext"/> to a
/// <see cref="LinksViewModel"/> (the panel hides itself while it is null). The search box takes the focus when
/// picking starts.
/// </summary>
public partial class LinksPanel : UserControl
{
    public static readonly DependencyProperty ShowActionsProperty = DependencyProperty.Register(
        nameof(ShowActions), typeof(bool), typeof(LinksPanel), new PropertyMetadata(false));

    public LinksPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>Shows "Link…" and "New note" under the chips (otherwise the page starts picking from its own button).</summary>
    public bool ShowActions
    {
        get => (bool)GetValue(ShowActionsProperty);
        set => SetValue(ShowActionsProperty, value);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is LinksViewModel old) old.PropertyChanged -= OnViewModelChanged;
        if (e.NewValue is LinksViewModel links) links.PropertyChanged += OnViewModelChanged;
        UpdateVisibility();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LinksViewModel.HasItems) or nameof(LinksViewModel.IsPicking)) UpdateVisibility();
        if (e.PropertyName == nameof(LinksViewModel.IsPicking) && sender is LinksViewModel { IsPicking: true })
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => QueryBox.Focus());
    }

    /// <summary>Takes no room when there is nothing to show: no links, not picking, and no action buttons.</summary>
    private void UpdateVisibility() =>
        Visibility = DataContext is LinksViewModel links && (links.HasItems || links.IsPicking || ShowActions) ? Visibility.Visible : Visibility.Collapsed;

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ShowActionsProperty) UpdateVisibility();
    }
}
