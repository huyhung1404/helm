# Prompt: add a new tool to Helm

How to use: fill in the three lines below, then paste this whole file into Claude Code at the root of the `helm` repo. Everything under "Rules" tells the AI how to build the tool, so it matches the existing Helm interface and code structure. You don't need to write anything else.

```
Tool name:     <e.g. Color Picker>
What it does:  <one or two sentences, e.g. "Win+Shift+C picks the color under the cursor and copies it as HEX">
Platforms:     <PC | Android | PC + Android>
```

---

## Rules for the AI

You are adding one new tool ("module") to **Helm**, a toolkit in the style of PowerToys with two apps: Windows (WPF, .NET 8, `src/Helm.App`) and Android (Avalonia 12, `src/Helm.App.Android`, see [android.md](android.md)). Home and General are shared; each tool exists only on the platforms named in "Platforms". Work from the three lines above. Decide every detail they leave open, following the rules below, and ask only if something is truly ambiguous. The finished tool must look and behave like any other Helm page.

### 1. Plan first (short)

Print a short plan before you start, then continue without waiting:
- **Platforms**: PC, Android or both (from the line above; if it is missing, ask). This decides the projects in §2.
- **Module id**: kebab-case, e.g. `color-picker`. The same id on both platforms.
- **Class prefix**: PascalCase, e.g. `ColorPicker`.
- **Group**: one of `SystemTools`, `WindowingAndLayouts`, `InputAndOutput`, `FileManagement`, `Advanced`. Pick the closest.
- **Icon**: a `SymbolRegular` value that exists (see §6). On Android the same icon is `FluentIcons.Common.Symbol.<Name>` (no size suffix).
  A tool may also have its own picture icon that replaces the symbol everywhere, exposed as `IconImage` (PC: a frozen
  `ImageSource`; Android: an `IImage`, built once). Keep the symbol as the fallback. Picture icons must all look the
  same size, so follow the **icon rule**:
  - **No frame**: no background tile, rounded square or circle behind the glyph. The glyph itself carries the colour
    (a solid colour or one gradient across the whole box), like the Claude symbol.
  - **Same size as the Claude symbol**: draw in a 100 × 100 box with a transparent 100 × 100 rectangle as the first
    drawing, and make the glyph fill the box: its longer side (including stroke width) spans about 98-100 of it,
    centred. Do not leave extra margin; the hosts (navigation, Home, page hero) already space icons.
  - **Vector, shared shapes**: path data and colours live in `<Prefix>IconShape` in the `.Core` project; the PC and
    Android projects each build a `DrawingImage` from it. No PNG (bitmaps scale badly and get cut out with frames).
  - **Readable at 16-20 px**: strokes at least 8 of 100. Render it next to `ClaudeLogo` at 128 and 20 px on dark and
    light backgrounds before shipping.
  - **Every tool gets a picture icon.** A bare `SymbolRegular` glyph fills only about 86 % of the box with half the
    stroke weight and no colour, so it looks smaller next to the picture icons on Home and in the menu. Aim for about
    the same ink as the others (28-30 % of the box at 128 px; measure it); fill a shape if thin strokes look light.
  - A tool on both platforms without a `.Core` project can link the shape file into its Android project
    (`<Compile Include="..\Helm.Modules.<Prefix>\<Prefix>IconShape.cs" Link="…" />`), as Quick Capture does.
  See `ClaudeLogo`, `VaultIconShape` / `VaultIcon`, `TrackerIconShape` / `TrackerLogo` / `TrackerIcon`,
  `QuickCaptureIconShape`, `CommandPaletteIconShape`.
- **Default hotkey**: only if the tool needs one; choose one that is unlikely to clash.
- **Content page**: yes if the tool is something you *work in* (lists, a chat, a vault, a viewer), no if it only
  runs in the background and is configured (a hotkey, a hook). See "Content page and settings page" in §4.
- **Settings and sections**: list them.
- **Win32 needs**: which hooks, hotkeys, overlays or new P/Invoke the tool requires.

### 2. Files to create (same shape as `src/Helm.Modules.AlwaysOnTop`, the reference module)

**PC** (the layout below):

