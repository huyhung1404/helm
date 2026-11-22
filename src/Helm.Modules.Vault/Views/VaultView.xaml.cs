using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Helm.Modules.Vault.ViewModels;

namespace Helm.Modules.Vault.Views;

/// <summary>How much room the vault has: three columns, sections as icons only, or one column at a time.</summary>
public enum VaultLayout
{
    /// <summary>Sections with names | items | the selected item.</summary>
    Wide,
    /// <summary>Sections as icons | items | the selected item.</summary>
    Compact,
    /// <summary>Sections as a row of chips above the items; the selected item takes the whole width (Back returns).</summary>
    Narrow,
}

public partial class VaultView : UserControl
{
    /// <summary>At least this wide: sections with their names.</summary>
    public const double WideWidth = 1000;

    /// <summary>Narrower than this: one column at a time.</summary>
    public const double NarrowWidth = 720;

    public static readonly DependencyProperty IsCompactNavProperty =
        DependencyProperty.Register(nameof(IsCompactNav), typeof(bool), typeof(VaultView), new PropertyMetadata(false));

    private VaultAppViewModel? _app;
    private Point? _dragStart;
    private double _dragOffset;
    private bool _dragged;

    public VaultView()
    {
        InitializeComponent();
        Panes.SizeChanged += (_, _) => ApplyLayout();
        DataContextChanged += OnDataContextChanged;
        PreviewKeyDown += OnPreviewKeyDown;

        ChipScroller.PreviewMouseWheel += OnChipWheel;
        ChipScroller.PreviewMouseLeftButtonDown += OnChipPress;
        ChipScroller.PreviewMouseMove += OnChipDrag;
        ChipScroller.PreviewMouseLeftButtonUp += OnChipRelease;
        ChipScroller.ScrollChanged += (_, _) => UpdateChipArrows();
        ChipLeft.Click += (_, _) => ChipScroller.ScrollToHorizontalOffset(ChipScroller.HorizontalOffset - ChipScroller.ViewportWidth * 0.7);
        ChipRight.Click += (_, _) => ChipScroller.ScrollToHorizontalOffset(ChipScroller.HorizontalOffset + ChipScroller.ViewportWidth * 0.7);
    }

    /// <summary>The left column shows icons only (the names are tooltips).</summary>
    public bool IsCompactNav
    {
        get => (bool)GetValue(IsCompactNavProperty);
        set => SetValue(IsCompactNavProperty, value);
    }

    public VaultLayout Layout { get; private set; } = VaultLayout.Wide;

