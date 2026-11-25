# HANDOFF Helm

Updated 2026-11-25 18:30, packed on the work PC (cty), session "Avalonia UI: Tracker + Vault on PC and Android (v0.87.0, v0.88.0),
then the Home ("today") and General redesign demo". Sessions still to pack: none.
For Claude (skill handoff; the user reads it with `compact-skill dịch`). New machine: read all of this, then CLAUDE.md and
`.claude/skills/helm-ui-ux/SKILL.md`.

## 1. Last message (verbatim)
> chốt, đóng gói toàn bộ lên ( đóng gói cả gallery lên để máy khác cũng biết để thực thi, anh sẽ chạy ở nhà cho phần này, đừng để thiếu data ở máy khác)

State: done (this file). Anh "chốt" the Home + General demo as it stands on `feat/home-ui` and will run the Gallery at home.
Next step waits for Anh: wire Home + General into Helm (PC, then Android), see §3.

## 2. State
- Released: **v0.88.0** = main = develop = tag `v0.88.0` = 2a32596 (Release run 45593034431 was waiting for Anh's approval
  in Actions when packed; check it was approved). v0.87.0 = 9732b21 before it.
- v0.88.0 contents: Vault in Avalonia (`src/Helm.Modules.Vault.UI`) on PC and Android; Tracker on Android uses the shared
  `TrackerView`; old Tracker/Vault settings pages and the old WPF/Android vault views deleted (settings = gear sheet,
  first card = the tool's on/off switch); card shadows (no dark band), `ScrollFade`, sticky search bar, full-width
  buttons; `HotkeyBox` (Avalonia shortcut recorder, `src/Helm.Ui.Windows`); Android Back closes sheets first
  (`Sheet.CloseTopmost`).
- Not hand-tested by anyone yet (Anh said he tests himself): Android Tracker/Vault (menu button, Back, widget, autofill,
  fingerprint, Vietnamese typing), PC Vault create/unlock/items flow, Windows Hello, auto-type shortcut recording.
- Just done (branch **feat/home-ui**, pushed, 46ee7c4, from v0.88.0, NOT merged): demo of the new Home and General in
  `src/Helm.Shell.UI` (namespaces `Helm.Home`, `Helm.General`), shown in Helm.Gallery only:
  - Home = "today": greeting + "n things need you today", capture box (Quick Capture's own icon, /t /n /v), "Needs
    attention" (plain cards, no colours/levels, in the order the platform gives, most important first), "Today" cards per
    tool (whole card opens the tool, Tracker/Habits tick in place), "All clear", footer "Helm vX · synced …" pinned to the
    bottom.
  - General = parts list on the left (each with its state line, own picture icons in `GeneralIcons.cs`, version at the
    foot), the chosen part on the right, all parts the same width, tops lined up; phone: the list is the page, a part
    opens in its place with Back. Parts: Tools (only switches), Sync, Updates, Appearance & startup, Running in
    background, Help & troubleshooting.
  - Switches take the background's corner colour where they sit (`Helm.Ui/Ui/BackdropTint.cs`, teal fallback).
  - Gallery shows real tool icons via `src/Helm.Gallery/ToolIcons.props` + `ToolIcons.cs`.
  - Tests: `tests/Helm.Ui.Tests` 29 pass (HomeViewTests, SwitchColourTests, ScrollFadeTests, Vault/Tracker).

## 3. Open
- Waiting for Anh:
  1. Approve Release run 45593034431 (v0.88.0) in GitHub Actions if not done (Review deployments → Approve).
  2. Hand-test v0.88.0 on PC and the phone (list in §2).
  3. Look at the Home/General demo at home: `scripts\gallery.cmd` on branch `feat/home-ui` (see §6), then say go to wire it.
- Unfinished / next (needs Anh's go):
  - Wire Home + General into Helm: an interface for each tool's "today" part (none yet), alerts from updates/hotkey
    conflicts/vault backup/sync in that order, General's sync first-run screens (invite code, create passphrase, unlock,
    recovery key) which the demo does not have; the WPF Home/General pages go away after (PC hosts via `AvaloniaHost`),
    Android after that. Old PC shell pieces still WPF.
  - Proposals Anh has not answered yet (from the review): phone touch targets 44 px (chips/switches are 36), the tick
    inside a Today card is a button in a button, muted text contrast in the light theme, focus in the capture box on PC,
    reduced-motion, loading placeholders on Home.
  - Flaky: the first headless UI test after a fresh build sometimes fails in 1 ms (TextBoxTests / TrackerViewTests);
    passes on rerun. Not investigated.
- Other branches pushed while packing (older sessions, untouched, not merged): `feat/proxy-stream` (AI proxy streaming,
  2026-11-05), `feat/wallet-funds-redesign` (2026-11-02, may already be in main as Funds v0.72 — check before merging).

## 4. Stores
- Repo `huyhung1404/helm`: work PC `C:\Project\Helm` (main) + `C:\Project\Helm-develop` (develop) + worktrees under
  `C:\tmp\h*` (this session: `C:\tmp\hbase` = feat/home-ui, `C:\tmp\htrk` = fix/tracker-old-settings, merged).
  All clean.
- Skill used for every UI change: in the repo, `.claude/skills/helm-ui-ux/SKILL.md`.
- Not pushed:
  - `feat/screen-link`, `spike/mirror-m0`: features Anh removed on purpose (2026-10), kept local only.
  - `C:\Project\Helm\VocabStyles.axaml`: empty stray file (0 bytes), not part of anything.
  - Stash `stash@{0}` (habits streak reminder): already on develop as 0207be6; kept, not dropped.
  - User-level skill `ui-ux-pro-max` lives in this PC's `%USERPROFILE%\.claude\skills`, not in the repo.

## 5. Rules and lessons not in the repo
- UI demo → the real Avalonia view in Helm.Gallery with fake data; never an HTML mock-up, never a demo-only copy of a view.
- Tool icons: never replace a tool's own picture icon with a Fluent glyph; new icons are frameless vectors in a 100×100
  box, strokes ~10, one gradient bottom-left → top-right, same size/weight as the tool icons.
- Android: Anh tests on his own phone. Never launch LDPlayer/adb unasked. Local Android build works with the private SDK:
  `%LOCALAPPDATA%\HelmDev\dotnet\dotnet.exe build src/Helm.App.Android -c Debug -p:EmbedAssembliesIntoApk=true
  -p:AndroidSdkDirectory=%LOCALAPPDATA%\HelmDev\android-sdk -p:JavaSdkDirectory=%LOCALAPPDATA%\HelmDev\jdk` (~5 min).
- Release flow: push the branch first so CI (incl. the Android job) checks it, then fast-forward main + develop, push main,
  develop and the tag; the Release run waits for Anh's approval. Bump Directory.Build.props + CHANGELOG top section by
  hand (bump-version.ps1 fails to parse on Windows PowerShell 5.1).
- Seeing a real Helm window without touching Anh's desktop: start a test instance `Helm.exe --no-elevate --allow-multiple
  --data-dir <scratch>`, drive it with UI Automation, capture with PrintWindow. Vault hides the whole window from capture
  (black image) unless `<data-dir>\settings\vault.json` has `{"version": 1, "protectFromScreenCapture": false}` —
  settings JSON keys are camelCase.
- Stop the running Gallery before building: it locks the DLLs of its worktree.
- Python edits through bash heredocs lose backslashes (`\\H` → `\H`): write scripts to a file with the Write tool.
- Reply to Anh in Vietnamese (Anh/em); his name is Hùng.

## 6. Checks on a new machine
- Any window: `git fetch && git switch feat/home-ui && git pull` → HEAD at 46ee7c4 or later.
- `dotnet --list-sdks` → a .NET 8 SDK (the Gallery and Helm.Ui are net8.0; .NET 10 + the android workload only for Android).
- `scripts\gallery.cmd` → the Helm Gallery window; left list has `Home · Busy day`, `Home · All clear`, `General · PC`,
  `General · Phone`, Tracker and Vault entries. Tool icons show in General → Tools (not glyphs).
- `dotnet test tests/Helm.Ui.Tests` → 29 passed (rerun once if the first test fails in 1 ms).
