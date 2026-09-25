# Changelog

All notable changes to Helm. The top section is used as the GitHub release body and as the Velopack release notes.
Format: [Keep a Changelog](https://keepachangelog.com), versions follow [SemVer](https://semver.org).

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
