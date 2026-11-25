# HANDOFF Helm

Updated 2026-11-25 23:00, packed on the home PC, session "Notes, Scratch, Helm Keyboard and Quick Capture moved to the shared Avalonia
look; Shortcut Guide removed; wide-screen layout and one scrollbar (v0.90.0 to v0.92.0)". Sessions still to pack: none.
For Claude (skill handoff; the user reads it with `compact-skill dịch`). New machine: read all of this, then CLAUDE.md and
`.claude/skills/helm-ui-ux/SKILL.md` (read it BEFORE any UI work, and before the plan).

## 1. Last message (verbatim)
> đóng gói

State: done (this file). The message before it: "đẩy hết lên, bản 92" (done: v0.92.0 pushed). Nothing waits for Anh except the
approvals and tests in §3.

## 2. State
- Released: **v0.92.0** = main = develop = tag `v0.92.0` = 01341bf (branch `fix/wide-layout` is the same commit). Release runs of
  v0.90.0, v0.91.0 and v0.92.0 wait for Anh's approval in GitHub Actions; none was seen by Claude (no `gh` on the home PC).
  **Nothing built Android since v0.89.0 was tagged**: CI is the first build of the Android code of v0.90.0..v0.92.0.
- v0.90.0 (dc2de95): **Notes** and **Scratch** are the shared Avalonia pages (`Helm.Modules.Notes.UI`, `Helm.Modules.Scratch.UI`) on PC
  and Android; old WPF and Android views and settings pages deleted; settings are a gear sheet, `SettingsPageType` = content page;
  Gallery demos (`Notes · …`, `Scratch · …`). Look: transparent top bar over the page's own backdrop (cards fade under it with
  `ScrollFade`), search + add, Trash as a rounded-square floating button bottom right with a count badge, glass confirmations.
  Scratch keeps selection (Shift/Ctrl, Ctrl+A), double-click open, right-click menu, drag (mouse: on move; touch: hold 450 ms),
  paste (`IScratchPageHost.PasteAsync`) and drop (`AddPathsAsync`).
- v0.91.0 (63aa040): **Shortcut Guide removed** (project, tests, docs); **Helm Keyboard** settings page and **Quick Capture** (box + settings)
  are Avalonia on PC. Both live as `.axaml` inside their WPF module projects (mixed projects: use
  `xmlns:core="clr-namespace:Helm.Core.Ui;assembly=Helm.Ui"`, because `Helm.Core.Windows` has a WPF `MasonryPanel` in the same namespace).
  The capture box is a window of its own: `Helm.Ui.Windows/AvaloniaFloatingPanel` (a transparent WPF window cannot show an Avalonia
  child). Android's Quick Capture settings page was restyled in place.
- v0.92.0 (01341bf): Home alerts and the settings cards of Keyboard / Quick Capture flow into columns on wide windows (no max width);
  the tab's close button is round (`TabHeader.cs`); one slim scrollbar for every `ScrollViewer` (`HelmTheme.axaml`, documented in
  `helm-ui-ux`).
- Earlier this session (v0.89.0): Home "today" and General wired on PC and Android (see git history and the v0.89.0 CHANGELOG).
- Verified by screenshots of a real test instance: Notes, Scratch, Keyboard, Quick Capture (typed `/n …`, Enter saved to Notes, box closed),
  Home and Keyboard at 1950 px, the new scrollbar. Gallery demos for Home, General, Notes, Scratch.
- Tests: `Helm.Ui.Tests` 51 pass; `Helm.Tests` 3 fail, also on develop before (WalletFundsTests funds widget, WeeklyReportGitTests working
  time, WorkspacePhoneFixesTests timeline). Not covered: `GeneralComposer`, `HomeScreen`, platform classes, Keyboard and Quick Capture
  views (no Gallery demo: they depend on Windows libraries).

## 3. Open
- Waiting for Anh:
  1. Approve the Release runs in GitHub Actions (v0.92.0 at least, if the older ones were skipped): **read the Android job first**.
  2. Hand-test: PC drag and drop of files from Explorer onto Scratch (never tried: the page is an Avalonia child of a WPF window); Win+Alt+N for
     the capture box; Notes and Scratch on the phone (hold and drag a card, Back from a note/detail, share to "Helm Scratch", paste);
     Quick Capture's settings page on the phone; General and Home from v0.89.0 (see that release).
- Known flaws, not fixed: the shortcut recorder (`HotkeyBox`) in Keyboard and Quick Capture settings shows plain text with no visible border
  (the `ghost` class set in its constructor does not style it); the first headless UI test after a fresh build sometimes fails in 1 ms.
- Not done: "today" cards for Vocab, Wallet, Missions; the review proposals (44 px touch targets on phone, tick-in-a-card is a button in a
  button, muted text contrast in the light theme, focus in the capture box of Home, reduced motion, loading placeholders); the top search box
  no longer finds cards inside General; tests for `GeneralComposer`; the WPF shell (tabs, nav, tray, MainWindow) is still WPF.