```
src/Helm.Modules.<Prefix>/
  Helm.Modules.<Prefix>.csproj      UseWPF=true, ProjectReference ..\Helm.Core.Windows only, InternalsVisibleTo Helm.Tests
  <Prefix>Settings.cs               IVersionedSettings: static CurrentVersion => 1, Version, sensible defaults
  <Prefix>Module.cs                 sealed : HelmModuleBase — Id, DisplayName, Description, Group, Icon, SettingsPageType, Hotkeys, EnableAsync, DisableAsync
  <Prefix>Engine.cs                 (if it runs in the background) owns threads/hooks/overlays; IAsyncDisposable
  <Prefix>ViewModel.cs              ObservableObject; loads settings, writes back on change (Save pattern with _loading guard)
  <Prefix>Page.xaml(.cs)            the SETTINGS page: core:ModulePageBase, Module="{Binding Module}", DataContext = view model
  <Prefix>ContentPage.xaml(.cs)     (tools with content, §4) a plain Page with the tool itself; registered as a singleton
  <Prefix>Services.cs               AddXxxModule() => services.AddHelmModule<Module, Page, ViewModel>() (+ extra singletons,
                                    + AddSingleton<<Prefix>ContentPage>())
```

Wire it up:
1. `dotnet sln add src/Helm.Modules.<Prefix>/Helm.Modules.<Prefix>.csproj --solution-folder src`
2. `dotnet add src/Helm.App reference …` and `dotnet add tests/Helm.Tests reference …`
3. Add one line in `src/Helm.App/Hosting/HelmModules.cs`: `services.Add<Prefix>Module();`

**Android** (the layout below):

```
src/Helm.Modules.<Prefix>.Android/
  Helm.Modules.<Prefix>.Android.csproj  net10.0-android, SupportedOSPlatformVersion 26, PlatformTarget AnyCPU,
                                        Avalonia 12.1.3 + FluentIcons.Avalonia, ProjectReference ..\Helm.Core.Android only
                                        (brings Helm.Core and Helm.Shell); copy the <Using Remove> block and the
                                        SQLitePCLRaw.bundle_e_sqlite3 pin from Helm.Core.Android.csproj
  <Prefix>Module.cs                     sealed : AndroidModuleBase — Id, DisplayName, Description, Group, Icon, PageType,
                                        EnableAsync, DisableAsync
  <Prefix>Page.axaml(.cs)               the SETTINGS page: ui:ModulePageBase, Module="{Binding Module}", sets its own
                                        DataContext from DI
  <Prefix>ContentPage.axaml(.cs)        (tools with content, §4) a UserControl with the tool itself; AddTransient
  <Prefix>ViewModel.cs                  (or the shared one from the .Core project, see below)
  <Prefix>Services.cs                   AddXxxModule() => services.AddAndroidModule<Module, Page>() (+ extra singletons)
```

`AndroidModuleBase`, `AddAndroidModule` (namespace `Helm.Core.Modules`) and `ModulePageBase` (namespace
`Helm.Core.Ui`) live in `src/Helm.Core.Android`, the counterpart of Helm.Core.Windows. A tool never references
`Helm.App.Android`. Wire it up with a ProjectReference from `src/Helm.App.Android` and one line in
`src/Helm.App.Android/Hosting/AndroidModules.cs`. It is **not** added to `Helm.sln` (CI builds it on Linux).

**PC + Android**: everything that is not UI goes in a portable `src/Helm.Modules.<Prefix>.Core/` (`net8.0`,
`SupportedOSPlatformVersion` empty, `PlatformTarget` AnyCPU, ProjectReference `..\Helm.Core`): settings, engine logic,
the view model (CommunityToolkit.Mvvm, no WPF/Avalonia types). The PC project and the Android project above are then
thin UI + module wrappers that reference it. The settings file and module id are identical, so synced settings (if any)
mean the same on both.

Do **not** change the shell (navigation, Home, tray, search). They pick the module up automatically from `ModuleGroup`, `Hotkeys` and the `CardHeader` titles on its page.

### 3. Architecture rules

- PC modules reference **Helm.Core.Windows** (which brings Helm.Core), never Helm.App. Android modules and shared
  `.Core` projects reference **Helm.Core** only. Nothing Windows- or Android-specific goes into Helm.Core.
- **No P/Invoke in modules.** If a Win32 API is missing, add it to `src/Helm.Core.Windows/NativeMethods.txt` (CsWin32) and expose a managed wrapper from a Core service (`IWindowService`, `IMonitorService`, or a new Core service).
- **Reuse Core infrastructure**:
  - `IHotkeyManager.TryRegisterAsync` for global hotkeys. It never throws; show a failure through `StatusMessage`.
  - `LowLevelKeyboardHook` / `LowLevelMouseHook`: call `.Acquire()` and dispose the lease. `Intercept` handlers must return within microseconds; post any real work to your thread.
  - `WinEventHook.InstallAsync(thread, …)` for window events.
  - `MessageLoopThread` for your engine's own thread.
  - `OverlayWindow` for click-through GDI overlays.
  - `ISettingsStoreFactory.Get<T>(moduleId)` for settings, `IUiDispatcher` to reach the UI thread.
