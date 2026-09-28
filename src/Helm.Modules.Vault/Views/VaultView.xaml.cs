using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
}
