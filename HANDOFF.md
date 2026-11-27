# HANDOFF Helm

Updated 2026-11-27 12:10, packed on the work PC, session "v0.93 feedback fixes, a motion pipeline (motion that redraws
only what moves), bugs B4/B3; released v0.94.0 and v0.94.1". Sessions still to pack: none.
For Claude (skill handoff; the user reads it with `compact-skill dịch`). New machine: read all of this, then CLAUDE.md and
`.claude/skills/helm-ui-ux/SKILL.md` (read it BEFORE any UI work; §6 has the motion pipeline rules).

## 1. Last message (verbatim)
> đóng gói

State: done (this file). The request before it: "release 0.94.1 đi" (done, pushed). Nothing is waiting for an answer,
except two optional questions Claude asked earlier (see §3).

## 2. State
- Released: **v0.94.1** = main = develop = tag `v0.94.1` = 02e4e9c. Release run 45940646928 waits for Anh's approval
  (check its Android job). v0.94.0 (3b5cb16, run 45937767788) approved and published.
- v0.94.0 (CHANGELOG has the list): Anh's 13 bugs on v0.93.0 fixed:
  - PC:
    - window buttons do their own clicks;
    - static backdrop (it repainted the whole window 20×/s: the lag);
    - page fade 150 ms, no blur;
    - sync glyph turns ≥1.1 s after a click;
    - "Helm" in the title bar in semibold 15;
    - searches follow Helm Keyboard's composing word (`core:TypingText.Text`);
    - no date at the top of tool pages.
  - Android:
    - the app bar (menu, bug, sync) is on every page;
    - one backdrop behind it;
    - drawer without a scrollbar;
    - tool tabs on the left on a phone;
    - Quick Capture uses the shared `QuickCaptureView`;
    - Scratch's search has its own row.
  - Then the **motion pipeline** (bf7d1df):
    - `Motion.Spin` / `Motion.Pulse` (compositor loops);
    - `MotionClock` (RequestAnimationFrame, only while moving);
    - `Motion.SlideFrom` (the menu no longer animates the page width);
    - `BitmapCache` page fade;
    - `RenderDebug` overlay (Ctrl+Alt+Shift+R, or General › Help & troubleshooting › Show what is redrawn);
    - guard tests `MotionRulesTests` and `MotionPipelineTests`.
- v0.94.1: B4 fixed (5a529f2). Avalonia 12's `TopLevel.AutoSafeAreaPadding` (on by default) plus Helm's own
  `ContentInsets` padding left the status bar's height twice over the Android app bar. Auto padding is now off in
  `MainView` and `ToolWindowView`. B3 test added (f6dfcc0).
- Bugs (Helm › Bug reports):
  - B4 and B3 → "To verify" (B3 went away with the shared Vault UI in v0.88.0);
  - B1 (keyboard language jumps) still "Fixing"; its fix shipped in 0.86.1, and Anh may close it if it has not come back.
- Tests at 02e4e9c (work PC): Release `-warnaserror` build clean (`-p:NuGetAudit=false`); Helm.Tests 2167 pass,
  Helm.Ui.Tests 183 pass; Android Debug build OK. Claude did NOT see Android or the PC build by eye.

## 3. Open
- Waiting for Anh:
  1. Approve Release v0.94.1 in GitHub Actions.
  2. On the phone: close B4/B3 after checking (top bar gap gone; popups too).
  3. Hand tests of v0.94 (by eye, no screenshots from Claude):
     - PC: window buttons, tab switching, sync spin, search while typing Vietnamese, Ctrl+Alt+Shift+R overlay;
     - Android: app bar on every page, Quick Capture page, Scratch bar, tabs left, smoothness.
  4. Claude's open questions:
     - (a) change the Android share/tile capture dialog (still native AlertDialog) to the new look?
     - (b) measure Android tab switching with the overlay before changing page caching (pages are detached on every
       switch, `KeptPages = 3`)?
- Next, when Anh says:
  - Anh wants MORE animation everywhere, built on the pipeline (helm-ui-ux §6).
  - Remaining perf suspects: `ScrollFade`'s OpacityMask while scrolling; Android page re-attach on every tab switch.
  - The overlay menu `Surface` still animates its Width (allowed: it floats over the page).