- **Threading**: never block the UI thread. Hooks, timers and overlays live on the module's `MessageLoopThread`; UI updates go through `IUiDispatcher.Post`.
- **Lifecycle**:
  - `EnableAsync` starts everything.
  - `DisableAsync` must release everything (hooks, hotkeys, overlays, topmost state, threads) and be idempotent.
  - `ModuleRegistry` calls both methods when the toggle changes, on exit, and before an update.
- **Errors are never crashes.** Log them with `ILogger<T>` and set `StatusMessage`, which the page shows as an InfoBar. Timer and `async void` callbacks must catch everything.
- **MVVM**: logic lives in view models or engines. Code-behind contains only view glue.
- Settings save through `store.Update(...)`, which is debounced and atomic. Read the current values from `store.Current`.

### 4. UI rules (keeps every tool looking the same)

#### Content page and settings page (the structure every tool follows)

A tool you work in keeps **what it is for** apart from **how it is configured**:

| | Content page | Settings page |
|---|---|---|
| What | The tool itself: its lists, editor, chat, report, history | The **Enable** card and the options |
| Opened from | The navigation menu (PC) / drawer (Android) and the **Quick access** tile on Home | Home → **Utilities** (the `>` next to the toggle) and search results |
| Type | PC: a plain `Page`; Android: a `UserControl` | `core:ModulePageBase` / `ui:ModulePageBase` (as below) |
| Declared by | the module implements `IModuleContent` (`Helm.Core.Modules`): `Type ContentPageType` | `SettingsPageType` (PC) / `PageType` (Android) |

- The shell does the routing from `IModuleContent`; do not change the shell for a new tool. A module with its own Quick
  access action (`IModuleLauncher`, e.g. Claude Chat opening its window) keeps that action.
- PC page margin: the content page's root panel uses `Margin="24,16,24,24"`, the same as Home, General and
  `ModulePageBase`, so the title sits 16 px below the window's title bar on every page.
- Content page layout: the tool's title (PC; the Android app bar shows it already), then the tool's main picker (e.g.
  the workspace) as the first card under it, full width and easy to see, then the content. No settings button, no
  Enable card, no icon + description block: settings are reached from Home → Utilities only. When the tool is off, show
  a short note ("<Tool> is turned off…") and make the content read-only; the data stays.
- Settings page: only things you set once and rarely change (create, rename, delete, defaults, display options,
  export). Put an **Open <Tool>** card first that goes to the content page. Everything a user does every day belongs on
  the content page.
- Search indexes only the settings page (its `CardHeader` titles), so every option must live there.
- Both pages bind the same singleton view model; state lives in the view model or the module, never in a page.
- Tools without content (they only run in the background) have just the settings page, and every entry point opens it.
- Reference: Tracker (`src/Helm.Modules.Tracker*`: `TrackerContentPage` + `TrackerPage`).

#### Settings page

The page is a `core:ModulePageBase`. It already renders the title, the icon + description block, the InfoBar and the big **Enable <Tool>** toggle, so do not add those yourself. The page body is a `StackPanel` of sections:

```xml
<core:ModulePageBase x:Class="Helm.Modules.<Prefix>.<Prefix>Page"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
    xmlns:core="clr-namespace:Helm.Core.Ui;assembly=Helm.Core.Windows"
    Module="{Binding Module}">
    <StackPanel>
        <!-- Section title -->
        <TextBlock Text="Activation" Style="{DynamicResource HelmSectionHeader}" />

        <!-- Simple setting: CardControl + CardHeader + control on the right -->
        <ui:CardControl Margin="0,0,0,4" Icon="{ui:SymbolIcon Keyboard24}">
            <ui:CardControl.Header>
                <core:CardHeader Title="Activation shortcut" Description="What it does, in one sentence." />
            </ui:CardControl.Header>
            <core:HotkeyPicker Gesture="{Binding Hotkey}" DefaultGesture="{Binding DefaultHotkey}" />
        </ui:CardControl>

        <!-- Group of related settings: CardExpander + rows -->
        <ui:CardExpander Margin="0,0,0,4" Icon="{ui:SymbolIcon Color24}">
            <ui:CardExpander.Header>
                <core:CardHeader Title="Appearance" Description="Short description." />
            </ui:CardExpander.Header>
            <StackPanel>
                <Border Style="{DynamicResource HelmExpanderRow}" BorderThickness="0">   <!-- first row: no top line -->
                    <core:RowLayout>
                        <core:CardHeader Title="Setting" Description="Explanation." />
                        <ui:ToggleSwitch IsChecked="{Binding SomeFlag}" />
                    </core:RowLayout>
                </Border>
                <Border Style="{DynamicResource HelmExpanderRow}">                        <!-- next rows -->
                    <core:RowLayout>
                        <core:CardHeader Title="Color" />
                        <core:ColorSwatchPicker Color="{Binding Color}" />
                    </core:RowLayout>
                </Border>
            </StackPanel>
        </ui:CardExpander>
    </StackPanel>
</core:ModulePageBase>
```

