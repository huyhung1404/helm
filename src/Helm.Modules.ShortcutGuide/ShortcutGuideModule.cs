using Helm.Core.Hooks;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace Helm.Modules.ShortcutGuide;

/// <summary>
/// Shortcut Guide: hold Ctrl, Alt or Win (Shift if you turn it on) for a moment and a panel over the app in front
/// lists the shortcuts that start with what you hold, for that app first (VS Code, Unity, Slack, Edge …), then the
/// ones most apps share and those of Windows. Press a key and it is gone; let go and it is gone.
/// </summary>
public sealed class ShortcutGuideModule : HelmModuleBase, IModuleContent, IDisposable
{
    public const string ModuleId = "shortcut-guide";

    private readonly ShortcutGuideEngine _engine;
    private readonly ILogger<ShortcutGuideModule> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lazy<IReadOnlyList<CatalogApp>> _catalog;
    private ShortcutLibrary? _library;

    public ShortcutGuideModule(ISettingsStoreFactory settings, ShortcutGuideEngine engine, ILogger<ShortcutGuideModule> logger)
    {
        Settings = settings.Get<ShortcutGuideSettings>(ModuleId);
        _engine = engine;
        _logger = logger;
        _catalog = new Lazy<IReadOnlyList<CatalogApp>>(LoadCatalog);
        Settings.Changed += (_, _) =>
        {
            Volatile.Write(ref _library, null);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
            NotifyHotkeysChanged();
        };
    }

    public override string Id => ModuleId;
    public override string DisplayName => "Shortcut Guide";
    public override string Description => "Hold Ctrl, Alt or Win for a moment to see the shortcuts of the app in front, and of Windows.";
    public override ModuleGroup Group => ModuleGroup.WindowsAndDesktop;
    public override SymbolRegular Icon => SymbolRegular.Keyboard24;
    public override System.Windows.Media.ImageSource IconImage => ShortcutGuideLogo.Image;
    public override Type SettingsPageType => typeof(ShortcutGuidePage);
    public Type ContentPageType => typeof(ShortcutGuideContentPage);

    public ISettingsStore<ShortcutGuideSettings> Settings { get; }

    /// <summary>The catalog merged with your changes; rebuilt after every settings change. Any thread.</summary>
    public ShortcutLibrary Library
    {
        get
        {
            var library = Volatile.Read(ref _library);
            if (library is not null) return library;
            library = new ShortcutLibrary(_catalog.Value, Settings.Current);
            Volatile.Write(ref _library, library);
            return library;
        }
    }

    /// <summary>Raised (on the thread that changed the settings) when <see cref="Library"/> must be read again.</summary>
    public event EventHandler? LibraryChanged;

    public override IReadOnlyList<HotkeyDefinition> Hotkeys
    {
        get
        {
            var s = Settings.Current;
            var list = new List<HotkeyDefinition>(4);
            void Add(bool on, string id, HotkeyModifiers modifier, string name)
            {
                if (on) list.Add(new HotkeyDefinition(ModuleId, id, $"Show the shortcuts that start with {name} (hold)", new HotkeyGesture(modifier, 0)));
            }
            Add(s.HoldCtrl, "hold-ctrl", HotkeyModifiers.Ctrl, "Ctrl");
            Add(s.HoldAlt, "hold-alt", HotkeyModifiers.Alt, "Alt");
            Add(s.HoldWin, "hold-win", HotkeyModifiers.Win, "Win");
            Add(s.HoldShift, "hold-shift", HotkeyModifiers.Shift, "Shift");
            return list;
        }
    }

    public override async Task EnableAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            StatusMessage = null;
            _engine.Start(() => Settings.Current, () => Library);
        }
        catch (HookInstallException ex)
        {
            _logger.LogError(ex, "Shortcut Guide could not watch the keyboard");
            StatusMessage = $"Shortcut Guide cannot watch the keyboard: {ex.Message}";
        }
        finally
        {
            _gate.Release();
        }
    }

    public override async Task DisableAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _engine.Stop();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Shows the guide for one app as if <paramref name="held"/> were held. For the library page.</summary>
    public void Preview(string appId, HotkeyModifiers held)
    {
        if (!IsEnabled) return;
        try
        {
            _engine.Preview(appId, held);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shortcut guide preview failed");
        }
    }

    // The engine is a singleton of its own: the container disposes it.
    public void Dispose() => _gate.Dispose();

    private IReadOnlyList<CatalogApp> LoadCatalog()
    {
        try
        {
            return ShortcutLibrary.LoadBuiltInCatalog();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The built-in shortcut catalog could not be read");
            StatusMessage = "The built-in shortcuts could not be read; only yours are shown.";
            return [];
        }
    }
}
