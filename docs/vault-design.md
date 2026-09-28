# Vault design

Vault stores passwords, secure notes, cards, identities and documents on PC and Android. It is built on the sync
platform (docs/sync-protocol.md), with its own password. This file records the decisions and the invariants every
change must keep. The byte-level formats are in [vault-format.md](vault-format.md).

## Decisions

| Topic | Decision |
|---|---|
| Platforms | PC + Android, module id `vault`, class prefix `Vault`, group `Advanced`, icon `LockClosed` |
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
- Limits (documented, not solved): malware while the vault is unlocked; .NET strings cannot be wiped.

## Phases

P0 Android module infrastructure (`Helm.Core.Android`), P1 Vault core, P2 blobs (client + Worker), P3 backup
repository, restore tool and KDBX export, P4 PC UI, P5 Android UI, P6 later items. Every phase builds with
`-warnaserror` and passes the tests before the next starts.
