# Helm sync protocol

How Helm keeps data in step across devices. The client side lives in `src/Helm.Core/Sync`. The server lives in `server/sync-worker`. Its record semantics must match `tests/Helm.Tests/FakeSyncServer.cs`, the executable reference the engine tests run against. `SyncServerTests` run the same engine against the real Worker.

## Design in one paragraph

Helm is local-first. Every device keeps a full replica in `%LOCALAPPDATA%\Helm\sync\helm-sync.db` (SQLite), and modules read and write only that replica. The engine syncs in the background: it pushes local edits with compare-and-set on the record version, then pulls everything newer than its cursor. The server is a **schema-agnostic store of encrypted records**. It knows `collection`, `id`, `version`, `seq`, `deleted` and an opaque payload, nothing else. A new module therefore needs no server change.

## Records on the server

| Field | Set by | Meaning |
|---|---|---|
| `collection` | client | `^[a-z0-9][a-z0-9._-]{0,63}$`, e.g. `notes`, `chat.messages`, `settings.shell` |
| `id` | client | 1–128 chars, no control characters (Helm uses ULIDs) |
| `version` | **server** | Per-record counter. The first accepted write is 1, and each later accepted write adds 1. |
| `seq` | **server** | Global counter, strictly increasing across all records of the account, assigned on every accepted write |
| `deleted` | client | Tombstone flag. Tombstones are kept so other devices learn about the deletion. |
| `payload` | client | Encrypted envelope (see Encryption) |

## Access: invites and tokens

The server is `server/sync-worker`, deployed on `https://sync.huyhung1404.com`.

There is no open sign-up.

- **Invite codes** (`helm_inv_<secret:40><checksum:6>`) are created by the admin, work once, and expire (7 days by default).
  - Redeeming one in Helm creates a new account with the invite's storage quota, plus the first device token.
  - The registry stores only `SHA-256(code)`.
  - Consuming the code and registering the account happen in one transaction.
- **Tokens** (`helm_pat_<accountId:16>_<secret:40><checksum:6>`) work like GitHub PATs.
  - The server stores only `SHA-256(token)`, with its name, scopes, optional expiry, last use and revocation.
  - Scopes are `sync:read`, `sync:write` and `tokens:manage`. The last one lets a device list, create and revoke the account's tokens.
  - A device can never grant a scope it does not have itself.
- **Checksums** are CRC32 of everything before them. They are not a security measure: they let Helm catch typos offline and let secret scanners recognise leaked codes.
- **The admin** can also create accounts and tokens directly, set quotas and disable accounts (`/admin` page or `admin.ps1`).

Tokens grant access only. Encryption is separate (see below), so neither the admin nor the server can read the data.

## Endpoints

Requests send `Authorization: Bearer <token>`. Bodies are JSON and `payload` is base64.

Errors are `{"error": code, "message"?}`:
- `401 invalid_token | token_expired` and `403 insufficient_scope`: Helm shows the Unauthorized state and keeps the local data.
- `429` and `5xx`: Helm treats these as Offline and retries later.
- Any other `4xx` is a client bug.

### `GET /v1/me`

Returns `{ account: { accountId, name, createdAt, usedBytes, quotaBytes, blobBytes }, token: { id, name, scopes, expiresAt, ... }, limits: { maxChunkBytes, maxChunksPerBlob, maxBlobBytes, maxPayloadBytes } }`. Helm calls it when a token is entered.

### `POST /v1/sync/push` (scope `sync:write`)

```json
{ "items": [ { "collection": "notes", "id": "01J…", "baseVersion": 3, "deleted": false, "payload": "AQ…" } ] }
```

The items are applied in order, in one transaction:
- If `baseVersion` equals the stored version (0 when the record does not exist), the item is stored as `version = stored + 1` and `seq = ++globalSeq`, and the outcome is `accepted: true` with the new `version` and `seq`.
- Otherwise nothing is stored for that item. The outcome is `accepted: false`, with `current` set to the stored record, or `null` if there is none.

