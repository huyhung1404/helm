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
- Content page ↔ settings: a gear on the content page opens the tool's settings as a **sheet over the page** (no
  navigating away; Done / X closes it). Tracker: `TrackerSettingsView`. A tool that has moved has **no separate
  settings page**: its `SettingsPageType` is its content page (Home → Utilities and search open that), the sheet holds
  the tool's on/off switch as its first card, and the WPF settings page is deleted. A tool that has not moved yet keeps
  its WPF settings page until it does.
- Confirmations (delete…) are `GlassDialogs` (a `Sheet`): the question, what happens, Cancel + the action as a pill. A
  view model gets it through `IDialogService`; the view swaps it in (`viewModel.UseDialogs(new GlassDialogs(() => this))`).
  Never the OS or WPF-UI dialog.

## 2. Surface
- Background: `core:Backdrop` behind every glass page (spec: `Helm.Core/Ui/BackdropSpec.cs`, the only place for its
  colours). Dark graphite (#15171C → #0B0C0F) with four faint corner glows in the Helm icon's colours: blue #0077F2
  top left, sky #06AEFF bottom left, cyan #00C6E0 top right, teal #00CFC0 bottom right; they drift slowly on a shared
  clock. Light theme: the same over #F7F8FA → #ECEFF3.
- In the WPF shell the same background covers the whole window (`WindowBackdrop`), and the hosted page paints its part
  lined up with it (`Backdrop.Canvas`, set by `AvaloniaHost`). No panel, border or divider between shell and page:
  the tab bar is one borderless pill (`SubtleFillColorTertiaryBrush`) with pill tabs.
- Cards: `Border.glass` (radius 12, glass fill, thin stroke, soft shadow); `.glass.hero` (radius 16) for the one big
  card (first run). Sheets/dialogs: `Border.sheet` / `HelmSheetBackground` (solid, radius 26, 28 on top of a phone sheet, stroke): a floating card is more rounded than the cards under it, never sharper. Popup lists: radius 12.
- **Stacked cards sit on the page, they are not smudged onto it.** The card shadow (`HelmGlassShadow`, `HelmLiftShadow` on
  hover) is three layers: a tight contact shadow, a short soft one under the bottom edge with a **negative spread** (it
  never reaches into the gap between cards) and a thin light edge on top (`inset 0 1 0 0`). Never a wide dark blur
  (`0 8 24 0 black`): in a list it fills the gaps and shows as a black band under every card. Hover changes the
  shadow layers one for one, so the transition is smooth. Hero and sheets keep the big shadow (`HelmHeroShadow`).
- **A scrolling list keeps room for its scrollbar and shadows**: a `ScrollViewer` inside a column has `Padding="0,0,14,14"`,
  so the bar never touches the cards and the last card's shadow is not cut off. (A page-wide scroller needs it only
  when the page margin is under 16.)
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
- **Fields fill the row.** Inside a form or card a field is full width, or equal columns sharing the row; nothing sits
  left-aligned with a gap beside it. A `WrapPanel` is only for chips and buttons, never for fields. On a phone the
  usual arrangement is the longest field on its own line, then two equal columns (Grid positions set in code from
  `OnSizeChanged`, as `OptionsGrid` in TrackerView).
