using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Helm.Modules.Vault.ViewModels;

namespace Helm.Modules.Vault.Views;

public partial class VaultView : UserControl
{
    public VaultView() => InitializeComponent();

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

    /// <summary>A click on the row that is already open closes it; clicks on the row's buttons (show, copy) still work.</summary>
    private void OnRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not VaultAppViewModel app || e.OriginalSource is not DependencyObject source) return;
        ListBoxItem? row = null;
        for (var node = source; node is not null && node != sender; node = ParentOf(node))
        {
            if (node is ButtonBase) return;
            if (node is ListBoxItem item)
            {
                row = item;
                break;
            }
        }
        if (row is not { IsSelected: true }) return;
        app.Items.CloseDetailCommand.Execute(null);
        e.Handled = true;
    }

    // A click can land on text content (a Run), which is not a Visual; its parent is in the logical tree.
    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
