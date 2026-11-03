# Models & keys

Helm's tools can call AI models themselves, with your own API keys, on the PC and on the phone. Keys live on the
**AI** page, right after General, under **Models & keys**: one section per provider (Claude and Grok for now), each
with its keys.

- [What the page shows](#what-the-page-shows)
- [Get a Claude API key](#get-a-claude-api-key)
- [Get a Grok API key](#get-a-grok-api-key)
- [Where keys are kept](#where-keys-are-kept)
- [For developers](#for-developers)

## What the page shows

Only what the provider's API tells; nothing is typed in by hand.

| | Claude (Anthropic) | Grok (xAI) |
|---|---|---|
| Key works / rejected / blocked | yes | yes |
| Models the key may use | yes | yes |
| Key's name in the console | no | yes |
| Prepaid balance | no (the API does not tell it) | yes, with a management key |
| Credit expiry | no | no |

**Test and save** asks the provider about a key before keeping it, and a key the provider rejects is not saved. For
Claude this includes one tiny request to the cheapest model (far below a cent): listing models works without credit,
so only a real request tells an empty balance apart. For Grok the test is free.

Each key shows a status dot and the words for it: green *works*, amber *blocked* (no credit, a blocked key or team),
red *rejected* (mistyped, deleted, disabled), grey *not known* (no internet, or never tested). **Test** again at any
time; the result syncs too. The **default** key of a provider (its first one, or the one you star) is the one a tool
uses when it does not name a key.

## Get a Claude API key

1. Sign in to the [Claude Console](https://platform.claude.com) with the account that pays.
2. **Settings → Workspaces → Create**, name it `Helm`. Usage and Cost can then be filtered by workspace, so what
   Helm spends never mixes with your other work on the same account.
3. In the Helm workspace, set a **monthly spend limit** (5 USD is plenty to start).
4. **API keys → Create key**, pick the Helm workspace, copy the key (`sk-ant-…`). The Console shows it only once.
5. **Billing**: buy credit if the balance is empty. Prepaid credit expires a year after purchase, so add a little at
   a time.
6. **AI → Models & keys → Claude → Add key**, paste it, **Test and save**.

One key reaches every Claude model (Haiku, Sonnet, Opus): a tool picks the model per request.

## Get a Grok API key

1. Sign in to the [xAI Console](https://console.x.ai).
2. **Billing**: buy prepaid credit. Monthly invoicing is off by default, so requests stop when the credit runs out.
3. **API Keys → Create API key**, name it `Helm`, copy it (`xai-…`).
4. **AI → Models & keys → Grok → Add key**, paste it, **Test and save**.
5. Optional, to see the balance: create a **management key** in the console, then **Prepaid balance → Add management
   key**. Helm reads the balance of the team of your Grok key with it.

## Where keys are kept

API keys are records of the synced collection `ai.keys`: end-to-end encrypted on the way and on the sync server,
and in the encrypted local copy on each device, like the rest of your data. General → Sync → *What syncs on this
device* can keep them off a device (**AI keys**).

Management keys are records of `ai.management`, synced the same way, so every device shows the balance. A management
key can do more than read (an xAI one can buy credit with the saved card): keep it to the devices you trust with the
API keys. A key an older Helm kept in a file on one PC (DPAPI) moves to sync the first time the new Helm starts there.

Helm never shows a key again after you add it, only its prefix and last four characters. Removing a key in Helm
removes it from every device; to stop the key itself, delete it in the provider's console too.

## For developers

`src/Helm.Ai.Core` (net8.0, shared by both apps):

- `AiProviders`: the providers Helm knows (`Claude`, `Grok`), with the key prefix and the console link.
- `AiKeyStore`: add, rename, test results, default per provider (`DefaultFor`), delete.
- `IAiKeyChecker`: `ClaudeKeyChecker` (official Anthropic SDK, `Anthropic` on NuGet) and `GrokKeyChecker`
  (`GET https://api.x.ai/v1/api-key` and `/v1/models`).
- `IAiBalanceReader`: `GrokBalanceReader` (`GET https://management-api.x.ai/v1/billing/teams/{team}/prepaid/balance`;
  the ledger is in USD cents, purchases negative, so what is left is minus the total). `AiManagementKeys` keeps the
  management keys (synced, one per provider).
- `AiKeysViewModel`: the page's view model; the WPF view is `Helm.App/Views/Ai/AiKeysView.xaml`.

A new provider is an `AiProviders` entry plus an `IAiKeyChecker` (and an `IAiBalanceReader` if its API tells the
balance), registered in `AiCoreServices`.

## Ask Helm and the AI router

**Ask Helm** (Android) is a Quick Settings tile that opens a card over any app: it listens (the phone's own speech
recognition, Vietnamese), sends what you said, and lists what it did with Undo. It runs on the model picked in the
card, with Helm's own tools and the phone's (alarm, timer, calendar, clipboard).

Every Ask Helm turn goes through the **router** (`Helm.Ai.Core/Assistant/AiRouter.cs`), per feature
(`AiFeatureChoice.Route`):

| Policy | What happens |
|---|---|
| Prefer PC (default) | An agent on a Helm PC if one takes the turn within `PcWaitSeconds` (8 s), else the API |
| PC only | Only a Helm PC; nothing is spent on the API |
| API only | Only the API |

Agents are tried whatever model is picked; the pick is the API's, when no agent answers. A conversation that started on a PC stays there;
if the PC stops answering, the API takes over with what was said so far.

**The agents** (AI → Proxy, `Helm.App/Ai/AgentRegistry.cs`) are the user's list, kept per PC in
`settings/ai-agents.json`: Codex (`CodexAgent.cs`), Claude Code (`ClaudeCodeAgent.cs`) or Grok (`GrokAgent.cs`), any number, tried in the
list's order; a conversation sticks to the agent that took it. Each has its own sign-in folder under
`ai/agents/<id>` (`CODEX_HOME`, `CLAUDE_CONFIG_DIR`, `GROK_HOME`), never the user's own login. **Add** opens the sign-in terminal
at once (through Explorer, so it runs unelevated); a file watcher on the credentials (`auth.json`,
`.credentials.json`; for Grok, a marker the sign-in script writes once `grok login` succeeded) turns the agent ready without a click, and the PC's heartbeat goes out at once. The worker
starts with Helm, so after a restart the agents take jobs again as soon as Helm runs.

**How a phone reaches the PC.** Each PC running Helm with a ready agent writes a heartbeat to the synced collection
`ai.workers` every minute. The phone writes the turn to `ai.jobs` (urgent, so it syncs at once) and keeps sync's
live channel open while it waits. The PC claims it, runs it, and writes the result back. The PC claims only up to
`PcWaitSeconds` after the turn was sent (by its own clock); the phone listens 4 s longer (`AgentJobs.ClaimGrace`), so a
claim that comes back late still binds. Once claimed, the turn is the PC's: it waits for the result (up to 2
minutes), and when the PC fails or runs out of time the card says so (`AiRouteTakenException`) instead of sending the
turn to the API, so nothing is saved twice; what the PC recorded before it stopped comes back with Undo. Closing the
card withdraws the turn: a PC that has not taken it never will, one that has stops. Undo goes back to the PC that
made the record. Jobs and heartbeats are end-to-end encrypted like all Helm data.

**On the PC** (`LocalAgentRoute`), the agent runs with its own tools off (Claude Code: no tools, no MCP, no hooks; Codex: every one of its own tools disabled, shell included;
Codex: a read-only sandbox in an empty folder; Grok: headless `grok --prompt-file` with `--tools ""`, no web
search, no subagents and Helm's instructions instead of Grok's coding prompt): Helm lists its tools in the first message, the agent answers with the calls it wants as JSON
(`CliAgentProtocol`), Helm runs them in-process and sends the results back. Claude Code keeps one process per conversation
(about 3 s for the first turn, 1–2 s after); Codex runs one per turn and resumes its thread; Grok runs one per turn and resumes the session Helm named, and reports no usage
(what it saved is estimated from the text). Calls that would need a yes are not offered there, and phone tools are
handed back to the phone. An agent that reports its plan's limit takes no job for 30 minutes; the next one in the list does.

**Only the tools a request needs** (`Helm.Ai.Core/Assistant/ToolScope.cs`). Every tool's schema costs tokens on
every call, so a turn carries only the groups its words ask for (Wallet for “phở 45k”, Tracker for “nhắc anh…”,
the phone for “báo thức”…), and the groups of the conversation's earlier turns; a request that names no group gets
them all. A later turn that needs another group adds only that group (`IAssistantChat.AddTools`). For “phở 45k”: 4
tools instead of 19, 4,800 characters instead of 12,600, on the API and on an agent alike.

**A turn that only records things needs one call.** When every call of a step is a change Helm can undo (or a phone
action) and all of them went through, the turn ends there: the card shows their summaries with Undo, instead of a
second call to say so ("phở 45k" on Grok: 8.4 s became 4.8 s). The results go to the model with the next turn's text.
A question still takes two calls (the tools' answer, then the reply).

**Grok carries nothing of its own.** Grok CLI brings 24 built-in tools, 19 skills, workflows and the user's Claude
and Cursor set-up (MCP servers, hooks): 15,000 tokens and 10 s a call. Helm's agent removes the tools
(`--disallowed-tools`), turns skills, workflows and the Claude/Cursor compatibility off, and gives it a home folder of
its own: about 640 tokens of Grok's and 3.5 s a call.

**Costs.** Every call goes in the synced collection `ai.usage`: route (`api` or `agent`, with the agent's name), model, tokens, the
estimated USD (`AiPrices`), and on an agent what the same call would have cost (`SavedUsd`). The daily cap
(`DailyCapUsd`, 0.50 USD) only stops the API.

Only Helm's own features and your own services use the router. Other apps (Cursor, n8n, Open WebUI…) use their own
API keys: lending a Claude plan to other apps is against Anthropic's terms.

## Your VPS

The same agents can serve your own VPS (AI → Proxy → **Your VPS**): its services send AI jobs to this PC, and the
agents answer them on their plans. Type the VPS's worker endpoint (`wss://<vps host>/ai-worker`; `ws://` only to
`127.0.0.1`, for tests) and the worker token the VPS gave this PC, press **Save**, and turn it on. Address and switch
are this PC's own (`settings/ai-vps.json`, never synced); the token is kept encrypted for this Windows user
(`settings/ai/vps-worker-token.bin`, DPAPI), never synced and never shown again. The line under the switch says
"Connected to <host> · N jobs today" or why it is not connected.

`Helm.Ai.Core/Vps/VpsWorker.cs` keeps one WebSocket open (`Authorization: Bearer <token>`, JSON text frames,
`VpsProtocol.cs`):

| Frame | Way | When |
|---|---|---|
| `{"type":"hello","device","agents","version"}` | PC → VPS | On connect: the ready agents, best first |
| `{"type":"status","agents"}` | PC → VPS | The ready agents changed (empty: online, takes no job) |
| `{"type":"job","id","feature","system","prompt","model","effort"}` | VPS → PC | A job |
| `{"type":"claim","id"}` / `{"type":"decline","id","reason"}` | PC → VPS | At once: an agent is ready, or why not |
| `{"type":"result","id","text","agent","savedUsd"}` / `{"type":"error","id","message"}` | PC → VPS | The answer |

A job is **text only**: no Helm tool and no phone tool is offered (the VPS's services have their own data), its
`system` is the system prompt (null: a short neutral one, not Ask Helm's instructions), and it is a conversation of
its own, ended once answered. Two run at once; one more is declined, so the VPS can send it elsewhere. A job may take
2 minutes. After a drop the worker connects again after 1 s, waiting twice as long each time up to a minute; jobs
running on a connection that dropped are cancelled. The token and job contents are never logged.

## Automatic jobs (briefs from the PC)

A Helm PC that is on runs a few jobs by itself on its agents (AI → Proxy) and sends the result to the phone as a short
**brief**: a notification there, a tray balloon on the PC, and Ask Helm reads them out ("bản tin sáng nay nói gì?").
They cost nothing on the API: they run only on a PC agent, never on the paid API, unless that feature's route is set
to allow it (`AiFeatureChoice.Route`, `PcOnly` for these by default).

| Job (`AiFeatureIds`) | When (default) | On by default | What |
|---|---|---|---|
| Bản tin buổi sáng (`morning-brief`) | every day 07:00 | yes | today's and overdue tasks, habits to do, yesterday's spending |
| Tổng kết buổi tối (`evening-review`) | every day 21:30 | yes | tasks done today, those slipping to tomorrow, one suggestion |
| Huấn luyện thói quen (`habit-coach`) | Sundays 19:00, and the evening a habit is skipped a 2nd day in a row | no | suggestions from the reasons given for skipped days |
| Tóm tắt chi tiêu (`spending-digest`) | Sundays 20:00 (the week) and the 1st (the month before) | yes | top categories, against the period before, funds running low |
| Ngày quan trọng (`important-days`) | every day 08:00 | no | 3 days before and on the day: a gift idea or a wish |
| Tự đặt tiêu đề và tag (`note-titles`) | every hour | no | a title and tags for new notes without one (undoable) |
| Dọn Scratch (`scratch-tidy`) | Saturdays 10:00 | no | what to keep as a note, make a task of, or let go (suggests only) |

On/off and the time are each feature's choice in the synced `settings.ai-features` (`Enabled`, `At` = "HH:mm"); Ask
Helm changes them (`ai_auto_jobs`, `ai_auto_job_set`, with Undo).

**Days to remember** live in the note **Ngày quan trọng**, one per line, which the user may also write by hand:
`15/03 · Sinh nhật mẹ` (every year), `10/07 âm · Giỗ ông nội` (a lunar day, every year), `20/05/2027 · Đăng kiểm xe`
(that day only). Ask Helm adds one with `notes_add_important_day` ("nhớ giúp anh giỗ ông ngày 10 tháng 7 âm").
Lunar days are turned into solar ones by `Helm.Core.Text.LunarCalendar` (Hồ Ngọc Đức's algorithm, UTC+7).

**Note titles** are a normal edit of the note (`notes_retitle`); the brief keeps each change, and `ai_brief_undo`
takes them back (Ask Helm asks first).

### For developers

`src/Helm.Ai.Core/Auto`:

- `AutoJobScheduler` ticks every minute (the user's local time). A job's slot (`AutoSlot`: hourly, daily, a weekday, a
  day of the month) may run from its time for 4 hours. Each slot runs once per period, kept in the synced
  `ai.autoruns` (`AutoJobRuns`): done stays done after a restart; a failure is tried again after 15 minutes, 3 times
  at most; a run left "running" by a PC that closed is taken again after 20 minutes. With two PCs, the one that runs
  them is, of the PCs whose heartbeat (`ai.workers`) says an agent is ready (this one counts when its own are), the
  one with the lowest device id. No agent ready: nothing is claimed and it is tried again next minute. Nothing runs
  while a Vault is unlocked (`IVaultSecrets.IsUnlocked`); no job reads Vault.
- `IAutoJob` (one class per job in `Auto/Jobs`) reads with Helm's own read tools in-process (`AutoJobTools`: no MCP,
  only read tools, and changes only through tools with Undo), asks with a short prompt, and returns an `AutoBrief`;
  null when there is nothing to say (then no call is made at all).
- `AutoJobAsker`: a text-only `AiJob` (no tools, the briefs' own system prompt, Vietnamese) on this PC's agents
  (`AgentRegistry`); the API only as above, within the feature's daily cap. Every call is in `ai.usage`.
- `AiBriefBook`: the synced `ai.briefs` (`{Kind, Title, Text, CreatedAt, ReadAt, Device, Actions, UndoneAt}`,
  urgent, kept 30 days). `AiBriefAlerts` tells of each new brief once per device (`settings/ai-brief-alerts.json`),
  not one already read nor one older than 12 hours; Android posts it (`QuickCapture.Android/AiBriefNotifications`),
  the PC shows a tray balloon.
- `AiBriefTools`: `ai_briefs` (reading marks them read), `ai_brief_undo`, `ai_auto_jobs`, `ai_auto_job_set`.
- Scratch is read through `ScratchDigestTool` (names only, a text that looks like a code is hidden), given to the
  jobs alone, never to the MCP server. Funds come from `wallet_funds`.