```json
{ "outcomes": [ { "collection": "notes", "id": "01J…", "accepted": false, "version": 0, "seq": 0,
                  "current": { "collection": "notes", "id": "01J…", "version": 4, "seq": 812, "deleted": false, "payload": "AQ…" } } ] }
```

Limits:
- 100 items per push.
- 1 MiB per payload. Payloads over 64 KiB will move to R2 once blobs are added.
- 8 MiB per request body.
- An invalid item rejects the whole batch with `400` or `413`.

### `GET /v1/sync/pull?since=<seq>&limit=<n>` (scope `sync:read`)

Returns records with `seq > since`, ordered by `seq`. `limit` is 1–1000 (Helm asks for 500):

```json
{ "records": [ … ], "nextSeq": 1312, "hasMore": true }
```

`nextSeq` is the highest `seq` returned, or `since` when the page is empty. Each record appears once, in its latest state.

### `POST /v1/redeem` (no token)

`{ invite, accountName, deviceName }` → `201 { account, token }`, where `token.token` is the plaintext token and appears only here. An unknown, used, revoked or expired invite returns `400 invalid_invite`.

### `GET /v1/tokens`, `POST /v1/tokens`, `DELETE /v1/tokens/:id` (scope `tokens:manage`)

The account's devices. `POST { name, expiresInDays?, scopes? }` returns the plaintext token only in that response. Without `scopes`, the new token gets the caller's scopes.

### Storage quota

Each account has a quota (256 MB by default, or what the invite set). A push that would grow the stored payloads beyond it is rejected as a whole with `413 quota_exceeded`, and Helm shows "Storage is full". Pushes that shrink the account (deletes, smaller edits) are always accepted. `/v1/me` reports `usedBytes` and `quotaBytes`.

### `GET /v1/keyring` (scope `sync:read`), `PUT /v1/keyring` (scope `sync:write`)

The account's wrapped master key: an opaque string of at most 16 KiB, produced and read only by Helm.
- `GET` returns `{ version, data }`, or `404` before the first device uploads it.
- `PUT { baseVersion, data }` is compare-and-set: `409` with the current version if another device won the race.

### Blobs (Vault documents)

Large files, encrypted on the device: a random key per blob, AES-GCM per chunk. The server stores opaque chunks in the R2 bucket `helm-sync-blobs` (`blobs/<accountId>/<blobId>/<index>`) and their sizes in the account's Durable Object. `GET` routes need `sync:read`, the others `sync:write`. Without the bucket every blob route returns `503 blobs_disabled`.

- **Id:** a ULID, `^[0-9A-HJKMNP-TV-Z]{26}$`, chosen by the client.
- **Limits:** a chunk is 1 byte to 4 MiB + 4096. A blob has at most `maxChunksPerBlob` chunks (`ceil(MAX_BLOB_MB / 4) + 1`, 65 by default) and at most `maxBlobBytes` (that count × the chunk limit). `GET /v1/me` returns `limits: { maxChunkBytes, maxChunksPerBlob, maxBlobBytes, maxPayloadBytes }`. Helm reads them from there rather than hard-coding them.
- **States:** `pending` (uploading) → `committed` (sealed, never changes again) → optionally in the trash (`deletedAt` set) → `purging` → gone. A `purging` blob answers `404` to everything except `GET /v1/blobs/:id` and `DELETE`.
- **Quota:** a reservation adds `size` to `usedBytes`, the same counter as records, or fails with `413 quota_exceeded`. Blobs in the trash still count. The space comes back only when the blob is purged. `/v1/me` also reports `blobBytes`.
- **Record order:** Helm pushes the record that references a blob only after the blob is committed.

A blob as returned below is `{ id, size, chunkCount, state, createdAt, committedAt, deletedAt, chunks }`, where `chunks` lists the indexes uploaded so far so an upload can resume. The list route leaves `chunks` out.

