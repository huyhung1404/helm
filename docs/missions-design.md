# Missions — design

Status: v1 built (everything in §8 v1). Where the build differs from the first draft, this document says what was
built.

A **mission** is a goal reached through an ordered chain of steps ("pass HSK3", "run a half marathon", "ship the
v1 of my app"). The user does not type the steps: an AI writes them. Helm gives a prompt template, the user pastes it
into any AI and imports the JSON that comes back, or asks Claude in Helm, which creates the mission directly over MCP.
Helm then shows one step at a time, logs when each one started and finished, keeps track of the pace against the
deadline, and celebrates phases and the finished mission with the reward the user chose.

Decisions already taken:

| | |
|---|---|
| Platforms | PC + Android, like the other tools (no iOS) |
| Shape | Its own module, `missions`, with a shared `.Core` project (not a new Tracker workspace kind) |
| Order | Strictly sequential: only the current step can be started, completed or skipped |
| Claude | MCP tools in v1 (create, read, complete the current step) |

## 1. User flow

1. **Copy the prompt.** Missions → *New mission* → *Copy prompt*. A small form asks for the goal (required), where the
   user is now, the time per day, a deadline and free notes; *Copy* fills them into the template (§4) and puts it on
   the clipboard.
2. **Ask an AI.** The user pastes the prompt into ChatGPT, Gemini, Claude… and copies the JSON answer.
   Or, on PC, asks Claude Chat directly ("make me a mission to pass HSK3 by March"): Claude calls `mission_create`
   and step 3 is skipped (the mission appears as *Planned*).
3. **Import.** *New mission* → *Import JSON* → paste (Android: long-press the box → Paste). Helm parses
   forgivingly (§3.3) and shows a **preview**: title, goal, deadline, reward, phases and steps, total estimated days
   and the finish date that implies, plus the AI's `note` if any. The user can edit the title, goal, deadline and
   rewards and remove steps, then *Create mission*. Errors say what is wrong and where ("phase 2, step 4: no title").
4. **Start.** A new mission is *Planned*. *Start mission* sets its start date (the pace counts from here) and makes
   step 1 current.
5. **Work.** The content page shows the **current step** large: phase, title, description, *Done when…*, checklist,
   resources, how long it has been running. Actions: *Start* (optional; logs the start time), *Complete* (optional
   note), *Skip* (asks first; logged as skipped). The next step becomes current immediately.
6. **Celebrate.**
   - Step done: a short inline animation and "Step 8 of 24 done".
   - Phase done: a dialog with the phase's reward and its numbers (days taken vs planned).
   - Mission done: a full celebration (confetti, can be turned off) with the mission reward and a **summary**:
     days taken vs planned vs deadline, steps done and skipped, longest streak, the notes written on each step.
     *Copy summary* (Markdown); *Mark reward as claimed* records when the user actually treated themselves.
7. **Undo.** The last completed or skipped step can be reopened; it becomes current again (a finished mission goes
   back to *Active*).

## 2. Module layout

```
src/Helm.Modules.Missions.Core/        net8.0, references Helm.Core only
  MissionsModels.cs                    Mission, MissionPhase, MissionStep, ChecklistItem, MissionEvent, enums
  MissionsStore.cs                     synced collections + the sequential rules (§3.2); raises Changed
  MissionImport.cs                     JSON text -> MissionDraft + errors/warnings (§3.3)
  MissionPrompt.cs                     the prompt template and its form (§4)
  MissionPace.cs                       planned dates, ahead/behind, projected finish, streaks (§5)
  MissionSummary.cs                    the end-of-mission summary as Markdown
  MissionsMcpTools.cs                  IMcpToolProvider (§6)
  MissionsViewModel.cs                 shared page view model (CommunityToolkit.Mvvm)
  MissionsSettings.cs                  IVersionedSettings
  MissionsIconShape.cs                 picture icon shape (flag on a path)
  MissionsCoreServices.cs              AddMissionsCore(): collections, store, MCP tools, view model
src/Helm.Modules.Missions/             WPF: MissionsModule, MissionsPage (settings), MissionsContentPage,
                                       MissionsLogo, CelebrationOverlay, MissionsServices
src/Helm.Modules.Missions.Android/     Avalonia: same set, one column
tests/Helm.Tests/Missions/             import, store rules, pace, prompt, MCP tools
```

- Module id `missions`, display name **Missions**, group `SystemTools`, symbol `Flag24` (U+F40C, exists and is below
  U+FFFF; Android `Symbol.Flag`) as the fallback for the picture icon.
- Description: "Reach a goal one step at a time: import a roadmap written by an AI, work through it in order, and
  see every step's dates and your pace."
- Content page + settings page, as Tracker (`IModuleContent`).
- Wiring: one line in `HelmModules.cs`, one in `AndroidModules.cs`, sln/test references as in `new-tool-prompt.md`.

## 3. Data

### 3.1 Records (Helm Sync, end-to-end encrypted, sync group `missions.`)

`missions.missions` — `ISyncedCollection<Mission>`, last writer wins:

| Field | Type | Notes |
|---|---|---|
| Title | string | ≤ 200 chars |
| Goal | string | the measurable end result, ≤ 1000 |
| Note | string | the AI's caveat from the import, shown on the mission card |
| Reward | string | the user's reward for the whole mission |
| Deadline | DateOnly? | |
| Phases | List&lt;MissionPhase&gt; | `{ Key, Title, Reward, RewardClaimedAt? }`, in order; `Key` is a short random id |
| Status | enum | `Planned`, `Active`, `Paused`, `Completed`, `Abandoned` |
| CreatedAt / StartedAt? / CompletedAt? | DateTimeOffset | UTC |
| PausedAt? / PausedTotal | DateTimeOffset? / TimeSpan | paused time is left out of the pace |
| RewardClaimedAt? | DateTimeOffset? | |
| Source | enum | `Import`, `Claude`, `Manual` |
| Order | double | position in the mission picker |

`missions.steps` — `ISyncedCollection<MissionStep>`, last writer wins, one record per step (so ticking a step on the
phone and renaming another on the PC never collide):

| Field | Type | Notes |
|---|---|---|
| MissionId, PhaseKey | string | |
| Order | double | position in the whole mission (phases are contiguous) |
| Title / Description / DoneWhen | string | ≤ 200 / 4000 / 500 |
| EstimateDays | double | 0.25 – 60, default 1 |
| Resources | List&lt;string&gt; | URLs or plain text (book titles); URLs open with `IProcessLauncher` |
| Checklist | List&lt;ChecklistItem&gt; | `{ Text, DoneAt? }`, ≤ 20 |
| StartedAt? / StartedExplicitly | | as Tracker: set by *Start*, else on completion from the previous step's finish |
| CompletedAt? / Skipped | | a skipped step has `CompletedAt` and `Skipped = true` |
| Note | string | written when completing |

`missions.history` — `ISyncedLog<MissionEvent>`, append-only: `Kind` (`MissionCreated`, `MissionStarted`,
`StepStarted`, `ChecklistTicked`, `StepCompleted`, `StepSkipped`, `StepReopened`, `PhaseCompleted`,
`MissionCompleted`, `Paused`, `Resumed`, `Abandoned`, `RewardClaimed`), `At`, `MissionId`, `StepId?`, `PhaseKey?`,
and a snapshot of the titles, so the timeline survives edits and deletions.

### 3.2 Rules (in `MissionsStore`, unit-tested)

- **Current step** = the first step by `Order` with no `CompletedAt`. None left = the mission is complete.
- Only the current step of an `Active` mission can be started, ticked, completed or skipped. Steps after it are
  readable but locked. A `Planned`, `Paused`, `Completed` or `Abandoned` mission is read-only except for its own
  status buttons.
- `CompleteStep` returns `{ PhaseCompleted?, MissionCompleted }` so the UI knows which celebration to play. Completing
  the last step of a phase logs `PhaseCompleted`; the last step overall sets the mission `Completed`.
- Completing with unticked checklist items asks once ("2 items are not ticked. Complete anyway?").
- `ReopenLast` reopens only the most recent completed/skipped step. Reopening the last step of a finished mission
  makes it `Active` again.
- Editing: future steps (not current, not done) can be edited, moved within their phase, deleted, and new steps
  added after the current one. Done steps keep their text (it is the record of what was done); only their note can
  be edited.
- Sync: two devices completing the same step write the same record, so the latest write wins and nothing doubles.
  The history may then hold two `StepCompleted` for one step; the timeline and stats take the latest per step.
  Steps that arrive before their mission (sync order) are ignored until it arrives.
- Deleting a mission (settings page, confirmed) deletes the mission and its steps; history entries stay, like
  Tracker's.
- Nothing is ever deleted through MCP.

### 3.3 Import format

The import accepts exactly what the prompt asks for, and forgives what AIs usually get wrong:

- Text around the JSON (explanations, ```` ```json ```` fences): the first `{` to its matching `}` is taken.
- Typographic quotes `“ ”` used as JSON quotes, trailing commas, `//` comments.
- `steps` at the top level with no `phases`: one phase named after the mission.
- Unknown fields are ignored; missing optional fields get defaults; `estimateDays` out of range is clamped (warning).
- Limits: ≤ 30 phases, ≤ 300 steps. Over that is an error (it is not a plan anymore).
- `"helmMission": 1` marks the version; when missing, version 1 is assumed. A later version Helm does not know is an
  error that says to update Helm.

```json
{
  "helmMission": 1,
  "title": "Pass HSK3",
  "goal": "Score 210+ on the HSK3 exam",
  "deadline": "2027-03-01",
  "reward": "A new set of graded readers",
  "note": "Optional: anything the user should know, e.g. the deadline is tight.",
  "phases": [
    {
      "title": "HSK1–2 foundations",
      "reward": "Hotpot night",
      "steps": [
        {
          "title": "Learn the 150 HSK1 words",
          "description": "Anki, 20 new words a day, review every evening.",
          "doneWhen": "90% right on a self-test of all 150 words",
          "estimateDays": 8,
          "checklist": ["Words 1–50", "Words 51–100", "Words 101–150", "Self-test"],
          "resources": ["https://www.hskstandardcourse.com"]
        }
      ]
    }
  ]
}
```

*Copy as JSON* on a mission writes the same format (plus `status`, `startedAt`, `completedAt` per step), so a
mission can be backed up, shared or handed back to an AI.

## 4. The prompt template

Form fields: **Goal** (required), **Where I am now**, **Time I can spend** (e.g. "1 hour a day, 3 on weekends"),
**Deadline** (date or empty), **Other notes**. Empty fields are left out of the prompt. The template is English; it
tells the AI to write the content in the language of the goal, so a goal typed in Vietnamese gives a Vietnamese
mission.

```text
You are an expert coach. Plan a step-by-step roadmap for my goal and return it as JSON that my app,
Helm Missions, will import.

My goal: {goal}
Where I am now: {level}
Time I can spend: {time}
Deadline: {deadline | "none — suggest a realistic one"}
Other notes: {notes}

Rules:
- Reply with ONE ```json code block and nothing else.
- Write every title, description and reward in the language of my goal.
- 3–8 phases in the order I should do them; 3–10 steps per phase.
- Each step is one concrete piece of work that takes 0.5–3 days at my pace. No vague steps like "practise more".
- "doneWhen" says how I can check the step is done: a number, a test, something I produce.
- "estimateDays" must be realistic for my time; the total must fit the deadline. If it cannot, still give the
  plan and explain in "note".
- "checklist" (optional, up to 7 items) splits a step into ticks. "resources" (optional) are well-known, real
  links or book titles; leave it out rather than invent one.
- End each phase with a short review or test step.
- Give each phase a small "reward" and the whole mission a bigger one.

Use exactly this shape:
{the example from §3.3, with placeholder values}
```

The template text lives in `MissionPrompt` (one place, unit-tested so that its example always parses with
`MissionImport`).

## 5. Pace and tracking

All computed from the records, nothing extra stored (`MissionPace`, pure, unit-tested):

- **Planned date** of step *i* = mission start + paused time + the sum of `estimateDays` of steps 1…*i*.
- **Ahead/behind** = today vs the planned date of the current step, in days ("2 days behind", "on track").
- **Projected finish** = today + the remaining estimate × the pace so far (actual days ÷ planned days of the done
  steps, between 0.5 and 3 so one odd step does not swing it). Compared with the deadline: "Projected 14 Mar, 13
  days after the deadline".
- **Streak** = consecutive local days with a `StepCompleted` or `ChecklistTicked`; current and longest.
- **Step duration** = `CompletedAt − StartedAt` (wall time: a pause during a step counts in it; only the mission's
  pace leaves paused time out).
- **Timeline** (content page, *Roadmap*): phases as expanders; each step shows ✓ with its finish date and duration,
  ● for current, a lock for future, ⤼ for skipped. Tapping a step shows its details and note.

## 6. MCP tools (PC: Helm's MCP server; results sync to the phone)

| Tool | | What it does |
|---|---|---|
| `missions_list` | read | Missions with status, progress (done/total), current step, pace, deadline |
| `mission_get` | read | One mission (id or title): phases, steps with dates and notes, pace |
| `mission_create` | write | Same shape as the import JSON, through the same validator; created as *Planned* with `Source = Claude` (`start: true` starts it at once) |
| `mission_complete_step` | write | Completes the **current** step of a mission (optional note); refuses any other step and says which one is current |

Like Tracker's tools: dates in the user's local time, errors returned as text Claude can act on, nothing deleted.

## 7. Pages

**Content page** (PC title + cards; Android one column):

1. Mission picker (first card, full width): missions in progress first, then planned and paused, then (with *Show
   finished missions*) completed and abandoned; buttons *New mission* (the prompt form) and *Import*.
2. Mission card: title, goal, progress bar "Step 8 of 24 · phase 2 of 4", chips for status, pace, deadline and
   streak, the projected finish, the AI's note and the reward; *Start mission* / *Resume* / *Take it up again* /
   *I took my reward*, *Pause*, *Copy summary*, *Copy as JSON*, *Abandon*.
3. Current step card (the big one), with *Complete step*, *Start*, *Skip*, *Undo last step*.
4. Roadmap (timeline, §5): each step expands to its details; steps not done yet can be edited or deleted, done steps
   can get a note; *Add a step* at the end. Before *Start mission*, no step is marked "now".
5. Empty state (no missions): the three steps of §1 with *Write the prompt* and *Import JSON* buttons.
6. A finished mission whose celebration was not seen on this device (completed over MCP or on another device) shows
   the mission celebration once, also in place of a phase's card that was still open.

**Settings page** (`ModulePageBase`): *Open Missions*; *Edit a mission* (title, goal, reward, deadline, position,
delete); *Behavior*: *Play celebrations* (on), *Ask before completing an unfinished checklist* (on), *Show finished
missions* (on); *New missions*: *Copy the prompt template*, and a note on asking Claude. Abandoned missions are taken
up again from the content page.

`MissionsSettings` v1: `PlayCelebrations`, `ConfirmUntickedChecklist`, `ShowFinishedMissions`,
`SelectedMissionId` (per device), `CelebratedMissionIds` (per device; which finishes were already celebrated here).

## 8. Plan

**v1** (0.21.0): everything above — module on PC and Android, import + preview, prompt form, sequential runner,
history and timeline, pace and streaks, the three celebrations and summary, settings page, MCP tools, tests,
CHANGELOG/README rows, icon.

**v2** (built):
- *Re-plan* (mission card, while the mission is not finished): what changed (optional) → a prompt with the mission,
  the done steps (how long each took against its plan, their notes), the pace, and the steps left as JSON → the AI's
  answer in the import panel (title optional) → preview "Replaces the N steps not done yet with M steps…" →
  `MissionsStore.Replan`. Done and skipped steps stay with their phases; every step not done (the current one too) is
  replaced; a plan phase whose title matches a phase that is not finished continues it (key and reward kept), other
  phases are new and come after the phases with done steps; phases left empty go. The plan's deadline and note replace
  the mission's when given. Claude does the same with `mission_replan` (asked to read `mission_get` first and to ask
  the user). No history kind is added for it: older apps would not read a new `MissionEventKind`.
- *Daily step reminder* (`MissionReminderService`, settings `RemindersEnabled` on, `ReminderHour` 8,
  `LastReminderDate`): once a day after the hour, one line per active mission, "Title: current step (n days behind)";
  missions with a done step or a tick today are left out, and a day with nothing waiting is not used up. PC: the
  module's 10-minute timer and a tray notification that opens Missions. Android: an hourly inexact alarm, a boot
  receiver, the "Daily step reminders" channel and a notification that opens Missions (as Tracker's reminders);
  the notification permission is asked once when Missions opens. *Remind me now* on the settings pages.
- *Share and Quick Capture*: `MissionCaptureTarget` ("mission", prefix `/m`). Shared text that imports as a mission
  picks it by itself, and saving creates the mission as *Planned* (not the import preview: the Quick Capture dialog
  is its own small window; the mission waits for *Start mission* anyway, and can be edited on the page).
- *Save summary to Notes*: through Notes' Quick Capture target ("note"), so Missions does not reference Notes; shown
  when Notes is on and a step is done. The note's title is "Mission: title".

**v3** (built):
- *Badges* (`MissionBadges`): 13 fixed badges — first step; 10, 50, 100 steps; 3, 7, 30 days in a row; a phase; 1
  and 3 missions; a mission finished in less time than planned; one finished by its deadline; a step after resuming a
  paused or abandoned mission. Worked out from the history and the missions on each refresh (earned at the moment
  the condition was first met), so nothing new is stored or synced; a reopened step stops counting until it is done
  again. The page lists them under *Badges* (collapsed by default) and a step that earns one adds "New badge: …" to
  its message or to the phase or mission celebration.
- *Android widget* (`MissionWidgetModel` in the core, `Widget/MissionsWidgets.cs`): up to three missions (in
  progress first, then paused), each with its title, current step, a progress bar and "3/10 · 2 days behind" (in red
  when behind); "+n more"; a short text when nothing runs or Missions is off. A fixed layout (no list, so no
  RemoteViewsService), redrawn 400 ms after store changes settle, when the tool is turned on or off, and hourly; a tap
  opens Missions. *Add to home screen* on the Android settings page when the launcher supports pinning.
- *Links*: `LinkKinds.Mission`; `MissionLinkProvider` (title and goal search, "Mission · step 3 of 10") registered
  with `AddMissionLinks`; notes link to tasks, people and missions (Notes' picker and `notes_link` with
  `mission_id`); the mission card shows its notes in a `LinksPanel` (*Link a note…*, *New note*) while Notes is
  there.

**v4** (built):
- *Today* (`MissionsViewModel.Today`, `TodayRow`): with two or more missions in progress, a card at the top lists
  each one's current step ("Step 3 of 10 · 2 days behind · 1/3 ticked"), with *Open* and *Complete* (asks first when
  the checklist is not all ticked; the mission is selected so its celebration has its place).
- *Send to Tracker*: `ITaskBridge` in Helm.Core (`Add`, `IsDone`, `Complete`, `Changed`), implemented by Tracker
  (`TrackerTaskBridge`: the first to-do list, "To-do" made when there is none), so Missions does not reference
  Tracker. `MissionTaskSync` sends the current step as a task due on the day the step should end (its estimate
  less the time already on it) and records `MissionStep.TaskId`; a task done in Tracker completes its step when it
  is the current one ("Done in Tracker."), and a step done in Missions (any device) completes its task. It works
  while both tools are on, from the modules' start. Older Helm versions drop `TaskId` when they edit that step.
- *Statistics* (`MissionStats`): steps this week and in each of the last 4 weeks (Monday-based), actual against
  planned time over the steps done (skipped ones left out), missions done and how many by their deadline, missions
  in progress. *CSV* of every step: mission, status, phase, number, step, status (done, skipped, current, open),
  planned and actual days, dates, done-when, note, ids; *Export…* on the PC settings page (UTF-8 with BOM),
  *Share CSV* on Android.
- *Ready-made missions* (`MissionTemplates`): five English roadmaps in the import format (a test imports each one);
  picking one opens the import preview, from the empty state or the prompt form.

**Later**: badges as notifications, a widget size with one big mission, completing a step from the widget.

Verification: the Windows part is built and tested locally (`dotnet build -c Release -warnaserror`,
`dotnet test`, smoke test with `--page Missions`). The Android project is not in `Helm.sln` and this machine has no
Android workload, so it is checked by CI unless the workload is installed.
