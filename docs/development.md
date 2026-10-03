# Developing Helm

Everything needed to build, run, extend and release Helm. For what Helm is and how to install it, see the [README](../README.md). For the Android app, see [android.md](android.md).

- [Build and run](#build-and-run)
- [Where things live](#where-things-live)
- [Solution layout](#solution-layout)
- [Adding a module](#adding-a-module)
- [Updates](#updates)
- [Release process](#release-process)

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

## Where things live

| Path | Content |
|---|---|
| `%LOCALAPPDATA%\Helm\settings\general.json` | Theme, window placement, enabled modules, last update check |
| `%LOCALAPPDATA%\Helm\settings\<moduleId>.json` | One file per module (`zones.json`, `always-on-top.json`, `tracker.json`) |
| `%LOCALAPPDATA%\Helm\sync\helm-sync.db` | Synced data (encrypted local replica), e.g. Tracker workspaces, items and history |
| `%LOCALAPPDATA%\Helm\settings\zones\` | `layouts.json`, `applied.json` (monitor id → layout), `app-zone-history.json` |
| `%LOCALAPPDATA%\Helm\logs\helm-YYYYMMDD.log` | Serilog rolling log, 14 days (`velopack-hooks.log` for install/update/uninstall hooks) |
| `%LOCALAPPDATA%\HelmApp\` | Velopack install root: `Helm.exe` stable launcher, `Update.exe`, `current\` |

"Run at startup" registers a Task Scheduler task named **Helm** with *Run with highest privileges* and an *At log on* trigger, so Helm starts elevated without a UAC prompt.

## Solution layout

```
src/
  Helm.Core/                 net8.0, no UI: module contract (IModule, ModuleRegistry<T>), settings, sync, update rules
  Helm.Shell/                net8.0: view models shared by both apps' Home and General (Updates, Sync)
  Helm.Core.Windows/         Windows: IHelmModule, Win32 (CsWin32), hooks, hotkeys, window/monitor services,
                             GDI overlay window, DPAPI, shared WPF controls (Ui/); namespaces stay Helm.Core.*
  Helm.App/                  WPF-UI shell: DI host, MainWindow, tray, Home/General/Diagnostics pages
  Helm.Core.Android/         Android: IAndroidModule, ModulePageBase, ActivityHost, HelmAndroidServices (not in Helm.sln)
  Helm.App.Android/          Avalonia Android app: shell, Home/General, Keystore, APK updater (not in Helm.sln)
  Helm.Modules.ClaudeChat/   Windows only: Claude Code chat window
  Helm.Modules.AlwaysOnTop/  Windows only: engine, settings page
  Helm.Modules.Tracker.Core/ net8.0: Tracker data (on Helm Sync), reports, CSV, shared view model
  Helm.Modules.Tracker/      Windows: Tracker page
  Helm.Modules.Tracker.Android/ Android: Tracker page and home-screen widget (not in Helm.sln)
  Helm.Modules.Missions.Core/ net8.0: missions (on Helm Sync), JSON import, prompt, pace, MCP tools, shared view model
  Helm.Modules.Missions/     Windows: Missions pages and confetti
  Helm.Modules.Missions.Android/ Android: Missions pages, reminders and home-screen widget (not in Helm.sln)
  Helm.Modules.Vault.Core/   net8.0: vault keys, items, locking, backups, KDBX export, shared view models
  Helm.Modules.Vault/        Windows: vault window and page, Windows Hello, protected clipboard
  Helm.Modules.Vault.Android/ Android: vault page, fingerprint unlock, FLAG_SECURE, SAF backups (not in Helm.sln)
  Helm.VaultRestore/         helm-vault-restore: opens a vault backup without Helm
tests/Helm.Tests/            xUnit: settings, sync (+ end-to-end against a local worker), updates, hotkeys, Claude Chat, Tracker, Missions, Vault
```

Windows modules reference `Helm.Core.Windows`, Android modules reference `Helm.Core.Android`, and shared tool logic references `Helm.Core`; `Helm.App` / `Helm.App.Android` reference everything they ship.

Key infrastructure in `Helm.Core`:

- `MessageLoopThread` is an STA thread with its own `HWND_MESSAGE` window and message pump. Hotkeys, low-level hooks, WinEvent hooks and each module's overlays run on these threads, never on the WPF dispatcher.
- `LowLevelKeyboardHook` / `LowLevelMouseHook` are ref-counted (`Acquire()`). `Intercept` runs inside the hook and can set `Handled` to swallow input. `Observed` is fed through a `Channel<T>` for heavier work.
- `HotkeyManager` wraps `RegisterHotKey` and detects conflicts, both between modules and with other apps. It suspends itself while a hotkey picker is recording.
- `WindowService` works with DWM *extended frame bounds*, so snapped windows sit flush without the invisible resize border.
- `OverlayWindow` is a click-through, non-activating, layered GDI window that works in physical pixels. Zones overlays and pinned-window borders use it.

## Adding a module

> **Fastest way:** open [new-tool-prompt.md](new-tool-prompt.md), fill in the tool name, what it does and its platforms (PC, Android or both), and paste the file into Claude Code. It contains every rule needed so a new tool matches the others (structure, UI, testing, release).

1. Create `src/Helm.Modules.<Name>` (WPF class library referencing `Helm.Core.Windows`). Android tools follow [android.md](android.md#tools-and-platforms).
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

A tool you work in (lists, a chat, a vault) also has a **content page**: implement `IModuleContent` and the menu and the Quick access tile open it, while Home → Utilities and search open the settings page. See "Content page and settings page" in [new-tool-prompt.md](new-tool-prompt.md).

## Updates

Helm checks GitHub Releases 30 seconds after it starts and then every 6 hours, downloads in the background and installs only when the user chooses **Restart to update** (or turns on *Install updates automatically when Helm restarts*). Before applying an update, Helm disables every module (hooks, topmost windows, overlays). The restarted Helm stays elevated without another UAC prompt. The repository is public, so update checks need no account or token.

**Portable and dev builds do not auto-update.** Running from `bin/`, `dotnet run` or an unpacked portable zip shows "Updates unavailable" with an explanation, and every update action is disabled.

## Release process

1. Add the changes under a new section at the top of [CHANGELOG.md](../CHANGELOG.md). That section becomes the release notes.
2. Bump, commit and tag:

   ```powershell
   ./scripts/bump-version.ps1 0.3.0            # or 0.4.0-preview.1 for the preview channel
   git push origin HEAD --follow-tags
   ```

3. The tag push runs [.github/workflows/release.yml](../.github/workflows/release.yml). Its `android` job builds `Helm-android.apk` with the same version, signed with the release keystore from the `ANDROID_KEYSTORE_BASE64` / `ANDROID_KEYSTORE_PASSWORD` secrets ([android.md](android.md#release-signing)). The `release` job then tests, publishes `win-x64`, packs with `vpk` (packId `HelmApp`, title "Helm", channel from the tag), uploads to GitHub Releases (pre-release for suffixed tags) and attaches `Helm-win-Setup.exe` and `Helm-android.apk`.

The version has one source: `<Version>` in `Directory.Build.props`. CI overrides it from the tag (`v1.2.3` → `1.2.3`). Home and General display it.

To pack locally without CI, run `./scripts/release-local.ps1` (output goes to `./releases`). Add `-SelfContained` to bundle the runtime. [testing-updates.md](testing-updates.md) is the end-to-end manual test for install → update → restart.

