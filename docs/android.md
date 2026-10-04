# Helm for Android

Helm has two apps built from one repository:

| | Windows | Android |
|---|---|---|
| Project | `src/Helm.App` (WPF, WPF-UI) | `src/Helm.App.Android` (Avalonia 12, Fluent theme) |
| Home, General | Same content: What's new, Updates tile, Quick access, Utilities; Updates, theme, Sync, logs, reset | Same, without the Windows-only rows (administrator mode, run at startup, tray, shortcuts) |
| Tools | `src/Helm.App/Hosting/HelmModules.cs` | `src/Helm.App.Android/Hosting/AndroidModules.cs` |
| Distribution | `Helm-win-Setup.exe` + Velopack updates | `Helm-android.apk` + the in-app updater |
| Minimum OS | Windows 10 19041 | Android 8.0 (API 26) |

Every release tag builds both and attaches both files to the same GitHub release, with the same version number.

## Shared code

```
src/
  Helm.Core/          net8.0, no UI: module contract (IModule, ModuleBase, ModuleRegistry<T>), settings, sync,
                      update rules (UpdatePolicy, UpdateStateMachine, GitHubReleases), platform seams
                      (ISecretProtector, IProcessLauncher, IUiDispatcher, IUpdateService, ...)
  Helm.Shell/         net8.0: the view models Home and General share (UpdatesViewModel, SyncViewModel,
                      UpdateTile) and their seams (IDialogService, IClipboardService, IDeviceInfo)
  Helm.Core.Windows/  WPF + Win32 on top of Helm.Core: IHelmModule, hooks, hotkeys, overlays, DPAPI, WPF controls.
                      Namespaces stay Helm.Core.*, so Windows modules did not change when Core was split.
  Helm.Core.Android/  Android infrastructure for tools, like Helm.Core.Windows: IAndroidModule / AndroidModuleBase,
                      AddAndroidModule, the page frame ModulePageBase, ActivityHost (the activity, results,
                      lifecycle), IBackHandler, and HelmAndroidServices (the process-wide service provider, for
                      widgets and receivers that run without the activity)
  Helm.App.Android/   the Android app: shell, Avalonia views, Android Keystore, GitHub APK updater
```

Platform pieces behind the shared seams:

| Seam | Windows | Android |
|---|---|---|
| `ISecretProtector` (sync token, account key, replica key) | DPAPI, current user | AES-256-GCM key in the Android Keystore |
| `IUpdateService` | Velopack (`VelopackUpdateService`) | `AndroidUpdateService`: GitHub releases → download → SHA-256 check → system installer |
| `IDialogService` | WPF-UI message box | overlay in `MainView` |
| `IProcessLauncher` | explorer / shell | intents (open URL, share a log file) |

Settings and sync data use the same formats on both, so a phone joins the same sync account as the PC
(General → Sync → "I have a device token").

## Tools and platforms

Each tool says which platforms it runs on, and is registered in that platform's list:

- **Windows only** (e.g. Claude Chat): `src/Helm.Modules.<Name>` as today, one line in `HelmModules.cs`.
- **Android only**: `src/Helm.Modules.<Name>.Android` (`net10.0-android`, Avalonia, references
  `Helm.Core.Android`), a module deriving from `AndroidModuleBase` and a `ui:ModulePageBase` page, registered with
  `services.AddAndroidModule<TModule, TPage>()` in `AndroidModules.cs`. Keep the project's root namespace free of
  a trailing `.Android` (e.g. `Helm.Modules.<Name>`), or it hides the `Android.*` SDK namespaces.
- **Both**: the logic (settings, engine, view model) in a portable `src/Helm.Modules.<Name>.Core` (`net8.0`,
  references `Helm.Core`), plus a thin UI project per platform that references it.

Android tools use the same Fluent System Icons as the Windows ones (`FluentIcons.Avalonia`, `Symbol.<Name>`).

A tool can open its own page from outside the app (a widget, a shortcut) by launching Helm with the string extra
`ShellIntents.ExtraModule` set to its module id; `MainActivity` navigates there. Tracker's widget is the example
(a list with a RemoteViewsService): `src/Helm.Modules.Tracker.Android/Widget` (layouts in its `Resources/`, looked up
by name at run time); Missions' widget (`src/Helm.Modules.Missions.Android/Widget`) is a simpler fixed layout.

Wallet reads other apps' notifications with a `NotificationListenerService`
(`src/Helm.Modules.Wallet.Android/Capture`, fixed `Name` so the access the user granted survives updates). Android
binds it in the app process once the user turns Helm on in *Notification access*; on Android 13+ a sideloaded APK
first needs *Allow restricted settings* in App info. The listener drops every notification that is not from a bank on
the spot and hands the rest to `WalletCapture` in the shared core, which is where the reading is tested.

