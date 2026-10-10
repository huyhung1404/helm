# Changelog

All notable changes to Helm. The top section is used as the GitHub release body and as the Velopack release notes.
Format: [Keep a Changelog](https://keepachangelog.com), versions follow [SemVer](https://semver.org).

## [0.37.0] - 2026-10-10

### Changed
- **Novel Reader: the AI name scan uses Claude Code on your PC instead of an API key.** Choose *AI: Claude Code on this
  PC* under Finding names; *Scan for names* then runs Claude Code without a window, allowed only Novel Reader's tools.
  It reads the possible names through Helm's MCP server and adds the real ones while you watch, with your own Claude
  plan. It needs Claude Code installed and Helm added to it on the AI & MCP page. On the sample novel it took about two
  minutes and added 79 names. *Stop* ends it and keeps what it added. The names sync to the phone, which uses the logic
  scan itself. The Anthropic API key and model settings are gone.
- **AI & MCP: Novel Reader's tools.** Any AI agent can now find a novel's names: `novel_list`, `novel_name_candidates`
  (each with its count, Hán Việt reading, the logic scan's opinion and sentences where it appears), `novel_names`,
  `novel_add_names` and `novel_ignore_names`. Ask Claude Code, for example, "find the character names of my novel … in
  Helm".
- **Novel Reader: no pause between sentences.** Voices leave silence at both ends of every sentence, which added up
  to almost half a second between two sentences. It is now cut (also from audio downloaded before), so the next
  sentence follows at once. Measured from what was heard: from 0.41–0.51 s down to 0.08–0.11 s on the PC (VieNeu),
  0.10–0.14 s on Android (Google's voice). The pause between paragraphs now starts at 0.15 s and can be 0.
- **Novel Reader: downloading ahead goes on into the next chapters without stopping**, until it is *Chapters
  downloaded ahead* (2 by default, 1 to 10 in settings) past the chapter being read. It moves on as reading does, and
  goes no further, so nothing is downloaded that may never be heard. The voices on this PC (VieNeu) now make four
  sentences at once.

### Fixed
- **SSH:** a wrong passphrase for a key always says so. About one wrong passphrase in 256 used to show a technical
  error instead.

## [0.36.0] - 2026-10-10

### Added
- **Novel Reader: names are found by themselves.** The first time a novel is opened, its character names are looked
  for and the sure ones are added to its names, so "林宛" reads "Lâm Uyển" instead of "Rừng uyển" without saving each
  name by hand. Two ways to find them (settings → Finding names):
  - **Logic** (free, offline): from how the words are used in the novel. A name stands next to punctuation, after
    words like 对/向 and before words like 道/说. Grammar words, pieces of idioms, and words stuck to a name are left
    out, and so is anything used only a few times.
  - **AI**: Claude reads each possible name in a couple of its sentences, with your own Anthropic API key. Choose
    Haiku 4.5 (a few cents a novel), Sonnet 5.5 or Opus 5.5. The cost is shown before it runs. The key stays
    encrypted on the device, and only the possible names and their sentences are sent, not the novel.

  Found names are marked "found automatically" in the names panel, with *Undo found names*, and sync like the others.
  A name you saved is never replaced. A found name you delete, or a suggestion you dismiss, is not added again.
  Doubtful ones (houses like 林府, names without a common surname) are listed under Suggestions. *Scan for names*
  looks again.

## [0.35.0] - 2026-10-10

### Added
- **Novel Reader** (PC and Android): read Chinese web novels in Vietnamese. Add a .txt file (UTF-8, UTF-16 or GB18030/GBK; chapters
  are found by their headings, "第12章", "番外…", or the way downloaders lay them out) and it is converted the
  QuickTranslator way with the VietPhrase, Names, Hán Việt, LuatNhan and Pronouns dictionaries, a chapter at a time.
  Click a word to see its Chinese, Hán Việt and known meanings, widen or narrow the selection, and save a name or a
  meaning for this novel or for all of them; the chapter converts again at once. *Find names* suggests likely
  character names with their Hán Việt reading. Read aloud like an audiobook with Microsoft's natural Vietnamese voice
  HoaiMy (online; a Vietnamese Windows voice takes over offline): a player bar to pause mid-sentence, skip by
  sentence or paragraph and seek through the chapter, a sleep timer that fades out, the media keys and headset
  buttons, speed ([ and ]), pitch, volume and the pause between paragraphs. The paragraph and sentence being read are
  highlighted and followed as it reads (scroll away and *Back to reading position* returns); double-click a sentence
  or use a paragraph's ▶ to read from there, and it goes on with the next chapter. While it reads, the rest of the
  chapter (and the start of the next) downloads ahead and is kept on the PC, so listening again plays at once and
  offline; *Download* fetches this chapter, the next ten or the whole novel for listening offline (✓ in the chapter
  list), and the settings show and clear the downloaded audio. Each novel is a private folder that syncs end-to-end encrypted with Helm Sync: its text,
  the names and meanings you saved, and where you stopped reading; opening Novel Reader continues the novel read last,
  where it stopped, and offers to jump ahead when another device read further. The dictionaries download once
  (*Download dictionaries*, sources editable) or import from files, and sync too, so a new device or a reinstall
  gets them by itself. Give a novel a cover picture (or keep the drawn one) on its card in the library; covers sync too.
  Voices on the PC: HoaiMy through a hidden Microsoft Edge (starts in about half a second), HoaiMy online, and the
  female voices of VieNeu-TTS running on the PC itself (a local server Helm starts and stops; set its folder in the
  settings).
- **Novel Reader on Android**: the same library, reading, names and sync on the phone, and reading aloud that keeps
  going with Helm in the background or the screen off, chapter after chapter (the whole next chapter downloads ahead).
  Pause, skip and stop from its notification, the lock screen, headphones or a watch; a call or another app playing
  pauses it and it goes on afterwards, and unplugging headphones pauses it. It reads with the phone's own Vietnamese
  voices (offline, e.g. Google's; *Get Vietnamese voices* installs them) or HoaiMy online. Tap a word to fix it,
  double-tap to read aloud from there; chapters, reading options and names open as sheets.

## [0.34.0] - 2026-10-07

### Added
- **Scratch** (PC and Android): a scratch space on every device for the photos, videos, files and text you move around.
  On the phone: Share → *Helm Scratch* from Gallery or any app (one or many at once), or *Files*, *Text*, *Paste*.
  On the PC there are no buttons: Ctrl+V adds the files, screenshot or text you copied, or drop files on the page.
  Everything shows as a wall of cards with its content (a text in full, a photo at its shape). On PC click a card
  (Shift+click or Ctrl+click for more, Ctrl+A for all), then Ctrl+C copies (several texts as one, files all at once),
  Delete moves to the trash, Shift+Delete deletes for good (asked first), Enter opens; right-click for Save as. On the phone each card has Copy and Delete, and a tap shows Open, Share and Save to phone. Drag a card onto
  another to move it (hold, then drag, on the phone); the order syncs. Files are encrypted on the device before they
  upload, like Vault attachments. Deleting moves to the trash (the bin filter); deleting from the trash removes it
  from every device. *Free up space* drops this device's copies of uploaded files (they download again when opened).
- **Scratch: Send to clipboard**: (PC: Ctrl+Shift+C) puts a thing on this device's clipboard and on every other device that is syncing at
  that moment (text as text, a file as a file, a photo also as a picture on PC). Only a fresh signal (under 2 minutes)
  is followed, so a device that comes online later never overwrites its clipboard; files over 100 MB are not copied
  by themselves. Each device can turn receiving off in Scratch's settings.

## [0.33.0] - 2026-10-07

### Added
- **SSH: AI agent tab** (PC). Once an AI agent runs something on a server (`ssh_exec` or `ssh_menu_run`), the SSH page
  shows an *AI agent* tab beside *Terminal*: each command, its output as it arrives and how it ended (exit code, time
  limit, error output). Switch tabs at any time: your shell keeps running and stays yours while the agent works. The
  tab is read-only, marks new activity with a dot instead of taking over, and has *Stop* (the agent is told you
  stopped it) and *Clear*.

## [0.32.0] - 2026-10-07

### Added
- **SSH: shell commands for AI agents** (PC). A second switch per server, *AI agents may run shell commands*, lets an
  AI agent run commands with the new `ssh_exec` tool on a server you connected in Helm. Every command is shown to you
  exactly and asked first; *Allow this exact command for this session* covers only that one command line. As root,
  every command is asked each time. Simple status commands (`uptime`, `df -h`, `ps`, `ls`, `pm2 ls`,
  `systemctl status`, `docker ps`, `git status`…) can run without a question (*Status commands without asking*, per
  server); they still show in the activity log. See docs/mcp-security.md.

## [0.31.0] - 2026-10-07

### Changed
- **AI & MCP** (was *Claude & MCP*): Helm's tools are for any AI agent, not only Claude. The page, its switches, the
  approval dialog, notifications, the SSH "AI agents may use this server's menu" switch and the Missions hints now say
  *AI agents*, and the approval notification names the client that asks.
- The **sync button** (Windows title bar and Android app bar) keeps one sync icon in every state: it turns while
  syncing, shows a tick for a moment when a sync finishes, fades when offline or not set up, and turns red with a dot
  on a problem. Windows and Android now look the same.

### Added
- **Other MCP clients** card on the AI & MCP page: the command that starts Helm's MCP server and the usual
  `mcpServers` entry, to connect Cursor, Windsurf, Gemini CLI or any other MCP client.

## [0.30.0] - 2026-10-07

### Added
- **Claude & MCP** page (PC, after General): turn Helm's tools for Claude on or off, allow or forbid changes, and see
  exactly which tools Claude can reach (read / change / remote). Connect Claude Code (terminal and VS Code extension)
  with one click (*Add for me* runs `claude mcp add`, *Remove* takes it out again), or copy the ready-made config for
  VS Code's agent mode and Claude Desktop; each client shows whether Helm is added, and says so when it points to
  another Helm. *Connections* shows which Claude clients are connected now, since when and how many tools they
  called, and disconnects one. [docs/mcp.md](docs/mcp.md) explains each client.
- **Permissions** on that page: *Ask before every change*, whether Helm runs as Administrator, the calls allowed for
  this session (*Revoke*), and an activity log of every call that changes something or runs on a server
  (device-local, last 500, *Clear*).
- **SSH for Claude**: `ssh_servers`, `ssh_menu`, `ssh_menu_choices` and `ssh_menu_run` let Claude use a server's
  menu, never a shell, and only on servers connected in Helm with *Claude may use this server's menu* on (off by
  default, PC). Every run is asked first with the exact command line, runs without a terminal, with a timeout and at
  most the last 64 KB of output. Menus can mark items `readOnly`, and `"agents": false` keeps an item away from Claude
  ([protocol](docs/ssh-menu.md)).
- **Missions for Claude**: update, start, pause, resume or abandon a mission, skip or reopen a step, tick a step's
  checklist and edit a step or its note.
- **Tracker for Claude**: add lists, add subtasks to an existing task, add daily repeating tasks, stop a repeat and
  mark a task started.

### Changed
- **Navigation groups** say what is inside: Planning (Notes, Tracker, Missions, Quick Capture), Money & Media (Wallet,
  Watch Later), Security & Servers (Vault, SSH) and Windows & Desktop (Always on Top, Command Palette), on PC and
  Android. "System Tools" and "Advanced" are gone.
- **Claude asks first when it matters**: changes while Helm runs as Administrator (or when you ask for it), and every
  command on a server. The dialog says who asks, what will happen, why Helm asks and the exact command; Deny is the
  default and no answer in 2 minutes means no. *Allow for this session* stops repeats of the same change; commands as
  root or marked high are asked every time.
- **SSH menu**: no longer a tab that replaces the terminal. The **Menu** button next to *Disconnect* opens a popup over
  the terminal; choosing an item types its command into the terminal, so its output shows there next to everything
  else and Ctrl+C stops it. An item with parameters (Restart app) opens a small form in the popup first; dangerous
  items still ask, showing the line Helm will type. The command starts with a space, which keeps it out of bash's
  history.

### Removed
- **Claude Chat**. Use Claude Code in VS Code (or the terminal) instead: it reaches your notes, Tracker, Missions and
  servers through Helm's MCP server (see the Claude & MCP page). Your chat settings and history stay on disk.

### Security
- Helm's MCP pipe checks each caller (this machine, this user) and its elevation, serves at most 8 connections and
  caps message size; more than 10 questions a minute are refused; secret arguments are hidden in the dialog and the
  log. Switching Helm's tools off, or switching changes off, ends the connections that are open.
- Helm reads only its own `helm` entry in the clients' config files, never other servers' settings or tokens.
- A tool that runs something on another machine is refused unless Helm can ask first. Server output is passed to
  Claude as data, never as instructions; a root user or a high-danger item is asked with a stronger warning.

## [0.29.0] - 2026-10-06

### Added
- **SSH menu** (PC): a *Menu* tab next to the terminal shows what the server offers to run, as the server itself
  describes it: one program on the server (`~/.helm/menu`, or a path set per server) lists its items as JSON and runs
  them, so the menu always matches that server ([protocol](docs/ssh-menu.md)). Items are buttons with a form (lists the
  server fills, text, numbers, switches) or views that refresh themselves; output is a live log, a table with buttons
  on each row, or a few figures. Items marked dangerous ask first, and the riskiest show the exact command. Helm
  checks every value against what the menu declared and quotes each argument, so nothing typed can run as a command.

### Fixed
- **SSH**: *Install this device's key* now shows for every server that signs in without this device's key, also with
  a password or key from Vault and with a key file (0.28.0 offered it only after typing a password). Once installed,
  the server signs in with the key and forgets the Vault field or key file it used.

## [0.28.0] - 2026-10-06

### Added
- **SSH** (PC and Android): a terminal on your servers, in Helm. Add a server as `user@host` (or `user@host:port`), choose it and
  *Connect*; the session keeps running while you use the rest of Helm, and each server keeps its own. Helm signs in
  with an Ed25519 key it makes for this device (*SSH settings → This device's key*: copy the public line into
  `~/.ssh/authorized_keys`), or with a password you type each time. Signed in with a password, *Install this
  device's key* adds the key to the server for you and the next sign-in uses it. Copy and paste as in Windows
  Terminal (Ctrl+C with a selection, Ctrl+V, right click); the text size is in the settings.
- **SSH**: *Import from SSH config* adds the servers you already use with the ssh command (`.ssh\config`), with
  their key files (Helm keeps only the path) and the server keys `known_hosts` already trusts, so they connect with
  one click. A server can also sign in with a key file you choose (its passphrase is asked when needed), or with a
  password or private key kept in **Vault**: pick the item and field, and Helm reads it when connecting (Windows
  Hello opens a locked vault when quick unlock is on; otherwise *Open Vault*).
- **SSH on Android**: the same terminal on the phone, with a key bar for what a phone keyboard lacks (Ctrl, Esc,
  Tab, arrows, Home/End, `| ~ / -`, paste, show the keyboard); Enter and Backspace from the keyboard work, and the
  terminal fits the screen above the keyboard. The phone's own key is protected by the Android Keystore; a password or
  key can also come from Vault (fingerprint unlock). Key files and *Import from SSH config* are on PC only.

### Security
- **SSH**: the device key's private half is encrypted for your Windows account (DPAPI) and never synced, exported or
  shown; passwords are never saved. A server's key is shown with its fingerprint the first time and pinned; if it
  changes later Helm refuses to connect (there is no "connect anyway"). Algorithms OpenSSH retired (SHA-1, CBC, DSA)
  are not offered. The terminal page is served from Helm itself with a strict content policy: no network, no links
  that open, no browser shortcuts; pasting text with line breaks asks first. Nothing typed or shown is written to
  disk or to Helm's logs. A server signing in with Vault keeps only which item and field; the value is read once per
  connection while the vault is unlocked, and other tools see field names, never values, two-factor secrets or files.

## [0.27.0] - 2026-10-06

### Changed
- **Wallet**: the debt book moved here from Tracker. *Money* and *Debts* sit side by side at the top of Wallet;
  *Debts* has the same book as before: who owes you and whom you owe, repayments that bring a balance back to 0,
  due dates (+1 day, +1 week, +1 month), notes linked to a person, and *Settled*. Your debts are copied over by
  themselves the first time this version runs, with nothing to do; notes linked to a person still are. Update Helm
  on every device: an older version keeps showing the Tracker's old book, and a debt added there is copied over the
  next time a new version checks.
- **Wallet**: a transaction can go in the debt book. Open it and choose *Lent, borrowed, paid back* (the person icon
  on the phone): pick the person and the kind, and its money becomes a debt entry. It then counts neither as
  spending nor as income, and the entry shows the bank icon. Deleting the entry puts the transaction back with the
  ones to categorize.
- **Tracker** is for to-do lists only. Its calendar, its .ics file and its daily reminder still show the debts that
  are due, from Wallet; tap one to open it there. *Quick Capture* `/d Nam 200k` and Claude's tools (`wallet_debts`,
  `wallet_add_debt`, which replace `tracker_debts` and `tracker_add_debt`) write to Wallet. Tracker's CSV no longer
  has the person, amount and direction columns.

## [0.26.1] - 2026-10-05

### Changed
- **Wallet**: an amount gets its thousands separators as you type it, in a new transaction and when you edit one
  (1000000 reads 1.000.000), as in Tracker's debt book. *50k*, *10tr* and *1.5tr* still work as before.

### Fixed
- **Wallet** (Android): in *Spending by category*, the bar of a large share no longer runs left over the
  category's icon; every bar starts under the name.
- **Tracker**: a '.' or ',' you type in a debt's amount is kept, so *1.5k* can be typed; before, it vanished
  as soon as you typed it.

## [0.26.0] - 2026-10-05

### Added
- **Missions**: a step's resources can carry what you need to study, not only links. A resource can have a label
  (*Vocabulary*, *Grammar*, *Mock test*… in your own words), a text (the theory, where the test is and how to open it)
  and a table whose columns the plan names for its subject (*Word / Pinyin / Meaning*, *Formula / When to use*). Tap a
  resource to open it on the current step or in the roadmap; *Copy* puts it on the clipboard, with the table as columns
  that paste into a spreadsheet or a flashcard app. The prompt asks the AI to write this content in, and Claude can
  write it through `mission_create` and `mission_replan`. Older Helm versions still show each resource as one line.

## [0.25.0] - 2026-10-05

### Added
- **Wallet** (Android): Techcombank sends no notification for a payment you make in its app. When the next balance
  a bank gives shows money that went out (or came in) without a notification, Helm asks what it was, with the likeliest
  categories and *Later* (saved to categorize). Nothing is saved unless you tap a button, and it does not ask about an
  amount you already typed in. Settings → Wallet → *Ask about missing payments* turns it off.

### Fixed
- **Wallet** widget (Android): a large balance no longer spills out of the ring. The balance is now drawn inside the
  ring, in full when it fits and shortened (12.4M ₫) when it does not, whatever size the launcher really gives the
  widget.

## [0.24.1] - 2026-10-05

### Changed
- **Android** uses less battery in the background. Wallet's notification reader keeps Helm running all day, so sync
  now checks for other devices' changes every 30 minutes in the background instead of every 5 (and not at all while
  Battery Saver is on). Your own changes are still sent within seconds, and opening Helm fetches the rest at once.
- **Vault**: the one-time codes count down only while the vault is open, instead of waking Helm every second for as
  long as it runs (on the PC too).

### Fixed
- **Wallet** widget (Android): the middle of the ring always shows your balance, never the period's spending. The
  balance is everything received minus everything spent, not the banks' own balances: cash you take out is still your
  money, and what you spend from the bank is subtracted as it is read. Transfers between your own accounts count as
  neither.

## [0.24.0] - 2026-10-05

### Changed
- **Wallet** (Android): the home-screen widget is new. Your balance (the latest balance each bank gave, added up over
  your accounts) sits in the middle of a ring; tabs pick **today, this week (from Monday), this month or all time**, and
  the widget shows what you spent and earned in that period. The ring splits the spending by category, or, if you
  prefer, compares it with the balance. A red dot counts the transactions waiting for a category and disappears when
  there are none. It fits from 2 × 2 (one button steps through the periods) to 4 × 3 (a title, full tab names and
  five categories with their shares). Settings → Wallet → Home screen sets the ring, background, opacity, text colour,
  and can hide the balance from anyone who sees your home screen.
- **Tracker** and **Wallet** widgets (Android): the *Transparent* background now takes the phone theme's colour at the
  opacity you choose (35 % when you first pick it) instead of being fully clear, so the text stays readable on any
  wallpaper. A Tracker widget already set to Transparent switches to 35 %.

### Security
- **Vault**: the count of wrong vault passwords (which makes Helm wait longer after each one) is now kept in a file
  only this Windows user (or this phone) can read, instead of the readable settings file, so it can no longer be reset
  by editing or deleting a file. A deleted or damaged count is treated as a recent wrong password: one short wait. The
  first password unlock after updating, on a new device, or after resetting settings may ask you to wait 2 seconds once
  (it says so, instead of claiming wrong passwords).
- **Vault** (Android): the fingerprint quick-unlock file is now protected by the phone's keystore too. Quick unlock
  turns itself off once after updating: unlock with the password and turn it on again in Vault settings.
- **Vault** (Android autofill): a login remembered for an app is now tied to who signed that app, so a fake app that
  took the same name (installed from a file) is never offered it. Logins remembered before this update are offered
  in one tap again after you pick them once with *Choose from Helm Vault…* in that app.
- **Vault** (PC): the key Windows Hello quick unlock keeps is now made with a random salt, like every other key in the
  vault. Quick unlock keeps working; the file is updated by itself the next time you use it.
- **Vault**: saving the Emergency Kit as a file (on the phone, or as a PDF) now says plainly that it opens the whole
  vault without the password: print it or move it to a USB stick, then delete the file.

## [0.23.2] - 2026-10-05

### Fixed
- **Wallet** (Android): when Android 13 or later blocks notification access for Helm ("Restricted setting", because
  Helm is installed from a file), the Wallet page now says how to allow it: try turning Helm on once and close the
  message, then App info → ⋮ → *Allow restricted settings*, then turn Helm on again. A button opens App info.

### Security
- **Wallet** never keeps a one-time code (OTP, verification code, password) from a bank's notification, not even in
  the list of notifications it could not read. Codes that 0.23.0 and 0.23.1 kept there are deleted.
- **Wallet** reads only the banks' own apps, by their exact package name, and SMS whose sender is exactly the bank's
  name, shown by the phone's SMS app. Another app can no longer add transactions by naming itself like a bank or an
  SMS app, and a contact called "ACB …" is no longer read.
- Releases wait for the owner's approval before anything is built with the signing key, and a release is published
  only once all its files are attached.

## [0.23.1] - 2026-10-04

### Added
- **Wallet** (PC and Android): see where your money goes. Helm on the phone reads **Techcombank** and **ACB** balance
  notifications (their apps, or their SMS) and saves each transaction by itself: the amount, the balance after it, the
  account, the description and the time. The same transaction from the app and the SMS is saved once. Transactions
  sync end-to-end encrypted to your PC.
  - **To categorize**: each new transaction waits with a few likely categories as buttons (those of similar earlier
    ones first); one tap files it, and Helm offers to file the similar ones too. A description you always put in the
    same category is filed by itself (marked *auto*). Need a category that is not there? Type its name right on the
    transaction (or when adding one by hand) and it is made and used.
  - **What was it?** After a payment the phone shows a quiet notification with three categories as buttons, so you can
    file it without opening Helm.
  - **Charts**: the month's **cash flow** (money in, spent and net as bars on one scale), the **budget** meter with a
    mark where spending would be on track today, **spending by category** as a donut with its legend, **spending by
    day** with the budget per day, and the **last 6 months** of money in and spent side by side (click a month to
    open it). Point at (or tap) a slice or a column to read its numbers.
  - **Categories with an icon and a colour**, everywhere they appear: rename, hide or recolour the built-in ones, add
    your own and pick from 45 icons and 8 colours (chosen to stay apart for colour-blind eyes). *Between my accounts*
    counts neither as spending nor as money in.
  - Add cash by hand with its day and time, or with `/s 50k coffee` (`/s +2tr bonus` for money in) in Quick Capture.
    Every transaction of the month by day, with filters and search; click one to change its category, edit it, see the
    bank's notification, or delete it.
  - An Android **home-screen widget**: this month's spending against the budget, today's, how many transactions wait
    for a category and the top categories.
  - Settings: notification access (with the steps for Android's *Restricted setting*), which banks to read, **Try a
    notification** (paste one to see what Helm reads), the notifications Helm could not read, and CSV export.

### Changed
- **One box for a day and a time**, everywhere (Wallet, Tracker's due dates, Missions' deadlines). On PC, type it the
  way you say it ("hôm qua 18h45", "mai 9h", "thứ 6", "3/10 14:30", "3h chiều") and the line under the box shows what
  Helm read; or tap a shortcut (Now, Yesterday, Tomorrow, In a week…); or open the panel with the month, the times and
  the exact minutes. On the phone the box opens one sheet: a strip of days to swipe and hour and minute columns.

### Fixed
- On a narrow window the sync button no longer slips under the minimise button; it is one plain glyph, as on Android.

## [0.22.0] - 2026-10-03

### Added
- Missions: **Re-plan**. Behind, ahead, or something changed? Say what (optional) and copy the prompt: it carries
  what you have done, how long each step took and what is left. Paste the AI's new plan and check it; the steps not
  done yet are replaced, the done ones stay. Claude can do the same (`mission_replan`).
- Missions: a **daily step reminder** (PC and Android), once a day at the hour you pick, with the step each mission in
  progress is on; missions you already moved on today are left out. Turn it off or try it in the Missions settings.
- Missions: **share an AI's answer to Helm** on the phone (Share → Save to Helm), or paste it into Quick Capture on
  the PC (`/m`): the mission in it is saved, ready to start.
- Missions: **save the summary to Notes**.
- Missions: **badges** across all your missions (first step, ten steps, a full week in a row, mission complete, ahead
  of plan, beat the clock, comeback…), at the bottom of the Missions page; a step that earns one says so.
- Missions: a **home-screen widget** on Android with the missions in progress, the step each one is on and its
  progress; tap it to open Missions. Add it from the Missions settings or the launcher's widget list.
- Missions: **Today**. When more than one mission runs, a card at the top lists the step each one is on; complete it
  right there or open the mission.
- Missions and Tracker: **Send to Tracker** puts the step you are on in your to-do list, due when it should be done.
  Finishing it in Tracker completes the step, and completing the step finishes the task.
- Missions: **statistics** across all missions (steps per week, how long steps take against their plan, missions done
  by their deadline) and **CSV export** of every step (Save on the PC, Share on the phone).
- Missions: **ready-made missions** to start without an AI (run 5 km, read 12 books, language basics, a 30-day habit,
  ship a side project); each opens the import preview to adjust before creating it.
- Notes and Missions **link to each other**: link a note to a mission from the note (or Claude does it), and the
  mission page lists its notes, with Link a note… and New note.

### Fixed
- Missions on Android: the text and icons of the wide buttons were pushed to the top; they are centered now.

## [0.21.0] - 2026-10-03

### Added
- **Missions** (PC and Android): reach a goal one step at a time. Describe the goal and Helm writes a prompt for any AI
  (ChatGPT, Gemini, Claude…); paste its answer and Helm finds the mission in it, shows the phases and steps to check
  (untick the ones you do not want), and creates it. Steps are done in order: the page shows the step you are on, with
  how to tell it is done, its checklist and links; Complete (with a note), Skip and Undo. Every step's start and
  finish dates are kept, the roadmap shows them, and the pace says how far ahead of the plan or behind you are, when
  you should finish at that pace and how that compares with the deadline. Finishing a phase or the whole mission is
  celebrated, with the reward you chose and a summary to copy. Missions sync end-to-end encrypted between your devices.
- Helm tools for Claude: **Claude can make a mission for you** ("make me a mission to pass HSK3 by March"), read your
  missions and progress, and complete the step you are on.

## [0.20.0] - 2026-09-30

### Changed
- Watch Later: videos now **play right on the Watch Later page**, in Helm's window, instead of a window of their own.
  Leaving the page pauses the video (where it was is saved). The mini player opens only from the player's Mini player
  button, and its Back to Helm button (or a double-click on its bar, or Esc) returns the video to the page, carrying on
  where it was.

### Added
- Watch Later: **playback speed** 1×, 1.5×, 2×, 2.5× and 3× (the list above the video, or the speed button of the mini
  player), for YouTube and Facebook videos and downloaded files alike. Helm remembers the speed you picked last.

## [0.19.0] - 2026-09-30

### Added
- Watch Later: **play videos in Helm** (PC). Play opens Helm's player window: YouTube's and Facebook's own players, or
  the downloaded file when there is one (it plays offline). Links inside the player open in the browser; a video
  whose owner does not allow playing outside YouTube, or a private Facebook video, says so and offers the browser.
- **Mini player**: a small player that stays on top of other windows, in the bottom-right corner at first; drag its
  bar to move it, double-click it (or Esc) to go back to the normal window. Helm remembers both positions and which
  one you used last.
- **Carry on where you stopped**, on every device: the place is saved while you watch and synced; the card shows a
  red bar and "stopped at 3:12", and opening a YouTube video in the browser or the phone's YouTube app starts there.
- Videos you watch to about 90 % are marked as watched by themselves.

## [0.18.1] - 2026-09-30

### Changed
- Home: the shortcut-conflicts tile shows a green check when there are none and an amber warning when there are.
- Tracker: every field of the add and edit forms has a label (Person, Amount, Kind, Priority, Repeat, Due date, Time),
  and times read like in Notes ("just now", "5 min ago", "Yesterday", "28 Sep"), updated every minute.
- Notes and Tracker: delete buttons sit apart from the other actions, so they are harder to hit by mistake; small
  badges are easier to read.
- Screen readers name Home's tiles and toggles and each note in the list.

## [0.18.0] - 2026-09-30

### Added
- **Watch Later** (PC and Android): a place for the YouTube and Facebook videos, Shorts and Reels you want to watch
  later, synced end-to-end encrypted like your notes.
  - Save: on the phone, tap Share → **Save to Helm** in YouTube or Facebook (a video link picks Watch Later by itself);
    on PC, paste or drop a link on the page, type `/w <link>` in Quick Capture, or paste it into the command palette.
    What you type after the link becomes the video's note ("why I saved it"). Saving a video twice moves it to the top.
  - The title, channel, length and thumbnail are filled in by themselves (YouTube's own data, Facebook's link preview,
    and yt-dlp on PC). fb.watch and facebook.com/share links are followed to the real video; a Facebook video that
    turns out to be a Reel is filed as one.
  - Filters: To watch, Videos, Shorts & Reels, Watched, by site, and search (accents do not matter). Open plays it in
    the YouTube or Facebook app on the phone, the browser on PC.
  - **Download** (PC): pick the quality first (the ones the video really has, with their size). Helm fetches yt-dlp the
    first time and keeps it up to date; ffmpeg, which YouTube needs for anything but audio, installs from the picker or
    Settings in one click. Two downloads at a time, with progress, and a notification when done.
  - **Download on PC** (Android): pick a quality and your PC downloads the video while Helm runs there; the phone then
    shows "Downloaded on <PC>". Settings: the download folder, requests from the phone, and sign-in cookies from a
    browser for private Facebook videos.

## [0.17.1] - 2026-09-29

0.17.0 was tagged but never published (its release build stopped at a failing test); 0.17.1 is that release with the fix below.

### Fixed
- Helm tools for Claude (MCP) now connect when Helm runs as administrator, as the installed Helm does: Claude Chat's
  chats (and Claude Code started from an administrator terminal) were refused by the pipe's owner check.

### Added
- Sync: **Change passphrase** (General → Sync, PC and Android). Enter the current passphrase, or the recovery key if
  you forgot it, then the new one. Devices already syncing carry on without asking, and the recovery key stays the
  same; a device you add from now on unlocks with the new passphrase.
- Sync: **live updates**. A change made on one device reaches the others within seconds instead of at their next check:
  Helm keeps a light connection to the sync server open (on Android while Helm is in front), and the server says when
  something changed. The General → Sync status shows "live" while it is connected. Without it (an older server, no
  network) Helm checks every 30 seconds or 5 minutes as before.
- Sync: **choose what syncs on each device** (General → Sync → What syncs on this device): untick Notes, Tracker or
  Vault to stop syncing that tool on this device, e.g. the vault on a shared PC. What it already has stays; ticking it
  again downloads the tool's data afresh and sends the changes made here meanwhile.
- **Notes on tasks and debts** (PC and Android): link notes to a task, or to a person in the debt book (their bank
  details, what was agreed…), and a note to tasks and people. The links show as chips on both sides; click one to open
  it in the other tool, × removes the link (nothing is deleted). On a task, the link button offers **New note**, which
  writes a note titled after the task. A repeating task keeps its notes every day. Links sync like everything else.
- Vault: **two-factor codes** (PC and Android). Add a field *One-time code (2FA)* to a login and paste the key the site
  shows when you turn on two-factor sign-in (or the otpauth:// link of its QR code). The card and the item show the
  current 6-digit code with how many seconds it has left; **Code** copies it (through the protected clipboard). The key
  stays hidden. KeePass export writes it where KeePassXC reads it (`otp`).
  Note: an item with a one-time code field cannot be read by Helm 0.16 or older (it is kept, not lost); update every device.
- Tracker: a **calendar** (PC and Android). **Calendar** next to Lists shows the month, or a week, with every task
  and debt that has a due date, from every workspace (or only the chosen one): overdue in red, done crossed out, a
  daily task on its coming days too. Click a day to see what is due, tick or edit it there, or add a task due that day.
  **Export .ics** (PC) or **Share .ics** (Android) makes a calendar file of the open ones for Google Calendar, Outlook or
  a phone; importing it again updates the same events. Timed items get a reminder 15 minutes before.
- **Helm tools for Claude** (MCP, PC): Claude can find, read and write your notes, to-do lists and debt book. Helm's
  own chats get the tools by themselves; for Claude Code in a terminal, Claude Chat's settings show the one line to
  add them (`claude mcp add … Helm.exe --mcp`). Claude asks before each use, **Allow changes** off makes the tools
  read-only, and nothing is deleted for good (notes go to the trash). The vault is never included. Helm has to be
  running; the tools of a tool you turned off are not offered.
- Vault: **autofill on Android**. Choose Helm in Vault settings → Autofill (Android asks to confirm). Sign-in forms in
  apps and browsers then offer the vault's logins for that site; a locked vault opens with the fingerprint or the
  password first. For an app, the login you pick is offered there again next time. The current two-factor code fills a
  code field too. Chrome and other browsers that do not ask autofill services themselves are read in Android's
  compatibility mode. A login is never offered to a look-alike site.
- Vault: **auto-type on Windows**. In a sign-in window press Ctrl+Alt+A (change it in Vault settings): Helm types the
  username, Tab and the password of the login for that site (read from the browser's address bar) or app, or lets you
  pick one. Nothing goes through the clipboard, except the two-factor code, copied for the next step. Optionally it
  presses Enter too.

### Changed
- Sync: deleted items no longer stay on the server for ever. A deletion is kept 90 days, long enough for every device
  to learn about it, then removed, which gives the space back. A device that was away longer downloads everything
  again on its next sync and drops what was deleted meanwhile; a vault that would lose many items that way asks first,
  as for any mass deletion. Old deletions are also forgotten on each device.
- Sync: Vault files go straight between your devices and the storage (Cloudflare R2) instead of through the sync
  server, once the server has its storage keys (see server/sync-worker/README.md). Until then, and if anything goes
  wrong with it, files take the old way.

## [0.16.0] - 2026-09-29

### Added
- Command Palette searches Windows too, below Helm's own results: files and folders (from the Windows Search index,
  the one the Start menu uses), Microsoft Store apps next to the Start menu's, open windows (switch to one), pages of
  Windows Settings ("wifi", "bluetooth", "âm thanh", "cập nhật"…) and, last, "Search the web" (Google, Bing or
  DuckDuckGo). A result from Windows only comes before a note or a task when it matches much better. Each source can
  be turned off in the palette's settings. Everything opens as you, not as administrator.

### Fixed
- Quick Capture (Android): the share dialog follows Helm's theme (light, dark or the phone's setting) with Helm's
  colours; its text and choices were in the wrong colours for the dialog's background.

## [0.15.0] - 2026-09-29

### Added
- Notes (PC and Android): notes that sync across your devices, end-to-end encrypted like the rest of Helm. They save
  as you type; pin a note to keep it on top; search ignores accents ("ghi chu" finds "Ghi chú") and looks inside the
  text. When two devices edit the same note, both versions are kept: the other one arrives as a "(conflict copy)".
  Deleted notes stay in the trash for 30 days. The settings have the sort order, the text size, a fixed-width font,
  and (PC) Export as Markdown.
- Quick Capture (PC and Android): save a note, a task or a debt from anywhere. On PC, Win+Alt+N opens a small box over
  any app; on Android, Share → "Save to Helm" in any app, or the Quick Settings tile. The line under the box says what
  Enter will do. Short forms, in English or Vietnamese:
  - `/t buy milk tomorrow 9h #Home !` a task in the list "Home", due tomorrow at 9:00, high priority (`!!` urgent);
    days and times such as "mai", "thứ 6", "chủ nhật 3h chiều", "15/10", "mon 9:30pm" are read at the end.
  - `/d Nam 200k lunch` Nam owes you, `Nam -200k` you owe Nam, `Nam trả 50k` a repayment.
  - `/n` or nothing: a note (several lines: the first one is the title).
- Command Palette (PC): Alt+Space opens one search box over any app. It finds your notes, open tasks, people in the
  debt book, vault items while the vault is unlocked (titles only), Helm's pages and settings, and the apps in the
  Start menu (they open as you, not as administrator). Text that finds nothing can be saved as a note, task or debt.

### Changed
- Vault: an item picked in the command palette opens with its card expanded.
- Tracker has a new icon: a tick leaving a ring (also its reminder notifications on Android). Every tool icon now
  has the same size and weight.
- Home (PC and Android): the Quick access icons stay in one line when a tool's name takes two lines.

## [0.14.0] - 2026-09-29

### Added
- Vault: file fields: Image (with a preview), Document (any file), Text file (.txt) and Keystore. Add one with
  Add field, then Choose… a file; it is encrypted at once and joins the item when you save (Cancel leaves nothing
  behind). Viewing the item, each file has Open and Save a copy.

### Changed
- Vault: the separate Documents section is gone: files are fields now. Documents attached before show as file fields
  (an Image, Text file, Keystore or Document by their type), so nothing is hidden.
- Note: an item with a file field cannot be read by Helm 0.13 or older (it is kept, not lost); update every device.

## [0.13.0] - 2026-09-29

### Changed
- Vault: the backup warning only shows when none of your devices has backed the vault up for a week. One device with
  a backup folder is enough; the others no longer ask for one.
- Vault: no more reminder to confirm the Emergency Kit in the list (its state is still in the Vault settings).
- Vault: Favorites is the first sub-tab, shown as a star, and the trash the last, shown as a bin. Each card has a star
  to make it a favorite or not with one click (this is not a new version in the item's history).
- Vault: every card has an icon: the item's picture, a login's initial, or its type's symbol (token, note, card…).
- Vault: a new item is shown closed in the list once saved (an edited one stays open).
- Vault: secret fields have an eye next to them while editing too (on Android also for passwords), and the eye shows
  whether the value is visible.

## [0.12.0] - 2026-09-29

### Added
- Tracker: tasks can repeat every day, for ever or for a number of days. Each new day the task is there again (with
  the same due time and subtasks), on every device; days Helm was not opened are skipped. The task shows how many days
  it was done, and the report lists each repeating task with the days done in its period. Deleting a repeating task
  (or setting it to "Does not repeat") stops it.

### Changed
- Tracker widget (Android): the small button shows the number of open items itself, large enough not to be cut,
  instead of a gear with a badge.

## [0.11.0] - 2026-09-28

### Changed
- Vault: a new look. One search box above everything searches every tab (each tab shows how many items match). Two
  tabs, **Credentials** (logins and tokens) and **Other** (Info items), each with its own **New** button, and sub-tabs
  instead of the Show list (All, Logins, Tokens, Favorites, Trash; All, Favorites, Trash in Other).
- Vault: items are cards across the whole width. A login card shows its icon, title, username and password, each with
  Copy; a token card its title and token; an Info card its title and description. Click a card to open its details
  below it, click again to close them.
- Vault: adding and editing happen in a window above the list, with one row per field: type, name, value, remove.
- Vault: Other has one type, **Info**, with the fields you add; cards, identities and documents made before are Info
  items now and keep their fields.

- Tracker debt book: one line per person. Entries for the same name (whatever its case or spacing) add up to one
  balance: **Owes me** adds, **I owe** subtracts, and the new **Repayment** brings the balance back toward 0. A person
  moves to Settled when the balance reaches 0; there is no tick box any more. Click a person to see every change of
  the amount with the balance after it, and to set when it is due (+1 day, +1 week, +1 month, or a date and time);
  click again to close. Amounts are not edited: add a repayment or delete a wrong entry.
- Tracker debt book: amounts get thousands separators while you type, and names already in the book are suggested.
- Tracker: due dates can have a time (hour and minute), for tasks and debts.
- Tracker: tasks can have subtasks, each with its own tick; the task shows how many are done, and finishing the task
  finishes them too.
- Sync: a sync button on every page (next to the search box on Windows, in the app bar on Android) shows whether
  everything is synced, syncing, offline or needs attention; click it to sync now, or to set sync up. While Helm is in
  front it now checks for other devices' changes every 30 seconds (every 5 minutes in the background or tray), and
  right away when you come back to it.
- Tracker widget (Android): sync, + and a shrink button. With nothing to do, or when shrunk, it becomes a small button
  showing how many items are open, on a see-through background; tap it for sync and +. Choose the widget's
  background, its opacity and the text colour in Tracker's settings. The debt book shows one line per person.

### Fixed
- Vault: "Emergency Kit not confirmed" no longer shows on your other devices once you confirmed it on one.

## [0.10.1] - 2026-09-28

### Fixed
- Android: the empty band that could still appear above the title bar right after opening Helm (Android 15 and newer),
  until the screen was turned off and on. Helm now measures the status and navigation bars together with its own
  position, and again shortly after start.

## [0.10.0] - 2026-09-28

### Added
- Vault: **Token** items for API keys and access tokens: one hidden field, shown and copied like a password. Older Helm
  versions list tokens as "cannot be opened" (they keep them) until they are updated.
- Vault: an item's **Type** can be changed while editing, e.g. to turn a token saved as a login into a token. Its fields
  and history are kept.

### Changed
- Vault: the list has two tabs. **Logins & tokens** shows each one with its icon, the hidden password or token and Copy;
  **Other** holds notes, cards, identities, documents and pictures, each with its picture or its type's icon. The Show
  choices and the trash belong to the open tab.
- Vault (PC): click the open item in the list again to close it; the next click opens it again. An item you are
  editing stays open.
- A login without a password no longer shows "No password" in the list.
- Pages start 16 px below the window's title bar instead of right under it.

## [0.9.3] - 2026-09-28

### Fixed
- Vault: the page title, headings and labels were black in the dark theme (hard to read on the dark background).

## [0.9.2] - 2026-09-28

### Fixed
- Opening Helm from the Start menu or the desktop could bring up another copy of Helm running on the PC (for
  example a test build) instead of the installed one, a different one each time. Only the installed Helm answers now.
- Tracker and Vault icons no longer sit on a coloured tile and are the same size as the Claude Chat icon, on the
  PC and on Android.

## [0.9.1] - 2026-09-28

### Fixed
- Tracker's icon was drawn larger than Vault's (and the other tool icons) in the menu, on Home and on its pages.
  It now fills the same square, on the PC and on Android.

## [0.9.0] - 2026-09-28

### Added
- Vault (PC and Android): passwords, secure notes, cards, identities and documents in one encrypted vault.
  - Its own vault password, separate from the sync passphrase, plus a recovery key on a printable Emergency
    Kit. Nothing can be read without one of them, not even by the sync server.
  - Unlock with Windows Hello or a fingerprint; the password is asked again every two weeks (configurable).
    The vault locks after inactivity, when Windows locks, and shortly after the phone app goes to the background.
  - A user guide in Vietnamese inside the PC app (Guide at the top of Vault, also on the first screen), the same
    text as docs/vault-guide.md: the three secrets, setup, backups, restoring, and what to do when things go wrong.
  - Documents up to 256 MB each (the server's limit is adjustable), encrypted on the device and synced in chunks.
  - Nothing is lost by mistake: deleted items stay 30 days in the trash, every edit keeps the previous 10
    versions, edits made on two devices at once are both kept, and a sync that would delete many items is held
    until you decide.
  - Daily encrypted backups into a folder you pick (Google Drive, OneDrive, a USB stick), verified after
    writing. Restore a whole vault on a new device, or bring back deleted items. `helm-vault-restore`, attached
    to every release, opens a backup without Helm, and the vault exports to KeePass (KDBX 4).
  - Copied secrets stay out of the clipboard history and are cleared after 30 seconds; the vault is hidden
    from screenshots and screen sharing.
- Sync: files (blobs) for tools such as Vault, and a guard that holds bulk deletions for review (counted over
  24 hours, so they cannot be spread thin).
- Stronger password check for the vault password and new sync passphrases: common words, names, years and keyboard
  patterns no longer count as strong (for example "Password@2026!!").
- Android tools can now be added (Helm.Core.Android), with the same page layout as on the PC.
- Tools you work in open their content from the menu and Quick access, and their settings from Home → Utilities.
- Tracker (PC and Android, System Tools): lists you keep in workspaces, synced across your devices with Helm Sync.
  - To-do lists (title, priority Low / Normal / High / Urgent, due date, notes), as many as you like, and one debt
    book for everyone (who, how much, owes me or I owe, with running totals; amounts in ₫ by default).
  - The menu and the Quick access tile open your lists; Home → Utilities opens Tracker's settings (workspaces,
    display, CSV export).
  - Open items are sorted by priority, then by your own order (move up and down within a priority).
  - Tick an item and it is marked done and written to the history with the time it was added, started (the Start
    button, or when it was added) and finished. Reopening and deleting are recorded too, and deleted items stay
    in the reports.
  - Report for the last 7, 30 or 90 days or all time: completed and added counts, average and median time to
    finish, working time, share done by the due date, completions per day and by priority. Export every item or
    the whole history as CSV (Save on the PC, Copy on the phone).
  - Android home-screen widget: a workspace's open items; tap a circle to finish one, the title to switch
    workspace, + to open Tracker. Settings → "Add to home screen" places it on launchers that allow it.
  - Due date reminders: once a day (9:00 by default) a notification lists open tasks and debts that are overdue
    or due soon (on the day, or 1–3 days before); click it to open Tracker. On the PC it comes from the tray; on
    Android from a daily alarm, also when Helm is closed and after a restart (Android 13+ asks for permission).

## [0.8.1] - 2026-09-28

### Fixed
- Android: an empty band could appear above the title bar on some starts (Android 15 and newer). The page is now
  moved clear of the status bar exactly once, however Android lays the window out.

## [0.8.0] - 2026-09-28

### Added
- Helm for Android (8.0 or newer): `Helm-android.apk` is attached to every release, with the same Home and
  General pages as on the PC. Tools come per platform; the Android app starts without any, and Claude Chat
  stays PC only.
  - Sync works on the phone too: add it to your account from General → Sync with a device token, then unlock
    with your passphrase. Its keys are kept in the Android Keystore.
  - Updates like on the PC (Stable or Preview): Helm downloads new versions from GitHub in the background,
    checks them, and asks Android to install them when you tap Install update.
  - Light, dark or system theme, shareable log files, and Reset all settings.

## [0.7.0] - 2026-09-27

### Added
- Claude Chat (Advanced): chat with Claude Code in its own Helm window. It uses the Claude Code you already
  have installed and signed in, so there is no API key to set up and nothing extra to pay per message.
  - Work on several projects at once: add folders on the left, open as many chats in each as you like, and
    let them run side by side. Each chat shows whether it is working, waiting for you, finished or stopped
    with an error.
  - Helm notifies you when a chat you are not looking at finishes, needs your permission or fails. Click the
    notification to go straight to that chat.
  - Claude asks before it changes anything: allow once, allow for the rest of the chat, or deny. "For the rest
    of the chat" never writes to your Claude Code settings.
  - Type / for commands and actions (attach or mention a file, clear, export as Markdown, switch model, and
    Claude Code's own commands and skills), and @ to mention a file of the project.
  - Past chats of a folder are one click away (the clock button), and the folders and chats you had open come
    back the next time you open Helm, ready to continue.
  - Replies show formatted text, lists, tables and code. Choose the model per chat and switch it mid-chat.
  - Open it from Quick access on Home, with Win+Alt+C, or from the Claude Chat page, which holds its settings.
- Quick access tiles can now start a tool's main action directly, and tools can show their own logo.

### Fixed
- The icon on a tool's "Enable" card was smaller than the other card icons.

## [0.6.0] - 2026-09-25

### Security
- Stronger passphrase protection: the account passphrase is now protected with Argon2id, which makes
  guessing it far harder. Accounts set up with 0.5.0 are upgraded the next time a device unlocks, or from
  General → Sync → "Strengthen passphrase protection".
- New passphrases need at least 14 characters. Easily guessed ones (repeats, runs like "abcd" or "1234") are
  refused, and the page shows how strong the passphrase is while you type it.
- Removing a device can now change the account key: choose "Remove and change key" and enter your passphrase.
  The removed device can never read anything written afterwards, and you get a new recovery key (the old one
  stops working). Your other devices ask for the passphrase once.
- Synced data stored on this PC is now encrypted as well, with a key that never leaves the device. Existing
  data is encrypted automatically on the first start.
- The sync server now keeps a nightly backup (30 days) and limits repeated invite and sign-in attempts.

## [0.5.0] - 2026-09-25

### Added
- Sync across devices (General → Sync). Your Helm data is kept in step across your PCs and encrypted on the
  device before it leaves: neither the sync server nor the person who invited you can read it.
  - Start with an invite code, which creates your own account. On other devices, use a device token that you
    create under Devices.
  - The first device sets the account passphrase and shows a recovery key once. Keep that key safe: it is the
    only way back in if you forget the passphrase.
  - Shows sync status and storage used, lets you add or remove devices, and turns sync off on one device while
    keeping its data.
  - Syncs when Helm starts, every 5 minutes, and shortly after each change. Changes made offline are sent later.
- No tool uses sync yet; Notes will be the first.

## [0.5.0-preview.1] - 2026-09-25

### Added
- Sync across devices (General → Sync). Your Helm data is kept in step across your PCs and encrypted on the
  device before it leaves: neither the sync server nor the person who invited you can read it.
  - Start with an invite code, which creates your own account. On other devices, use a device token that you
    create under Devices.
  - The first device sets the account passphrase and shows a recovery key once. Keep that key safe: it is the
    only way back in if you forget the passphrase.
  - Shows sync status and storage used, lets you add or remove devices, and turns sync off on one device while
    keeping its data.
  - Syncs when Helm starts, every 5 minutes, and shortly after each change. Changes made offline are sent later.
- No tool uses sync yet. Notes will be the first; this preview is for trying the sync setup itself.

## [0.4.0] - 2026-09-25

### Removed
- The Zones tool. Helm now ships as a clean shell (Home, General, search, tray, auto-update) with no tools yet;
  new tools are planned and will arrive as updates. The Zones code remains in git history (tag v0.3.0) as the
  starting point for a redesigned window-layout tool. Existing zone files in %LOCALAPPDATA%\Helm\settings\zones
  are left untouched.
## [0.3.0] - 2026-09-25

### Changed
- Zones layouts are now fully custom: the built-in templates are gone and you draw zones directly on your
  monitors. Edges snap to other zones and to monitor edges; zones can be split in half. Existing custom grid and
  canvas layouts are converted automatically; a two-column "Layout 1" is created on a fresh install.

### Added
- Layouts across monitors: one layout can cover several monitors and a zone can straddle the boundary between them.
- Several layouts per monitor: give a layout a number and press Ctrl+Win+Alt+1…9 to switch the monitor under the
  mouse to it; the new zones and the layout name are shown briefly.
- Win+PgUp / Win+PgDn activates the previous / next window that shares the focused window's zone.
- Highlight distance setting.

### Fixed
- Editor buttons (Delete / Apply) overlapped on narrow picker panels.
## [0.2.6] - 2026-09-25

### Fixed
- The pin button added an empty row at the top of the menu and shifted every item down; it is now the last
  footer item ("Pin menu" / "Unpin menu") and also shows as an icon in the collapsed strip.
- On narrow content areas, long setting descriptions ran underneath switches, combo boxes and buttons. Setting
  rows now give the control its space first and wrap the text in what remains.
## [0.2.5] - 2026-09-25

### Added
- Auto-hiding navigation menu: it shrinks to a strip of icons and slides out over the page when you hover the
  strip or the Helm icon in the title bar, then hides again when the mouse leaves.
- Pin button at the top of the menu to keep it open next to the page.
- Drag the right edge of the open menu to change its width (220–520 px). Pin state and width are remembered.
## [0.2.4] - 2026-09-25

### Changed
- Helm now focuses on Zones: Always On Top, the Diagnostics page, the Welcome page and the empty tool groups are
  hidden (the code stays and can be re-enabled later).
- Removed the GitHub token setting; the repository is public, so update checks never need one.

### Fixed
- The title-bar and Home icons were a downscaled 256 px image and looked blurry; they now use the icon frame drawn
  for their exact pixel size.
## [0.2.3] - 2026-09-25

### Fixed
- The content background started 24 px to the right of the navigation pane, leaving a strip of pane color;
  the content area now starts right at the pane and the 24 px inset is applied inside each page.
## [0.2.2] - 2026-09-25

### Fixed
- Pages were measured wider than the window, so long tile text (e.g. an update error) pushed the Home content
  past the right edge and clipped it. Pages now always fit the window; long tile text is trimmed with a tooltip.
- Shortcuts and the taskbar kept showing the previous icon after an update or reinstall; Explorer's icon cache is
  refreshed on first run and after every update.
## [0.2.1] - 2026-09-25

### Fixed
- Crash (and system-wide input lag while Windows collected the crash dump) shortly after the first start:
  an unset window position (NaN) could not be written to general.json. Settings writes can no longer
  terminate Helm, and window placement only stores real, finite values.

### Changed
- New app icon: blue and teal windows side by side crossed by a white bar; the installer uses it on a dark tile.
  The tray uses the color icon on both light and dark taskbars.

## [0.2.0] - 2026-09-25

### Added
- Installer and automatic updates (Velopack + GitHub Releases), with Stable and Preview channels.
- General → Updates: check, download progress, release notes, "Restart to update", auto-download and auto-install toggles.
- Live Home update tile and a "Restart to update" tray item.
- Option to delete settings when Helm is uninstalled.

### Changed
- Helm is now manifested `asInvoker` and elevates itself at startup, because Velopack cannot launch
  `requireAdministrator` executables. Helm still always runs as administrator.
- Installs to `%LOCALAPPDATA%\HelmApp`, so settings in `%LOCALAPPDATA%\Helm` survive uninstall and reinstall.

## [0.1.0] - 2026-09-25

### Added
- Fluent shell modelled on PowerToys Settings: Home, General, title-bar search, tray, single instance.
- Zones: templates, custom grid/canvas layouts, per-monitor editor, Shift+drag snapping, Win+Arrow override.
- Always On Top: Win+Ctrl+T pinning with a colored border and pinned-window list.
