# HANDOFF Helm

Updated 2026-11-25 20:45, packed on the home PC, session "Home ('today') and General wired into Helm on PC and Android
(v0.89.0)". Sessions still to pack: none.
For Claude (skill handoff; the user reads it with `compact-skill dịch`). New machine: read all of this, then CLAUDE.md and
`.claude/skills/helm-ui-ux/SKILL.md` (read it BEFORE any UI work: this session skipped it once and Anh noticed).

## 1. Last message (verbatim)
> giờ sửa đến phần notes và phần scratch

State: waiting for Anh. Claude read Notes and Scratch and proposed moving both to the shared Avalonia UI (one tool per PR,
`Helm.Modules.Notes.UI` then `Helm.Modules.Scratch.UI`, demo in Gallery first, delete the WPF and Android copies). Open
questions to Anh (§3): new look or copy the current layout, order, and how to ship.
Anh's two last messages after that: "skill helm-ui-ux đâu, handoff không đóng gói à" (asked why the skill was not used and
why no handoff yet; this file is the answer to the second).

## 2. State
- Released: **v0.89.0** = main = develop = tag `v0.89.0` = 19a0c5a (also branch `feat/home-wire`). Release run: asked Anh to
  approve it in GitHub Actions (Review deployments → Approve); not confirmed. v0.88.0 = 2a32596.
- v0.89.0 contents (commits b958001, 14b9efd, 6596759, 19a0c5a):
  - Home ("today") and General (6 parts) are the shared Avalonia pages (`src/Helm.Shell.UI`) on PC and Android. The WPF
    Home/General pages and their view models, and the Android Home/General views and view models, are deleted.
  - Portable layer: `ITodaySource` / `IHomeAlertSource` (`src/Helm.Core/Today`). Today cards: Tracker (due today or overdue,
    tick) and Habits (habits to build, tick). Alerts: update, sync (Shell), hotkey conflicts (PC), vault backup (Vault.Core,
    both platforms). `HomeComposer` fills Home; `HomeScreen` builds it; `HomeCaptureHost` routes `/t /n /v`.
  - `GeneralComposer` mirrors the real `SyncViewModel`, `UpdatesViewModel`, `BackgroundActivitiesViewModel` into
    `GeneralViewModel` (two ways for switches). Sync's connect / unlock / recovery key / change passphrase / held deletions /
    new device token are `SyncFlowsView` bound to the real `SyncViewModel`. Platform rows through `IGeneralPlatform`
    (`Helm.Shell/Home`): `WindowsGeneralPlatform`, `AndroidGeneralPlatform` (`Services/ShellPlatform.cs`).
  - New projects: `Helm.Shell.Windows` (two WPF pages hosting the views, links, hotkey alert; Helm.App holds no Avalonia types
    because `Helm.Ui` is aliased there), `Helm.ToolIcons` (tool pictures for PC and Gallery; the phone uses
    `IAndroidModule.IconImage`).
- Checked on PC (screenshots of a real test instance): Home (greeting, capture, alerts) and General (Tools, Sync connect
  screen) render; no errors in the log. NOT checked: Updates / Appearance / Background parts, ticking on Home, capture, the
  vault-backup alert, anything on Android (this PC has no Android workload; CI builds it).
- Tests: `tests/Helm.Ui.Tests` 35 pass; `tests/Helm.Tests` 3 fail, same 3 on `develop` before this work
  (WalletFundsTests funds widget, WeeklyReportGitTests working time, WorkspacePhoneFixesTests timeline). Not covered by a
  test: `GeneralComposer`, `HomeScreen`, the Windows/Android platform classes.

## 3. Open
- Waiting for Anh:
  1. Approve the Release run of v0.89.0 in GitHub Actions; look at the Android job first (nothing built Android before the tag).
  2. Hand-test v0.89.0: PC Home (tick a task / habit, capture `/t`, alerts' buttons), General parts (switches, theme, startup,
     Updates, Sync connect/unlock), and on the phone Home, General, Back from a part, AI features under Help, share log, reset.
  3. Answer for Notes and Scratch: (a) new Tracker-style look (cards, sheets) with a Gallery demo first, or copy the current
     layout; (b) Notes first then Scratch, or both; (c) push the branch for CI to build Android before releasing (Claude
     recommends) instead of releasing straight away.
- Next: move **Notes**, then **Scratch**, to Avalonia per CLAUDE.md (start from their Android views in
  `Helm.Modules.<X>.Android`, add the wide layout, port PC-only parts: Scratch = paste Ctrl+V, drop files, drag ghost,
  thumbnails, open, copy files; delete WPF and Android copies; settings become a gear sheet with the tool's on/off switch
  first, `SettingsPageType` = content page). Read `helm-ui-ux` first and demo in Gallery.