| Route | Result |
|---|---|
| `POST /v1/blobs { id, size, chunkCount }` | `201` blob. The same id with the same `size` and `chunkCount` returns `200` and the current blob; different values return `409 blob_exists`. `size` is the total ciphertext, from `chunkCount` to `chunkCount × maxChunkBytes`, else `400 invalid_size`. Over the limits: `413 blob_too_large`. |
| `PUT /v1/blobs/:id/chunks/:index` | Raw `application/octet-stream` body with a `Content-Length`: `411 length_required`, `413 chunk_too_large`, `400 empty_chunk`. The body streams straight to R2. Returns `200 { id, index, size }`. Uploading an index again replaces it. `400 invalid_index` if `index ≥ chunkCount`, `400 chunk_exceeds_blob` if the chunks would hold more than `size`, `409 blob_committed`, `404 blob_not_found`. |
| `POST /v1/blobs/:id/commit` | `200` blob, once every index `0…chunkCount-1` is uploaded and the chunk sizes add up to `size`. Otherwise `409 blob_incomplete` with `missing: [indexes]`, which is `[]` when only the sizes are wrong. Committing again returns `200`. |
| `GET /v1/blobs/:id` | `200` blob, or `404 blob_not_found`. |
| `GET /v1/blobs?after=<id>&limit=<n>` | `{ blobs, hasMore, next }` ordered by id. `limit` is 1–1000 (500 by default); pass `next` as `after` for the following page. |
| `GET /v1/blobs/:id/chunks/:index` | The chunk bytes (`application/octet-stream`, `Content-Length`, `Cache-Control: no-store`). Only for committed blobs, including ones in the trash. Otherwise `404 blob_not_found` or `404 chunk_not_found`. |
| `DELETE /v1/blobs/:id` | Committed: moves the blob to the trash and returns `200` blob with `deletedAt`. Pending: deletes its chunks and releases its quota now, and returns `200 { id, state: "purged" }`. Deleting again returns `200`; an unknown id returns `404`. |
| `POST /v1/blobs/:id/restore` | Takes the blob out of the trash and returns `200` blob. `404` once it is purging or gone. |

**Garbage collection** runs with the nightly cron, independently of backups. It purges blobs that have been in the trash for more than 30 days and pending uploads created more than 7 days ago. It deletes their chunks from R2 first, then their rows, then releases their quota, so a run that fails halfway is completed by the next one. `POST /admin/blobs/gc { now? }` runs it on demand (`now` in ms lets tests look ahead) and returns `{ accounts, blobs, objects, bytes, failed }`.

**Raising limits** needs no code change. Edit the Worker variables `MAX_BLOB_MB` (256 by default) and `MAX_QUOTA_MB` (the highest quota the admin may set, 102400 by default) under Settings → Variables and Secrets, or in `[vars]` in `wrangler.toml`. An invalid value falls back to the default. Each account's quota is still set by the admin. The chunk size is part of the client format and does not change.

### Admin (`Authorization: Bearer <ADMIN_TOKEN>`)

- `POST /admin/accounts {name}` and `GET /admin/accounts`
- `POST /admin/accounts/:id/tokens {name, scopes?, expiresInDays?}`: the plaintext token appears only in this response.
- `GET /admin/accounts/:id/tokens`
- `DELETE /admin/accounts/:id/tokens/:tokenId`
- `POST /admin/accounts/:id/disable`: revokes every token of the account.
- `POST /admin/blobs/gc { now? }`: runs blob garbage collection now.

## Client behavior

- **Local write:** the row becomes `dirty` and its `local_rev` is bumped. `version` stays at the server version the edit was made on.
- **Push:** send the dirty rows with `baseVersion = version`.
  - When accepted, set `version = new` and clear `dirty`, but only if `local_rev` did not change during the push. An edit typed during a push is therefore never lost.