- Round things stay round: a button in a pill (the tab bar's +) has `CornerRadius` = half its height, so its hover never
  squares off the pill.
- A row's tap area (`Button.rowhit`) has no look of its own: the whole card lights up (`Border.glass.lift`).

## 5. Layout: one view, PC and phone
- One adaptive view; below 720 px the view gets class `narrow` (set in code from `OnSizeChanged`). Select it with
  `:is(UserControl).narrow …` (a plain `UserControl.narrow` selector never matches the view's own type).
- Phone: actions are icons in one row (labels `TextBlock.label` hidden), side-by-side things stack, lists of cards go
  two by two or scroll sideways with the scrollbar hidden. The tabs and the gear stay on the **right**, as on PC (on a
  phone on their own line under the date, right-aligned).
- **Text never runs out of its card.** Text beside an icon or a button is in a `Grid` with an `Auto,*` column (the text in
  the `*` column, `TextWrapping="Wrap"`), never in a horizontal `StackPanel` (a StackPanel gives its children
  unlimited width, so a long sentence is cut at the card's edge). Check every message and warning at Phone 390.
- Every floating card (date picker, settings, confirmation) is `Sheet.Show(anchor, body)`: a card in the middle on PC, a
  sheet from the bottom on a phone. It opens over the nearest ancestor with class `screen` (the Gallery's phone frame)
  or the window, keeps the page's light/dark, and grows into place.

## 6. Motion: never static, never in the way
- Things grow into place (popup scale ~0.94 → 1 with opacity, 260 ms, cubic ease-out), not a plain fade:
  `core:Motion.EnterOnShow="True"` on anything shown again (tabs, folded sections, an opened task, edit forms, option
  rows, messages); add `core:Motion.FromTop="True"` for a section opening under its header. `Motion.Enter(control)`
  in code (a list just picked, a sheet: phone sheets grow from the bottom edge). `Classes="enter"` for first load.
- ComboBox lists grow open from the top (`ComboBox.glass:dropdownopen` style) and open under the field with the left
  edges lined up (`Placement=BottomEdgeAlignedLeft` on its Popup; the default centres a list wider than the field).
- Disclosure arrows point **right when closed, turn a quarter to point down when open, and turn back**: a
  `ChevronRight` in a `Panel.turn` with `core:Motion.Turn="{Binding IsExpanded}"` (never an arrow that flips up). The
  tick ring pops when a task is done.
- Hover: cards light up and cast a deeper shadow (Background + BoxShadow transitions), no movement.
- **Avalonia 12 pitfalls**: transform strings in a Setter (`"translateY(-2px)"`, `"rotate(180deg)"`) do nothing; use
  transform objects (`<ScaleTransform/>`, `<RotateTransform/>`) and animate their properties
  (`ScaleTransform.ScaleX`, `RotateTransform.Angle`). Once a transform is animated, Avalonia replaces RenderTransform
  with its standard TransformGroup: code must accept a group. A selector list mixing a control and a
  `/template/` part fails to compile: write two styles.

## 7. Reference components (copy these, do not reinvent)
- **Page frame**: `core:Backdrop` + ScrollViewer + StackPanel. Margin 40,28 on PC, 16,18 on a phone. Spacing scale
  8 · 12 · 16 · 22 · 28; between a title and what follows 16–22.
- **Top bar of a tool**: left the eyebrow line (today's date + counts, wraps on a phone); right the segmented tabs
  (`RadioButton.seg` in a small glass group: the chosen one lighter and bold) and the gear (`chip`). Tab changes use
  `IsCheckedChanged`, so keyboard and screen readers work too.
- **List switcher**: a row of `Button.glass` cards (initial tile, name, "n open · n overdue" with overdue in
  HelmDanger); the open one has the strong stroke; "New list" is a `ghost` at the end; the row scrolls sideways with its
  scrollbar hidden. Under it the list's `display` title and a `muted` summary.
- **Quick add**: one glass card, a bare text box ("Add a task — press Enter"), an options `chip` (toggle) and a `pill`
  "+" button (40 × 40). Options (date, priority, repeat) open under it in the `OptionsGrid` and only show what applies.
- **Task row**: `Border.glass.lift`; left the round tick (ring 22, fills green with a pop when done); title 15; tags
  (`Border.tag`: Urgent red, High accent, In progress green, step count) then the details line (overdue in red); notes
  max 2 lines; a right-pointing chevron that turns down. Tapping the text opens it: "+ Add a step" (bare box), one row
  of `act` buttons (Start, Split into steps in accent, Edit, Link a note, ↑ ↓, Delete in HelmDanger), and the inline edit
  form (all fields `glass`, Save `pill`, Cancel `ghost`). Done tasks sit under a folded "Done" header.
- **Managing things (lists, folders…) lives in the settings sheet, fully**: a "Lists" card with one row per list (tile
  with its initial, the name as a `glass` field you edit in place and save on Enter or leaving it, a `secondary` line
  "n open · showing now", a red trash `chip` that asks first), then a field + `pill` "Add list". Do not leave a lone
  "delete this …" button for the current item. On a wide screen the page itself has no "New list" button (it is added
  in the settings); a phone keeps a quick "New list" at the end of the list row.
- **Settings sheet** (`TrackerSettingsView`): header = eyebrow (the tool's name) + "Settings" 22 semibold + a close
  `chip`; then one `Border.glass` card per subject (lists, display, reminders, export), each with a `head` title;
  buttons inside a card fill its width (one button: full width; two: equal columns) and are never left-aligned with a gap;
  a setting is its name and a `secondary` explanation on the left and a switch on the right, or a label above a full-width
  `glass` field; a dependent setting is disabled while its switch is off; a destructive action is a `ghost` with a
  red icon and text and a `secondary` line saying what it removes; the bottom is one full-width `pill` "Done". Order:
  what is about the current thing first, then display, reminders, export. Platform-only settings (the Android widget) show
  only on that platform.
- **Date picker** (`DateTimeBox`): the field's name as eyebrow, the chosen value as a sentence (20 semibold), quick-pick
  chips (Today, Tomorrow, This weekend, Next week; for past things Today, Yesterday, 2 days ago), the month with round
  days (today ringed in teal, chosen day filled like a pill, other months faded), the time as chips (No time, 09:00 …) plus
  hour and minute fields, footer: Clear (`ghost`, only when the value may be empty) left, Cancel `ghost` and Done `pill`
  right.
- **Confirmation** (`GlassDialogs`): title 18 semibold, a `secondary` sentence saying what will be removed and what stays,
  Cancel `ghost` + the action `pill` on the right; the action is named by its verb ("Delete"), never "OK".
- **First run**: eyebrow "START HERE", a title, three step cards (the first outlined, the rest faded), one pill.
- **Calendar**: month grid of `glass` cells (round only inside the date picker), dots for what is due (red overdue,
  amber debts, faded done), week view as rows; the chosen day's tasks under it with a quick add.
- **Insights**: numbers in `stat` (26 semibold) in glass cards, then bars in the accent colour.

## 8. Writing
- English UI text in sentence case (Settings, Add a task), short. The eyebrow line is upper case via `core:UpperCase`.
- Placeholders say the action and the key ("Add a task — press Enter"). Empty states say what to do next and point at it.
- A question names the thing ("Delete this task?"), then one sentence says what is removed and what stays.
- Vietnamese typing must work in every text field (Helm Keyboard): use the shared `TextBox`, never a custom one.

## 9. Inside the WPF shell (until the shell moves to Avalonia)
- Show a page with `AvaloniaHost.Show(() => new …View(...))`; it shows a message instead of failing.
- Popups are drawn inside the page (`OverlayPopups`): a separate popup window opens in the wrong place after the WPF
  window moves.
- The WPF page sets `ScrollViewer.CanContentScroll="False"`; the Avalonia view scrolls itself.

## 10. Demo and check
- Demo in Helm.Gallery (`scripts/gallery.cmd`): the real view with fake data, never an HTML mock-up or a demo-only
  copy. Check Phone 390, Tablet 800, PC; dark and light; tool off.
- Then in a test Helm (`Helm.exe --no-elevate --allow-multiple --data-dir <scratch>`) to see it with the whole app.
  Never send keys or clicks to the user's desktop; drive test windows with UI Automation, capture with PrintWindow.
- Headless tests in `tests/Helm.Ui.Tests` for behaviour (tabs, visibility, transforms' targets).
