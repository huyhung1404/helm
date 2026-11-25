using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Helm.Modules.Vault.Ui;

/// <summary>One item of the vault, shown or (New / Edit) edited; its view model is <c>ItemDetailViewModel</c>.</summary>
public partial class VaultItemView : UserControl
{
    public VaultItemView() => AvaloniaXamlLoader.Load(this);
}
