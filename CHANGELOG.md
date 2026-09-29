# Changelog

All notable changes to Helm. The top section is used as the GitHub release body and as the Velopack release notes.
Format: [Keep a Changelog](https://keepachangelog.com), versions follow [SemVer](https://semver.org).

## [Unreleased]

### Changed
- Vault: the backup warning only shows when none of your devices has backed the vault up for a week. One device with
  a backup folder is enough; the others no longer ask for one.
- Vault: no more reminder to confirm the Emergency Kit in the list (its state is still in the Vault settings).
- Vault: Favorites is the first sub-tab, shown as a star, and the trash the last, shown as a bin. Each card has a star
  to make it a favorite or not with one click (this is not a new version in the item's history).

## [0.12.0] - 2026-09-29

### Added
- Tracker: tasks can repeat every day, for ever or for a number of days. Each new day the task is there again (with
  the same due time and subtasks), on every device; days Helm was not opened are skipped. The task shows how many days
  it was done, and the report lists each repeating task with the days done in its period. Deleting a repeating task
  (or setting it to "Does not repeat") stops it.

### Changed
- Tracker widget (Android): the small button shows the number of open items itself, large enough not to be cut,
  instead of a gear with a badge.

## [0.11.0] - 2026-09-28

### Changed
- Vault: a new look. One search box above everything searches every tab (each tab shows how many items match). Two
  tabs, **Credentials** (logins and tokens) and **Other** (Info items), each with its own **New** button, and sub-tabs
  instead of the Show list (All, Logins, Tokens, Favorites, Trash; All, Favorites, Trash in Other).
- Vault: items are cards across the whole width. A login card shows its icon, title, username and password, each with
  Copy; a token card its title and token; an Info card its title and description. Click a card to open its details
  below it, click again to close them.
- Vault: adding and editing happen in a window above the list, with one row per field: type, name, value, remove.
- Vault: Other has one type, **Info**, with the fields you add; cards, identities and documents made before are Info
  items now and keep their fields.

- Tracker debt book: one line per person. Entries for the same name (whatever its case or spacing) add up to one
  balance: **Owes me** adds, **I owe** subtracts, and the new **Repayment** brings the balance back toward 0. A person
  moves to Settled when the balance reaches 0; there is no tick box any more. Click a person to see every change of
  the amount with the balance after it, and to set when it is due (+1 day, +1 week, +1 month, or a date and time);
  click again to close. Amounts are not edited: add a repayment or delete a wrong entry.
- Tracker debt book: amounts get thousands separators while you type, and names already in the book are suggested.
- Tracker: due dates can have a time (hour and minute), for tasks and debts.
- Tracker: tasks can have subtasks, each with its own tick; the task shows how many are done, and finishing the task
  finishes them too.
- Sync: a sync button on every page (next to the search box on Windows, in the app bar on Android) shows whether
  everything is synced, syncing, offline or needs attention; click it to sync now, or to set sync up. While Helm is in
  front it now checks for other devices' changes every 30 seconds (every 5 minutes in the background or tray), and
  right away when you come back to it.
- Tracker widget (Android): sync, + and a shrink button. With nothing to do, or when shrunk, it becomes a small button
  showing how many items are open, on a see-through background; tap it for sync and +. Choose the widget's
  background, its opacity and the text colour in Tracker's settings. The debt book shows one line per person.

### Fixed
- Vault: "Emergency Kit not confirmed" no longer shows on your other devices once you confirmed it on one.

## [0.10.1] - 2026-09-28

### Fixed
- Android: the empty band that could still appear above the title bar right after opening Helm (Android 15 and newer),
  until the screen was turned off and on. Helm now measures the status and navigation bars together with its own
  position, and again shortly after start.

## [0.10.0] - 2026-09-28

### Added
- Vault: **Token** items for API keys and access tokens: one hidden field, shown and copied like a password. Older Helm
  versions list tokens as "cannot be opened" (they keep them) until they are updated.
- Vault: an item's **Type** can be changed while editing, e.g. to turn a token saved as a login into a token. Its fields
  and history are kept.

### Changed
- Vault: the list has two tabs. **Logins & tokens** shows each one with its icon, the hidden password or token and Copy;
  **Other** holds notes, cards, identities, documents and pictures, each with its picture or its type's icon. The Show
  choices and the trash belong to the open tab.
- Vault (PC): click the open item in the list again to close it; the next click opens it again. An item you are
  editing stays open.
- A login without a password no longer shows "No password" in the list.
- Pages start 16 px below the window's title bar instead of right under it.

## [0.9.3] - 2026-09-28

### Fixed
- Vault: the page title, headings and labels were black in the dark theme (hard to read on the dark background).

## [0.9.2] - 2026-09-28

### Fixed
- Opening Helm from the Start menu or the desktop could bring up another copy of Helm running on the PC (for
  example a test build) instead of the installed one, a different one each time. Only the installed Helm answers now.
- Tracker and Vault icons no longer sit on a coloured tile and are the same size as the Claude Chat icon, on the
  PC and on Android.

## [0.9.1] - 2026-09-28

### Fixed
- Tracker's icon was drawn larger than Vault's (and the other tool icons) in the menu, on Home and on its pages.
  It now fills the same square, on the PC and on Android.

## [0.9.0] - 2026-09-28

### Added
- Vault (PC and Android): passwords, secure notes, cards, identities and documents in one encrypted vault.
  - Its own vault password, separate from the sync passphrase, plus a recovery key on a printable Emergency
    Kit. Nothing can be read without one of them, not even by the sync server.
  - Unlock with Windows Hello or a fingerprint; the password is asked again every two weeks (configurable).
    The vault locks after inactivity, when Windows locks, and shortly after the phone app goes to the background.
  - A user guide in Vietnamese inside the PC app (Guide at the top of Vault, also on the first screen), the same
    text as docs/vault-guide.md: the three secrets, setup, backups, restoring, and what to do when things go wrong.
  - Documents up to 256 MB each (the server's limit is adjustable), encrypted on the device and synced in chunks.
  - Nothing is lost by mistake: deleted items stay 30 days in the trash, every edit keeps the previous 10
    versions, edits made on two devices at once are both kept, and a sync that would delete many items is held
    until you decide.
  - Daily encrypted backups into a folder you pick (Google Drive, OneDrive, a USB stick), verified after
    writing. Restore a whole vault on a new device, or bring back deleted items. `helm-vault-restore`, attached
    to every release, opens a backup without Helm, and the vault exports to KeePass (KDBX 4).
  - Copied secrets stay out of the clipboard history and are cleared after 30 seconds; the vault is hidden
    from screenshots and screen sharing.
- Sync: files (blobs) for tools such as Vault, and a guard that holds bulk deletions for review (counted over
  24 hours, so they cannot be spread thin).
- Stronger password check for the vault password and new sync passphrases: common words, names, years and keyboard
  patterns no longer count as strong (for example "Password@2026!!").
- Android tools can now be added (Helm.Core.Android), with the same page layout as on the PC.
- Tools you work in open their content from the menu and Quick access, and their settings from Home → Utilities.
- Tracker (PC and Android, System Tools): lists you keep in workspaces, synced across your devices with Helm Sync.
  - To-do lists (title, priority Low / Normal / High / Urgent, due date, notes), as many as you like, and one debt
    book for everyone (who, how much, owes me or I owe, with running totals; amounts in ₫ by default).
  - The menu and the Quick access tile open your lists; Home → Utilities opens Tracker's settings (workspaces,
    display, CSV export).
  - Open items are sorted by priority, then by your own order (move up and down within a priority).
  - Tick an item and it is marked done and written to the history with the time it was added, started (the Start
    button, or when it was added) and finished. Reopening and deleting are recorded too, and deleted items stay
    in the reports.
  - Report for the last 7, 30 or 90 days or all time: completed and added counts, average and median time to
    finish, working time, share done by the due date, completions per day and by priority. Export every item or
    the whole history as CSV (Save on the PC, Copy on the phone).
  - Android home-screen widget: a workspace's open items; tap a circle to finish one, the title to switch
    workspace, + to open Tracker. Settings → "Add to home screen" places it on launchers that allow it.
  - Due date reminders: once a day (9:00 by default) a notification lists open tasks and debts that are overdue
    or due soon (on the day, or 1–3 days before); click it to open Tracker. On the PC it comes from the tray; on
    Android from a daily alarm, also when Helm is closed and after a restart (Android 13+ asks for permission).

## [0.8.1] - 2026-09-28

### Fixed
- Android: an empty band could appear above the title bar on some starts (Android 15 and newer). The page is now
  moved clear of the status bar exactly once, however Android lays the window out.

## [0.8.0] - 2026-09-28

### Added
- Helm for Android (8.0 or newer): `Helm-android.apk` is attached to every release, with the same Home and
  General pages as on the PC. Tools come per platform; the Android app starts without any, and Claude Chat
  stays PC only.
  - Sync works on the phone too: add it to your account from General → Sync with a device token, then unlock
    with your passphrase. Its keys are kept in the Android Keystore.
  - Updates like on the PC (Stable or Preview): Helm downloads new versions from GitHub in the background,
    checks them, and asks Android to install them when you tap Install update.
  - Light, dark or system theme, shareable log files, and Reset all settings.

## [0.7.0] - 2026-09-27

### Added
- Claude Chat (Advanced): chat with Claude Code in its own Helm window. It uses the Claude Code you already
  have installed and signed in, so there is no API key to set up and nothing extra to pay per message.
  - Work on several projects at once: add folders on the left, open as many chats in each as you like, and
    let them run side by side. Each chat shows whether it is working, waiting for you, finished or stopped
    with an error.
  - Helm notifies you when a chat you are not looking at finishes, needs your permission or fails. Click the
    notification to go straight to that chat.
  - Claude asks before it changes anything: allow once, allow for the rest of the chat, or deny. "For the rest
    of the chat" never writes to your Claude Code settings.
  - Type / for commands and actions (attach or mention a file, clear, export as Markdown, switch model, and
    Claude Code's own commands and skills), and @ to mention a file of the project.
  - Past chats of a folder are one click away (the clock button), and the folders and chats you had open come
    back the next time you open Helm, ready to continue.
  - Replies show formatted text, lists, tables and code. Choose the model per chat and switch it mid-chat.
  - Open it from Quick access on Home, with Win+Alt+C, or from the Claude Chat page, which holds its settings.
- Quick access tiles can now start a tool's main action directly, and tools can show their own logo.

### Fixed
- The icon on a tool's "Enable" card was smaller than the other card icons.

## [0.6.0] - 2026-09-25

### Security
- Stronger passphrase protection: the account passphrase is now protected with Argon2id, which makes
  guessing it far harder. Accounts set up with 0.5.0 are upgraded the next time a device unlocks, or from
  General → Sync → "Strengthen passphrase protection".
- New passphrases need at least 14 characters. Easily guessed ones (repeats, runs like "abcd" or "1234") are
  refused, and the page shows how strong the passphrase is while you type it.
- Removing a device can now change the account key: choose "Remove and change key" and enter your passphrase.
  The removed device can never read anything written afterwards, and you get a new recovery key (the old one
  stops working). Your other devices ask for the passphrase once.
- Synced data stored on this PC is now encrypted as well, with a key that never leaves the device. Existing
  data is encrypted automatically on the first start.
- The sync server now keeps a nightly backup (30 days) and limits repeated invite and sign-in attempts.

## [0.5.0] - 2026-09-25

### Added
- Sync across devices (General → Sync). Your Helm data is kept in step across your PCs and encrypted on the
  device before it leaves: neither the sync server nor the person who invited you can read it.
  - Start with an invite code, which creates your own account. On other devices, use a device token that you
    create under Devices.
  - The first device sets the account passphrase and shows a recovery key once. Keep that key safe: it is the
    only way back in if you forget the passphrase.
  - Shows sync status and storage used, lets you add or remove devices, and turns sync off on one device while
    keeping its data.
  - Syncs when Helm starts, every 5 minutes, and shortly after each change. Changes made offline are sent later.
- No tool uses sync yet; Notes will be the first.

## [0.5.0-preview.1] - 2026-09-25

### Added
- Sync across devices (General → Sync). Your Helm data is kept in step across your PCs and encrypted on the
  device before it leaves: neither the sync server nor the person who invited you can read it.
  - Start with an invite code, which creates your own account. On other devices, use a device token that you
    create under Devices.
  - The first device sets the account passphrase and shows a recovery key once. Keep that key safe: it is the
    only way back in if you forget the passphrase.
  - Shows sync status and storage used, lets you add or remove devices, and turns sync off on one device while
    keeping its data.
  - Syncs when Helm starts, every 5 minutes, and shortly after each change. Changes made offline are sent later.
- No tool uses sync yet. Notes will be the first; this preview is for trying the sync setup itself.

## [0.4.0] - 2026-09-25

### Removed
- The Zones tool. Helm now ships as a clean shell (Home, General, search, tray, auto-update) with no tools yet;
  new tools are planned and will arrive as updates. The Zones code remains in git history (tag v0.3.0) as the
  starting point for a redesigned window-layout tool. Existing zone files in %LOCALAPPDATA%\Helm\settings\zones
  are left untouched.
## [0.3.0] - 2026-09-25

### Changed
- Zones layouts are now fully custom: the built-in templates are gone and you draw zones directly on your
  monitors. Edges snap to other zones and to monitor edges; zones can be split in half. Existing custom grid and
  canvas layouts are converted automatically; a two-column "Layout 1" is created on a fresh install.

### Added
- Layouts across monitors: one layout can cover several monitors and a zone can straddle the boundary between them.
- Several layouts per monitor: give a layout a number and press Ctrl+Win+Alt+1…9 to switch the monitor under the
  mouse to it; the new zones and the layout name are shown briefly.
- Win+PgUp / Win+PgDn activates the previous / next window that shares the focused window's zone.
- Highlight distance setting.

### Fixed
- Editor buttons (Delete / Apply) overlapped on narrow picker panels.
## [0.2.6] - 2026-09-25

### Fixed
- The pin button added an empty row at the top of the menu and shifted every item down; it is now the last
  footer item ("Pin menu" / "Unpin menu") and also shows as an icon in the collapsed strip.
- On narrow content areas, long setting descriptions ran underneath switches, combo boxes and buttons. Setting
  rows now give the control its space first and wrap the text in what remains.
## [0.2.5] - 2026-09-25

### Added
- Auto-hiding navigation menu: it shrinks to a strip of icons and slides out over the page when you hover the
  strip or the Helm icon in the title bar, then hides again when the mouse leaves.
- Pin button at the top of the menu to keep it open next to the page.
- Drag the right edge of the open menu to change its width (220–520 px). Pin state and width are remembered.
## [0.2.4] - 2026-09-25

### Changed
- Helm now focuses on Zones: Always On Top, the Diagnostics page, the Welcome page and the empty tool groups are
  hidden (the code stays and can be re-enabled later).
- Removed the GitHub token setting; the repository is public, so update checks never need one.

### Fixed
- The title-bar and Home icons were a downscaled 256 px image and looked blurry; they now use the icon frame drawn
  for their exact pixel size.
## [0.2.3] - 2026-09-25

### Fixed
- The content background started 24 px to the right of the navigation pane, leaving a strip of pane color;
  the content area now starts right at the pane and the 24 px inset is applied inside each page.
## [0.2.2] - 2026-09-25

### Fixed
- Pages were measured wider than the window, so long tile text (e.g. an update error) pushed the Home content
  past the right edge and clipped it. Pages now always fit the window; long tile text is trimmed with a tooltip.
- Shortcuts and the taskbar kept showing the previous icon after an update or reinstall; Explorer's icon cache is
  refreshed on first run and after every update.
## [0.2.1] - 2026-09-25

### Fixed
- Crash (and system-wide input lag while Windows collected the crash dump) shortly after the first start:
  an unset window position (NaN) could not be written to general.json. Settings writes can no longer
  terminate Helm, and window placement only stores real, finite values.

### Changed
- New app icon: blue and teal windows side by side crossed by a white bar; the installer uses it on a dark tile.
  The tray uses the color icon on both light and dark taskbars.

## [0.2.0] - 2026-09-25

### Added
- Installer and automatic updates (Velopack + GitHub Releases), with Stable and Preview channels.
- General → Updates: check, download progress, release notes, "Restart to update", auto-download and auto-install toggles.
- Live Home update tile and a "Restart to update" tray item.
- Option to delete settings when Helm is uninstalled.

### Changed
- Helm is now manifested `asInvoker` and elevates itself at startup, because Velopack cannot launch
  `requireAdministrator` executables. Helm still always runs as administrator.
- Installs to `%LOCALAPPDATA%\HelmApp`, so settings in `%LOCALAPPDATA%\Helm` survive uninstall and reinstall.

## [0.1.0] - 2026-09-25

### Added
- Fluent shell modelled on PowerToys Settings: Home, General, title-bar search, tray, single instance.
- Zones: templates, custom grid/canvas layouts, per-monitor editor, Shift+drag snapping, Win+Arrow override.
- Always On Top: Win+Ctrl+T pinning with a colored border and pinned-window list.
