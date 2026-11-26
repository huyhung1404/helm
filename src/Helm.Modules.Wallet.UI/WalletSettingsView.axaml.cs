using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Helm.Core.Modules;

namespace Helm.Modules.Wallet.Ui;

/// <summary>Wallet's settings, shown as a sheet over the page (see <see cref="WalletView"/>'s gear).</summary>
public partial class WalletSettingsView : UserControl
{
    /// <summary>For the XAML loader and the previewer only.</summary>
    public WalletSettingsView() => InitializeComponent();

    public WalletSettingsView(WalletViewModel viewModel, IModule module, IWalletPageHost host, Action close)
    {
        DataContext = viewModel;
        InitializeComponent();
        AmountGrouping.Attach(this);
        PlatformCards.Content = host.CreatePlatformSettings();
        PlatformCards.IsVisible = PlatformCards.Content is not null;
        CloseButton.Click += (_, _) => close();
        DoneButton.Click += (_, _) => close();
        ExportText.Text = host.ExportText;
        QuickCaptureText.Text = host.QuickCaptureHint;
        ExportButton.Click += (_, _) =>
        {
            try
            {
                host.ExportCsv(viewModel.Csv());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                viewModel.ReportExportFailure(ex);
            }
        };
        // The tool's on/off switch (it used to be on a settings page of its own).
        EnableSwitch.IsChecked = module.IsEnabled;
        EnableSwitch.IsCheckedChanged += (_, _) => module.IsEnabled = EnableSwitch.IsChecked == true;
        void OnModule(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(() => EnableSwitch.IsChecked = module.IsEnabled);
        }
        AttachedToVisualTree += (_, _) => module.PropertyChanged += OnModule;
        DetachedFromVisualTree += (_, _) => module.PropertyChanged -= OnModule;
    }

    // A category's name is saved when its field loses focus, or on Enter.
    private void Rename_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        Avalonia.Data.BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }
}