Parts that run without Helm's activity (a widget, a broadcast, Quick Capture's share dialog and Quick Settings tile)
get services from `HelmAndroidServices.Current`, and read whether a tool is on from `GeneralSettings.EnabledModules`
(missing means on): the module registry only starts with the activity. Quick Capture
(`src/Helm.Modules.QuickCapture.Android`) is the example of an exported activity and a `TileService` declared in a
tool's own project, with fixed `Name`s so that disabling them while the tool is off (`SetComponentEnabledSetting`)
is remembered.

[docs/new-tool-prompt.md](new-tool-prompt.md) has the full rules.

## Build and run locally

The Android project is **not in `Helm.sln`**, so `dotnet build` at the root keeps working on machines without the
Android workload. It needs the .NET 10 SDK with the `android` workload, JDK 17 and the Android SDK:

```powershell
dotnet workload install android      # needs an elevated prompt when the SDK is in Program Files
dotnet build src/Helm.App.Android -t:InstallAndroidDependencies -f net10.0-android `
  -p:AndroidSdkDirectory=<sdk dir> -p:JavaSdkDirectory=<jdk dir> -p:AcceptAndroidSDKLicenses=True
dotnet build src/Helm.App.Android -c Debug -p:EmbedAssembliesIntoApk=true `
  -p:AndroidSdkDirectory=<sdk dir> -p:JavaSdkDirectory=<jdk dir>
adb install -r src/Helm.App.Android/bin/Debug/net10.0-android/com.huyhung1404.helm-Signed.apk
```

Without admin rights, install a private SDK instead (`dotnet-install.ps1 -InstallDir <dir>`, then run
`<dir>\dotnet.exe workload install android` with `DOTNET_ROOT=<dir>`).

Notes:

- **`EmbedAssembliesIntoApk=true`** is needed when you install the Debug APK with `adb install`. Without it, Debug
  uses Fast Deployment and the app aborts at start ("No assemblies found … Assuming this is part of Fast
  Deployment"). `dotnet build -t:Install` deploys the assemblies itself.
- **LDPlayer / emulators** are x86_64; the APK carries `android-arm64` and `android-x64`. LDPlayer's adb is
  `C:\LDPlayer\LDPlayer9\adb.exe` (device `emulator-5554`).
- **Screenshots**: `adb exec-out screencap -p > shot.png` captures the emulator only, never the desktop.
- **Taps from adb**: Android 9's `input tap` sends touches with tool type UNKNOWN, which Avalonia drops;
  `MainActivity` passes them on as a finger, so `adb shell input tap/swipe/text` drive the app.
- **Debug builds do not auto-update** ("development build"): only APKs signed with the release key can install
  over each other. Uninstall first when switching between a debug and a release build.

### Development overrides

`-p:HelmAndroidEnvFile=<file>` bakes `NAME=value` lines into the APK as environment variables:

| Variable | Use |
|---|---|
| `HELM_SYNC_SERVER=http://127.0.0.1:8787` | a local sync worker (`npx wrangler dev`) reached through `adb reverse tcp:8787 tcp:8787` |
| `HELM_UPDATE_SOURCE=http://127.0.0.1:8000/releases.json` | a fake GitHub "list releases" response, to test the updater |

Plain HTTP is allowed only to `127.0.0.1` and `localhost` (`Resources/xml/network_security_config.xml`).

## Versions

`versionName` is `<Version>` from `Directory.Build.props` (CI: the tag). `versionCode` is
`major*1000000 + minor*10000 + patch*100 + (99 for stable, else the preview number)`, so `1.2.0-preview.3`
(1020003) installs before `1.2.0` (1020099) and every release installs over the previous one. Android cannot
downgrade: switching from Preview back to Stable waits until the stable version is newer.

## Release signing

The release APK is signed with one RSA 4096 key (PKCS12, alias `helm`, valid 100 years). **Losing it means no phone
can install updates any more** (users would have to uninstall and reinstall). Keep the keystore and its password
in a password manager or another safe backup.

CI reads two repository secrets (GitHub → Settings → Secrets and variables → Actions):

| Secret | Value |
|---|---|
| `ANDROID_KEYSTORE_BASE64` | the keystore file, base64 (`[Convert]::ToBase64String([IO.File]::ReadAllBytes('helm-release.keystore'))`) |
| `ANDROID_KEYSTORE_PASSWORD` | the store/key password |

The release job fails when they are missing, prints the signer's certificate SHA-256 to the log, and deletes the
decoded keystore. To build a signed APK locally:

```powershell
$env:HELM_KEYSTORE_PASSWORD = '<password>'
dotnet build src/Helm.App.Android -c Release -p:Version=0.8.0 -p:HelmKeystore=<path>\helm-release.keystore ...
```

## Updates on the phone

Same channels and settings as Windows (General → Updates). Helm checks 30 seconds after start and then every
6 hours while it is open, downloads in the background (if *Automatically download updates* is on) and shows
**Install update**. Android always asks before installing: the first time it opens *Install unknown apps* for Helm,
then the system installer. When the update is installed, Android closes Helm; open it again.