- Known flaws, not fixed: `HotkeyBox` border invisible; first headless UI test after a fresh build sometimes fails in 1 ms.

## 4. Stores
- Repo `huyhung1404/helm`.
  - Work PC: `C:\Project\Helm` (main), `C:\Project\Helm-develop` (develop) and worktree `C:\tmp\hfix`
    (`fix/bug-B4-top-gap`, = main, clean, can be removed).
  - Home PC: `F:\Helm\helm*` (pull).
- Local test builds (work PC): `C:\tmp\happ-fix` (Release with the v0.94.0 code, Anh runs it now), `C:\tmp\happ-avalonia*`.
- Not pushed:
  - `feat/screen-link`, `spike/mirror-m0`: kept local on purpose;
  - stash `stash@{0}`: already in develop, not dropped;
  - empty junk file `C:\Project\Helm\VocabStyles.axaml` (0 bytes, untracked);
  - probe project `C:\tmp\avprobe` (reflection over Avalonia 12 APIs, throwaway).
- Merged local branches (`feat/ui-*`, `feat/wire-*`, `feat/x-*`, `feat/r2-*`, `fix/v093-feedback`, …) still exist locally;
  deleting them needs Anh's OK (local deletions may be blocked: then hand Anh the commands).

## 5. Rules and lessons not in the repo
- Reply to Anh in Vietnamese (Anh/em); his name is Hùng.
- Ask Anh before working on disabled/hidden code (unregistered tool, hidden page).
- Anh checks UI by eye himself:
  - no screenshot runs for him;
  - when done, build a local Helm with `-o C:\tmp\<dir>`;
  - his installed Helm runs elevated and holds DLLs and hotkeys, so he must exit it (tray → Exit) before a waiting
    script starts the new build. Elevated processes show no Path.
- A phone may be attached over adb (`2a0abfcf0123`, Anh's Nubia). Never install a dev build on it: the signature differs,
  so it would need an uninstall and Anh's data would be lost. Android is checked by Anh after a release.
- Bugs come through Helm's MCP (`bugs_list`, `bug_get`, `bug_images`, `bug_blackbox`):
  - claim before fixing, resolve with cause + fix in Vietnamese;
  - the black box log has the numbers (e.g. "Shell padding … dp").
- Avalonia 12 lessons:
  - `TopLevel.AutoSafeAreaPadding` defaults to true.
  - Headless tests draw a frame only when something changed: tests that wait for animations toggle a marker each tick.
  - `DispatcherTimer` does not fire in headless `RunJobs` loops.
  - Style animations never reach their last frame if nothing else draws (hence `RunToEnd`).
  - A transition string in a Setter does nothing (use transform objects).
- Local SDK 10.0.301 fails restore with NU1903 under -warnaserror: build locally with `-p:NuGetAudit=false`.
- Android builds only with the private SDK: `%LOCALAPPDATA%\HelmDev\dotnet\dotnet.exe build src\Helm.App.Android
  -p:AndroidSdkDirectory=%LOCALAPPDATA%\HelmDev\android-sdk -p:JavaSdkDirectory=%LOCALAPPDATA%\HelmDev\jdk`
  (JAVA_HOME the same).
- Release flow:
  - bump `Directory.Build.props`;
  - rename CHANGELOG `[Unreleased]` to `[X.Y.Z] - date` (never leave an empty Unreleased on top);
  - commit `chore: release vX.Y.Z`;
  - fast-forward main + develop, tag, push;
  - the Release run waits for Anh's approval. Push/release only when Anh says.
- No `gh` on the work PC: read runs with `curl https://api.github.com/repos/huyhung1404/helm/actions/runs`.
- PowerShell edits: `Get-Content -Raw` corrupts UTF-8 without BOM. Edit with Python or `[IO.File]::ReadAllText(p, UTF8)`.

## 6. Checks on a new machine
- `git fetch && git switch main && git pull` → HEAD `chore: release v0.94.1` (02e4e9c) plus this file.
- `dotnet build Helm.sln -c Release -warnaserror -p:NuGetAudit=false` → 0 errors.
- `dotnet test Helm.sln -c Release --no-build` → Helm.Tests 2167 passed, Helm.Ui.Tests 183 passed.
- `git grep -n "IterationCount=\"INFINITE\"" -- '*.axaml'` → nothing (motion rules).