- Not done from v0.89.0 (Anh knows): "today" cards for Vocab, Wallet, Missions; review proposals (44 px touch targets on
  phone, tick-in-a-card is a button in a button, muted text contrast in light theme, focus in the capture box on PC,
  reduced motion, loading placeholders); the top search box no longer finds cards inside General; `GeneralComposer` tests;
  the new Help card and `SyncFlowsView` were written without re-checking against `helm-ui-ux` (e.g. `TextBlock title` with
  FontSize 15 in an infobar, `Fields fill the row`).
- Flaky: the first headless UI test after a fresh build sometimes fails in 1 ms; passes on rerun.
- Other branches (older, untouched, not merged): `feat/proxy-stream`, `feat/wallet-funds-redesign` (may already be in main),
  `feat/bug-reports` (see memory: worktree `F:\Helm\helm-bugs`, 3 commits, a Vault session merges it).

## 4. Stores
- Repo `huyhung1404/helm`: home PC `F:\Helm\helm` (develop, at 2a32596, not yet pulled to v0.89.0), `F:\Helm\helm-home`
  (worktree, `feat/home-wire` = main 19a0c5a + this file), `F:\Helm\helm-bugs` (feat/bug-reports), `F:\Helm\helm-keyboard`
  (feat/helm-keyboard). Work PC: `C:\Project\Helm`, `C:\Project\Helm-develop`, `C:\tmp\h*`. All clean and pushed.
- `feat/home-ui` (46ee7c4) is merged into v0.89.0 through `feat/home-wire`; both branches can go once Anh agrees.
- Not pushed: `feat/screen-link`, `spike/mirror-m0` (work PC, removed on purpose), stray empty `C:\Project\Helm\VocabStyles.axaml`,
  stash `stash@{0}` on the work PC (habits streak, already in develop), user-level skill `ui-ux-pro-max` (work PC only).
  Scratch test data of this session lives under this PC's temp folder only.

## 5. Rules and lessons not in the repo
- Read `.claude/skills/helm-ui-ux/SKILL.md` before any UI change. Plan in Vietnamese, wait for Anh's approval, then code; he
  may say "build only when done" (then do not build in between; build once at the end).
- UI demo → the real Avalonia view in Helm.Gallery with fake data; never an HTML mock-up.
- Tool icons: never replace a tool's picture icon with a Fluent glyph; frameless vector, 100×100 box, strokes ~10, one gradient.
- Android: Anh tests on his phone; never launch LDPlayer/adb unasked. Only the work PC has the private Android SDK
  (`%LOCALAPPDATA%\HelmDev\...`, command in the old handoff: `dotnet build src/Helm.App.Android -c Debug
  -p:EmbedAssembliesIntoApk=true -p:AndroidSdkDirectory=... -p:JavaSdkDirectory=...`); the home PC cannot build Android.
- Helm.App aliases `Helm.Ui` as `avalonia`: put code that touches Avalonia types in a project like `Helm.Shell.Windows`, and
  give Helm.App only plain interfaces (e.g. `IShellWindowsHost`).
- Release flow: push the branch first so CI (incl. Android) checks it, then fast-forward main + develop, push both and the tag;
  the Release run waits for Anh's approval. Bump `Directory.Build.props` + CHANGELOG top by hand. Merge main into the branch
  first when main has a docs-only commit (HANDOFF). This session released straight away at Anh's order ("bỏ qua test").
- Seeing a real Helm window without touching Anh's desktop: start a test instance `Helm.exe --no-elevate --allow-multiple
  --data-dir <scratch> --page General`, drive it with UI Automation (`InvokePattern` on a named element), capture with
  PrintWindow. Stop it by process id afterwards (check the command line: Anh's own Helm runs too). A second instance reports
  every hotkey as taken by another app because Anh's Helm holds them: not a bug.
- Bash heredocs with apostrophes inside this tool's shell sometimes fail to parse ("unexpected EOF"): write files with the
  Write tool. Python in a heredoc: `\H` in a normal string is an invalid escape (warning, but the text survives).
- Reply to Anh in Vietnamese (Anh/em); his name is Hùng.

## 6. Checks on a new machine
- Any window: `git fetch && git switch main && git pull` → HEAD has `chore: release v0.89.0` (19a0c5a) or later.
- `dotnet --list-sdks` → a .NET 8 SDK or 10 (the home PC has only 10.0.400 and still builds `Helm.App`, `Helm.Gallery`, tests).
- `dotnet build src/Helm.App` → 0 errors; `dotnet test tests/Helm.Ui.Tests` → 35 passed (rerun once if the first fails in 1 ms).
- `dotnet test tests/Helm.Tests` → 2485 passed, 3 failed (the three named in §2, failing on develop too).
- `scripts\gallery.cmd` → the Helm Gallery; entries `Home · Busy day`, `Home · All clear`, `General · PC`, `General · Phone`.
