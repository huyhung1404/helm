using Avalonia.Controls;

namespace Helm.General;

/// <summary>What sync asks of the user beyond a status line (connect, unlock, recovery key, passphrase change). Its data context is the real <c>SyncViewModel</c>.</summary>
public partial class SyncFlowsView : UserControl
{
    public SyncFlowsView() => InitializeComponent();
}