- Next, if Anh asks: move the next tool to Avalonia, one per change (still WPF: Wallet, Missions, Vocab, SSH, Novel Reader, Slack Bot, Window
  Zones, Always on Top, Weekly Report, Workspace). Ask Anh the three usual questions first (new look or copy; order; ship how).
- Other branches (older, untouched): `feat/proxy-stream`, `feat/wallet-funds-redesign` (may be in main), `feat/bug-reports`
  (worktree `F:\Helm\helm-bugs`, a Vault session merges it); local branches of this session (`feat/home-ui`, `feat/home-wire`, `feat/notes-ui`,
  `chore/remove-shortcut-guide`, `fix/wide-layout`) are all merged into main and can be deleted once Anh agrees.

## 4. Stores
- Repo `huyhung1404/helm`: home PC `F:\Helm\helm` (develop, may be behind: `git pull`), `F:\Helm\helm-home` (worktree, `fix/wide-layout` = main
  01341bf + this file), `F:\Helm\helm-bugs`, `F:\Helm\helm-keyboard`. Work PC: `C:\Project\Helm`, `C:\Project\Helm-develop`, `C:\tmp\h*`.
  All clean and pushed on the home PC.
- Not pushed: `feat/screen-link`, `spike/mirror-m0` (work PC, removed on purpose), stray empty `C:\Project\Helm\VocabStyles.axaml`,
  stash `stash@{0}` on the work PC (already in develop), user-level skill `ui-ux-pro-max` (work PC; this home PC has it under `%USERPROFILE%\.claude\skills`).
  A local-only test file `tests/Helm.Ui.Tests/ZzShots.cs` (renders pages to PNG when env `HELM_SHOTS` is set) is excluded through
  `.git/info/exclude` of this worktree only; it is not in the repo.

## 5. Rules and lessons not in the repo
- Read `.claude/skills/helm-ui-ux/SKILL.md` before the plan of any UI work (Anh noticed once it was skipped). Plan in Vietnamese, wait for approval,
  demo in the Gallery first for portable pages, then wire. Anh may say "build only when done": then do not build in between.
- Seeing a page without touching Anh's desktop: Gallery (`scripts\gallery.cmd`, select a row with UIA `SelectionItemPattern`), or a test
  instance of Helm `Helm.exe --no-elevate --allow-multiple --data-dir <scratch> --page <Name>` driven with UI Automation and captured with
  PrintWindow; stop it by process id (Anh's own Helm runs too; stop test instances before rebuilding, they lock the DLLs). A second instance
  reports every hotkey as taken: not a bug. The floating box hides when focus leaves it (design), so open it and type in one script.
- Headless screenshots: a temporary test with `window.CaptureRenderedFrame()` (see `ZzShots`). Use `FormattableString.Invariant` for geometry strings
  (the Vietnamese culture writes decimal commas).
- Helm.App aliases `Helm.Ui` as `avalonia`: Avalonia code lives in projects like `Helm.Shell.Windows` or in the module projects that host views
  through `Helm.Ui.Windows` (`AvaloniaHost`); give Helm.App only plain interfaces. A transparent WPF window cannot host an Avalonia child.
- Android cannot be built on the home PC (no workload, no private SDK; the work PC has `%LOCALAPPDATA%\HelmDev`). Push the branch and let CI build
  before releasing when Anh allows; he often says "release luôn".
- Release flow: bump `Directory.Build.props` and the CHANGELOG top by hand, commit `chore: release vX.Y.Z`, push the branch, fast-forward `develop` and
  `main`, tag, push the tag; the Release run waits for Anh's approval. Merge main into the branch first when main has a docs-only commit (HANDOFF).
- Shell quirks of this tool: heredocs containing apostrophes sometimes fail to parse; write files with the Write tool. `rm -f $VAR/*` is blocked
  (use `"${VAR:?}"/*` or a literal path). Python `\H` in a normal string only warns.
- Reply to Anh in Vietnamese (Anh/em); his name is Hùng.

## 6. Checks on a new machine
- `git fetch && git switch main && git pull` → HEAD has `chore: release v0.92.0` (01341bf) or later (plus this file).
- `dotnet build src/Helm.App` → 0 errors; `dotnet test tests/Helm.Ui.Tests` → 51 passed; `dotnet test tests/Helm.Tests` → 3 failed (the three of §2).
- `scripts\gallery.cmd` → entries Home, General, Tracker, Vault, Notes (Wall, Open note, Empty), Scratch (Wall, Details (phone), Empty).
- Helm test instance `--page Keyboard` and `--page QuickCapture` open the new settings pages; "Open Quick Capture now" opens the box.
