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

Returns `{ account: { accountId, name, createdAt }, token: { id, name, scopes, expiresAt, ... } }`. Helm calls it when a token is entered.

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

### Admin (`Authorization: Bearer <ADMIN_TOKEN>`)

- `POST /admin/accounts {name}` and `GET /admin/accounts`
- `POST /admin/accounts/:id/tokens {name, scopes?, expiresInDays?}`: the plaintext token appears only in this response.
- `GET /admin/accounts/:id/tokens`
- `DELETE /admin/accounts/:id/tokens/:tokenId`
- `POST /admin/accounts/:id/disable`: revokes every token of the account.

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

## Encryption (end-to-end)

- A 32-byte random master key per account. On each device it is stored with DPAPI (CurrentUser) in `sync\master.key`.
- The keyring (`/v1/keyring`, opaque to the server) wraps the master key twice with AES-256-GCM:
  - with a passphrase key, `PBKDF2-HMAC-SHA256(passphrase, 16-byte salt, 600 000 iterations)`;
  - with a recovery key, `HKDF-SHA256(32 random bytes, info "helm-sync/v1/recovery")`, shown once as `HELM-XXXX-…` in Crockford base32.

  The first device creates the keyring. Other devices unlock it with either secret. Changing the passphrase re-wraps the same master key and keeps the recovery key.
- The key for a collection is `HKDF-SHA256(master, salt = "", info = "helm-sync/v1/collection:<name>")`.
- Envelope: `[0x01][nonce 12][tag 16][AES-256-GCM ciphertext]`, with AAD `"helm-sync/v1|<collection>|<id>"`. The server cannot move a payload onto another record.
- Plaintext: `{"s": schemaVersion, "t": updatedAtMs, "d": deviceId, "b": bodyJson | null}`. Edit times, device ids and schema versions are therefore hidden from the server as well.
- Limits:
  - The server still learns collection names, ids, sizes and timing.
  - It can replay an older payload of the same record.
  - It cannot read or forge payloads.

## Not built yet

- Blobs: files in R2, split into chunks and encrypted per chunk, uploaded through presigned URLs. Payloads over 64 KiB will move there too.
- WebSocket change notifications (Durable Object hibernation).
- Selective sync per device, plus tombstone and blob garbage collection.
- Changing the passphrase from the UI: `SyncSetupService.ChangePassphraseAsync` exists, but no button calls it yet.
