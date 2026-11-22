# Vault design

Vault stores passwords, secure notes, cards, identities and documents on PC and Android. It is built on the sync
platform (docs/sync-protocol.md), with its own password. This file records the decisions and the invariants every
change must keep. The byte-level formats are in [vault-format.md](vault-format.md).

## Decisions

| Topic | Decision |
|---|---|
| Platforms | PC + Android, module id `vault`, class prefix `Vault`, group `Security & Servers`, icon `LockClosed` |
| Password | A **vault password**, separate from the sync passphrase. At least 14 characters, with the same strength check as sync (`SyncKeyVault.ValidateNewPassphrase`) |
| Recovery | A vault recovery key (`HELMV-…`), shown once and saved in an Emergency Kit. Forgetting the password *and* losing the kit is unrecoverable, by design |
| Quick unlock | Windows Hello / Android biometric, per device. The password is required again every 14 days (configurable) and after 5 failed quick unlocks |
| Documents | Encrypted blobs in R2 (sync platform). Nothing is hard-coded: the account quota is set per account by the admin (/admin), the per-file ceiling and the quota ceiling are Worker variables (`MAX_BLOB_MB`, default 256; `MAX_QUOTA_MB`), and Helm reads the effective limits from `/v1/me` |
| Backup | Automatic encrypted backup repository in a folder the user picks (Google Drive for desktop, OneDrive, USB; Storage Access Framework on Android), verified after every run; KDBX 4 export of passwords and notes; `helm-vault-restore` standalone tool |
| Not now | Quick access hotkey (not wanted), TOTP, SMS (never), email alerts and device approval, trusted-device recovery, security keys |

## Key hierarchy

```
vault password ──Argon2id(128 MiB, t3, p4, 16-byte salt)──► KEK_pw  ─┐
recovery key (32 random bytes) ──HKDF "helm-vault/v1/recovery"──► KEK_rec ─┼─ AES-256-GCM wrap ─► VaultKey (32 random bytes)
device key (Hello / Keystore, device-local only) ──────────► KEK_dev ─┘
VaultKey ─HKDF "helm-vault/v1/items"──► item key: encrypts each item
         ─HKDF "helm-vault/v1/backup"─► backup key: encrypts backup snapshots
each document: FileKey (32 random bytes) stored inside its item, so only the VaultKey can reach it
```

- Changing the password or the recovery key re-wraps the VaultKey; nothing else is re-encrypted.
- Locked means the VaultKey is not in memory. The local replica then holds only ciphertext under the VaultKey, in
  addition to the replica's own device key, so malware running as the same user cannot read a locked vault.
- On the server, items are encrypted twice (item key, then the sync envelope). Blobs are encrypted once, with
  their FileKey, which the server never sees.

## Storage

| Collection | Policy | Content |
|---|---|---|
| `vault.keyring` | LastWriterWins | One record `keyring`: KDF parameters, the password slot and the recovery slot (with a random `recoveryId` printed on the Emergency Kit) |
| `vault.items` | KeepBoth | One record per item version stream: `{v, uid, epoch, trashed, blobs[], data}`. `data` is the AES-GCM sealed item, AAD `helm-vault/v1|item|<uid>|<epoch>` |

- `uid` identifies the item, so a KeepBoth conflict copy (a second record with the same `uid`) is shown as a
  conflict on that item, never silently merged.
- `trashed` and `blobs` sit outside the item encryption (still inside the sync envelope and the replica
  encryption), so the engine can see them while the vault is locked. It uses them for the mass-change guard and
  for blob upload ordering and garbage collection.
- The item plaintext holds the fields, notes, attachments (blob ref + FileKey + name + type) and the **last 10
  versions** (history). Old versions keep their attachments alive.

## Invariants

| # | Invariant | Enforced by |
|---|---|---|
| I1 | An item is never removed except by emptying it from the trash (kept 30 days) | `VaultStore` (no delete API besides `Purge` on trashed items) |
| I2 | Every edit moves the previous version into the history (last 10) | `VaultStore.Save` |
| I3 | Concurrent edits are both kept | `vault.items` is KeepBoth; conflicts are shown per `uid` |
| I4 | A record from a newer schema is never overwritten | Sync engine (existing) |
| I5 | A blob referenced by any item, history entry or trashed item is never deleted | Blob GC (references from all live records), plus 30-day soft delete on the server |
| I6 | An item is pushed only after every blob it references is committed on the server | Engine: records whose blobs are not committed stay dirty |
| I7 | A backup counts as good only after it was read back, decrypted and matched (item count and digest) | `VaultBackupService` |
| I8 | A new password or recovery key takes effect only after the new keyring was opened with it | `VaultKeyring` |
| I9 | A pull that deletes vault records that are not in the trash — at least 20 % of the live records, but at least 3 and at most 10 — is held for the user, who applies it or restores the records everywhere. Pages are judged together, so deletions cannot be spread thin | Engine `SyncChangeGuard` |
| I10 | Decrypted content never reaches the disk, except "Open with…" into the app's private temp folder, wiped on lock and on start | Attachment viewer |

