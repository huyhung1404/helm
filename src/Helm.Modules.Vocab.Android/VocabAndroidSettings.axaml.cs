using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Helm.Modules.Vocab;

/// <summary>The phone-only part of Vocabulary's settings sheet: the permissions, the bubble and the apps the pop-up stays out of.</summary>
public partial class VocabAndroidSettings : UserControl
{
    private readonly VocabOverlaySettingsViewModel? _overlay;

    /// <summary>For the XAML runtime loader and the designer.</summary>
    public VocabAndroidSettings() => InitializeComponent();

    public VocabAndroidSettings(VocabOverlaySettingsViewModel overlay)
    {
        _overlay = overlay;
        DataContext = overlay;
        InitializeComponent();
    }

    private void Unblock_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: VocabAppRow row }) _overlay?.UnblockCommand.Execute(row);
    }
}
