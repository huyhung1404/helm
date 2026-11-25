using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Helm.Core.Modules;
using Helm.Core.Ui;
using Helm.Modules.Vault.ViewModels;

namespace Helm.Modules.Vault.Ui;

/// <summary>
/// The Vault itself, the same view on Windows, Android, macOS and iOS: set up, locked, the Emergency Kit, and the
/// unlocked vault (sections, search, items, the open item). One layout that narrows for a phone (below 720 px). The
/// editor and the settings are sheets over the page. While the tool is off the page shows a note pointing to its
/// settings.
/// </summary>
public partial class VaultView : UserControl
{
    private const double Narrow = 720;
    private readonly IModule? _module;
    private readonly VaultAppViewModel? _app;
    private readonly VaultSettingsViewModel? _settings;
    private readonly IVaultPageHost? _host;
    private SheetHandle? _editorSheet;
    private SheetHandle? _settingsSheet;
    private bool _narrow;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public VaultView() => InitializeComponent();

    public VaultView(IModule module, VaultAppViewModel app, VaultSettingsViewModel settings, IVaultPageHost host)
    {
        _module = module;
        _app = app;
        _settings = settings;
        _host = host;
        DataContext = app;
        // The desktop shows an item beside the list; a phone waits for a tap.
        app.Items.AutoSelectFirst = true;
        InitializeComponent();
        Logo.Source = VaultIcon.Image;
        // Any use of the vault counts as activity for the auto-lock.
        AddHandler(PointerPressedEvent, (_, _) => app.Session.Touch(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, _) => app.Session.Touch(), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _narrow = e.NewSize.Width < Narrow;
        Classes.Set("narrow", _narrow);
        Page.Margin = _narrow ? new Thickness(16, 18, 16, 16) : new Thickness(40, 28, 40, 24);
        // The actions beside the title on a wide page, on their own line (right-aligned) on a phone.
        var actions = Actions;
        Grid.SetColumn(actions, _narrow ? 0 : 1);
        Grid.SetRow(actions, _narrow ? 1 : 0);
        Grid.SetColumnSpan(actions, _narrow ? 2 : 1);
        actions.Margin = _narrow ? new Thickness(0, 12, 0, 0) : default;
        actions.HorizontalAlignment = _narrow ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left;
        ItemsView.Narrow = _narrow;
    }

    // The page outlives nothing it listens to: it listens only while shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        VaultDialogs.Anchor = this;
        if (_app is null) return;
        _app.PropertyChanged += OnAppChanged;
        _app.Items.PropertyChanged += OnItemsChanged;
        if (_module is not null)
        {
            _module.PropertyChanged += OnModuleChanged;
            ApplyEnabled();
        }
        UpdateScreens(first: true);
        UpdateEyebrow();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (ReferenceEquals(VaultDialogs.Anchor, this)) VaultDialogs.Anchor = null;
        if (_app is not null)
        {
            _app.PropertyChanged -= OnAppChanged;
            _app.Items.PropertyChanged -= OnItemsChanged;
            // Leaving the page forgets what was typed and hides what was shown, so nobody finds them there later.
            _app.ForgetTypedSecrets();
            _app.Items.HideRevealed();
        }
        if (_module is not null) _module.PropertyChanged -= OnModuleChanged;
        _editorSheet?.Close();
        _settingsSheet?.Close();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(VaultAppViewModel.Screen) or nameof(VaultAppViewModel.IsItems) or nameof(VaultAppViewModel.HasKit) or nameof(VaultAppViewModel.MustChangePassword):
                UpdateScreens(first: false);
                UpdateEyebrow();
                break;
            case nameof(VaultAppViewModel.LockCountdown):
                UpdateEyebrow();
                break;
        }
    }

    private void OnItemsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ItemsViewModel.Editor)) UpdateEditor();
    }

    /// <summary>The cards for the screen the vault is on: setup, locked, new password or kit in the middle; the items otherwise.</summary>
    private void UpdateScreens(bool first)
    {
        if (_app is null) return;
        var auth = _app.IsSetup || _app.IsUnlock || _app.MustChangePassword || _app.HasKit;
        var items = _app.IsItems && !_app.HasKit;
        var authScroller = AuthScroller;
        var itemsHost = ItemsHost;
        var changed = authScroller.IsVisible != auth || itemsHost.IsVisible != items;
        authScroller.IsVisible = auth;
        itemsHost.IsVisible = items;
        if (!changed && !first) return;
        Motion.Enter(auth ? authScroller : itemsHost);
        // The cursor goes where the user types first.
        Dispatcher.UIThread.Post(() =>
        {
            if (_app.IsUnlock) UnlockPassword.Focus();
            else if (items && !_narrow) ItemsView.FocusSearch();
        }, DispatcherPriority.Input);
    }

    /// <summary>"VAULT · UNLOCKED · LOCKS IN 4:52".</summary>
    private void UpdateEyebrow()
    {
        if (_app is null) return;
        var state = _app.IsSetup ? "NOT SET UP" : _app.IsUnlock ? "LOCKED" : _app.IsItems ? "UNLOCKED" : "ALMOST THERE";
        var line = "VAULT · " + state;
        if (_app.IsItems && _app.LockCountdown is { } left) line += " · " + left.ToUpperInvariant();
        Eyebrow.Text = line;
    }

    /// <summary>
    /// New or Edit opens the editor as a sheet over the page. A tap outside it or Escape is Cancel (which asks first when
    /// something was typed); if the user keeps editing, the sheet comes back as it was.
    /// </summary>
    private void UpdateEditor()
    {
        var editor = _app?.Items.Editor;
        if (editor is null)
        {
            _editorSheet?.Close();
            _editorSheet = null;
            return;
        }
        if (_editorSheet is { IsOpen: true }) return;
        ShowEditor(editor);
    }

    private void ShowEditor(ItemDetailViewModel editor)
    {
        var sheet = Sheet.Show(this, new VaultItemView { DataContext = editor }, 580);
        _editorSheet = sheet;
        if (sheet is null) return;
        sheet.Closed += async () =>
        {
            // Saved or cancelled from inside: the view model already let go of the editor.
            if (_app is null || !ReferenceEquals(_app.Items.Editor, editor)) return;
            await editor.CancelCommand.ExecuteAsync(null);
            if (ReferenceEquals(_app.Items.Editor, editor) && editor.IsEditing) ShowEditor(editor);
        };
    }

    /// <summary>The gear: the vault's settings as a sheet over the page, not a page to navigate to.</summary>
    private void Settings_Click(object? sender, RoutedEventArgs e)
    {
        if (_settings is null || _host is null || _settingsSheet is { IsOpen: true }) return;
        SheetHandle? sheet = null;
        sheet = Sheet.Show(this, new VaultSettingsView(_settings, _host, () => sheet?.Close()), 520);
        _settingsSheet = sheet;
    }

    /// <summary>The question mark: how the vault works (it works locked, unlocked and before setup).</summary>
    private void Guide_Click(object? sender, RoutedEventArgs e)
    {
        SheetHandle? sheet = null;
        sheet = Sheet.Show(this, new VaultGuideView(() => sheet?.Close()), 700);
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(ApplyEnabled);
    }

    private void ApplyEnabled()
    {
        if (_module is null) return;
        Body.IsEnabled = _module.IsEnabled;
        OffBar.IsVisible = !_module.IsEnabled;
    }
}
