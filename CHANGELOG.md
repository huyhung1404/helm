# Changelog

All notable changes to Helm. The top section is used as the GitHub release body and as the Velopack release notes.
Format: [Keep a Changelog](https://keepachangelog.com), versions follow [SemVer](https://semver.org).

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
