# Vault format

This is the byte-level format of Helm Vault: enough to read a vault or a backup without Helm, which is why it is
written down. The design and its guarantees are in [vault-design.md](vault-design.md). The code is
`src/Helm.Modules.Vault.Core`, blobs are in `src/Helm.Core/Sync/Blobs`, and `src/Helm.VaultRestore` is a reader that
uses only this.

## Primitives

| Name | Definition |
|---|---|
| `Seal(k, p, aad)` | AES-256-GCM with a random 12-byte nonce. Output `[nonce 12][tag 16][ciphertext]`. AAD is the UTF-8 string given |
| `Derive(k, info)` | HKDF-SHA256, 32 bytes, empty salt, UTF-8 `info` |
| `PasswordKey(pw, kdf)` | Argon2id, v1.3, 32 bytes, over the UTF-8 bytes of the password in Unicode NFC, with the keyring's salt (16 bytes), memory (KiB), passes and lanes. The default is 128 MiB, 3 passes, 4 lanes |
| JSON | UTF-8, camelCase property names, byte arrays as base64 |

## Keyring

The keyring is stored in collection `vault.keyring`, record `keyring-<vaultId>`, and in backups as `keyring.json`.

```json
{ "v": 1, "vaultId": "01J…", "epoch": 1, "createdAtMs": 0, "changedAtMs": 0,
  "kdf": { "alg": "argon2id", "memoryKib": 131072, "passes": 3, "lanes": 4, "salt": "…" },
  "pass": "…", "rec": "…", "recoveryId": "7F3A-91C2", "keyCheck": "…" }
```

- `pass = Seal(PasswordKey(password, kdf), vaultKey, "helm-vault/v1/keyring|pass|<vaultId>|<epoch>")`
- `rec = Seal(Derive(recoverySecret, "helm-vault/v1/recovery"), vaultKey, "helm-vault/v1/keyring|rec|<vaultId>|<epoch>")`
- `keyCheck = Derive(vaultKey, "helm-vault/v1/key-check")[0..16]`

### Recovery key

The text is `HELMV-` followed by 56 Crockford base32 characters in groups of four:
- 52 characters encode the 32-byte `recoverySecret`. The 4 trailing padding bits are zero.
- The last 4 characters are a checksum: the first 4 characters of the base32 form of `SHA-256(secret)[0..3]`.

Readers accept lower case, spaces and dashes. They read `O` as 0 and `I` and `L` as 1.

The recovery id is the first 8 base32 characters of `SHA-256("helm-vault/v1/recovery-id|" + secret)`, written as `XXXX-XXXX`.

## Items

Items are stored in collection `vault.items`, one record per version stream. The body JSON is:

```json
{ "v": 1, "vault": "<vaultId>", "uid": "01J…", "epoch": 1, "trashed": false, "trashedAtMs": null,
  "blobs": ["01J…"], "data": "…" }
```

- `data = Seal(Derive(vaultKey, "helm-vault/v1/items"), itemJson, aad)`.
- `aad = "helm-vault/v1|item|<vault>|<uid>|<epoch>|<1 or 0 (trashed)>|<trashedAtMs or 0>|<hex SHA-256 of the sorted blob ids joined by \n>"`.
- `blobs` must equal the blob ids used by the item's attachments and history attachments.
- A second record with the same `uid` is a conflict copy of that item.

The item JSON has these fields:
- `kind`: one of `login`, `note`, `card`, `identity`, `document`.
- `title`.
- `fields`: a list of `{ name, value, kind }`.
- `notes`, `tags`, `favorite`.
- `attachments`: a list of `{ id, name, mediaType, size, blob }`.
- `createdAtMs`, `modifiedAtMs`.
- `history`: the last 10 versions as `{ savedAtMs, item }`, newest first.

Each `blob` is `{ id, size, chunkSize, chunkCount, key }`:
- `size` is the plaintext length.
- `key` is the file's own random 32-byte key.

## Blobs (documents)

Chunk `i` of blob `id` is stored as:

```
Seal(key, plaintext[i*chunkSize .. (i+1)*chunkSize), "helm-blob/v1|<id>|<i>")
```

- `chunkSize` is 4 MiB.
- The last chunk is padded with zeros up to a multiple of 64 KiB.
- A reader decrypts the chunks in order and keeps the first `size` bytes.

The same bytes appear in three places:
- on the server, at `blobs/<accountId>/<id>/<i>`;
- in the device cache, at `sync/blobs/<id>/<i>.chunk`;
- in backups, at `blobs/<id>/<i>.chunk`.

## Backup repository

```
<folder>/HelmVault-<vaultId>/
  README.txt
  keyring.json
  snapshots/<yyyyMMddTHHmmssfffZ>.hvs
  blobs/<blobId>/<i>.chunk
```

A snapshot file is:

```
Seal(Derive(vaultKey, "helm-vault/v1/backup"), snapshotJson, "helm-vault/v1/snapshot|<vaultId>|<file name>")
```

`snapshotJson` is:

```json
{ "v": 1, "vaultId": "…", "createdAtMs": 0, "device": "…",
  "records": [ { "id": "<record id>", "record": { …item record as above… } } ],
  "blobs": [ { "id": "…", "chunkCount": 2 } ], "digest": "…" }
```

- `digest` is the hex SHA-256 over the records sorted by id. For each record it hashes `id + "\n"`, then the `data` bytes, then `"\n1\n"` if the record is trashed or `"\n0\n"` if not.
- The file name is part of the AAD, so renaming an older snapshot to look newer makes it fail to open.

To restore:
1. Unwrap the vault key from `keyring.json` with the password or the recovery key.
2. Open the newest snapshot.
3. Open each record with the item key.
4. Decrypt the attachments from `blobs/`.

## KDBX export

Helm writes KDBX 4.1:
- Argon2id KDF: 64 MiB, 3 iterations, 2 lanes, 32-byte salt.
- AES-256-CBC, gzip, the HMAC-SHA256 block stream, and ChaCha20 as the inner stream.

The content maps as follows:
- One group per kind of item.
- For logins: the first username, password and URL become `UserName`, `Password` and `URL`.
- Every other field becomes a custom string. Secrets and passwords are protected strings.
- Documents become attachments, and the history becomes the entry history.

The tests open every export with `keepassxc-cli`.
