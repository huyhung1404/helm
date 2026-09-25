# Prompt: add a new tool to Helm

How to use: fill in the two lines below, then paste this whole file into Claude Code at the root of the `helm` repo. Everything under "Rules" tells the AI how to build the tool, so it matches the existing Helm interface and code structure. You don't need to write anything else.

```
Tool name:     <e.g. Color Picker>
What it does:  <one or two sentences, e.g. "Win+Shift+C picks the color under the cursor and copies it as HEX">
```

---

## Rules for the AI

You are adding one new tool ("module") to **Helm**, a WPF (.NET 8) Windows toolkit in the style of PowerToys. Work from the two lines above. Decide every detail they leave open, following the rules below, and ask only if something is truly ambiguous. The finished tool must look and behave like any other Helm page.

### 1. Plan first (short)

Print a short plan before you start, then continue without waiting:
- **Module id**: kebab-case, e.g. `color-picker`.
- **Class prefix**: PascalCase, e.g. `ColorPicker`.
- **Group**: one of `SystemTools`, `WindowingAndLayouts`, `InputAndOutput`, `FileManagement`, `Advanced`. Pick the closest.
- **Icon**: a `SymbolRegular` value that exists (see §6).
- **Default hotkey**: only if the tool needs one; choose one that is unlikely to clash.
- **Settings and sections**: list them.
- **Win32 needs**: which hooks, hotkeys, overlays or new P/Invoke the tool requires.

### 2. Files to create (same shape as `src/Helm.Modules.AlwaysOnTop`, the reference module)

```
src/Helm.Modules.<Prefix>/
  Helm.Modules.<Prefix>.csproj      UseWPF=true, ProjectReference ..\Helm.Core only, InternalsVisibleTo Helm.Tests
  <Prefix>Settings.cs               IVersionedSettings: static CurrentVersion => 1, Version, sensible defaults
  <Prefix>Module.cs                 sealed : HelmModuleBase — Id, DisplayName, Description, Group, Icon, SettingsPageType, Hotkeys, EnableAsync, DisableAsync
  <Prefix>Engine.cs                 (if it runs in the background) owns threads/hooks/overlays; IAsyncDisposable
  <Prefix>ViewModel.cs              ObservableObject; loads settings, writes back on change (Save pattern with _loading guard)
  <Prefix>Page.xaml(.cs)            core:ModulePageBase, Module="{Binding Module}", DataContext = view model
  <Prefix>Services.cs               AddXxxModule() => services.AddHelmModule<Module, Page, ViewModel>() (+ extra singletons)
```

Wire it up:
1. `dotnet sln add src/Helm.Modules.<Prefix>/Helm.Modules.<Prefix>.csproj --solution-folder src`
2. `dotnet add src/Helm.App reference …` and `dotnet add tests/Helm.Tests reference …`
3. Add one line in `src/Helm.App/Hosting/HelmModules.cs`: `services.Add<Prefix>Module();`

Do **not** change the shell (navigation, Home, tray, search). They pick the module up automatically from `ModuleGroup`, `Hotkeys` and the `CardHeader` titles on its page.

### 3. Architecture rules

- Modules reference **Helm.Core only**, never Helm.App.
- **No P/Invoke in modules.** If a Win32 API is missing, add it to `src/Helm.Core/NativeMethods.txt` (CsWin32) and expose a managed wrapper from a Core service (`IWindowService`, `IMonitorService`, or a new Core service).
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

The page is a `core:ModulePageBase`. It already renders the title, the icon + description block, the InfoBar and the big **Enable <Tool>** toggle, so do not add those yourself. The page body is a `StackPanel` of sections:

```xml
<core:ModulePageBase x:Class="Helm.Modules.<Prefix>.<Prefix>Page"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
    xmlns:core="clr-namespace:Helm.Core.Ui;assembly=Helm.Core"
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
- Shared controls to reuse: `core:HotkeyPicker` (hotkeys), `core:ColorSwatchPicker` (colors, stored as `"#RRGGBB"`), `core:Keycaps` (showing shortcuts), `ui:ToggleSwitch`, `ComboBox`, `Slider` with a value `TextBlock`, `ui:TextBox` (for excluded-app lists use `AcceptsReturn` and `UpdateSourceTrigger=LostFocus`, parsed with `ProcessExclusions.ParseLines`).
- Colors and fonts come from theme resources only (`{DynamicResource TextFillColorPrimaryBrush}`, `TextFillColorSecondaryBrush`, `CardBackgroundFillColorDefaultBrush`, `AccentTextFillColorPrimaryBrush`, …). Styles: `HelmSectionHeader`, `HelmSecondaryText`, `HelmSurface`, `HelmExpanderRow`. **No hard-coded hex colors in XAML**; user-chosen colors come from settings.
- Card spacing is always `Margin="0,0,0,4"`. Sections are separated by `HelmSectionHeader`. The page provides the outer 24 px inset, so don't add another one.
- Texts are English, in sentence case, and short. A description is one sentence that explains the effect.
- Typical section order: *Activation / Shortcut* → *Behavior* → *Appearance* → *Excluded apps* → tool-specific lists.
- Full-screen overlays or editors (like the old Zones editor) use one window per monitor, are placed in physical pixels, close on Esc, and put buttons in a `WrapPanel` so they never overlap.

### 5. Home, tray and search integration (free if you follow the contract)

- `Hotkeys` returns a `HotkeyDefinition` for every shortcut, including hold-keys (`Key = 0`, e.g. "Hold Shift"). Home lists them and the conflict tile checks the registered ones. Call `NotifyHotkeysChanged()` when they change.
- `Description` is one sentence and appears under the page title.
- The module shows up on its own in the nav group, Home Quick access / Utilities, the tray toggles and search.

### 6. Pitfalls this repo has already hit (check them)

- **Icons**: every `SymbolRegular` name must exist. Validate with:
  ```powershell
  $a=[Reflection.Assembly]::LoadFrom("$env:USERPROFILE\.nuget\packages\wpf-ui\4.3.0\lib\net472\Wpf.Ui.dll"); [Enum]::GetNames($a.GetType('Wpf.Ui.Controls.SymbolRegular')) | Select-String '<Name>'
  ```
  A missing name compiles fine but crashes at runtime with a XamlParseException.
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

### 8. Ship

- Add a section at the top of `CHANGELOG.md` ("### Added – <Tool>: …").
- Add a row for the tool to the README tool table and remove it from the roadmap if it was listed there.
- Bump `<Version>` in `Directory.Build.props` (minor for a new tool). Commit, tag `vX.Y.Z`, and push `main` plus the tag. `.github/workflows/release.yml` publishes the release, and installed copies update from General → Updates.
- Finish with a short summary: what the tool does, its shortcuts, which settings exist, what was verified, and what the user should test by hand.
