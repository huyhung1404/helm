# Helm

A personal Windows admin and productivity toolkit in the spirit of Microsoft PowerToys. Helm is one Fluent desktop app made of independent tools ("modules"), each with its own tab.

**v0.1** ships the full shell and two modules:

| Module | What it does |
|---|---|
| **Zones** | Splits each monitor into zones (templates or custom grid/canvas layouts). Hold <kbd>Shift</kbd> while dragging a window to snap it; <kbd>Win</kbd>+<kbd>Shift</kbd>+<kbd>`</kbd> opens the layout editor; optional <kbd>Win</kbd>+<kbd>←</kbd>/<kbd>→</kbd> override. |
| **Always On Top** | <kbd>Win</kbd>+<kbd>Ctrl</kbd>+<kbd>T</kbd> pins the active window above all others and draws a colored border around it. |

## Install

Download **`Helm-win-Setup.exe`** from the [latest release](https://github.com/huyhung1404/helm/releases/latest) and run it. Setup installs per user into `%LOCALAPPDATA%\HelmApp` (no admin needed to install) and creates Start menu and desktop shortcuts. It also installs the .NET 8 Desktop Runtime if it is missing. When Helm starts, it asks once for administrator rights (UAC).

Settings live separately in `%LOCALAPPDATA%\Helm`, so they survive an uninstall and reinstall. To remove them with the app, turn on General → Updates → *Delete my settings when Helm is uninstalled*.

## Update

Updates are automatic. Helm checks GitHub Releases 30 seconds after it starts and then every 6 hours. It downloads new versions in the background (setting: *Automatically download updates*) and tells you with a tray notification and the Home tile. Nothing is installed until you choose **Restart to update**, either in General → Updates or from the tray menu. You can also turn on *Install updates automatically when Helm restarts*.

- **Manual check**: General → Updates → *Check for updates*.
- **Channels**: *Stable* (default) or *Preview*, which receives GitHub pre-releases such as `v0.4.0-preview.1`.
- Before applying an update, Helm disables every module (hooks, topmost windows, overlays). The restarted Helm stays elevated without another UAC prompt.
- **Rate limits**: the repository is public, so no token is needed. If GitHub throttles you, add a token in General → Updates.

**Portable and dev builds do not auto-update.** Running from `bin/`, `dotnet run` or an unpacked portable zip shows "Updates unavailable" with an explanation, and every update action is disabled.

## Release process

1. Add the changes under a new section at the top of [CHANGELOG.md](CHANGELOG.md). That section becomes the release notes.
2. Bump, commit and tag:

   ```powershell
   ./scripts/bump-version.ps1 0.3.0            # or 0.4.0-preview.1 for the preview channel
   git push origin HEAD --follow-tags
   ```

3. The tag push runs [.github/workflows/release.yml](.github/workflows/release.yml). It tests, publishes `win-x64`, packs with `vpk` (packId `HelmApp`, title "Helm", channel from the tag), uploads to GitHub Releases (pre-release for suffixed tags) and attaches `Helm-win-Setup.exe`.

The version has one source: `<Version>` in `Directory.Build.props`. CI overrides it from the tag (`v1.2.3` → `1.2.3`). Home and General display it.

To pack locally without CI, run `./scripts/release-local.ps1` (output goes to `./releases`). Add `-SelfContained` to bundle the runtime. [docs/testing-updates.md](docs/testing-updates.md) is the end-to-end manual test for install → update → restart.

## Build and run

Requirements: Windows 10 19041+ / Windows 11, the .NET 8 SDK or newer (the projects target `net8.0-windows10.0.19041.0` and roll forward to newer runtimes).

```powershell
dotnet build
dotnet test
dotnet run --project src/Helm.App
```

Helm **runs as administrator**, so low-level hooks, hotkeys and window moves also work on elevated windows. The manifest is deliberately `asInvoker`, and `Program.Main` relaunches itself elevated (UAC) right after the Velopack bootstrap. The reason: Velopack's Setup and Update start the app with `CreateProcess`, which fails with `ERROR_ELEVATION_REQUIRED` for `requireAdministrator` executables. `dotnet run` therefore shows a UAC prompt. If you start Visual Studio as administrator, there is no prompt and the debugger stays attached.

A few command-line flags are handy while developing:

- `--no-elevate`: skip self-elevation for quick UI checks. Hooks then cannot touch elevated windows.
- `--startup`: start hidden in the tray (this is what the logon task uses).
- `--page <Name>`: open a page directly, for example `--page Zones` or `--page Diagnostics`.

### Where things live

| Path | Content |
|---|---|
| `%LOCALAPPDATA%\Helm\settings\general.json` | Theme, window placement, enabled modules, last update check |
| `%LOCALAPPDATA%\Helm\settings\<moduleId>.json` | One file per module (`zones.json`, `always-on-top.json`) |
| `%LOCALAPPDATA%\Helm\settings\zones\` | `layouts.json`, `applied.json` (monitor id → layout), `app-zone-history.json` |
| `%LOCALAPPDATA%\Helm\logs\helm-YYYYMMDD.log` | Serilog rolling log, 14 days (`velopack-hooks.log` for install/update/uninstall hooks) |
| `%LOCALAPPDATA%\HelmApp\` | Velopack install root: `Helm.exe` stable launcher, `Update.exe`, `current\` |

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

- Code signing for the installer and binaries (removes SmartScreen warnings).
- **System Tools**: Color Picker, Screen Ruler, Text Extractor (OCR), Awake, Light Switch.
- **Windowing & Layouts**: Crop And Lock, Window Hopper, per-virtual-desktop layouts for Zones.
- **Input & Output**: Keyboard Manager (remaps), Find My Mouse, mouse jump between monitors.
- **File Management**: File Locksmith, bulk rename, Hosts file editor, Environment Variables.
- **Advanced**: Registry preview, a command palette / launcher, Unity-specific helpers (kill stuck editors, clear Library caches).
