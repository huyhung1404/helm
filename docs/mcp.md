# AI & MCP

Helm can give AI agents its own tools through the [Model Context Protocol](https://modelcontextprotocol.io) (MCP):
Claude Code in a terminal or in the VS Code extension, VS Code's own agent mode, Claude Desktop, and any other MCP
client (Cursor, Windsurf, Gemini CLI…) can then find,
read and (when you allow it) change your notes, to-do lists, debt book and missions, and find the character names of
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
| **Change** | Changes Helm's data: add or edit notes and tasks, tick tasks, add debts, create, re-plan and advance missions. Changes sync to your other devices. Nothing is deleted for good: notes go to a 30-day trash. |
| **Remote** | Runs something on another machine (an item of a server's SSH menu). Only on servers you allowed, and Helm always asks you first. |

Only the tools of Helm tools that are turned on (Notes, Tracker, Wallet, Missions, SSH, Novel Reader) are offered. The **Vault is never reachable**: no tool reads or writes
it, whatever the settings.

Novel Reader's tools (`novel_list`, `novel_name_candidates`, `novel_names`, `novel_add_names`, `novel_ignore_names`)
let an agent find a novel's character names: it reads the possible names with sentences where they appear and adds
the real ones, marked as found; it never replaces a name the user saved. Novel Reader's AI name scan runs Claude Code
without a window with only these tools allowed (`claude -p --allowedTools mcp__helm__novel_…`), so it needs Claude
Code installed and Helm added to it.

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
