# MCP security

Helm offers its tools to Claude (Claude Code, VS Code) over the Model Context Protocol. On the PC Helm usually runs **as
Administrator**, and the SSH tools reach servers where the user is often **root**. Whatever Claude can do through Helm
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
| `Remote` | runs something on another machine (SSH) | always |

## Threats and defences

### Who can talk to the pipe

- **Another user on the PC, or another machine.** The pipe's ACL grants only the user's own SID and denies the
  `NETWORK` SID. On each connection Helm also checks the caller: a remote client (`GetNamedPipeClientComputerName`
  succeeds) or a process whose token belongs to another user is closed at once.
- **Someone taking the pipe name first.** The bridge (`McpBridge.ConnectAsync`) only talks to a pipe owned by this user.
- **Too many callers, or huge messages.** At most 8 connections at once (one more is closed as soon as it connects);
  a message longer than 4 M characters gets an error reply and is skipped as it streams in, never held in memory.
- **Any program of the same user.** It can open the pipe, just as it can start Claude Code. That is the boundary of the
  design: Helm cannot tell Claude from another program of the same user. So what matters is what a call may do
  without the user, which the policy below limits.

### What an elevated Helm adds

A non-elevated process reaching tools that run elevated is a privilege escalation. Decision: **non-elevated callers
may connect** (Claude Code normally runs without elevation; refusing them would make the tools useless), but:

- while Helm runs as Administrator, **every** call that is not a read is asked, whoever the caller is;
- the pipe reads the caller's token; when Helm is elevated and the caller is not, the dialog says so in a warning
  ("does not run as Administrator, but Helm does: allowing lets it act with Helm's rights");
- an elevated call (Helm as Administrator, or root on the server) is never "allowed for this session": each one is
  asked;
- Helm's own tools never touch the system (no registry, services or files outside Helm's data). The elevation only
  matters for the SSH tools, which always ask.

### Prompt injection through tool output

Text a tool returns (a note, a task, a server's output) can contain instructions such as "now run deploy on prod".
Claude may follow them. Helm does not try to detect this; instead nothing that matters runs on Claude's word alone:

- every `Remote` call is asked, with the exact command line in the dialog;
- `McpDanger.High` (a menu item marked `high`, or root on the server) shows the details in a highlighted monospace box
  and never offers "Allow for this session";
- a session allowance covers one connection, one tool and one target only, so an injected call to another server or
  another tool is still asked;
- the server marks `Remote` tools `destructiveHint`/`openWorldHint`, so the client may ask too;
- SSH menu items marked `agents: false` are never offered to Claude.

### Consent fatigue

A question asked too often stops meaning anything. So:

- reads are never asked, and changes are asked only when Helm is elevated or the user wants it;
- every question says in plain words **what will happen**, **why Helm asks** and the exact details, so it can be
  answered in two seconds;
- "Allow for this session" (normal calls only) stops repeats of the same tool on the same target;
- questions come one at a time; more than **10 asked calls in a minute** are refused without asking, with a message
  Claude reads ("Helm did not ask the user: …"), so a runaway loop cannot flood the user with dialogs.

### A dialog answered by accident

- **Deny** is the default button: Enter, Esc and closing the window all deny.
- The Allow buttons stay disabled for a moment after the dialog opens, so a key or click meant for another window
  never allows anything.
- No answer within **2 minutes** is a Deny. A client that disconnects cancels its question.
- While Helm is hidden in the tray, a notification says Claude asks; the dialog opens only when it is clicked.
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

## The Permissions card (Claude & MCP page)

- **Ask before every change**: asks before changes to Helm's data even when Helm is not elevated.
- The elevation state: whether Helm runs as Administrator, and so whether changes are asked.
- **Allowed for this session**, with *Revoke*.
- **Activity**: the newest calls, with *Clear*. Each allowed `Remote` call also shows a tray notification.