## Leak protection

- Auto-lock after inactivity (default 5 min), on Windows session lock / sleep, and on Android 30 s after the app
  goes to the background. Locking zeroes the VaultKey and drops decrypted items.
- Clipboard: cleared after 30 s if it still holds what Vault put there. Windows: the clipboard history and cloud
  clipboard exclusion formats. Android 13+: `ClipDescription.EXTRA_IS_SENSITIVE`.
- Screen capture: `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` on Windows; `FLAG_SECURE` on Android.
- Secret types print `***`; tests make sure logs never contain item content.
- Typed passwords: the boxes of the lock, setup and settings screens empty (and stop showing) when the password is
  used, when the vault locks, and when the page is left, hidden or minimized; secrets shown on items are hidden again
  (S14).
- Limits (documented, not solved): malware while the vault is unlocked; .NET strings cannot be wiped.

## Screens

Shared view models (`src/Helm.Modules.Vault.Core/ViewModels`), one layout per platform:

| Part | View model | PC (`VaultView`) | Android (`VaultContentPage`) |
|---|---|---|---|
| Sections: All, Favorites, Logins, 2FA & tokens, Cards, Identities, Notes, Documents, one per tag, Trash | `ItemsViewModel.Sections` / `TagSections` / `TrashSection` | Left column; icons only from 720 to 1000 px; chips above the list below 720 px | A row of chips to swipe |
| The list of the section, with search | `Cards`, `Search` (within the section; `SearchEverywhere` when nothing matches) | Middle column; the first item is selected (`AutoSelectFirst`) | Rows; a tap opens the item in their place |
| The selected item, read only | `SelectedCard` → `Detail` | Right column (Back below 720 px) | In place of the list, Back returns |
| New / Edit | `Editor` (a separate `ItemDetailViewModel`, so the detail does not change while editing) | Pop-up over the vault, inside the window (screen-capture exclusion covers it) | Full screen; Back = Cancel |
| Status | `VaultAppViewModel.LockCountdown`, `BackupTitle` / `BackupDetail` / `BackupOk` | Next to the title; bottom of the left column | Next to the icon |

"2FA & tokens" lists tokens and every item with a two-factor code; "Documents" lists documents and every item with
a file. Closing the editor with changes asks first (`ItemDetailViewModel.HasChanges`). Locking closes the editor and
drops the detail and the list (`ItemsViewModel.Clear`).

## Phases

P0 Android module infrastructure (`Helm.Core.Android`), P1 Vault core, P2 blobs (client + Worker), P3 backup
repository, restore tool and KDBX export, P4 PC UI, P5 Android UI, P6 later items. Every phase builds with
`-warnaserror` and passes the tests before the next starts.

## Security review (2026-09-28)

An attacker's pass over the vault, with each attack written as a test first (tests/Helm.Tests/VaultAttackTests.cs,
VaultLogLeakTests.cs, VaultBackupTests.cs, VaultCryptoTests.cs) and then fixed.

| # | Attack | Before | Fix |
|---|---|---|---|
| S1 | Steal the backup folder (or the replica) and crack the vault password offline: the only lock left | "Password@2026!!", "HelmVault2026!!", "Nguyen@1990Hung!" passed as strong | A zxcvbn-style estimate (common and Vietnamese words, leetspeak, years, keyboard walks, repeats), also for the sync passphrase. Argon2id 128 MiB costs ~0.3 s per guess on a PC |
| S2 | Hide deletions between thousands of filler records so each batch stays under the guard | Two batches of 3 slipped through (threshold 6) | Deletions of the last 24 h count together (stored in the replica), across batches and runs |
| S3 | Trickle deletions, two per sync, until the vault is empty | All 10 applied | Same rolling 24 h count; a decision (apply or keep) resets it |
| S4 | Tamper the keyring's Argon2 costs down, so the next password change is wrapped weakly | Costs were copied | A password change never goes below today's default |
| S5 | Read an opened document's text from the Windows Search index after it was deleted | vault-open was indexable | The folder and files are "not content indexed" (and hidden); files are marked before any byte is written |
| S6 | A chunk in the backup rots (disk, sync client), found only at restore time | Only existence was checked | Each backup decrypts a random 64 MiB of older chunks and rewrites damaged ones from the device or server |
| S7 | Leftover temp files from a crash in the backup folder | Kept forever | Removed after a day |
| S8 | Read secrets from the log files (they can be shared) | — | Verified: the whole lifecycle logs no password, recovery key, field, title, note or file name (a planted leak is caught) |

Checked and holding: a hostile server cannot read, forge, reorder or move records or chunks (AAD binds collection,
id, uid, epoch, trash state, blob list and chunk index); renaming an old snapshot to look newer fails; a planted
backup keyring does not open with the user's password; the recovery id reveals nothing about the key.

