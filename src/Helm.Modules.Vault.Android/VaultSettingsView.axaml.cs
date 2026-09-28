using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Helm.Modules.Vault;

public partial class VaultSettingsView : UserControl
{
    public VaultSettingsView() => AvaloniaXamlLoader.Load(this);
}