- **Every setting title uses `core:CardHeader`.** Global search indexes those titles.
- **Inside expanders, every row is `core:RowLayout`.** Never use a one-cell Grid with a right-aligned control; on narrow windows the text runs under the control. If a header has a control on the right, use `core:RowLayout` in the header as well.
- Shared controls to reuse: `core:HotkeyPicker` (hotkeys), `core:ColorSwatchPicker` (colors, stored as `"#RRGGBB"`), `core:Keycaps` (showing shortcuts), `core:DateTimeBox` (a day and a time in one box; bind `Date` and `Time`, set `Lean`, `ShowTime`, `AllowEmpty`; Android has the same control in `Helm.Core.Ui`), `ui:ToggleSwitch`, `ComboBox`, `Slider` with a value `TextBlock`, `ui:TextBox` (for excluded-app lists use `AcceptsReturn` and `UpdateSourceTrigger=LostFocus`, parsed with `ProcessExclusions.ParseLines`).
- Colors and fonts come from theme resources only (`{DynamicResource TextFillColorPrimaryBrush}`, `TextFillColorSecondaryBrush`, `CardBackgroundFillColorDefaultBrush`, `AccentTextFillColorPrimaryBrush`, …). Styles: `HelmSectionHeader`, `HelmSecondaryText`, `HelmSurface`, `HelmExpanderRow`. **No hard-coded hex colors in XAML**; user-chosen colors come from settings.
- Card spacing is always `Margin="0,0,0,4"`. Sections are separated by `HelmSectionHeader`. The page provides the outer 24 px inset, so don't add another one.
- Texts are English, in sentence case, and short. A description is one sentence that explains the effect.
- Typical section order: *Activation / Shortcut* → *Behavior* → *Appearance* → *Excluded apps* → tool-specific lists.
- Full-screen overlays or editors (like the old Zones editor) use one window per monitor, are placed in physical pixels, close on Esc, and put buttons in a `WrapPanel` so they never overlap.

#### Android pages

- Phone first: design for 360 dp wide, one column; stack what the PC page puts side by side. The shell provides
  the app bar (title), the 16 px inset and scrolling. The page is a `ui:ModulePageBase`
  (`xmlns:ui="using:Helm.Core.Ui"`), which already renders the icon + description, the InfoBar for
  `StatusMessage` and the **Enable <Tool>** card, like on PC; its content is a `StackPanel` of sections:

  ```xml
  <ui:ModulePageBase xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:ui="using:Helm.Core.Ui" xmlns:vm="using:Helm.Modules.<Prefix>"
      x:Class="Helm.Modules.<Prefix>.<Prefix>Page" x:DataType="vm:<Prefix>ViewModel" Module="{Binding Module}">
      <StackPanel>
          <TextBlock Text="Behavior" Classes="section" />
          <Border Classes="card">
              <StackPanel>
                  <DockPanel>
                      <ToggleSwitch DockPanel.Dock="Right" IsChecked="{Binding SomeFlag}" OnContent="" OffContent="" />
                      <StackPanel VerticalAlignment="Center">
                          <TextBlock Text="Setting" Classes="title" />
                          <TextBlock Text="What it does, in one sentence." Classes="secondary" />
                      </StackPanel>
                  </DockPanel>
                  <Border Classes="row"> <!-- next row of the same card --> </Border>
              </StackPanel>
          </Border>
      </StackPanel>
  </ui:ModulePageBase>
  ```
- The shell creates a new page each time the tool is opened (pages are transient); the view model and module are
  singletons.
