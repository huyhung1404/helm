# Fixing a reported bug

How an AI agent (Claude Code, through Helm's MCP tools) fixes a bug the user reported in Helm. **Hand to Claude
Code** on a bug types a request that points here.

## What a bug gives you

The user reports from anywhere: Ctrl+Alt+F12 (or the title bar's bug button, or the tray) on the PC; the app bar's
bug, a shake, or the Quick Settings tile on the phone. Each report carries:

- what the user wrote, how bad it is, the tool it is in, the device and the Helm version;
- the pictures the user chose to attach (Helm never takes screenshots);
- the **black box**: the 60 seconds before the report, frozen when the report window opened. It has the user's input
  (clicks and taps, clicks on disabled buttons, shortcuts, picks, how much was typed in which field — never the
  text), the pages and windows shown, Helm's log from Information up with every exception's stack, UI freezes longer
  than 0.5 s, and sync. A crash writes the black box to disk; the next start offers to report it.

## The tools

| Tool | Use |
|---|---|
| `bugs_list` | The bugs, newest first (default: not closed). |
| `bug_get` | One bug: the conversation and the black box summed up (steps, errors with stacks, freezes, Helm's state). Start here. |
| `bug_blackbox` | Every event of a black box, by kind. |
| `bug_images` | The attached pictures, as images and as files on the PC. |
| `bug_claim` | Take the bug: it shows "Fixing" with your name on the user's devices. |
| `bug_comment` | Write how it goes (cause found, what changed). |
| `bug_ask` | Ask the user one clear question; their phone tells them, read the answer with `bug_get`. |
| `bug_resolve` | Fixed: the cause, the change, the branch and the version. The bug waits for the user to check. |
| `bug_release` | Give it back when you cannot fix it, with why. |

An agent never closes a bug. `bug_resolve` moves it to **To verify**; the user closes it after checking, and Helm
asks them once their device runs the version you named in `fixed_in`. Every change can be taken back (Undo).

## The steps

1. `bug_get` the bug; read the steps and the errors. `bug_images` when the user attached pictures.
2. `bug_claim` it, with your project (`helm`) and the branch you will use.
3. Branch from `develop`: `fix/bug-B12-<short-name>`.
4. Reproduce it in a test that fails first (tests/Helm.Tests). The black box's steps are usually the test's steps.
5. Fix it. Keep the change to the cause; build and run the tests (`dotnet test`).
6. `bug_comment` what the cause was, then `bug_resolve` with the cause and the fix in the user's language, the branch,
   and `fixed_in` when you know the release that will carry it.
7. Do not merge, tag or release: the user does that.

When the black box is not enough, `bug_ask` the user instead of guessing.
