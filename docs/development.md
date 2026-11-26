# Developing Helm

Everything needed to build, run, extend and release Helm. For what Helm is and how to install it, see the [README](../README.md). For the Android app, see [android.md](android.md).

- [Build and run](#build-and-run)
- [Where things live](#where-things-live)
- [Solution layout](#solution-layout)
- [Adding a module](#adding-a-module)
- [The Linux connector](#the-linux-connector)
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
  Helm.Shell/                net8.0: view models shared by both apps' Home and General (Updates, Sync), Bug reports (store, black box, view models)
  Helm.Shell.UI/             Avalonia: Home, General and Bug reports (the page and the report form), the MCP consent question, every platform
  Helm.Shell.Windows/        Windows: the WPF pages that host Home, General and Bug reports, the report window, the MCP consent window, this PC's pictures and MCP part
  Helm.Core.Windows/         Windows: IHelmModule, Win32 (CsWin32), hooks, hotkeys, window/monitor services,
                             GDI overlay window, DPAPI, shared WPF controls (Ui/, also the confetti); namespaces stay Helm.Core.*
  Helm.App/                  WPF-UI shell: DI host, MainWindow, tray, Home/General/Diagnostics pages
  Helm.Core.Android/         Android: IAndroidModule, ModulePageBase, ActivityHost, HelmAndroidServices (not in Helm.sln)
  Helm.App.Android/          Avalonia Android app: shell, Home/General, Keystore, APK updater (not in Helm.sln)
  Helm.Modules.AlwaysOnTop/  Windows only: engine, settings page
  Helm.Keyboard.Engine/      net8.0: Telex (namespace Helm.Typing), the ids Helm and the input method share
  Helm.Keyboard.Tip/         HelmKeyboard.dll: the Vietnamese input method (TSF, NativeAOT, 64- and 32-bit);
                             built by scripts/build-keyboard.ps1, needs the C++ build tools
  Helm.Modules.Keyboard/     Windows only: installs the input method, EN / VI / 中 shortcut, keyboard per app (on Helm Sync)
  Helm.Modules.Tracker.Core/ net8.0: Tracker data (on Helm Sync), reports, CSV, shared view model
  Helm.Modules.Tracker/      Windows: Tracker page
  Helm.Modules.Tracker.Android/ Android: Tracker page and home-screen widget (not in Helm.sln)
  Helm.Modules.Missions.Core/ net8.0: missions (on Helm Sync), JSON import, prompt, pace, MCP tools, shared view model
  Helm.Modules.Missions.UI/  Avalonia: the Missions page, its sheets and the mission views (every platform)
  Helm.Modules.Missions/     Windows: the module, the tray reminder, the WPF page that hosts the shared view
  Helm.Modules.Missions.Android/ Android: the module, the page that hosts the shared view, reminders and home-screen widget (not in Helm.sln)
  Helm.Modules.Habits.Core/  net8.0: habits and their days (on Helm Sync), streaks, the year grid, reasons, MCP tools, shared view model
  Helm.Modules.Habits.UI/    Avalonia: the Habits page, its sheets and the year grid (every platform)
  Helm.Modules.Habits/       Windows: the module, the tray reminders, the WPF page that hosts the shared view
  Helm.Modules.Habits.Android/ Android: the module, the page that hosts the shared view, the reminder alarm (not in Helm.sln)
  Helm.Modules.Wallet.Core/  net8.0: transactions (on Helm Sync), bank notification parser, statistics, shared view model
  Helm.Modules.Wallet.UI/    Avalonia: the Wallet page (money, debts, goals, insights), its sheets and settings (every platform)
  Helm.Modules.Wallet/       Windows: the module, the WPF page that hosts the shared view (CSV save dialog)
  Helm.Modules.Wallet.Android/ Android: the module, the page that hosts the shared view, bank notification listener, "what was it?" notification, widgets (not in Helm.sln)
  Helm.Modules.Vault.Core/   net8.0: vault keys, items, locking, backups, KDBX export, shared view models
  Helm.Modules.Vault/        Windows: vault window and page, Windows Hello, protected clipboard, auto-type (its picker is the shared AutoTypePickerView in an Avalonia window)
  Helm.Modules.Vault.Android/ Android: vault page, fingerprint unlock, FLAG_SECURE, SAF backups (not in Helm.sln)
  Helm.Modules.Vocab.Core/   net8.0: words and their parts (on Helm Sync), the quiz, the card, MCP tools, shared view models
  Helm.Modules.Vocab.UI/     Avalonia: the Vocabulary page (Learn, Words, Roots), its sheets and settings (every platform), the PC's corner card
  Helm.Modules.Vocab/        Windows: the module, the WPF page that hosts the shared view, the corner card's window, the Claude Code agent
  Helm.Modules.Vocab.Android/ Android: the module, the page that hosts the shared view, the pop-up card and bubble over other apps (not in Helm.sln)
  Helm.Modules.NovelReader.Core/ net8.0: novels (on Helm Sync), conversion, dictionaries, reading aloud, names, reports, MCP tools, shared view model
  Helm.Modules.NovelReader.UI/ Avalonia: the Novel Reader page (library, reader, sheets, settings), every platform
  Helm.Modules.NovelReader/  Windows: the module, scraping the web, Windows voices, the WPF page that hosts the shared view
  Helm.Modules.NovelReader.Android/ Android: the module, the page that hosts the shared view, the phone's voices, Novel Lens (not in Helm.sln)
  Helm.Modules.Ssh.Core/     net8.0: servers, keys, known hosts, sessions, the server's menu, AI agents' tools, shared view model
  Helm.Modules.Ssh.UI/       Avalonia: the SSH page (servers, connect bar, key bar, menu and settings sheets), every platform
  Helm.Modules.Ssh/          Windows: the module, the terminal (xterm.js in WebView2, in a NativeControlHost), the WPF page that hosts the shared view
  Helm.Modules.Ssh.Android/  Android: the module, the page that hosts the shared view, the terminal (xterm.js in WebView) (not in Helm.sln)
  Helm.Modules.SlackBot.Core/ net8.0: Slack Report engine (reads Slack, sorts it with Claude), inbox, MCP tools, shared view models
  Helm.Modules.SlackBot.UI/  Avalonia: the Slack Report page, its reply sheet and settings sheet, the PC's always-on-top list
  Helm.Modules.SlackBot/     Windows only: the module, the always-on-top list's window, the WPF page that hosts the shared view
  Helm.Modules.WindowZones.Core/ net8.0: zone layouts, rules, engine contracts, shared view model
  Helm.Modules.WindowZones.UI/ Avalonia: the Window Zones page, its settings sheet, the zone layout view, the full-screen editor and the tab strip
  Helm.Modules.WindowZones/  Windows only: the module, the snapping engine and drag overlay, the Avalonia windows of the full-screen editor and the tab strip, the WPF page that hosts the shared view
  Helm.VaultRestore/         helm-vault-restore: opens a vault backup without Helm
  Helm.Core.Linux/           net8.0, Linux for the connector: secrets under a host key (0600, optionally sealed by
                             systemd-creds), MCP on a Unix socket, plain child processes; namespaces stay Helm.Core.*
  Helm.Connector/            helm-connector: Helm's data on a Linux home server, in sync, with Helm's tools over MCP
tests/Helm.Tests/            xUnit: settings, sync (+ end-to-end against a local worker), updates, hotkeys, MCP, Tracker, Missions, Habits, Wallet, Vault
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
       public override ModuleGroup Group => ModuleGroup.Planning;
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

## The Linux connector

`helm-connector` (src/Helm.Connector) keeps Helm's data on a Linux home server in sync and offers Helm's tools over
MCP to Fairy Core, the box's own AI (a separate app). It runs no agent. It uses the apps' own code: the sync engine,
and each synced tool's store and agent tools from its *.Core project. Only the platform parts are its own:
`FileKeySecretProtector` instead of DPAPI, and a Unix socket for MCP instead of the named pipe.

```bash
dotnet publish src/Helm.Connector -r linux-x64 -c Release   # one self-contained file: .../linux-x64/publish/helm-connector
helm-connector sync pair [--device NAME]          # shows an 18-digit code: type it into Helm on a PC (General → Sync
                                                  # → Devices → Add a device with a code); no passphrase on the box
helm-connector sync connect --token helm_pat_…    # or --invite helm_inv_… --account NAME [--device NAME]
helm-connector sync unlock                        # asks for the passphrase (or --recovery-key KEY), then a first sync
helm-connector serve                              # sync (live) + the MCP socket, until Ctrl+C or SIGTERM
helm-connector mcp                                # MCP on stdio for Fairy Core: relays to the running serve
helm-connector status                             # sync, what is synced, holds, the running serve (changes nothing)
```

- **Releases**: every Helm release carries `helm-connector-linux-x64` and `helm-connector-linux-x64.sha256` (the
  Release workflow publishes them); the Fairy box installs a newer one by itself, after checking the hash.
- **Data folder**: `--data-dir`, else `$HELM_DATA_DIR` (the box: /srv/fairy/connectors/helm, its own Linux user),
  else `$XDG_DATA_HOME/helm`, else `~/.local/share/helm`. Same layout as on Windows (settings/, sync/), plus
  secrets/ and run/ (serve.json, mcp.sock).
- **What it syncs**: `--sync ai,wallet,tracker,habits` (the default), or `"sync": [...]` in <data>/connector.json;
  notes, missions and vocab can be added, vault never. It is the sync engine's allowlist (`SyncEngine.SetIncluded`):
  nothing else is downloaded or uploaded, and only these tools' stores and agent tools are registered. A tool added
  later is downloaded in full at the next sync.
- **Unattended**: more than 40 pushes within a minute, or 150 within ten (the box normally does about one a minute,
  a few more per Ask Helm turn), stops sync until the
  process restarts. Held mass deletions are never decided on the box. Both are logged and, with `$HELM_HC_URL` (a
  Healthchecks ping URL), pinged to `<url>/fail`.
- **MCP**: tool calls run without a question, except tools marked `McpTool.Destructive` (notes_trash,
  mission_replan, any bulk deletion later), which run only after the user's yes (below). Tools that reach other
  machines are refused. Only `serve` opens the replica: `mcp` relays everything (custom methods too) to its socket,
  `$HELM_MCP_SOCKET` (e.g. /run/helm-connector/mcp.sock, with a group Fairy Core's user is in) or
  <data>/run/mcp.sock (mode 0660).
- **Confirming a deletion** (`ConfirmationGate`, in serve's memory only):
  1. `tools/call` of a destructive tool changes nothing. Its result is a normal one whose text the model reads,
     `{"status":"needs_confirmation","tool":…,"summary":"Move the note “…” to the trash…","message":…}`, and whose
     `_meta` carries `{"helm/confirmation":{"confirmationId":"<32 hex>","expiresAt":"<ISO 8601>"}}`. **Fairy Core
     must strip `_meta` before the result reaches the model.**
  2. Fairy Core shows the summary; on the user's yes it sends `{"jsonrpc":"2.0","id":…,"method":"helm/confirm",
     "params":{"confirmationId":…, "name":…, "arguments":…}}` (name and arguments optional; when given they must be
     the original call's). The reply's `result` is the tool's real `tools/call` result. On no: `helm/cancel`
     `{"confirmationId":…}` → `{"cancelled":true,"tool":…}`. `helm/pending` lists what waits.
  3. An id is random, single-use (confirmed or cancelled, even when the call then fails), valid 5 minutes, bound to
     the tool and a hash of its arguments (keys sorted). Unknown, used, expired or mismatched: a JSON-RPC error
     (-32602) with the reason, and nothing runs.

  These methods are not tools, so a model (which only issues `tools/call`) cannot confirm for the user even if it
  saw an id. From a terminal: `helm-connector pending`, `confirm ID`, `cancel ID`.
- **Fairy Core** (the box's AI, a separate private app) is the connector's MCP client: `tools/list`, `tools/call`
  (Undo-able tools carry `_meta: {"helm/undoable": true}`; `helm/undo {tool, result}` takes a call back). With AI
  synced, the phone's agent jobs go to it: the connector runs `AgentJobWorker` (ai.jobs, ai.workers, exactly as a
  PC) on a `FairyRoute`, up only while Core keeps asking. Core sends `helm/jobs/hello {agents: [AiWorkerAgent],
  readsPhotos}` (the phone's picker lists those agents), long-polls `helm/jobs/next {wait ≤ 25, received: [ids]}` →
  `{job: null | {id, kind: run|end|warm, conversationId, job: AiJob, images: [{mediaType, data}]}}`, and answers a
  run with `helm/jobs/result {id, result: AiJobResult | error}`. A run Core does not say it received (in its next
  poll's `received`) within 5 s goes to the next poll: a Core killed mid-poll loses nothing. Undo runs on the
  connector. Nothing is kept across connector restarts: an unanswered job fails at its wait, as with a PC that
  stopped mid-turn.
- **Secrets**: AES-GCM under a 32-byte host key, secrets/host.key (0600, folder 0700). With `HELM_SEAL_KEY=tpm2` a
  new key is sealed by `systemd-creds` instead (secrets/host.key.cred); a service started with
  `LoadCredentialEncrypted=helm-host-key:<data>/secrets/host.key.cred` gets it unsealed from systemd.
- **Logs**: stderr with syslog priorities, for the journal; `HELM_LOG_LEVEL=Debug` for more. The box needs ICU
  (libicu, present on Ubuntu).

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

3. The tag push runs [.github/workflows/release.yml](../.github/workflows/release.yml). Its `android` job builds `Helm-android.apk` (arm64, phones) and `Helm-android-x64.apk` (x86_64, emulators) with the same version, signed with the release keystore from the `ANDROID_KEYSTORE_BASE64` / `ANDROID_KEYSTORE_PASSWORD` secrets ([android.md](android.md#release-signing)). The `release` job then tests, publishes `win-x64`, packs with `vpk` (packId `HelmApp`, title "Helm", channel from the tag), uploads to a draft GitHub Release, attaches `Helm-win-Setup.exe`, both APKs and the Vault restore tool, and only then publishes it (pre-release for suffixed tags). No one sees a release with files missing.

4. The `android` job runs in the `release` environment and waits for the owner's approval: open the run under
   **Actions** → **Review deployments** → **Approve and deploy**. Nothing is published until then (the `release` job
   needs the `android` one). To rebuild with **Run workflow**, pick the tag itself under *Use workflow from*: the
   environment only accepts tags. A published release is final (see *Immutable releases* below): rebuild only a
   release a failed run left as a draft, and fix a published one with a new version.

### Who can release

Windows updates are trusted because they come from this repository's releases; Android ones also need the release
key. So only the owner may publish, and these GitHub settings make sure of it:

- **No one else has write access**: Settings → Collaborators, Deploy keys and GitHub Apps; on the account, Developer
  settings → Personal access tokens (revoke unused ones). The account uses two-factor sign-in (a passkey or an app).
- **Release tags**: Settings → Rules → Rulesets → *New tag ruleset*, target `v*`, rules *Restrict creations*,
  *Restrict updates* and *Restrict deletions*, bypass list *Repository admin*.
- **The `release` environment**: Settings → Environments → `release`: *Required reviewers* = the owner (leave
  *Prevent self-review* off), *Deployment branches and tags* = *Selected*, tag rule `v*`, and the two
  `ANDROID_KEYSTORE_*` secrets as environment secrets (then delete the repository-level ones). Only an approved job
  on a release tag can read the key.
- **Branches**: a branch ruleset on `main` and `develop` with *Restrict updates*, *Restrict deletions* and *Block
  force pushes*, bypass *Repository admin*: only the owner pushes there; anyone else goes through a pull request.
- **Immutable releases**: Settings → General → Releases → *Enable release immutability*. Once published, a
  release's files and tag cannot change, so no one can swap an update after the fact.

The version has one source: `<Version>` in `Directory.Build.props`. CI overrides it from the tag (`v1.2.3` → `1.2.3`). Home and General display it.

To pack locally without CI, run `./scripts/release-local.ps1` (output goes to `./releases`). Add `-SelfContained` to bundle the runtime. [testing-updates.md](testing-updates.md) is the end-to-end manual test for install → update → restart.

