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

A Grok pick always goes to the API (no agent runs Grok's models). A conversation that started on a PC stays there;
if the PC stops answering, the API takes over with what was said so far.

**The agents** (AI → Proxy, `Helm.App/Ai/AgentRegistry.cs`) are the user's list, kept per PC in
`settings/ai-agents.json`: Codex (`CodexAgent.cs`) or Claude Code (`ClaudeCodeAgent.cs`), any number, tried in the
list's order; a conversation sticks to the agent that took it. Each has its own sign-in folder under
`ai/agents/<id>` (`CODEX_HOME`, `CLAUDE_CONFIG_DIR`), never the user's own login. **Add** opens the sign-in terminal
at once (through Explorer, so it runs unelevated); a file watcher on the credentials (`auth.json`,
`.credentials.json`) turns the agent ready without a click, and the PC's heartbeat goes out at once. The worker
starts with Helm, so after a restart the agents take jobs again as soon as Helm runs.

**How a phone reaches the PC.** Each PC running Helm with a ready agent writes a heartbeat to the synced collection
`ai.workers` every minute. The phone writes the turn to `ai.jobs` (urgent, so it syncs at once) and keeps sync's
live channel open while it waits. The PC claims it, runs it, and writes the result back. The phone waits only
`PcWaitSeconds` for the claim; once claimed it waits for the result (up to 2 minutes) and never sends the same turn
to the API, so nothing is saved twice. Undo goes back to the PC that made the record. Jobs and heartbeats are
end-to-end encrypted like all Helm data.

**On the PC** (`LocalAgentRoute`), the agent runs with its own tools off (Claude Code: no tools, no MCP, no hooks;
Codex: a read-only sandbox in an empty folder): Helm lists its tools in the first message, the agent answers with the calls it wants as JSON
(`CliAgentProtocol`), Helm runs them in-process and sends the results back. Claude Code keeps one process per conversation
(about 3 s for the first turn, 1–2 s after); Codex runs one per turn and resumes its thread. Calls that would need a yes are not offered there, and phone tools are
handed back to the phone. An agent that reports its plan's limit takes no job for 30 minutes; the next one in the list does.

**Costs.** Every call goes in the synced collection `ai.usage`: route (`api` or `claude-code`), model, tokens, the
estimated USD (`AiPrices`), and on Claude Code what the same call would have cost (`SavedUsd`). The daily cap
(`DailyCapUsd`, 0.50 USD) only stops the API.

Only Helm's own features and your own services use the router. Other apps (Cursor, n8n, Open WebUI…) use their own
API keys: lending a Claude plan to other apps is against Anthropic's terms.
