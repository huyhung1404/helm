# AI & MCP

Helm can give AI agents its own tools through the [Model Context Protocol](https://modelcontextprotocol.io) (MCP):
Claude Code in a terminal or in the VS Code extension, VS Code's own agent mode, Claude Desktop, and any other MCP
client (Cursor, Windsurf, Gemini CLI…) can then find,
read and (when you allow it) change your notes, to-do lists, debt book, missions and habits, and find the character names of
your novels. Everything is set up on the
**AI & MCP** page, right after General. Windows only: the server runs inside Helm on the PC.

- [What AI agents get](#what-ai-agents-get)
- [Connect a client](#connect-a-client)
- [See who is connected](#see-who-is-connected)
- [Switch it off](#switch-it-off)
- [How it works](#how-it-works)

## What AI agents get

The **Tools AI agents can reach** card lists every tool offered right now, in three groups:

| Group | What a call does |
|---|---|
| **Read** | Only reads Helm's own data (search notes, list tasks, show a mission). |
| **Change** | Changes Helm's data: add or edit notes and tasks, tick tasks, add debts, create, re-plan and advance missions, tick a habit. Changes sync to your other devices. Nothing is deleted for good: notes go to a 30-day trash. |
| **Remote** | Runs something on another machine (an item of a server's SSH menu). Only on servers you allowed, and Helm always asks you first. |

Only the tools of Helm tools that are turned on (Notes, Tracker, Wallet, Missions, Habits, SSH, Novel Reader, Vocabulary) are offered. The **Vault is never reachable**: no tool reads or writes
it, whatever the settings.

Vocabulary's tools let an agent break down the words the user adds: `vocab_pending` lists the words waiting (with the
user's note, and what they found wrong in an analysis they sent back), `vocab_find_parts` finds pieces Helm already
has so a root keeps one meaning across words, and `vocab_save_analysis` / `vocab_save_analyses` (many words in one
call) save a word's phonetic, Vietnamese meaning, Hán Việt and tree of parts, and optionally up to 3 example sentences
(with translation), up to 6 collocations, a mnemonic, up to 3 confusable words (with what tells them apart), its
level (`A1`–`C2` or `HSK1`–`HSK9`), part of speech, topic, up to 8 word forms (each with its part of speech and
meaning), up to 6 synonyms and up to 4 antonyms. Helm checks each analysis before saving
anything (a Chinese word splits into its characters in order, characters need pinyin and Hán Việt, no strokes, no
loops) and answers what to fix. `vocab_edit` rewords words and parts without touching their trees; `vocab_words`,
`vocab_get` and `vocab_add_words` read and add words (`level` there is the CEFR/HSK level; `quiz_level` 0–8 and
`due` are the user's spaced-repetition progress). `vocab_follow_ups` lists the words that need more than their
analysis: `often_forgotten` (forgotten 4 times or more since the last help: they need a new mnemonic) and
`missing_details` (analyzed before Helm asked for word forms, synonyms and antonyms); `vocab_save_details` saves those
details without touching the trees, marks a hard word as helped, and takes a word off `missing_details` even when it
has no forms (`[]`). Nothing is deleted through them. On the PC, Vocabulary's **Analyze now** (and its automatic run
a little after there is something to do) runs Claude Code without a window with only these tools allowed
(`claude -p --allowedTools mcp__helm__vocab_…`), 12 words of each kind per run; when a run leaves the same work behind
it does not start by itself again until something changes. "Suggest words with this root" on the Roots tab does the
same to add a family's words.

Habits' tools let an agent see how the user's habits are going and why they slip: `habits_list` gives each habit with
whether it is done today, its current and longest streak, the share of days done in the last 30 days and this year,
and how many days in a row it has been skipped; `habit_report` gives one habit's year (default: this year): the days
done, each month's share, and every run of missed days with the reason the user wrote when they came back (Habits
asks for one after two or more days in a row). `habit_check` ticks a day (today by default, never before the habit
started or in the future). Nothing is unticked or deleted through them. Try "why do I keep skipping my reading
habit?".

Novel Reader's tools (`novel_list`, `novel_name_candidates`, `novel_names`, `novel_add_names`, `novel_ignore_names`)
let an agent find a novel's character names: it reads the possible names with sentences where they appear and adds
the real ones, marked as found; it never replaces a name the user saved. Novel Reader's AI name scan runs Claude Code
without a window with only these tools allowed (`claude -p --allowedTools mcp__helm__novel_…`), so it needs Claude
Code installed and Helm added to it.

`novel_translation_reports` (read only) gives the sentences the user reported as translated wrong in Novel Reader: the
Chinese, what Helm showed, the right translation and the user's note, the dictionary pieces Helm cut the sentence
into, and what Helm shows for it now (`helm_now`, `right_now`), so an agent improving the converter can work from them
and check its changes. `novel_suggest_translations` writes an agent's suggested translation of reports, with a note
and the kind of error; the user's own translation is never replaced. Novel Reader's **Ask Claude Code** runs Claude
Code without a window with only `novel_translation_reports`, `novel_names` and `novel_suggest_translations` allowed.

## Connect a client

Helm has to be running when an agent uses its tools. Each client starts `Helm.exe --mcp`, which passes the agent's messages
to the running Helm. A client that is not connected yet shows **Not added**; after you add Helm it shows **Added**.

### Claude Code (terminal and VS Code extension)

Both read your user settings, so one command adds Helm in every folder:

```
claude mcp add --scope user helm -- "<Helm folder>\Helm.exe" --mcp
```

Copy it from the page, or press **Add for me**: Helm runs that command for you, with your normal rights, and shows
what it printed. **Remove** runs `claude mcp remove helm --scope user`. Start a new Claude session afterwards. If
Claude Code is not installed, the page says so; install it, check that `claude --version` works in a new terminal,
and try again.

### VS Code (built-in MCP, Copilot agent mode)

Run **MCP: Open User Configuration** in VS Code and add the snippet from the page:

```json
{
  "servers": {
    "helm": { "type": "stdio", "command": "<Helm folder>\\Helm.exe", "args": ["--mcp"] }
  }
}
```

If the file already lists other servers, copy only the `helm` entry into its `servers`.

### Claude Desktop

Open **Settings → Developer → Edit Config** in Claude Desktop (or **Open config folder** on Helm's page), add the
`helm` entry from the page to `mcpServers`, save, and restart Claude Desktop.

### Other MCP clients

Any agent that can start a local (stdio) MCP server can use Helm. Add a server named `helm` that starts the command
shown under **Other MCP clients** on the page (`"<Helm folder>\Helm.exe" --mcp`). Most clients (Cursor, Windsurf,
Gemini CLI…) take the `mcpServers` JSON shown there; the client's own MCP guide says which file it goes in. Helm
cannot tell whether those clients have it, so this card has no status; the **Connections** card shows them once they
connect.

### When Helm moved

If a client's `helm` entry starts another Helm.exe (Helm was installed in another folder, or it is another copy), the
page says **Added, but it starts another Helm.exe**. Press **Add for me** again for Claude Code, or replace the entry
with the page's snippet for the others.

To find out whether a client has Helm, the page reads only the `helm` entry of that client's config file. It never
shows, logs or changes anything else in those files (they can hold other servers' tokens), and it writes only through
`claude mcp add/remove` when you press the button.

## See who is connected

The **Connections** card lists the clients connected now: their name and version, since when, and how many tools they
called. **Disconnect** ends that connection at once; the client connects again the next time it starts a session.
Clients usually connect when a session starts, so an empty list just means no agent session is open with Helm.

## Switch it off

- **Let AI agents use Helm's tools** off: no client gets any Helm tool, even where Helm is added, and the clients
  connected now are disconnected.
- **AI agents may change things** off: agents only get the Read tools. The clients connected now are disconnected too,
  because they were given the Change tools when they connected; in Claude Code, `/mcp` reconnects with the Read tools.
- To remove Helm from a client for good, press **Remove** (Claude Code) or delete the `helm` entry from its config
  file.

## How it works

Helm listens on a named pipe that only your Windows account can open, and never from another machine. A separate data
folder (a test copy started with `--data-dir`) has its own pipe, so its clients never reach your usual Helm; the
page's commands then include `--data-dir` too. Each connection gets its own server, built with the settings of that
moment; that is why turning a switch off ends the connections made before.
