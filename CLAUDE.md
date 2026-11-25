# Helm

## UI: one Avalonia codebase (WPF is legacy)
- Goal: one UI codebase for Windows, macOS, Android and iOS on Avalonia; per-page demos without building all of Helm.
- Per tool: logic in `Helm.Modules.<X>.Core` (portable, no UI types); views + view models in `Helm.Modules.<X>.UI`
  (Avalonia, shared by every platform); only OS-specific code (Win32, Android widgets/services/notifications) in thin
  platform projects.
- New feature or new page: build it in Avalonia in the tool's `.UI` project, one adaptive layout (narrow = phone,
  wide = PC) in the same view.
- **Never write new WPF UI. Never write a second, platform-only copy of a view.**
- Shared base: `Helm.Ui` (theme `HelmTheme`, shared controls, `PageFrame`; every platform references it),
  `Helm.Ui.Windows` (`AvaloniaHost`: shows an Avalonia page inside a WPF page, with a fallback message if it fails),
  `Helm.Gallery` (`scripts/gallery.cmd`), headless tests in `tests/Helm.Ui.Tests`. A shared control goes in `Helm.Ui`,
  never copied into a platform project. Never fall back to WPF silently.
- Existing WPF page: bug fixes stay in WPF; a redesign or larger change means moving that tool to Avalonia, ask first.
- Moving an old tool: start from its Android Avalonia views, move them into `.UI`, add the wide layout, port PC-only
  parts from WPF, then delete the WPF page and the Android copy. One tool per change; the shell (main window, nav,
  tray) moves last.
- `docs/new-tool-prompt.md` still describes WPF + separate Android pages: where it conflicts, this file wins.
- UI demo: demo a new or moved page in `Helm.Gallery` (the real view + fake data, one line in `GalleryPage.All`)
  instead of a local HTML page.
- Prototype of Gallery + a shared page: `C:\tmp\hgallery` on the work PC only, not in git.
- Do not re-propose Flutter: its desktop multi-window API is still experimental; revisit only when it is stable on
  Windows and macOS.
