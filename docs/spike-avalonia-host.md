# Spike: shared Avalonia pages inside the WPF app

Branch `spike/avalonia-host`. Question: can Helm move to one Avalonia UI one tool at a time, with per-page demos?

## What was built
- `src/Helm.Ui`: the shared Avalonia base (theme and styles moved from `Helm.App.Android/App.axaml`, `DateTimeBox`,
  `LinksPanel`, `PageFrame`). Portable `net8.0`. SPIKE: the controls are copied, not moved; Android still has its own.
- `src/Helm.Modules.Tracker.UI`: Android's `TrackerContentPage.axaml` as `TrackerView`, unchanged except the class
  name. Platform bits (calendar export, reminders on open) go through `ITrackerPageHost`.
- `src/Helm.Ui.Windows`: `AvaloniaHost`, a WPF element showing an Avalonia page
  (WPF `WindowsFormsHost` -> `WinFormsAvaloniaControlHost`; Avalonia ships no WPF host). Starts Avalonia lazily and
  follows Helm's light/dark theme.
- `src/Helm.Modules.Tracker`: the WPF content page is now only the host + the save dialog.
- `src/Helm.Gallery`: one page at a time, the real view + the real `TrackerViewModel` over an in-memory store, frame
  size / theme / tool on-off switches, HotAvalonia hot reload.

## Results
- Works in the real WPF window: tabs, menu, theme, UI Automation (screen readers), real view model, data saved.
- Gallery: incremental build about 3 s, window in about 4 s; hot reload of an `.axaml` in Tracker.UI in about 4 s,
  state kept.
- Cost: about +70 MB private memory once Avalonia is loaded (159 -> 225 MB on the Tracker page); about +25 MB of
  shipped files (Skia 11, ANGLE 5, FluentIcons 5, Avalonia ~6). `-o` output looks +130 MB only because of native .pdb.

## Problems found
- Avalonia 12 needs `UseHarfBuzz()` (package `Avalonia.HarfBuzz`) when started by hand.
- An exception in the host crashed Helm at start: the real host must catch and show a fallback page.
- Airspace: the hosted page is an HWND. It ignores WPF render transforms (the tab slide-in left it 60 px low; fixed by
  a layout pass after the animation) and cannot be transparent (no Mica behind it) or overlapped by in-window WPF
  content. WPF popups are separate windows and should draw above it (not verified).
- The page must opt out of WPF-UI's scroller (`ScrollViewer.CanContentScroll="False"`); Avalonia scrolls itself.

## Not tested (hand tests)
- Typing Vietnamese with Helm's own keyboard into an Avalonia text box inside WPF (TSF through WinForms hosting).
- Focus and Tab moving between WPF and the Avalonia page; Esc/shortcuts while focus is inside the page.
- Per-monitor DPI when dragging the window between screens of different scale.
- The search popup and other WPF popups over the page.
- Android build (no Android workload on this PC).
