using System.Windows;
using System.Windows.Input;
using Helm.Modules.Vault.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Helm.Modules.Vault.Views;

public partial class VaultWindow : FluentWindow
{
    private readonly VaultAppViewModel _app;

    public VaultWindow(VaultAppViewModel app)
    {
        _app = app;
        DataContext = app;
        InitializeComponent();
        SourceInitialized += (_, _) => ApplicationThemeManager.Apply(this);
        // Any use of the window counts as activity for the auto-lock.
        PreviewKeyDown += (_, _) => app.Session.Touch();
        PreviewMouseDown += (_, _) => app.Session.Touch();
        app.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VaultAppViewModel.Screen)) Dispatcher.BeginInvoke(FocusFirstInput);
        };
    }

    /// <summary>Puts the caret where the user types first: the password on the lock screen, search in the vault.</summary>
    public void FocusFirstInput()
    {
        IInputElement? target = _app.Screen switch
        {
            VaultScreen.Setup => NewPasswordBox,
            VaultScreen.Unlock => UnlockPasswordBox,
            _ => SearchBox,
        };
        if (target is not null) Keyboard.Focus(target);
    }
}
