# HANDOFF Helm

Updated 2026-11-26 18:05, packed on the work PC, session "Helm becomes passive (Fairy automates via MCP); every tool moved to
the shared Avalonia UI; Helm.App rewritten as a pure Avalonia app, no WPF left anywhere (v0.93.0)". Sessions still to pack: none.
For Claude (skill handoff; the user reads it with `compact-skill dịch`). New machine: read all of this, then CLAUDE.md and
`.claude/skills/helm-ui-ux/SKILL.md` (read it BEFORE any UI work, and before the plan).

## 1. Last message (verbatim)
> đóng gói

State: done (this file). The request before it: "relase bản mới nào" (done: v0.93.0 pushed). Still open from earlier, not answered
by a fix yet:
> mở các tab bây giờ anh đang cảm thấy hơi lag, nguyên nhân có thể là gì

Claude's answer (no fix made): Anh was running a **Debug** build (`C:\tmp\happ-avalonia`); other suspects are the full-page
`BlurEffect` of `Motion.EnterPage` (`src/Helm.Ui/Ui/Motion.cs`, blur 16 → 0 on every page layer) and the first build of each page
(pages are cached after, `ShellViewModel._pages`). Asked Anh to try the Release build and to say whether it lags **every** switch
(→ drop the blur, keep the fade) or **only the first** open (→ build common pages ahead while idle).