    /// <summary>Puts the caret where the user types first: the password on the lock screen, search in the vault.</summary>
    public void FocusFirstInput()
    {
        if (DataContext is not VaultAppViewModel app) return;
        IInputElement target = app.Screen switch
        {
            VaultScreen.Setup => NewPasswordBox,
            VaultScreen.Unlock => UnlockPasswordBox,
            _ => SearchBox,
        };
        Keyboard.Focus(target);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_app is not null) _app.Items.PropertyChanged -= OnItemsChanged;
        _app = DataContext as VaultAppViewModel;
        if (_app is not null) _app.Items.PropertyChanged += OnItemsChanged;
        ApplyLayout();
    }

    private void OnItemsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Narrow: opening an item shows it instead of the list, and Back shows the list again.
        if (e.PropertyName == nameof(ItemsViewModel.Detail) && Layout == VaultLayout.Narrow) ApplyLayout();
    }

    /// <summary>Lays the vault out for the width it has (called on every resize).</summary>
    internal void ApplyLayout() => ApplyLayout(Panes.ActualWidth);

    internal void ApplyLayout(double width)
    {
        if (width <= 0) return;
        var layout = width >= WideWidth ? VaultLayout.Wide : width >= NarrowWidth ? VaultLayout.Compact : VaultLayout.Narrow;
        Layout = layout;
        IsCompactNav = layout == VaultLayout.Compact;
        var detailOpen = _app?.Items.Detail is not null;
        if (_app is not null)
        {
            // Side by side, an item is always shown; one column at a time, it waits for a click.
            var autoSelect = layout != VaultLayout.Narrow;
            if (_app.Items.AutoSelectFirst != autoSelect)
            {
                _app.Items.AutoSelectFirst = autoSelect;
                if (autoSelect && _app.Items.SelectedCard is null && _app.IsItems) _app.Items.Refresh();
            }
        }

        switch (layout)
        {
            case VaultLayout.Wide:
            case VaultLayout.Compact:
                NavColumn.Width = new GridLength(layout == VaultLayout.Wide ? 216 : 60);
                ListColumn.Width = new GridLength(Math.Clamp(width * 0.3, 280, 360));
                DetailColumn.Width = new GridLength(1, GridUnitType.Star);
                NavPane.Visibility = Visibility.Visible;
                ListPane.Visibility = Visibility.Visible;
                DetailPane.Visibility = Visibility.Visible;
                ChipRow.Visibility = Visibility.Collapsed;
                BackButton.Visibility = Visibility.Collapsed;
                BackupDetailText.Visibility = layout == VaultLayout.Wide ? Visibility.Visible : Visibility.Collapsed;
                BackupTitleText.Visibility = BackupDetailText.Visibility;
                ListPane.BorderThickness = new Thickness(0, 0, 1, 0);
                break;
            case VaultLayout.Narrow:
                NavColumn.Width = new GridLength(0);
                NavPane.Visibility = Visibility.Collapsed;
                ChipRow.Visibility = Visibility.Visible;
                BackButton.Visibility = Visibility.Visible;
                ListPane.BorderThickness = new Thickness(0);
                ListColumn.Width = detailOpen ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
                DetailColumn.Width = detailOpen ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
                ListPane.Visibility = detailOpen ? Visibility.Collapsed : Visibility.Visible;
                DetailPane.Visibility = detailOpen ? Visibility.Visible : Visibility.Collapsed;
                Dispatcher.BeginInvoke(UpdateChipArrows);
                break;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && _app is { IsItems: true, Items.IsEditorOpen: false })
        {
            if (Layout == VaultLayout.Narrow && _app.Items.Detail is not null) _app.Items.CloseDetailCommand.Execute(null);
            Keyboard.Focus(SearchBox);
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    // The chip row has no scrollbar: the mouse wheel scrolls it sideways, dragging scrolls it (a drag is not a click on a
    // chip), and the ‹ › buttons at its edges page through it.
    private void OnChipWheel(object sender, MouseWheelEventArgs e)
    {
        if (ChipScroller.ScrollableWidth <= 0) return;
        ChipScroller.ScrollToHorizontalOffset(ChipScroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void OnChipPress(object sender, MouseButtonEventArgs e)
    {
        if (ChipScroller.ScrollableWidth <= 0) return;
        _dragStart = e.GetPosition(ChipScroller);
        _dragOffset = ChipScroller.HorizontalOffset;
        _dragged = false;
    }

    private void OnChipDrag(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var dx = e.GetPosition(ChipScroller).X - start.X;
        if (!_dragged && Math.Abs(dx) > SystemParameters.MinimumHorizontalDragDistance * 2)
        {
            _dragged = true;
            ChipScroller.CaptureMouse();
            ChipScroller.Cursor = Cursors.ScrollWE;
        }
        if (_dragged) ChipScroller.ScrollToHorizontalOffset(_dragOffset - dx);
    }

    private void OnChipRelease(object sender, MouseButtonEventArgs e)
    {
        var dragged = _dragged;
        _dragStart = null;
        _dragged = false;
        if (!dragged) return;
        ChipScroller.ReleaseMouseCapture();
        ChipScroller.Cursor = null;
        e.Handled = true; // the drag ends here: it does not click the chip under the mouse
    }

    private void UpdateChipArrows()
    {
        var more = ChipScroller.ScrollableWidth > 1;
        ChipLeft.Visibility = more && ChipScroller.HorizontalOffset > 1 ? Visibility.Visible : Visibility.Collapsed;
        ChipRight.Visibility = more && ChipScroller.HorizontalOffset < ChipScroller.ScrollableWidth - 1 ? Visibility.Visible : Visibility.Collapsed;
    }
}
