# MCP security

Helm offers its tools to Claude (Claude Code, VS Code) over the Model Context Protocol. On the PC Helm usually runs **as
Administrator**, and the SSH tools reach servers where the user is often **root**. Whatever an AI agent can do through Helm
then runs with those rights, so this page lists what can go wrong and what Helm does about it.

Code: `src/Helm.Core.Windows/Mcp/McpPipeHost.cs` (the pipe), `src/Helm.Core/Mcp/McpServer.cs` (the protocol),
`src/Helm.Core/Mcp/McpConsent.cs` (the policy and the activity log), `src/Helm.App/Mcp/` (the dialog).

## How a call reaches a tool

Claude Code starts `Helm.exe --mcp`, which relays its standard input and output to a named pipe of the running Helm
(`Helm.Mcp.<hash of the data folder>`). Each connection gets its own `McpServer`. Before a tool runs, the server asks
the consent policy unless the tool only reads.

| Risk | Meaning | Asked? |
|---|---|---|
| `Read` | only reads Helm's own data | never |
| `Change` | changes Helm's data (synced to the other devices) | when Helm runs as Administrator, when the call is marked elevated, or when **Ask before every change** is on |
| `Remote` | runs something on another machine (SSH) | always, except status commands that only read (see *Shell commands*) |

## Threats and defences

### Who can talk to the pipe

- **Another user on the PC, or another machine.** The pipe's ACL grants only the user's own SID and denies the
  `NETWORK` SID. On each connection Helm also checks the caller: a remote client (`GetNamedPipeClientComputerName`
  succeeds) or a process whose token belongs to another user is closed at once.
- **Someone taking the pipe name first.** The bridge (`McpBridge.ConnectAsync`) only talks to a pipe owned by this user.
- **Too many callers, or huge messages.** At most 8 connections at once (one more is closed as soon as it connects);
  a message longer than 4 M characters gets an error reply and is skipped as it streams in, never held in memory.
- **Any program of the same user.** It can open the pipe, just as it can start Claude Code. That is the boundary of the
  design: Helm cannot tell an AI agent from another program of the same user. So what matters is what a call may do
  without the user, which the policy below limits.

### What an elevated Helm adds

A non-elevated process reaching tools that run elevated is a privilege escalation. Decision: **non-elevated callers
may connect** (Claude Code normally runs without elevation; refusing them would make the tools useless), but:

- while Helm runs as Administrator, **every** call that is not a read is asked, whoever the caller is;
- the pipe reads the caller's token; when Helm is elevated and the caller is not, the dialog says so in a warning
  ("does not run as Administrator, but Helm does: allowing lets it act with Helm's rights");
- Helm's own tools never touch the system (no registry, services or files outside Helm's data), so the elevation adds
  nothing to a `Change`: it may be "allowed for this session" (same connection, same tool), or an agent editing five
  notes would mean five dialogs;
- a command run as root (or as Administrator) on another machine is never "allowed for this session": each one is
  asked.

### Prompt injection through tool output

Text a tool returns (a note, a task, a server's output) can contain instructions such as "now run deploy on prod".
The agent may follow them. Helm does not try to detect this; instead nothing that matters runs on the agent's word alone:

- every `Remote` call is asked, with the exact command line in the dialog;
- `McpDanger.High` (a menu item marked `high`, or root on the server) shows the details in a highlighted monospace box
  and never offers "Allow for this session";
- a session allowance covers one connection, one tool and one target only, so an injected call to another server or
  another tool is still asked;
- the server marks `Remote` tools `destructiveHint`/`openWorldHint`, so the client may ask too;
- SSH menu items marked `agents: false` are never offered to AI agents.

### Shell commands (ssh_exec)

A shell is far more than a menu: one injected command can delete or leak anything the server's user can reach. So:

- it is a second switch per server, **AI agents may run shell commands**, off by default and shown only once the
  menu switch is on; turning the menu off turns it off too. An agent never connects: the server must be connected in
  Helm by the user;
- every command is asked with its exact text, highlighted. "Allow for this session" is offered as **Allow this exact
  command for this session** (`McpConsentRequest.Scope`): the allowance covers that command line on that server only,
  never the shell as a whole;
- as root, every command is High: asked each time, never allowed for the session, never run without asking;
- it runs on Helm's own channel with no TTY and nothing on standard input (a password prompt fails or times out), is
  stopped after its timeout (2 min by default, 15 min at most) and its output is capped at 64 KB;
- **Status commands without asking** (on by default with the shell switch, can be turned off per server): a command
  that `SshShellCommands.IsStatusCommand` accepts runs without a question (`McpConsentRequest.AllowWithoutAsking`). The
  check is an allowlist, not a filter: the line may hold only letters, digits, spaces and `-_./:=,@+%` (no quotes,
  `$`, backticks, `;`, `|`, `&`, redirections, globs, `~` or line breaks), so the shell can only run one program with
  plain words; the program must be one of `uptime whoami id uname nproc free df ps ls w lsb_release hostname date`,
  `pm2 ls|list|status`, `systemctl status|is-*|list-units|list-timers|--failed`, `docker ps|images|version|stats
  --no-stream` or `git status|log|describe|rev-parse|show-branch|branch` (listing flags only), and options that set
  or write (`hostname x`, `date -s`, `git log --output`, `git branch -D`, `systemctl -H`…) are refused. Files are never
  read this way (no `cat`, `tail`, `env`, `pm2 jlist`), so a secret cannot leave the server without a question. These
  calls are still in the activity log, without a notification each.

### Consent fatigue

A question asked too often stops meaning anything. So:

- reads are never asked, and changes are asked only when Helm is elevated or the user wants it;
- every question says in plain words **what will happen**, **why Helm asks** and the exact details, so it can be
  answered in two seconds;
- "Allow for this session" stops repeats of the same tool on the same target (any change of Helm's data; a command
  on a server unless it is High danger or runs as root);
- questions come one at a time; more than **10 asked calls in a minute** are refused without asking, with a message
  the agent reads ("Helm did not ask the user: …"), so a runaway loop cannot flood the user with dialogs.

### A dialog answered by accident

- **Deny** is the default button: Enter, Esc and closing the window all deny.
- The Allow buttons stay disabled for a moment after the dialog opens, so a key or click meant for another window
  never allows anything.
- No answer within **2 minutes** is a Deny. A client that disconnects cancels its question.
- While Helm is hidden in the tray, a notification says the agent asks; the dialog opens only when it is clicked.
- Nothing is ever allowed without a click on an Allow button, and nothing is remembered across restarts.

### Secrets in arguments or logs

- The dialog and the activity log show arguments as `Details`. In the default question, a property whose schema says
  `"writeOnly": true`, or whose name contains password, passphrase, secret, token, API key, private key or OTP, is shown
  as `•••`. A tool that builds its own question (`AskFirst`) must leave secrets out of `Details`.
- The activity log (`<data folder>\mcp\activity.json`) is device-local, never synced, keeps the last 500 calls that
  were not reads (time, client, tool, target, answer, result) and cuts details to 1000 characters. The Permissions card
  clears it.
- Helm's own log records tool names and answers, never arguments.
- The Vault is never offered over MCP.

## The Permissions card (AI & MCP page)

- **Ask before every change**: asks before changes to Helm's data even when Helm is not elevated.
- The elevation state: whether Helm runs as Administrator, and so whether changes are asked.
- **Allowed for this session**, with *Revoke*.
- **Activity**: the newest calls, with *Clear*. Each allowed `Remote` call also shows a tray notification.
