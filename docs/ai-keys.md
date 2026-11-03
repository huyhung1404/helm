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
them all. A later turn that needs another group adds only that group (`IAssistantChat.AddTools`). For “phở 45k”: the
Wallet's tools only (6 of about 30 today), on the API and on an agent alike.

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

## Model lists update by themselves

The model choice (Ask Helm's card, and each feature's pick) lists the **chat** models of the provider's default key,
from that key's last test. A running PC tests each key again by itself once its last test is a day old: soon after
Helm starts, then every day (an hourly look for keys that are due). A test that gets no answer (no internet) keeps
the last list and is tried again an hour later; any other answer is saved, synced, so the phone and the other PCs do
not test it again. For Claude each test is the same tiny request as **Test** (far below a cent a day); for Grok it is
free.

Only chat models are offered: ids with a part that starts with `imag`, `video`, `embed`, `tts`, `speech`, `audio`,
`whisper`, `transcri`, `moderat` or `rerank` (`grok-imagine-video`, `grok-2-image-1212`, `text-embedding-3`) are
dropped, for every provider (`AiModels.IsChat`). When a key's new list no longer has a feature's picked model, the
feature moves to the provider's default (Claude Haiku 5.5, `grok-4.7`), or, when that is gone too, to the first chat
model listed; the log says which. The Models & keys view model shows `ModelsUpdatedText` ("Model lists updated today,
09:12": the oldest of the keys' last tests), for a view to bind.

Code: `AiModelRefresher` (`RefreshDueAsync`, `KeepPicksOffered`), started by `App.xaml.cs` on the PC; `AiModels`.

## Your own prompts (Skill Hub)

Each AI feature's prompt (Ask Helm's instructions today) can be replaced by your own, kept as a saved prompt on your
Skill Hub, so you change it on the web and Helm uses it from the next fetch, without a new Helm.

**Names.** A saved prompt named `helm-<feature id>` replaces that feature's built-in prompt: `helm-ask-helm` for Ask
Helm. The feature ids are the `AiFeatureIds` constants (lowercase letters, digits and dashes). The Skill Hub names a
new prompt after its title, so create it titled `helm-ask-helm` (rename the title afterwards if you like: the name
stays), or ask Claude Code to `prompt_save` it with `name: "helm-ask-helm"`. **Archive** or delete the prompt to put
the feature back on Helm's built-in text on every device.

**Rules.** Helm uses your prompt only while all of these hold, else the built-in one (nothing fails):

- it was fetched (confirmed by the hub) within the last 3 days;
- it is not empty and at most 32,000 characters (a longer one is not kept);
- it keeps every placeholder of the built-in prompt (`{name}`: a letter after `{`; JSON braces and `{{` are not
  placeholders). One missing is logged once as a warning, by name, never with the text.

**Fetching.** A running PC fetches every feature's prompt 10 s after Helm starts, every 15 minutes, and when the AI
page opens (at most once a minute). A failure (no internet, a token refused) keeps the prompts fetched before and is
logged once until fetching works again. What was fetched syncs (collection `ai.prompts`, end-to-end encrypted), so
the phone's Ask Helm uses it too, on the API and on a PC's agent; the same text is written again at most every 6
hours to keep it fresh, so it costs sync nothing between edits. Only reads: Helm never writes to the hub.

**Setting up (PC).** There is no screen for it: open `%LOCALAPPDATA%\Helm\settings\ai-skillhub.json` (create it) and
type:

```json
{
  "version": 1,
  "url": "https://skill.huyhung1404.com/api/mcp",
  "token": "<this machine's Skill Hub token: the site → Machines>",
  "accessClientId": "<Cloudflare Access service token: Client ID>",
  "accessClientSecret": "<Cloudflare Access service token: Client Secret>"
}
```

At the next fetch (within 15 minutes, or open the AI page) Helm moves the three tokens into
`settings\ai\skillhub.bin`, encrypted for this Windows user (DPAPI), never synced, and empties them in the JSON file.
They are the same values Claude Code's `skillhub` server has in `~/.claude.json`; a machine of its own on the site
(say `helm-pc-office`) is better, so it can be revoked alone. Type a token again to replace it; delete `skillhub.bin`
to forget them. `url` empty turns fetching off; only `https://` (or `http://` to this PC itself, for a hub under test).