- **Rejected push, or a pull that meets a dirty row:** resolve with the collection's policy.
  - `LastWriterWins`: the later edit time wins, and on a tie the higher device id wins. If local wins, the row is rebased onto the server version and pushed again.
  - `KeepBoth`: the server version keeps the id, and the local edit is saved under a new id as a conflict copy. An edit beats a deletion.
  - `RemoteWins`: used by append-only logs.
  - A record written by a **newer schema** always wins and is never overwritten. Older clients hide it and refuse to edit it.
- **Pull:** apply records newer than the local version, then store the cursor in the same transaction. Conflicts found while pulling are pushed again within the same run.
- **Failures:** network errors leave the data untouched and set the state to Offline. A payload that cannot be decrypted is logged and skipped.
- **Deletion guard** (collections with `GuardDeletions`, e.g. `vault.items`): pages that delete live records which are not expendable (not in a trash) are collected until the end of the pull and judged together. At least `clamp(ceil(20 % of live records), 3, 10)` such deletions stop the run in state `Held` without applying any of those pages or moving the cursor. The user then applies them (`ApproveHeldAsync`) or keeps the local records (`RejectHeldAsync`), which re-uploads each one on top of the deletion.
- **Blobs** (`src/Helm.Core/Sync/Blobs`):
  - Import encrypts the file at once into local chunks (`sync/blobs/<id>/<n>.chunk`): 4 MiB of plaintext each, the last one padded to a multiple of 64 KiB, sealed as `[nonce 12][tag 16][AES-256-GCM]` with a random per-file key and AAD `helm-blob/v1|<id>|<index>`. The key, size and chunk count live only in the `BlobRef` inside the (encrypted) record that uses the file.
  - A run uploads pending blobs first (reserve → missing chunks → commit; an interrupted upload resumes from the chunks the server lists). A record whose `BlobReferences` include a pending blob is not pushed until the blob is committed, so no device ever sees a record whose file is missing. A full quota stops only those records; the run ends as `QuotaExceeded`.
  - Reads use the local chunks and download missing ones (kept as a cache). Every chunk is authenticated; a swapped, truncated or foreign chunk fails.
  - Clean-up runs at most once a day after a complete run: local chunks of committed blobs nothing references are deleted; on the server, unreferenced blobs older than 7 days are soft-deleted (purged by the server 30 days later) and referenced ones that were soft-deleted are restored. It is skipped entirely when the replica holds a collection this build does not know, since its records might reference blobs.

## Encryption (end-to-end)

### Keys and epochs

- Each account has a 32-byte random master key per **epoch**. The epoch starts at 1 and increases each time the key is rotated.
- A device stores every epoch key it knows (a `SyncKeySet`) with DPAPI (CurrentUser) in `sync\master.key`. Helm 0.5.0 stored one bare 32-byte key there; it is read as epoch 1.
- The key for a collection is `HKDF-SHA256(master of the epoch, salt = "", info = "helm-sync/v1/collection:<name>")`.

### The keyring (`/v1/keyring`, opaque to the server)

Keyring v2 is `{v: 2, epoch, kdf, pass, rec, prev}`. It holds the current master key wrapped twice with AES-256-GCM, plus every older master key:

| Slot | Wraps | With |
|---|---|---|
| `pass` | the current master key | `Argon2id(passphrase, 16-byte salt, 128 MiB, 3 passes, 4 lanes)` |
| `rec` | the current master key | `HKDF-SHA256(32 random bytes, info "helm-sync/v1/recovery")`, shown once as `HELM-XXXX-…` in Crockford base32 |
| `prev` | each older epoch key | the current master key |

