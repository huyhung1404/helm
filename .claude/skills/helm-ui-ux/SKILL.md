---
name: helm-ui-ux
description: Helm's look and feel for every UI change in Helm (Avalonia pages in Helm.Modules.<X>.UI and Helm.Ui, the WPF shell around them, the Gallery demo). Use before designing, building, restyling or reviewing any Helm page, control, sheet, popup or animation, on PC or phone.
---

# Helm UI/UX

One look for every tool, on Windows, Android, macOS and iOS. Built once in Avalonia (see the repo's CLAUDE.md for where
code goes). Tracker (`src/Helm.Modules.Tracker.UI/TrackerView.axaml`) is the reference page: copy its patterns.

## 1. Flow first
- A page reads top to bottom in the order the user works: **where** (which list / item) → **add** → **do** → **done**
  (folded away). Calendar, reports, settings sit behind tabs or a gear, never in the main flow.
- One main action per area. After an action, put the user on the next step (a new list opens with the cursor in
  "Add a task").
- First run: one wide card that shows the steps (1 · 2 · 3), only step 1 active, one button.
- Empty states say what to do next and point at it ("Add the first task above" with an arrow).
- No decorative "featured" cards when items are equal (no "Up next" hero for an unordered list).
- Tabs replace everything under them, from the page title down, and scroll to the top.
- Content page ↔ settings: a gear on the content page, "Open <tool>" first on the settings page.

## 2. Surface
- Background: `core:Backdrop` behind every glass page (spec: `Helm.Core/Ui/BackdropSpec.cs`, the only place for its
  colours). Dark graphite (#15171C → #0B0C0F) with four faint corner glows in the Helm icon's colours: blue #0077F2
  top left, sky #06AEFF bottom left, cyan #00C6E0 top right, teal #00CFC0 bottom right; they drift slowly on a shared
  clock. Light theme: the same over #F7F8FA → #ECEFF3.
- In the WPF shell the same background covers the whole window (`WindowBackdrop`), and the hosted page paints its part
  lined up with it (`Backdrop.Canvas`, set by `AvaloniaHost`). No panel, border or divider between shell and page:
  the tab bar is one borderless pill (`SubtleFillColorTertiaryBrush`) with pill tabs.
- Cards: `Border.glass` (radius 12, glass fill, thin stroke, soft shadow); `.glass.hero` (radius 16) for the one big
  card (first run). Sheets/dialogs/popup lists: `Border.sheet` / `HelmSheetBackground` (solid, radius 18, stroke).
- Colours only from HelmTheme keys: `HelmGlass`, `HelmGlassHover`, `HelmGlassStroke`, `HelmGlassStrokeStrong` (the
  chosen thing), `HelmEyebrow` (teal accent text), `HelmPillBackground/Foreground`, `HelmSecondaryText`,
  `HelmDanger`, `HelmSuccess`. Never a raw hex in a page.

## 3. Type
- `TextBlock.eyebrow`: the small-caps accent line above a title (date, "START HERE", a field's name), via
  `core:UpperCase`.
- `TextBlock.display`: the page title (34, 28 on a phone). `head` for section heads, `muted` for second lines.
- Overdue / danger text `late` (HelmDanger). Done items struck through and muted.

## 4. Buttons and inputs: one rule
- **Radius 10, height 40** for every button and input (36 for small actions inside a card). Round only for a day in a
  calendar.
- Three buttons: `pill` = the one main action of an area (bright, filled); `ghost` = secondary (thin stroke);
  `chip` = an icon or option button (thin stroke; `.selected` / `:checked` = stroke strong + accent text).
- Inputs: `TextBox.glass`, `ComboBox.glass` (its list is a rounded floating card, chosen row in accent text, no blue
  fill), `core:DateTimeBox Classes="glass"`. A text box inside a glass card that is itself the field: `TextBox.bare`.
- Every icon-only button has `AutomationProperties.Name` and a tooltip.
- A row's tap area (`Button.rowhit`) has no look of its own: the whole card lights up (`Border.glass.lift`).

## 5. Layout: one view, PC and phone
- One adaptive view; below 720 px the view gets class `narrow` (set in code from `OnSizeChanged`). Select it with
  `:is(UserControl).narrow …` (a plain `UserControl.narrow` selector never matches the view's own type).
- Phone: actions are icons in one row (labels `TextBlock.label` hidden), side-by-side things stack, lists of cards go
  two by two or scroll sideways with the scrollbar hidden. Tabs go under the date line.
- Pickers: `DateTimeBox` is a card in the middle on PC, a sheet from the bottom on a phone. It opens over the nearest
  ancestor with class `screen` (the Gallery's phone frame) or the window.

## 6. Motion: never static, never in the way
- Things grow into place (popup scale ~0.94 → 1 with opacity, 260 ms, cubic ease-out), not a plain fade:
  `core:Motion.EnterOnShow="True"` on anything shown again (tabs, folded sections, an opened task, edit forms, option
  rows, messages); add `core:Motion.FromTop="True"` for a section opening under its header. `Motion.Enter(control)`
  in code (a list just picked, a sheet: phone sheets grow from the bottom edge). `Classes="enter"` for first load.
- ComboBox lists grow open from the top (`ComboBox.glass:dropdownopen` style). Chevrons turn (`Panel.turn` with
  `.open`, `.quarter` for right-pointing); the tick ring pops when a task is done.
- Hover: cards light up and cast a deeper shadow (Background + BoxShadow transitions), no movement.
- **Avalonia 12 pitfalls**: transform strings in a Setter (`"translateY(-2px)"`, `"rotate(180deg)"`) do nothing; use
  transform objects (`<ScaleTransform/>`, `<RotateTransform/>`) and animate their properties
  (`ScaleTransform.ScaleX`, `RotateTransform.Angle`). Once a transform is animated, Avalonia replaces RenderTransform
  with its standard TransformGroup: code must accept a group. A selector list mixing a control and a
  `/template/` part fails to compile: write two styles.

## 7. Inside the WPF shell (until the shell moves to Avalonia)
- Show a page with `AvaloniaHost.Show(() => new …View(...))`; it shows a message instead of failing.
- Popups are drawn inside the page (`OverlayPopups`): a separate popup window opens in the wrong place after the WPF
  window moves.
- The WPF page sets `ScrollViewer.CanContentScroll="False"`; the Avalonia view scrolls itself.

## 8. Demo and check
- Demo in Helm.Gallery (`scripts/gallery.cmd`): the real view with fake data, never an HTML mock-up or a demo-only
  copy. Check Phone 390, Tablet 800, PC; dark and light; tool off.
- Then in a test Helm (`Helm.exe --no-elevate --allow-multiple --data-dir <scratch>`) to see it with the whole app.
  Never send keys or clicks to the user's desktop; drive test windows with UI Automation, capture with PrintWindow.
- Headless tests in `tests/Helm.Ui.Tests` for behaviour (tabs, visibility, transforms' targets).