- Same vocabulary as the PC page: section titles `TextBlock Classes="section"`, cards `Border Classes="card"`,
  rows inside a card `Border Classes="row"`, secondary text `TextBlock Classes="secondary"`, warnings
  `Border Classes="infobar warning"`, icons `ic:SymbolIcon` (`xmlns:ic="using:FluentIcons.Avalonia"`). Colors come
  from `{DynamicResource Helm…}` / Fluent resources only (both themes are defined in `App.axaml`).
- Compiled bindings are on (`x:DataType` on every view and `DataTemplate`). Buttons use `Classes="accent"` for the
  primary action and `Classes="danger"` for destructive ones; ask through `IDialogService` before anything destructive.
- No hotkeys and no Win32. Long-running work must survive the activity being recreated: state lives in the view
  model / module, never in the view.
- Platform services to reuse instead of calling Android APIs from a view model: `ISecretProtector` (Android
  Keystore, for small secrets), `IDialogService` (confirmations), `IClipboardService`, `IUiDispatcher`,
  `IProcessLauncher` (open URLs), `ISettingsStoreFactory`. Anything that needs the activity (permissions, pickers,
  window flags) goes behind an interface in the tool's portable code, implemented in the Android project.
- App-private files go under `HelmPaths` (`/data/data/<package>/files/Helm`); `android:allowBackup` is off, so
  anything that must survive losing the phone has to be synced or exported.

### 5. Home, tray and search integration (free if you follow the contract)

- `Hotkeys` returns a `HotkeyDefinition` for every shortcut, including hold-keys (`Key = 0`, e.g. "Hold Shift"). Home lists them and the conflict tile checks the registered ones. Call `NotifyHotkeysChanged()` when they change.
- `Description` is one sentence and appears under the page title.
- The module shows up on its own in the nav group, Home Quick access / Utilities, the tray toggles and search.
  With `IModuleContent`, the nav item and the Quick access tile open the content page and the Utilities chevron and
  search open the settings page (§4).
- **Quick Capture** (optional, both platforms): a tool whose data can be written from one line of text registers an
  `ICaptureTarget` (`Helm.Core.Capture`) in its `.Core` services, e.g. `services.AddSingleton<ICaptureTarget, MyTarget>()`.
  Give it a one-letter `Prefix` that is not taken (`n` Notes, `t` task, `d` debt, `w` Watch Later, `m` mission, `s` spending), a `Preview` that says what Enter
  will do, and never throw. See `NoteCaptureTarget`, `TaskCaptureTarget`.
- **Command palette** (optional, PC): a tool with things to find registers an `IPaletteProvider`
  (`Helm.Core.Palette`) in its PC project. Score with `PaletteQuery.Score` (accents and case do not matter), keep it
  fast (it runs on every key), resolve `IShellNavigation` from `IServiceProvider` inside `Run` (resolving it in the
  constructor makes a DI cycle through the module list), and never show secrets. See `NotesPaletteProvider`,
  `VaultPaletteProvider` (only while unlocked, titles only).
- Neither Quick Capture nor the palette references a tool: they find targets and providers in DI and offer only those
  of tools that are on.

### 6. Pitfalls this repo has already hit (check them)

- **Icons**: every `SymbolRegular` name must exist. Validate with:
  ```powershell
  $a=[Reflection.Assembly]::LoadFrom("$env:USERPROFILE\.nuget\packages\wpf-ui\4.3.0\lib\net472\Wpf.Ui.dll"); [Enum]::GetNames($a.GetType('Wpf.Ui.Controls.SymbolRegular')) | Select-String '<Name>'
  ```
  A missing name compiles fine but crashes at runtime with a XamlParseException.
  The name existing is not enough: WPF-UI 4.3 draws a symbol as a single 16-bit `char`, so any symbol whose value
  is above U+FFFF (e.g. `ArrowExport24` = U+F0281, `DocumentArrowDown24` = U+F0527) shows a stray letter such as
  "ʁ". Check the value too, and pick another icon if it is above `0xFFFF`:
  ```powershell
  '{0:X}' -f [int][Enum]::Parse($a.GetType('Wpf.Ui.Controls.SymbolRegular'), '<Name>')
  ```
- **Settings JSON**: doubles may be NaN, which is allowed. Dictionaries that need case-insensitive keys need `[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]`.
- **App types** are `internal` with `public` constructors, because DI needs public constructors. XAML views in App use `x:ClassModifier="internal"`.
- **Windows PowerShell 5.1** mangles UTF-8 files when you edit them with `Get-Content`/`Set-Content`. Edit with the Edit/Write tools, or with `[IO.File]::ReadAllText/WriteAllText` and an explicit `UTF8Encoding($false)`.
- **Git commit messages**: pass them with `git commit -F <file>` (UTF-8, no BOM). Quotes in `-m` break under PowerShell 5.1.
- In PowerShell scripts, don't name functions after aliases (`Move`, `Copy`, `Sort`…); the alias wins.