## Security review (2026-10-05)

A second pass, from an attacker who can change the device's files (tests in tests/Helm.Tests/VaultStoreTests.cs).

| # | Attack | Before | Fix |
|---|---|---|---|
| S9 | Reset the wrong-password backoff by editing or deleting settings/vault-device.json | The count and its time were plain JSON | The count lives in memory while Helm runs and in settings/vault/throttle.bin under `ISecretProtector` (DPAPI / Android Keystore). A file gone while a vault exists, or unreadable, counts as a wrong password just now (one 2 s wait; also once after updating, on a new device or after a settings reset). The time is held in memory, so a file that cannot be written never locks the user out, and a time in the future (a wrong clock) starts the wait now |
| S10 | Read or swap the Android quick-unlock file | biometric.bin was plain JSON (vault id, IV, wrapped key) | Protected like hello.bin on Windows; an older file no longer opens, so quick unlock turns off once |
| S11 | A fake app installed from a file takes a real app's package name and gets its login from autofill | Apps were matched by package name only | A picked login remembers the app as `androidapp://package#sha256` of its signing certificate (`AppSigning`; a launcher `<queries>` makes apps visible on Android 11+). Only the same signature is offered in one tap; a certificate that cannot be read matches nothing; logins remembered without one are in the picker only, and picking one there records it (tests in VaultAutofillTests.cs) |

| S12 | Windows Hello quick unlock: the wrapping key was SHA-256(purpose, signature) | Unsalted, unlike every other key here | HKDF-SHA256 of the signature with a random salt in hello.bin; older files still open and are rewritten salted at the next quick unlock, with no extra prompt (WindowsHelloUnlockTests.cs) |
| S13 | Find the Emergency Kit saved as a file (Drive, Downloads, a PDF) and open the vault with the recovery key | The kit said to keep the page offline, nothing about files | Android asks before saving and says to print it or move it to a USB stick, then delete the file; the kit itself says the same |

Known limits, not fixed in code:
- Decrypted items (passwords, notes, two-factor keys) are .NET strings while the vault is unlocked. Strings cannot be
  wiped, so after locking they stay in memory until the garbage collector reuses it. Zeroing the TOTP secret's byte
  array would not help: the same key stays in the item's field text. Reading it needs access to Helm's memory, which
  also reveals the unlocked vault itself.
- The autofill one-tap list trusts the first certificate seen for an app picked by hand: if a fake app is the first
  one picked for a package, its certificate is the one remembered (S11).

The backoff only slows guessing on the device itself. Whoever copies the replica or a backup guesses offline with no
backoff at all: there the strength of the vault password and Argon2id are the lock (S1).

Residual risks (documented, not fixed):
- Malware running as the user while the vault is unlocked can read it (memory, screen); .NET strings cannot be wiped.
- A compromised device that holds the sync key and token can overwrite records with garbage, which the guard cannot
  judge; the vault shows such records as unreadable, and the verified backups are the way back.
- Windows Hello keys of unpackaged apps share one namespace: another program of the same user could ask Windows Hello
  to sign for "Helm Vault", which still needs the user to approve the prompt.
- Documents opened in another app may be cached by that app; printing the Emergency Kit passes through the spooler.
- The sync server learns how many vault records exist and their approximate sizes (chunks are padded to 64 KiB).

## Security review (2026-11-22)

A third pass, after SSH started signing in with passwords kept in Vault (`IVaultSecrets`), from someone at the
user's PC and from a copy of the backup folder (tests in tests/Helm.Tests/VaultViewsTests.cs and VaultViewModelTests.cs).

| # | Attack | Before | Fix |
|---|---|---|---|
| S14 | Sit at the PC after the user typed the vault password with "show password" on, and open Vault | A WPF-UI password box that shows its text ignores changes from outside, so clearing the password after an unlock left it in the box: the next lock screen showed it, readable. A password typed but not used also stayed when the page was left | `PageLeaveGuard`: a shown box is hidden before the view model's value is put into it; the lock, setup and settings pages forget typed passwords and hide shown secrets when left, hidden (tray) or minimized, and on lock. Android: forgotten when the page is left |
| S15 | Copy the backup folder (OneDrive) and guess a vault password chosen before the strength check (S1) | Nothing told the user their password was below today's rule | After a password unlock, a password the check would refuse today shows a warning with "Change the password…"; in memory only, until it is changed |
| S16 | (Usability with a security edge) Press Unlock with nothing typed | Argon2 threw a technical error ("Something went wrong") | "Type the vault password.", before the backoff and without counting as a wrong password |

Checked and holding: AI agents never reach Vault secrets through SSH (`ssh_exec` and the menu tools only use a
connection the user opened in Helm; Helm never connects for an agent, and a Vault password is only sent to a server
whose host key is trusted); MCP, the connector and automatic AI jobs still never see Vault; pairing a device by code
moves the sync key only, not the vault key.
