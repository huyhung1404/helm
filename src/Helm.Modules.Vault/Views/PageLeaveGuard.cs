using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WpfUiPasswordBox = Wpf.Ui.Controls.PasswordBox;

namespace Helm.Modules.Vault.Views;

/// <summary>
/// Keeps what was typed into a vault page's password boxes from staying there, readable, for whoever opens the page
/// next.
/// <para>
/// A WPF-UI password box that shows its password (the eye button) ignores every change of its password from outside:
/// the view model clearing it after an unlock, or when the vault locks, left the old password in the box, still shown,
/// the next time the lock screen appeared. So a box is hidden before the view model's value is put into it.
/// </para>
/// <para>
/// The page also forgets what was typed, and hides what was shown, whenever it stops being in front of the user: it is
/// left for another page or tab (unloaded), hidden (Helm goes to the tray), or its window is minimized.
/// </para>
/// </summary>
internal static class PageLeaveGuard
{
    /// <param name="page">The page, built (its password boxes exist).</param>
    /// <param name="viewModel">The view model the page's password boxes are bound to.</param>
    /// <param name="forget">Clears the view model's typed passwords.</param>
    public static void Attach(FrameworkElement page, INotifyPropertyChanged viewModel, Action forget)
    {
        FollowViewModel(page, viewModel);

        Window? window = null;

        void OnStateChanged(object? sender, EventArgs e)
        {
            if (window?.WindowState == WindowState.Minimized) Leave(page, forget);
        }

        page.Loaded += (_, _) =>
        {
            if (window is not null) window.StateChanged -= OnStateChanged;
            window = Window.GetWindow(page);
            if (window is not null) window.StateChanged += OnStateChanged;
        };
        page.Unloaded += (_, _) =>
        {
            if (window is not null) window.StateChanged -= OnStateChanged;
            window = null;
            Leave(page, forget);
        };
        page.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false) Leave(page, forget);
        };
    }

    /// <summary>What the page does when it is left: hides every shown secret, then forgets what was typed.</summary>
    public static void Leave(DependencyObject page, Action forget)
    {
        foreach (var box in PasswordBoxes(page)) Hide(box);
        forget();
    }

    /// <summary>
    /// Puts the view model's value into the password boxes bound to it whenever it changes (cleared after use, on lock,
    /// on leaving), hiding a shown box first so the change is not ignored. Only the page's own boxes: an item's fields
    /// (in templates) belong to the item being edited and are left alone.
    /// </summary>
    public static void FollowViewModel(DependencyObject page, INotifyPropertyChanged viewModel)
    {
        var byProperty = PasswordBoxes(page)
            .Select(box => (Box: box, Path: BindingOperations.GetBinding(box, WpfUiPasswordBox.PasswordProperty)?.Path?.Path))
            .Where(x => !string.IsNullOrEmpty(x.Path) && !x.Path.Contains('.', StringComparison.Ordinal))
            .GroupBy(x => x.Path!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Box).ToList(), StringComparer.Ordinal);

        void Apply(string property)
        {
            if (!byProperty.TryGetValue(property, out var boxes)) return;
            var value = viewModel.GetType().GetProperty(property)?.GetValue(viewModel) as string ?? "";
            foreach (var box in boxes)
            {
                if (!ReferenceEquals(box.DataContext, viewModel) || box.Password == value) continue;
                Hide(box);
                box.SetCurrentValue(WpfUiPasswordBox.PasswordProperty, value);
            }
        }

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not { } property || !byProperty.ContainsKey(property)) return;
            if (page.Dispatcher.CheckAccess()) Apply(property);
            else page.Dispatcher.BeginInvoke(() => Apply(property));
        };
    }

    private static void Hide(WpfUiPasswordBox box)
    {
        // No public setter (the eye button flips it), but it is an ordinary dependency property.
        if (box.IsPasswordRevealed) box.SetCurrentValue(WpfUiPasswordBox.IsPasswordRevealedProperty, false);
    }

    /// <summary>Every password box of the page, also the collapsed ones and the ones inside templates.</summary>
    private static List<WpfUiPasswordBox> PasswordBoxes(DependencyObject root)
    {
        var found = new List<WpfUiPasswordBox>();
        var seen = new HashSet<DependencyObject>();
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!seen.Add(node)) continue;
            if (node is WpfUiPasswordBox box) found.Add(box);
            // Collapsed parts of a page are in the logical tree; templated parts (lists, the item editor) in the visual one.
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) pending.Push(child);
            if (node is Visual or System.Windows.Media.Media3D.Visual3D)
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) pending.Push(VisualTreeHelper.GetChild(node, i));
        }
        return found;
    }
}