### 7. Tests and verification (required before finishing)

1. Put pure logic (math, parsing, state machines) in plain classes and unit-test it in `tests/Helm.Tests` (xUnit). Test WPF layout pieces on an STA thread; `RowLayoutTests` shows how.
2. `dotnet build -c Release -warnaserror` and `dotnet test -c Release` must both pass with 0 warnings.
3. Smoke-test in isolation, without touching the user's installed Helm or its data:
   ```powershell
   dotnet build src/Helm.App -o <scratch>\run
   <scratch>\run\Helm.exe --no-elevate --allow-multiple --data-dir <scratch>\testdata --page <Prefix>
   ```
   Take a screenshot of **only that window** (PrintWindow). Don't simulate clicks or keys on the user's desktop. If an interaction really must be tested, target only the test window and check the real cursor position first. Never let a full-screen overlay stay open on the user's monitors.
4. Check the page at 1000 px width, both with the navigation pinned and unpinned: no clipped or overlapping text.
5. **Android** (if it is on Android): build and install on LDPlayer/an emulator as in [android.md](android.md), drive it
   with `adb shell input`, and check screenshots from `adb exec-out screencap -p` (the emulator only). Check light and
   dark theme, and that the Release build (trimmed) still works if the tool uses reflection or JSON.

### 8. Ship

- Add the tool under `## [Unreleased]` at the top of `CHANGELOG.md` ("### Added – <Tool> (PC / Android / PC and Android): …").
- Add a row for the tool to the README tool table (and to the Tool list in `.github/ISSUE_TEMPLATE/bug_report.yml`) and remove it from the roadmap if it was listed there.
- Bump `<Version>` in `Directory.Build.props` (minor for a new tool). Commit, tag `vX.Y.Z`, and push `main` plus the tag. `.github/workflows/release.yml` publishes the release, and installed copies update from General → Updates.
- Finish with a short summary: what the tool does, its shortcuts, which settings exist, what was verified, and what the user should test by hand.

### 9. MCP tools (only if the tool offers tools to Claude)

A module offers tools to Claude through an `IMcpToolProvider` singleton (see `NotesMcpTools.cs`). Every tool says what
a call can do, and Helm's consent policy decides from that whether the user is asked first
([mcp-security.md](mcp-security.md)):

- **`ReadOnly = true`** for a tool that only reads Helm's data: never asked. Anything else is `McpRisk.Change` by
  default (asked when Helm runs as Administrator, or when the user turned on *Ask before every change*).
- **`Risk = McpRisk.Remote`** for anything that runs on another machine: always asked. Never use `Remote` for local
  work, and never mark something that changes data as `ReadOnly`.
- **`AskFirst`** builds the question for one exact call when the default one ("Use “tool_name”" with the arguments)
  would not say enough. Return an `McpConsentRequest`:
  - `Title`: the action and its target, short: `Run “Deploy” on server “web”`.
  - `WhatItDoes`: plain words, what happens if the user allows it: "Runs ./deploy.sh in /srv/app on web. The site
    restarts." Not the tool's description.
  - `WhyAsk`: the risk, why Helm asks instead of just doing it: "It changes a live server; a wrong deploy takes the
    site down." Helm adds the Administrator/root line itself when `Elevated` is set.
  - `Details`: the exact command line or arguments, shown verbatim in a monospace box. **Never put a secret in it**
    (it is also written to the activity log).
  - `Target`: the server, list or note it acts on (session allowances are per target).
  - `Elevated`: Helm runs as Administrator (`Environment.IsPrivilegedProcess`) for local work, or the remote user is
    root. `Danger = McpDanger.High` for anything destructive or hard to undo. High removes "Allow for this session";
    so does `Elevated` on a `Remote` call (a `Change` only touches Helm's data, so it keeps the option).
- A secret argument (password, token, PIN) gets `"writeOnly": true` in its schema, so the default question shows it as
  `•••`. Names containing password, token, secret, key or OTP are hidden too.
- Throw `McpToolException` with a sentence Claude can act on when a call cannot run as asked.
- Test the tool's risk and question in the tool's own tests: a fake `IMcpConsent` sees the `McpConsentRequest`.