**How it asks.** One JSON-RPC `prompts/get` per feature to the hub's MCP endpoint, for the MCP prompt
`p-helm-<feature id>` (the hub's name for a saved prompt), with `Authorization: Bearer <token>` and the
`CF-Access-Client-Id` / `CF-Access-Client-Secret` headers. "Unknown prompt" (JSON-RPC error -32602: no such prompt, or
archived) means none; 401, a Cloudflare redirect or 403, any other status or an unreadable answer is a failure.
Redirects are not followed; the answer is capped at 1 MB.

**For a new feature.** Add its id to `AiFeatureIds` (it is fetched from then on: the refresher reads the constants) and
take its prompt through `IAiPrompts.Get(featureId, builtIn)`, passing the template before its placeholders are filled.
Code: `src/Helm.Ai.Core/Prompts/` (`AiPrompts`, `AiPromptRefresher`, `SkillHubPromptSource`, `SkillHubSettings`).

## Wallet's AI

Wallet uses AI only where the history and the bank parser fall short, through `AiOneShot`
(`Helm.Ai.Core/Assistant/AiOneShot.cs`: one question, no tools, agents on a Helm PC first, the API last). A feature
with no choice of its own takes Ask Helm's model; one that runs by itself goes to a PC agent only.

- **Filing a new payment** (`wallet-categorize`, runs by itself: PC agent only). `WalletClassifier` files it from the
  history first, without AI: earlier transactions of the same direction with the same words (or most of them) vote
  for their category, a little more in the same amount band; sure from two that agree. Only when it is not sure, AI
  gets the user's categories for that direction and up to 5 filed transactions like it (`WalletCategoryAi`), and
  answers one of them or a new name. Its answer goes first on the phone's "What was it?" notification; a new
  category shows as "+ Name" and is created only when tapped. AI never files anything by itself.
- **Read with AI** (`wallet-read-notification`, started by the user). Under Wallet → Troubleshooting → *Notifications
  Helm could not read*, each one has **Read with AI**: that one notification's text (never a one-time code) goes to
  AI, which answers the amount, direction, counterpart, description and balance as JSON. `WalletNotificationAi`
  checks it against the text (the amount and the balance must be written in it, a sign in it must agree) and saves
  the transaction the usual way; the row then says what was saved, with **Undo**.
- **Nói** on "What was it?" (before the two likeliest categories, `CategoryButtons.ForNotification`) opens Ask Helm about that transaction (`AskHelmContext`, `WalletAskContext`): "ăn trưa
  với Nam" fills in its category and description with `wallet_update_transaction` (Undo in the card), and the
  notification goes away.

## Ask Helm: directions, shared bills, bills, missions, ideas, Claude Code

More of Ask Helm's tools, each reached by saying it (no new screens). Changes run at once and are listed with Undo.

| Say | Tool | What happens |
|---|---|---|
| "Đường tới 36 Hoàng Cầu" | `phone_navigate` | Google Maps turn by turn (motorbike unless asked), else the directions web link |
| "Mở Grab" | `phone_open_app` | The launcher app whose name fits (`PhoneApps.Match`); several fit: nothing opens, the card asks which |
| "Ăn lẩu 1tr2 chia 4, Nam với Tú chưa trả" | `wallet_split_bill` | The user's share as spending, a debt per person who owes, a settled pair (owed + repaid) per person who paid; someone else paid: the user owes them. One call, one card row per record, each with its own Undo |
| "Nhắc Nam trả 200k" | `wallet_debt_reminder`, `phone_share_text` | A polite message from the debt book (amount, what for, since when, due), handed to Android's share sheet; Helm never sends it |
| "Thêm hoá đơn internet 250k ngày 15", "đóng tiền điện rồi 450k" | `bill_add`, `bills_list`, `bill_pay` | Recurring bills (`wallet.bills`, `BillBook`): amount or "varies", due day; paying saves the transaction and marks the month due nearest the payment paid. The Tracker's daily reminder lists bills coming due, like debts |
| "Đỗ Aptis B1 ngày 15/11" | `mission_create` | One mission with ordered steps, the exam its last step, the date its deadline; `mission_complete_step`, `mission_skip_step` and `mission_check_item` are Ask Helm's too, each with Undo |
| "Ý tưởng game: …" | `notes_create` `tags` | A note ending in `#idea #game`; the tool's description lists the hashtags in use (`NoteTags`), so the project tag is the user's own |
| "Ghi chú về lỗi server hôm trước" | `notes_search` `any` | Several alternative words the model expands (synonyms, Vietnamese and English); notes with more of them first, then by how well and how recently (`NoteRecall`, half-life 14 days). No embeddings service |
| "Gửi cho Claude Code ở Helm: …" | `workspace_sessions`, `workspace_send` | A `WorkspaceCommand` to the session (or a new one in the window) named by project, folder or title, on a PC online; several fit: nothing is sent, the card asks which. Free: Claude Code works on the user's plan. Undo withdraws it until the PC takes it |

**One call, several records.** A tool result with a `records` list (`McpTool.RecordsProperty`), each item with its own
`summary`, is listed as one row per item; Undo (here or on the PC that made it) gets that item alone.

The phone tools are in `PhoneToolSpecs` (one place for both the phone's `PhoneTools` and the PC, which hands their
calls back to the phone). `workspace_send` is also offered to other MCP clients on the PC, behind the usual consent.

## Ask Helm's card: photo to data, the brief chip

- **Photo to data.** The card's three round buttons are Aa · mic · camera. The camera offers *Chụp ảnh* (the camera
  app, writing into Helm's cache through the FileProvider, `ask-camera/`: no camera permission) or *Thư viện*
  (Android's photo picker: no storage permission). The photo is made upright and small on the phone
  (`AssistantPhotos`: at most 1568 px on its longer side, JPEG 85) and the camera's file is deleted once read. *Gửi
  ảnh* sends it alone; the mic or Aa adds a sentence first ("chia đôi với Nam"). `AssistantSession.SendPhotoAsync`
  sends it with the Wallet, Tracker and Notes tools: a receipt becomes a transaction (a split or a debt when the user
  names who shared it), a transfer screenshot a transaction or a debt, a whiteboard a note and tasks; saved at once
  with Undo, like words. `IAssistantChat.AddUser(text, image)`: Claude gets an image block before the text, Grok an
  `image_url` data URI.
- Photos always go through the **API** (within Ask Helm's daily cap), never to a PC agent: a job reaches a PC over
  sync as words. With the route set to *PC only*, the card says photos need the API.
- A model that cannot read pictures (`AssistantPhotos.Reads`: Grok's text-only lines) hands the photo to Claude
  Sonnet at once; one that turns the photo down (`AssistantImageException`) is retried once with Sonnet. The answer
  says so ("Haiku 5.5 không đọc được ảnh nên em nhờ Sonnet 5.5.").
- **The photo is not kept.** Its chat is dropped when the turn ends; the conversation goes on from the words and the
  summaries of what was saved. The card keeps the photo only while the turn is unfinished: it failed before saving
  anything (*Thử lại* sends it again) or em asked something back (the answer goes with it).
- **Brief chip.** When the morning brief or the evening review came today or yesterday, a chip tops the card
  ("Bản tin sáng · 06:45 ▾", `AiBriefChip`); a tap opens its title and lines and marks it read, and "‹ Hôm qua" shows
  yesterday's last one when the latest is today's. Nothing older.

## Text calls of the tools

Some features only need text back from a model: no Helm tool, no phone tool. They go through `IAiText`
(`Helm.Core/Ai/AiText.cs`, implemented by `Helm.Ai.Core/Assistant/AiTextService.cs`) on the same router: an agent on
a Helm PC first, as a text-only job (`AiJob.System` set, no tools; an agent on this device gets up to 60 s, one reached
through sync the usual claim wait), then the API with the feature's model, within its daily cap. Nothing a text call
does changes data, so a PC that took it and failed does not stop the API from answering. A feature with no choice of
its own on the AI page uses Ask Helm's (the model picked in the card). Every call is in `ai.usage` under its feature.
Only user-started calls use it: nothing here runs by itself.

| Feature id | Where | What |
|---|---|---|
| `text-actions` | Android text selection: **Helm**, **Helm: Dịch**, **Helm: Sửa**, **Helm: Viết lại** | Ask Helm's card on the selected text: translate (into Vietnamese; Vietnamese into English), fix, rewrite politely, summarize, explain a comment and draft a reply in its language, or what the user says. **Thay thế** puts the result back when the app allows it, **Chép**, **Chia sẻ** (`Helm.Ai.Core/Text/TextActions.cs`) |
| `vocab-quick` | Vocabulary on the phone: **Add word** in the selection menu, and Add words | One word is analysed at once, with the same guide and save shape as `vocab_save_analysis` and the parts Helm has that it may reuse, checked and saved the same way (`VocabQuickAnalyzer`); one retry with what Helm found to fix. Lists of words and texts still wait for the PC's agent |
| `sentence-explain` | Novel Reader, after holding a word | The sentence's meaning, grammar, a natural translation and the words worth learning, which go to Vocabulary with the sentence as their note (`SentenceExplainer`, `HeldWordViewModel.Explain`) |
| `task-proposals` | Tracker | The steps of a big task, the tasks in a note, as choices the user keeps or drops before anything is added; one Undo for the batch (`TaskProposals`, `TrackerProposalViewModel`) |

Each prompt puts what the user selected or wrote between `<<<` and `>>>` and tells the model it is data, never
instructions. Answers are one JSON object, read leniently (`AiJson`: a code fence or a word before it is fine).

In Ask Helm itself: `tracker_add_tasks` adds a batch of tasks to a list by name (made when missing) or as one task's
subtasks, with one Undo, for "biến ghi chú họp thành task trong list Công việc" (the model reads the note first) and
"chia task … thành bước nhỏ" (the model proposes the steps, asks which to keep, and adds those). A request that starts
with "ghi chú" or "note" carries only the notes tools, and `notes_create` asks for a dictated note to be cleaned up:
punctuation, paragraphs, a title.

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

## AI settings per feature

**AI → Features** on the PC is one table of every AI feature, grouped **On request** (Ask Helm, translate a
selection, word analysis, explain a sentence, task proposals, read a bank notification) and **Automatic** (auto-classify
transactions and the automatic jobs above). Each row has its switch, its route (**Agents → API**, **Agents only**,
**API only**), its model (the chat models the tested keys offer), its effort (Claude models only; greyed out for Grok),
its daily cap (what it may spend on the API a day, in USD) and a cost badge: "Runs on your PC agents · 0đ" on the
agents only, the cap as the worst case where the API may be used. A line on top counts today's runs, on the PC's
agents and on the API, with what the API ones cost.

On the phone the same settings are in **General › AI features**: the same groups, one row per feature (its state in
one line and its switch); tap a row for its detail (on, where it runs, model, effort, daily cap and what it may cost).

Every change is saved at once to the feature's choice in the synced `settings.ai-features`, so the other device shows
it within a moment. It is the same choice the Proxy tab's Ask Helm route and wait, Ask Helm's model picker and
`ai_auto_job_set` change. Automatic features start on **Agents only** (they never pay for the API unless you change
that); the others start on **Agents → API**. A feature with no choice of its own runs with Ask Helm's model; once a
setting is changed in the table, it keeps its own.

For developers: the list is `AiFeatureCatalog` (Helm.Ai.Core: name, description, automatic or not; an `AiFeatureIds`
constant without an entry still shows, named after its id), and `AiFeatureCatalog.Effective` is the choice a feature
runs with now. Both apps bind to `AiFeaturesViewModel` / `AiFeatureRow` (registered by `AddHelmAi`); the PC view is
`Views/Ai/AiFeaturesView.xaml`, the phone's `Helm.App.Android/Views/AiFeaturesView.axaml` (page key `ai-features`).
