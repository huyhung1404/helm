# Changelog

All notable changes to Helm. The top section is used as the GitHub release body and as the Velopack release notes.
Format: [Keep a Changelog](https://keepachangelog.com), versions follow [SemVer](https://semver.org).

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