- AAD names the slot and the epoch. Epoch 1 keeps the exact AAD strings of Helm 0.5.0.
- **Helm 0.5.0 keyrings** (v1: PBKDF2-HMAC-SHA256, 600 000 iterations) still open. Unlocking with the passphrase re-wraps the passphrase slot with Argon2id in place (compare-and-set). The Sync page also offers "Strengthen passphrase protection".
- **New passphrases** need at least 14 characters. The strength estimate must reach 60 bits: the lower of character pool × non-patterned length and a cracker-style estimate that counts common words (English, names and Vietnamese), leetspeak, years, keyboard walks and repeated blocks as a few bits each. So repeats, runs such as `abcd` or `1234`, "Password@2026!!" and short single-class passphrases are refused.
- **Rotation** (removing a device and changing the key):
  1. Check the passphrase, revoke the device's token, and sync.
  2. Create a new master key for `epoch + 1`, wrapped by the same passphrase and a **new** recovery key. The old recovery key stops working. Every older key is re-wrapped under the new one.
  3. Mark every local record for re-sealing (`reseal = 1`) and push. A re-seal loses every conflict, so it never creates a conflict copy.
- **Other devices after a rotation:** they meet a payload from a newer epoch, stop the run without skipping it or advancing the cursor, forget their key, and ask for the passphrase once (state `KeyChanged`). The removed device keeps only old keys and has no token.

### Payload envelope

- **v2:** `[0x02][epoch, uint32 BE][nonce 12][tag 16][AES-256-GCM ciphertext]`, with AAD `"helm-sync/v2|<epoch>|<collection>|<id>"`.
- **v1 (Helm 0.5.0):** `[0x01][nonce 12][tag 16][ciphertext]`, with AAD `"helm-sync/v1|<collection>|<id>"`, always epoch 1. It is still read.
- The server cannot move a payload onto another record.
- Plaintext: `{"s": schemaVersion, "t": updatedAtMs, "d": deviceId, "b": bodyJson | null}`. Edit times, device ids and schema versions are therefore hidden from the server as well.
- **Limits:**
  - The server still learns collection names, ids, sizes and timing.
  - It can replay an older payload of the same record.
  - It cannot read or forge payloads.

### At rest on the device

- `helm-sync.db` stores record bodies encrypted with a separate **device-local key**: 32 random bytes in `sync\local.key` under DPAPI, AES-256-GCM, AAD `"helm-local/v1|<collection>|<id>"`.
- `secure_delete` is on, so overwritten and deleted content is zeroed.
- A Helm 0.5.0 replica (plaintext, schema 1) is encrypted in place on first open, then checkpointed and vacuumed.
- If the local key is lost, the unreadable file is moved aside (`helm-sync.db.unreadable-<time>`) and the replica is rebuilt from the server.
- This protects a copied or backed-up database file. It does not protect against malware running as the same Windows user.

## Server protections

- **Rate limits** (Workers rate limiting, per client IP): `/v1/redeem` 20 per minute, and failed admin sign-ins 10 per minute. A correct admin token is never throttled.
- **Backups:** a cron trigger runs every night at 19:00 UTC (02:00 in Vietnam).
  - It writes `accounts/<id>/<YYYY-MM-DD>.json.gz` (ciphertext, keyring, seq and committed blob metadata; no tokens, no chunks) and `registry/<date>.json.gz` to the R2 bucket `helm-sync-backups`, and keeps 30 days.
  - `POST /admin/backups/run` runs a backup on demand.
  - `POST /admin/accounts/:id/restore {key, confirm: <accountId>}` restores one. Every restored record gets a version above both the current one and the backed-up one, plus a new seq, so every device pulls it. Records created after the backup are kept, and so are tokens and a newer keyring. Blob rows missing locally are recreated (the chunks stay in R2 until purged); existing blobs are not touched. Snapshots from before blobs still restore.

## Not built yet

- Blobs: chunks pass through the Worker rather than presigned R2 URLs, and payloads over 64 KiB still live in records. Orphaned R2 objects left by an upload racing a delete are not swept.
- WebSocket change notifications (Durable Object hibernation).
- Selective sync per device, plus tombstone garbage collection.
- An edit made on another device while a rotation re-encrypts everything may come back as a conflict copy. The KeepBoth policy guarantees that no data is lost.
