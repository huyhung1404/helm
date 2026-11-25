using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Helm.Core.Hotkeys;
using Helm.Ui.Windows;

namespace Helm.Modules.Vault.Platform;

/// <summary>
/// The Windows-only settings of the vault (auto-type), as one more card in the shared settings sheet. They live here,
/// not in the shared page: the shortcut is a Windows global hotkey.
/// </summary>
public sealed class VaultWindowsSettingsViewModel(AutoTypeService autoType)
{
    public HotkeyGesture DefaultAutoTypeHotkey => VaultWindowsSettings.DefaultAutoTypeHotkey;

    /// <summary>The auto-type shortcut; empty turns auto-type off.</summary>
    public HotkeyGesture AutoTypeHotkey
    {
        get => autoType.Settings.Current.AutoTypeHotkey;
        set
        {
            if (value != AutoTypeHotkey) autoType.Settings.Update(s => s.AutoTypeHotkey = value);
        }
    }

    public bool AutoTypePressEnter
    {
        get => autoType.Settings.Current.AutoTypePressEnter;
        set
        {
            if (value != AutoTypePressEnter) autoType.Settings.Update(s => s.AutoTypePressEnter = value);
        }
    }
}

/// <summary>Builds the auto-type card (Avalonia, in code: no WPF type is used in this file).</summary>
internal static class AutoTypeSettingsCard
{
    public static Control Create(VaultWindowsSettingsViewModel settings)
    {
        var shortcut = new HotkeyBox { Gesture = settings.AutoTypeHotkey, HorizontalAlignment = HorizontalAlignment.Stretch };
        shortcut.PropertyChanged += (_, e) =>
        {
            if (e.Property == HotkeyBox.GestureProperty) settings.AutoTypeHotkey = shortcut.Gesture;
        };
        var reset = new Button { Classes = { "ghost" }, Content = "Reset" };
        reset.Click += (_, _) => shortcut.Gesture = settings.DefaultAutoTypeHotkey;
        var shortcutRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 8 };
        shortcutRow.Children.Add(shortcut);
        Grid.SetColumn(reset, 1);
        shortcutRow.Children.Add(reset);

        var enter = new ToggleSwitch { IsChecked = settings.AutoTypePressEnter, OnContent = "", OffContent = "" };
        Avalonia.Automation.AutomationProperties.SetName(enter, "Press Enter after the password");
        enter.IsCheckedChanged += (_, _) => settings.AutoTypePressEnter = enter.IsChecked == true;
        var enterRow = new DockPanel();
        DockPanel.SetDock(enter, Dock.Right);
        enterRow.Children.Add(enter);
        enterRow.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Press Enter after the password" },
                new TextBlock { Text = "Signs in at once on most sites. Off: you check, then press Enter yourself.", Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap },
            },
        });

        return new Border
        {
            Classes = { "glass" },
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock { Text = "Auto-type", Classes = { "head" } },
                            new TextBlock
                            {
                                Text = "In a sign-in window, press the shortcut: Helm types the username, Tab and the password of the login for that site "
                                    + "(read from the browser's address bar) or app. With several, you pick one. The two-factor code, if any, goes to the clipboard.",
                                Classes = { "secondary" },
                                TextWrapping = TextWrapping.Wrap,
                            },
                        },
                    },
                    shortcutRow,
                    enterRow,
                },
            },
        };
    }
}
