# Claude & MCP

Helm can give Claude its own tools through the [Model Context Protocol](https://modelcontextprotocol.io) (MCP):
Claude Code in a terminal or in the VS Code extension, VS Code's own agent mode, and Claude Desktop can then find,
read and (when you allow it) change your notes, to-do lists, debt book and missions. Everything is set up on the
**Claude & MCP** page, right after General. Windows only: the server runs inside Helm on the PC.

- [What Claude gets](#what-claude-gets)
- [Connect a client](#connect-a-client)
- [See who is connected](#see-who-is-connected)
- [Switch it off](#switch-it-off)
- [How it works](#how-it-works)

## What Claude gets

The **Tools Claude can reach** card lists every tool offered right now, in three groups:

| Group | What a call does |
|---|---|
| **Read** | Only reads Helm's own data (search notes, list tasks, show a mission). |
| **Change** | Changes Helm's data: add or edit notes and tasks, tick tasks, add debts, create, re-plan and advance missions. Changes sync to your other devices. Nothing is deleted for good: notes go to a 30-day trash. |
| **Remote** | Runs something on another machine (an item of a server's SSH menu). Only on servers you allowed, and Helm always asks you first. |

Only the tools of Helm tools that are turned on (Notes, Tracker, Wallet, Missions, SSH) are offered. The **Vault is never reachable**: no tool reads or writes
it, whatever the settings.

## Connect a client

Helm has to be running when Claude uses its tools. Each client starts `Helm.exe --mcp`, which passes Claude's messages
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
Clients usually connect when a session starts, so an empty list just means no Claude session is open with Helm.

## Switch it off

- **Let Claude use Helm's tools** off: no client gets any Helm tool, even where Helm is added, and the clients
  connected now are disconnected.
- **Claude may change things** off: Claude only gets the Read tools. The clients connected now are disconnected too,
  because they were given the Change tools when they connected; in Claude Code, `/mcp` reconnects with the Read tools.
- To remove Helm from a client for good, press **Remove** (Claude Code) or delete the `helm` entry from its config
  file.

## How it works

Helm listens on a named pipe that only your Windows account can open, and never from another machine. A separate data
folder (a test copy started with `--data-dir`) has its own pipe, so its clients never reach your usual Helm; the
page's commands then include `--data-dir` too. Each connection gets its own server, built with the settings of that
moment; that is why turning a switch off ends the connections made before.
