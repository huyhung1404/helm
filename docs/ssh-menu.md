# SSH menu (protocol 1)

The **Menu** button of Helm's SSH tool opens a popup over the terminal with what a server offers to run: deploy,
backups, restarts, status. The server itself defines it, with one program next to the code it runs, so the menu always
matches that server and works the same from every device. Helm only reads the menu, shows it, checks what goes in and
asks before dangerous items.

## Where Helm looks

`~/.helm/menu` on the server, unless the server's *Menu* path in SSH settings says otherwise. Any executable works
(a shell script, Node, Python…). Helm runs it over SSH, on its own channel next to the terminal, as
`<path> '<arg>' '<arg>'…`: every argument single-quoted, so no value can reach the shell as a command. The path itself
may only hold letters, digits, `. _ - /` and a leading `~/`.

| Helm runs | The menu answers (standard output) |
|---|---|
| `menu describe` | the menu, as JSON (below) |
| `menu choices <item> <param>` | a JSON list of strings: the values the parameter may take now |
| `menu run <item> [--<param>=<value>...]` | runs the item (see *How Helm runs an item*) |

The exit code tells Helm whether it worked: 0 is success; anything else is a failure, shown with the standard error.
`127` from `describe` means "no menu here", `126` "not executable".

## How Helm runs an item

`describe` and `choices` go over Helm's own channel and never show in the terminal. Choosing an item (and filling its
form, if it has parameters) **types its `run` line into the terminal**, after a leading space so bash leaves it out of
its history: the output shows there, the user reads it next to everything else, and Ctrl+C stops it as usual. In the
terminal the menu's standard output is a TTY, so a menu should print for people there (WebAdmin's prints its tables as
aligned text) and keep JSON for when it is not (another client, or a script).

## describe

```json
{
  "protocol": 1,
  "title": "WebAdmin",
  "groups": [
    { "title": "Deploy", "items": [
      { "id": "deploy", "title": "Deploy", "description": "Runs deploy.sh.", "kind": "action", "output": "stream",
        "danger": "high", "confirm": "Deploy main to production?" }
    ]},
    { "title": "Processes", "items": [
      { "id": "pm2", "title": "Processes", "kind": "view", "output": "table", "refresh": 10, "rowActions": ["restart"] },
      { "id": "restart", "title": "Restart app", "kind": "action", "output": "text", "danger": "confirm",
        "params": [ { "id": "app", "title": "App", "type": "choice", "choices": "dynamic" } ] }
    ]}
  ]
}
```

**Items**

| Field | Values | Meaning |
|---|---|---|
| `id` | `[a-z0-9][a-z0-9_-]*` (64 at most) | unique in the menu |
| `title`, `description` | text | shown on the page |
| `kind` | `action` (default), `view` | an action waits for Run; a view runs when opened |
| `output` | `stream`, `text` (default), `table`, `stats` | how the output is shown (below) |
| `danger` | `none` (default), `confirm`, `high` | `confirm` asks first; `high` also shows the exact command line |
| `confirm` | text | the question asked before it runs |
| `refresh` | seconds (2 to 3600) | a view is read again this often while it is shown |
| `rowActions` | item ids | buttons on each row of a table; see *Row actions* |
| `params` | list | the form; see *Parameters* |

**Parameters**

| Field | Values | Meaning |
|---|---|---|
| `id`, `title` | as for items | the value arrives as `--<id>=<value>` |
| `type` | `choice`, `text` (default), `number`, `bool` | |
| `choices` | a list, or `"dynamic"` | `dynamic` asks `menu choices <item> <param>` when the form opens |
| `pattern` | a regular expression | a `text` value must match it entirely |
| `maxLength` | number (default 200) | for `text` |
| `min`, `max` | numbers | for `number` |
| `default` | text | the form's starting value |
| `required` | `true` (default), `false` | |

Helm refuses a value that does not fit before anything runs. The menu must check again: a value can come from
anywhere, and only the server knows what is valid now (Restart app checks that the app is one of pm2's processes).

## Outputs

`output` says what an item prints when its output is not a terminal (Helm's own channel, or another client):

- **stream** and **text**: plain text (stream for long work, such as a deploy).
- **table**: `{ "columns": [ { "id": "name", "title": "Name" } ], "rows": [ { "key": "web", "cells": { "name": "web" } } ] }`
- **stats**: `{ "stats": [ { "label": "Disk", "value": "78%", "level": "warn" } ] }`, where `level` is `ok`, `warn` or `bad`.

`kind`, `refresh` and `rowActions` describe how a client that shows tables may refresh them and put buttons on rows
(a row's button runs the named item; each parameter takes the row's cell of the same id, and the row's `key` fills the
others). Helm's popup runs every item in the terminal and does not use them yet.

## Jobs

Long work (a deploy) should not depend on the SSH connection: if the phone loses its signal, the command would die
halfway. WebAdmin's menu runs such items as jobs: started in their own session with their output in a log, so they
finish whatever happens to the connection; running the item again while the job runs follows its log instead of
starting a second one, and Ctrl+C only stops following. See `ops/helm/menu.js` in WebAdmin for one way to do it.

## Limits

Helm accepts at most 32 groups, 200 items, 16 parameters per item, 500 choices, 20 columns, 2000 rows and 50 stats,
and texts up to 500 characters; anything larger is refused or cut, so a broken menu cannot flood Helm.
