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

A management key can do more than read (an xAI one can buy credit with the saved card), so it never syncs: it stays
in a file on that PC, encrypted for the Windows user (DPAPI; the Android Keystore on a phone).

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
  management keys on the device.
- `AiKeysViewModel`: the page's view model; the WPF view is `Helm.App/Views/Ai/AiKeysView.xaml`.

A new provider is an `AiProviders` entry plus an `IAiKeyChecker` (and an `IAiBalanceReader` if its API tells the
balance), registered in `AiCoreServices`.
