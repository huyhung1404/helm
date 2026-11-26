# Helm

## UI: one Avalonia codebase (no WPF)
- Goal: one UI codebase for Windows, macOS, Android and iOS on Avalonia; per-page demos without building all of Helm.
- Per tool: logic in `Helm.Modules.<X>.Core` (portable, no UI types); views + view models in `Helm.Modules.<X>.UI`
  (Avalonia, shared by every platform); only OS-specific code (Win32, Android widgets/services/notifications) in thin
  platform projects.
- New feature or new page: build it in Avalonia in the tool's `.UI` project, one adaptive layout (narrow = phone,
  wide = PC) in the same view.
- **No WPF or WinForms, anywhere (since 2026-11-26 Helm on Windows is an Avalonia app). Never write a second,
  platform-only copy of a view.**
- Shared base: `Helm.Ui` (theme `HelmTheme`, shared controls, `PageFrame`; every platform references it),
  `Helm.Ui.Windows` (Windows-only Avalonia pieces: `HotkeyBox`, corner and floating windows, `DesktopWindows` pickers),
  `Helm.Gallery` (`scripts/gallery.cmd`), headless tests in `tests/Helm.Ui.Tests`. A shared control goes in `Helm.Ui`,
  never copied into a platform project.
- The PC app: `Helm.App` is an Avalonia desktop app; the shell (menu, tabs, title bar) is `Helm.Shell.UI`'s `ShellView`;
  a Windows tool implements `IHelmModule.CreatePage()` (its `.UI` view + a `Windows<X>PageHost` for what only Windows
  does). Clipboard: `WindowsClipboard`; pickers: `DesktopWindows`; sounds: `WindowsSounds`.
- Before moving, fixing or redesigning code that is switched off (a tool not registered, a page off the menu), ask
  whether to keep or delete it.
- `docs/new-tool-prompt.md` may still describe WPF + separate Android pages: where it conflicts, this file wins.
- UI demo: demo a new or moved page in `Helm.Gallery` (the real view + fake data, one line in `GalleryPage.All`)
  instead of a local HTML page.
- Prototype of Gallery + a shared page: `C:\tmp\hgallery` on the work PC only, not in git.
- Do not re-propose Flutter: its desktop multi-window API is still experimental; revisit only when it is stable on
  Windows and macOS.
