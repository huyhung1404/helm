using System.Windows.Controls;
using System.Windows.Input;

namespace Helm.Modules.Vault.Views;

public partial class ItemEditorView : UserControl
{
    public ItemEditorView()
    {
        InitializeComponent();
        // The caret starts in the title, so a new item can be typed right away.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Dispatcher.BeginInvoke(() => Keyboard.Focus(TitleBox), System.Windows.Threading.DispatcherPriority.Input);
        };
    }
}
