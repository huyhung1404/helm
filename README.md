# Helm

A personal Windows admin and productivity toolkit in the spirit of Microsoft PowerToys. Helm is one Fluent desktop app made of independent tools ("modules"), each with its own tab.

**v0.1** ships the full shell and two modules:

| Module | What it does |
|---|---|
| **Zones** | Splits each monitor into zones (templates or custom grid/canvas layouts). Hold <kbd>Shift</kbd> while dragging a window to snap it; <kbd>Win</kbd>+<kbd>Shift</kbd>+<kbd>`</kbd> opens the layout editor; optional <kbd>Win</kbd>+<kbd>←</kbd>/<kbd>→</kbd> override. |
| **Always On Top** | <kbd>Win</kbd>+<kbd>Ctrl</kbd>+<kbd>T</kbd> pins the active window above all others and draws a colored border around it. |

## Build and run

Requirements: Windows 10 19041+ / Windows 11, the .NET 8 SDK or newer (the projects target `net8.0-windows10.0.19041.0` and roll forward to newer runtimes).

```powershell
dotnet build
dotnet test
dotnet run --project src/Helm.App
```

Helm **requires administrator rights** (`requireAdministrator` in `src/Helm.App/app.manifest`), so `dotnet run` shows a UAC prompt. Low-level hooks, hotkeys and window moves then also work on elevated windows. To debug in Visual Studio, start VS as administrator.

For quick UI checks without UAC there is a dev-only switch that swaps in an `asInvoker` manifest:

```powershell
dotnet build src/Helm.App -p:HelmAsInvoker=true
```

A few command-line flags are handy while developing:

- `--startup`: start hidden in the tray (this is what the logon task uses).
- `--page <Name>`: open a page directly, for example `--page Zones` or `--page Diagnostics`.

### Where things live

| Path | Content |
|---|---|
| `%LOCALAPPDATA%\Helm\settings\general.json` | Theme, window placement, enabled modules, last update check |
| `%LOCALAPPDATA%\Helm\settings\<moduleId>.json` | One file per module (`zones.json`, `always-on-top.json`) |
| `%LOCALAPPDATA%\Helm\settings\zones\` | `layouts.json`, `applied.json` (monitor id → layout), `app-zone-history.json` |
| `%LOCALAPPDATA%\Helm\logs\helm-YYYYMMDD.log` | Serilog rolling log, 14 days |

"Run at startup" registers a Task Scheduler task named **Helm** with *Run with highest privileges* and an *At log on* trigger, so Helm starts elevated without a UAC prompt.

## Solution layout

```
src/
  Helm.Core/                 no pages: module contracts, settings, Win32 (CsWin32), hooks, hotkeys,
                             window/monitor services, GDI overlay window, shared UI controls (Ui/)
  Helm.App/                  WPF-UI shell: DI host, MainWindow, tray, Home/General/Diagnostics pages
  Helm.Modules.Zones/        Layouts/ (pure model), Engine/ (snapping), Editor/ (overlay editor), settings page
  Helm.Modules.AlwaysOnTop/  engine, settings page
tests/Helm.Tests/            xUnit: layout templates, grid editing, zone math, settings, hotkey conflicts
```

Modules reference `Helm.Core` only; `Helm.App` references everything.

Key infrastructure in `Helm.Core`:

- `MessageLoopThread` is an STA thread with its own `HWND_MESSAGE` window and message pump. Hotkeys, low-level hooks, WinEvent hooks and each module's overlays run on these threads, never on the WPF dispatcher.
- `LowLevelKeyboardHook` / `LowLevelMouseHook` are ref-counted (`Acquire()`). `Intercept` runs inside the hook and can set `Handled` to swallow input. `Observed` is fed through a `Channel<T>` for heavier work.
- `HotkeyManager` wraps `RegisterHotKey` and detects conflicts, both between modules and with other apps. It suspends itself while a hotkey picker is recording.
- `WindowService` works with DWM *extended frame bounds*, so snapped windows sit flush without the invisible resize border.
- `OverlayWindow` is a click-through, non-activating, layered GDI window that works in physical pixels. Zones overlays and pinned-window borders use it.

## Adding a module

1. Create `src/Helm.Modules.<Name>` (WPF class library referencing `Helm.Core`).
2. Implement the module, usually by deriving from `HelmModuleBase`:

   ```csharp
   public sealed class ColorPickerModule : HelmModuleBase
   {
       public override string Id => "color-picker";
       public override string DisplayName => "Color Picker";
       public override string Description => "Pick a color from anywhere on screen.";
       public override ModuleGroup Group => ModuleGroup.SystemTools;
       public override SymbolRegular Icon => SymbolRegular.Color24;
       public override Type SettingsPageType => typeof(ColorPickerPage);
       public override IReadOnlyList<HotkeyDefinition> Hotkeys => [...];
       public override Task EnableAsync(CancellationToken ct) { /* hooks, hotkeys */ }
       public override Task DisableAsync() { /* release everything; idempotent */ }
   }
   ```

3. Build the settings page as a `core:ModulePageBase` with `Module="{Binding Module}"` and put `ui:CardControl`/`ui:CardExpander` sections inside, each with a `core:CardHeader`. The search box indexes those headers automatically.
4. Register the module with `services.AddHelmModule<TModule, TPage, TViewModel>()` in a `Add<Name>Module()` extension, then add one line to `src/Helm.App/Hosting/HelmModules.cs`.

The nav tree, Home tiles, tray toggles, search and enable persistence all come from the registration. No other shell changes are needed.

## Roadmap

- Auto-update and installer (Velopack + GitHub Releases). This is next.
- **System Tools**: Color Picker, Screen Ruler, Text Extractor (OCR), Awake, Light Switch.
- **Windowing & Layouts**: Crop And Lock, Window Hopper, per-virtual-desktop layouts for Zones.
- **Input & Output**: Keyboard Manager (remaps), Find My Mouse, mouse jump between monitors.
- **File Management**: File Locksmith, bulk rename, Hosts file editor, Environment Variables.
- **Advanced**: Registry preview, a command palette / launcher, Unity-specific helpers (kill stuck editors, clear Library caches).