## 2. State
- Released: **v0.93.0** = main = develop = `feat/passive-helm` = `feat/avalonia-app` = tag `v0.93.0` = f1310f8. Release run
  45807017519 (was in progress at packing; its Android job needs Anh's approval). CI runs of that commit were still running at packing.
  An older CI run of `feat/passive-helm` (3b74020) failed; not looked at, superseded.
- v0.93.0 contents (CHANGELOG has them): Helm passive (AI page, Ask Helm, Weekly Report, Lumi, Workspace Controller, Always On
  Top removed; MCP in General › MCP; Slack Bot → Slack Report); Habits, Missions, Novel Reader, SSH, Slack Report, Window Zones,
  Wallet, Vocabulary, Bug reports on the shared Avalonia views; then round 2:
  - `Helm.App` is an Avalonia desktop app (`Program.cs` → `App.cs`; `Shell/HelmWindow`, `MainWindow`, `AppShellHost`,
    `ShellController`, `TrayService` (TrayIcon + NativeMenu), `NotificationCard` (corner card)). Shell = `Helm.Shell.UI` `ShellView`.
    Windows draws only the border (`WindowDecorations.BorderOnly`), Helm draws the title bar.
  - Module contract: `IHelmModule { Symbol Icon; IImage? IconImage; Control CreatePage() }`; navigation by key
    (`IShellNavigation.ShowTool(key)`); PC-only wiring per tool in `Windows<X>PageHost`; `WindowsClipboard` (P/Invoke),
    `DesktopWindows` (Avalonia StorageProvider pickers).
  - Last WPF gone: `BrowserSites` reads the address bar through COM UI Automation (CsWin32), `ShellIcons` gets icon pixels via
    GetIconInfo + GetDIBits, `WindowsSounds` (MessageBeep), Vault takes the `Microsoft.Win32.SystemEvents` package. No `UseWPF`,
    no `.xaml`; `Helm.runtimeconfig.json` needs only Microsoft.NETCore.App. `Directory.Build.targets` drops WebView2's WPF/WinForms
    wrapper references (they pulled WindowsBase → MSB3277 under -warnaserror).
  - Menu: starts as an icon rail, no frame/highlight on the open page (hover only); "+" flyout glass style; Welcome, Diagnostics,
    What's new, Give feedback removed; MCP consent opens at once.
- Tests at f1310f8 (work PC): Release `-warnaserror` build clean (with `-p:NuGetAudit=false`, see §5); `Helm.Tests` 2163 pass,
  `Helm.Ui.Tests` 176 pass; Android Debug build OK.
- Behaviour changes Anh was told: Vault Emergency Kit saved as .txt (no printing); goal/cover pickers no TIFF; notifications are
  corner cards; SSH paste through the Avalonia clipboard.

## 3. Open
- Waiting for Anh:
  1. Approve the v0.93.0 Release run in GitHub Actions (read the Android job first).
  2. Tab-switch lag (§1): try the installed Release v0.93.0, say "every switch" or "first open only".
  3. Hand tests of v0.93.0 on the PC: SSH typing incl. Vietnamese; Ctrl+Alt+F12 bug window; paste into Scratch; Vault copy to
     clipboard; tray menu; tearing a tab off into its own window; keyboard per web site (new COM UIA path); file icons in Window Zones
     (new GDI path). Android not run by Claude at all.
- Next, when Anh says: fix the lag; delete the temporary worktrees and merged branches (§4) — ask first, and local deletions may be
  blocked (then hand Anh the commands).
- Known flaws, not fixed: `HotkeyBox` border invisible; first headless UI test after a fresh build sometimes fails in 1 ms.

## 4. Stores
- Repo `huyhung1404/helm`. Work PC: `C:\Project\Helm` (main, f1310f8 + this file), `C:\Project\Helm-develop` (develop), and many
  temporary worktrees `C:\tmp\h*` — the session's: `hpassive` (`feat/avalonia-app`), `hr2-a/b/c`, `hx-*`, `hwire-*`, `hui-*`
  (all merged, safe to remove with Anh's OK), plus older ones from earlier sessions. Home PC: `F:\Helm\helm*` (pull).
- Not pushed: `feat/screen-link`, `spike/mirror-m0` (removed on purpose); empty stray files `C:\Project\Helm\VocabStyles.axaml`,
  `C:\tmp\hpassive\VocabPopupCard.axaml.cs`, `C:\tmp\hpassive\tests\Helm.Ui.Tests\WalletProbeTests.cs` (0 bytes, junk); stash
  `stash@{0}` (already in develop, not dropped); local test builds `C:\tmp\happ-avalonia` (Debug) and `C:\tmp\happ-avalonia-rel`.

## 5. Rules and lessons not in the repo
- Ask Anh before working on disabled/hidden code (unregistered tool, hidden page): Always On Top and Diagnostics were wasted work.
- Anh checks UI by eye himself: no screenshot runs for him ("không cần chụp, anh tự xem"); when done, open a local Helm for him.
- Anh's installed Helm runs elevated and holds DLLs / hotkeys: build test copies with `-o C:\tmp\<dir>`; to start a new build in his
  session he must exit the old one (tray → Exit); a waiting script then starts it. Elevated processes show no Path.
- Local SDK 10.0.301 fails restore with NU1903 (Microsoft.DotNet.ILCompiler 10.0.9, Helm.Keyboard.Tip) under -warnaserror:
  build locally with `-p:NuGetAudit=false`; CI installs the newest 10.0.x. Updating the SDK would fix it.
- Android builds only with the private SDK: `%LOCALAPPDATA%\HelmDev\dotnet\dotnet.exe build src\Helm.App.Android
  -p:AndroidSdkDirectory=%LOCALAPPDATA%\HelmDev\android-sdk -p:JavaSdkDirectory=%LOCALAPPDATA%\HelmDev\jdk` (JAVA_HOME the same).
- PowerShell edits: `Get-Content -Raw` reads UTF-8 without BOM as ANSI and corrupts "→"; use `[IO.File]::ReadAllText(p, UTF8)`.
  In Monitor commands `$_` gets mangled: put the script in a .ps1 and run `powershell.exe -File "C:/..."` (forward slashes).
- Release flow: bump `Directory.Build.props`, rename the CHANGELOG `[Unreleased]` heading to `[X.Y.Z] - date` (the top section is the
  release body: never leave an empty Unreleased on top), commit `chore: release vX.Y.Z`, fast-forward main + develop, tag, push;
  Release waits for Anh's approval. Push/release only when Anh says.
- Seeing a page without touching Anh's desktop: Gallery (`scripts\gallery.cmd`) or a test instance (`--no-elevate --allow-multiple
  --data-dir <scratch> --page <key>`) with UIA + PrintWindow; a second instance reports every hotkey taken (not a bug).
- Reply to Anh in Vietnamese (Anh/em); his name is Hùng.

## 6. Checks on a new machine
- `git fetch && git switch main && git pull` → HEAD has `chore: release v0.93.0` (f1310f8) plus this file.
- `dotnet build Helm.sln -c Release -warnaserror -p:NuGetAudit=false` → 0 errors; `dotnet test Helm.sln -c Release --no-build` →
  Helm.Tests 2163 passed, Helm.Ui.Tests 176 passed.
- `git grep -l UseWPF -- '*.csproj'` → nothing.
- `dotnet build src/Helm.App -o <dir>` then `<dir>\Helm.runtimeconfig.json` → framework Microsoft.NETCore.App only.
