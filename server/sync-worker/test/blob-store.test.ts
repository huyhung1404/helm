// Blob metadata, quota and garbage collection. R2 itself is exercised by the wrangler dev smoke test.

import assert from "node:assert/strict";
import { test } from "node:test";
import { AccountStore, LIMITS, limitsFromEnv } from "../src/account-store.ts";
import { BLOB_LIMITS, CHUNK_BYTES, blobKey, blobLimits } from "../src/blob-store.ts";
import { HttpError, toBase64 } from "../src/sql.ts";
import { hashToken, newAccountId } from "../src/token.ts";
import { nodeSql } from "./node-sql.ts";

const DAY = 86_400_000;
const MB = 1024 * 1024;

// MAX_BLOB_MB = 8: three chunks per blob, so the limits are easy to reach.
function setup(quotaMb = 64) {
  let now = 1_800_000_000_000;
  const store = new AccountStore(nodeSql(), () => now, limitsFromEnv({ MAX_BLOB_MB: "8" }));
  const accountId = newAccountId();
  store.init(accountId, "Anh", quotaMb);
  return { store, accountId, blobs: store.blobs, advance: (ms: number) => (now += ms), now: () => now };
}

const ID_A = "01J9Z3K4M5N6P7Q8R9S0T1V2W3";
const ID_B = "01J9Z3K4M5N6P7Q8R9S0T1V2W4";
const ID_C = "01J9Z3K4M5N6P7Q8R9S0T1V2W5";

function rejects(fn: () => unknown, status: number, code: string) {
  assert.throws(fn, (e: unknown) => e instanceof HttpError && e.status === status && e.code === code);
}

/** Reserves, uploads and commits a blob of chunks with the given sizes. */
function upload(blobs: AccountStore["blobs"], id: string, sizes: number[]) {
  blobs.reserve({ id, size: sizes.reduce((a, b) => a + b, 0), chunkCount: sizes.length });
  sizes.forEach((size, index) => {
    blobs.authorizeChunk(id, index, size);
    blobs.recordChunk(id, index, size);
  });
  return blobs.commit(id);
}

test("limits come from the Worker vars, with defaults for anything missing or invalid", () => {
  const defaults = limitsFromEnv({});
  assert.equal(defaults.maxChunkBytes, 4 * MB + 4096);
  assert.equal(defaults.maxChunksPerBlob, 65);
  assert.equal(defaults.maxBlobBytes, 65 * CHUNK_BYTES);
  assert.equal(defaults.maxQuotaMb, LIMITS.maxQuotaMb);
  assert.deepEqual(limitsFromEnv({ MAX_BLOB_MB: "abc", MAX_QUOTA_MB: "-5" }), defaults);
  assert.deepEqual(limitsFromEnv({ MAX_BLOB_MB: "0", MAX_QUOTA_MB: String(LIMITS.quotaMbCeiling + 1) }), defaults);
  assert.equal(limitsFromEnv({ MAX_BLOB_MB: "1024" }).maxChunksPerBlob, 257);
  assert.equal(limitsFromEnv({ MAX_BLOB_MB: 10 }).maxChunksPerBlob, 4);
  assert.equal(limitsFromEnv({ MAX_QUOTA_MB: "500000" }).maxQuotaMb, 500000);
  assert.deepEqual(blobLimits(8), { maxChunkBytes: CHUNK_BYTES, maxChunksPerBlob: 3, maxBlobBytes: 3 * CHUNK_BYTES });
  assert.equal(LIMITS.maxChunksPerBlob, 65);
});

test("the admin quota ceiling follows MAX_QUOTA_MB", () => {
  const store = new AccountStore(nodeSql(), Date.now, limitsFromEnv({ MAX_QUOTA_MB: "10" }));
  rejects(() => store.init(newAccountId(), "x", 11), 400, "invalid_quota");
  store.init(newAccountId(), "x", 10);
  rejects(() => store.setQuota(11), 400, "invalid_quota");
});

test("reserve takes quota, and a reservation that does not fit is refused", () => {
  const { store, blobs } = setup(1);
  const created = blobs.reserve({ id: ID_A, size: 600 * 1024, chunkCount: 1 });
  assert.equal(created.created, true);
  assert.deepEqual({ ...created.blob, createdAt: 0 }, {
    id: ID_A, size: 600 * 1024, chunkCount: 1, state: "pending", createdAt: 0, committedAt: null, deletedAt: null, chunks: [],
  });
  assert.equal(store.info().usedBytes, 600 * 1024);
  assert.equal(store.info().blobBytes, 600 * 1024);

  rejects(() => blobs.reserve({ id: ID_B, size: 500 * 1024, chunkCount: 1 }), 413, "quota_exceeded");
  assert.equal(store.info().usedBytes, 600 * 1024);
  assert.throws(() => blobs.get(ID_B), (e: unknown) => e instanceof HttpError && e.code === "blob_not_found");

  // Records and blobs share the quota.
  const records = toBase64(new Uint8Array(500 * 1024));
  rejects(() => store.push({ items: [{ collection: "vault", id: "x", baseVersion: 0, deleted: false, payload: records }] }), 413, "quota_exceeded");
});

test("reserve is idempotent for the same shape and refuses a different one", () => {
  const { store, blobs } = setup();
  assert.equal(blobs.reserve({ id: ID_A, size: 100, chunkCount: 2 }).created, true);
  const again = blobs.reserve({ id: ID_A, size: 100, chunkCount: 2 });
  assert.equal(again.created, false);
  assert.equal(again.blob.state, "pending");
  assert.equal(store.info().usedBytes, 100, "counted once");
  rejects(() => blobs.reserve({ id: ID_A, size: 101, chunkCount: 2 }), 409, "blob_exists");
  rejects(() => blobs.reserve({ id: ID_A, size: 100, chunkCount: 1 }), 409, "blob_exists");

  upload(blobs, ID_B, [10]);
  assert.equal(blobs.reserve({ id: ID_B, size: 10, chunkCount: 1 }).blob.state, "committed");
});

test("reserve validates the id, the size and the chunk count", () => {
  const { blobs } = setup();
  rejects(() => blobs.reserve(null), 400, "invalid_body");
  rejects(() => blobs.reserve({ id: "01j9z3k4m5n6p7q8r9s0t1v2w3", size: 1, chunkCount: 1 }), 400, "invalid_blob_id");
  rejects(() => blobs.reserve({ id: "01J9Z3K4M5N6P7Q8R9S0T1V2WU", size: 1, chunkCount: 1 }), 400, "invalid_blob_id");
  rejects(() => blobs.reserve({ id: ID_A.slice(1), size: 1, chunkCount: 1 }), 400, "invalid_blob_id");
  rejects(() => blobs.reserve({ id: ID_A, size: 0, chunkCount: 1 }), 400, "invalid_size");
  rejects(() => blobs.reserve({ id: ID_A, size: 1.5, chunkCount: 1 }), 400, "invalid_size");
  rejects(() => blobs.reserve({ id: ID_A, size: 10, chunkCount: 0 }), 400, "invalid_chunk_count");
  rejects(() => blobs.reserve({ id: ID_A, size: 1, chunkCount: 2 }), 400, "invalid_size");
  rejects(() => blobs.reserve({ id: ID_A, size: CHUNK_BYTES + 1, chunkCount: 1 }), 400, "invalid_size");
  rejects(() => blobs.reserve({ id: ID_A, size: 4, chunkCount: 4 }), 413, "blob_too_large");
  rejects(() => blobs.reserve({ id: ID_A, size: 3 * CHUNK_BYTES + 1, chunkCount: 3 }), 413, "blob_too_large");
  assert.equal(blobs.reserve({ id: ID_A, size: 3 * CHUNK_BYTES, chunkCount: 3 }).created, true);
});

test("chunks are authorized against the reservation and recorded, re-uploads replace", () => {
  const { blobs } = setup();
  blobs.reserve({ id: ID_A, size: 100, chunkCount: 2 });
  blobs.authorizeChunk(ID_A, 0, 60);
  assert.deepEqual(blobs.recordChunk(ID_A, 0, 60), { id: ID_A, index: 0, size: 60 });
  rejects(() => blobs.authorizeChunk(ID_A, 2, 10), 400, "invalid_index");
  rejects(() => blobs.authorizeChunk(ID_A, -1, 10), 400, "invalid_index");
  rejects(() => blobs.authorizeChunk(ID_A, 1, 0), 400, "invalid_length");
  rejects(() => blobs.authorizeChunk(ID_A, 1, CHUNK_BYTES + 1), 413, "chunk_too_large");
  // 60 + 41 > 100: more than was reserved.
  rejects(() => blobs.authorizeChunk(ID_A, 1, 41), 400, "chunk_exceeds_blob");
  rejects(() => blobs.authorizeChunk(ID_B, 0, 1), 404, "blob_not_found");
  rejects(() => blobs.authorizeChunk("nope", 0, 1), 400, "invalid_blob_id");

  // Uploading index 0 again replaces it, so its old size does not count.
  blobs.authorizeChunk(ID_A, 0, 90);
  blobs.recordChunk(ID_A, 0, 90);
  assert.deepEqual(blobs.get(ID_A).chunks, [0]);

  // A concurrent upload that no longer fits when it is recorded is forgotten.
  blobs.recordChunk(ID_A, 1, 10);
  rejects(() => blobs.recordChunk(ID_A, 0, 95), 400, "chunk_exceeds_blob");
  assert.deepEqual(blobs.get(ID_A).chunks, [1]);
});

test("commit needs every chunk and the reserved size, and is idempotent", () => {
  const { blobs, now } = setup();
  blobs.reserve({ id: ID_A, size: 100, chunkCount: 3 });
  blobs.recordChunk(ID_A, 1, 50);
  assert.throws(() => blobs.commit(ID_A), (e: unknown) =>
    e instanceof HttpError && e.status === 409 && e.code === "blob_incomplete" && JSON.stringify(e.details) === JSON.stringify({ missing: [0, 2] }));
  blobs.recordChunk(ID_A, 0, 20);
  blobs.recordChunk(ID_A, 2, 20);
  // Every chunk is there but they hold 90 bytes, not 100.
  assert.throws(() => blobs.commit(ID_A), (e: unknown) =>
    e instanceof HttpError && e.code === "blob_incomplete" && JSON.stringify(e.details) === JSON.stringify({ missing: [] }));
  blobs.recordChunk(ID_A, 2, 30);

  const committed = blobs.commit(ID_A);
  assert.equal(committed.state, "committed");
  assert.equal(committed.committedAt, now());
  assert.deepEqual(committed.chunks, [0, 1, 2]);
  assert.deepEqual(blobs.commit(ID_A), committed);
  rejects(() => blobs.authorizeChunk(ID_A, 0, 20), 409, "blob_committed");
  rejects(() => blobs.recordChunk(ID_A, 0, 20), 409, "blob_committed");
  rejects(() => blobs.commit(ID_B), 404, "blob_not_found");
});

test("only committed chunks can be read, also from the trash", () => {
  const { blobs } = setup();
  blobs.reserve({ id: ID_A, size: 10, chunkCount: 1 });
  blobs.recordChunk(ID_A, 0, 10);
  rejects(() => blobs.readChunk(ID_A, 0), 404, "blob_not_found");
  blobs.commit(ID_A);
  assert.deepEqual(blobs.readChunk(ID_A, 0), { size: 10 });
  rejects(() => blobs.readChunk(ID_A, 1), 404, "chunk_not_found");
  blobs.remove(ID_A);
  assert.deepEqual(blobs.readChunk(ID_A, 0), { size: 10 });
});

test("deleting a committed blob moves it to the trash, still counted; restore takes it back", () => {
  const { store, blobs, now } = setup();
  upload(blobs, ID_A, [40, 60]);
  const deleted = blobs.remove(ID_A);
  assert.equal(deleted.deletedAt, now());
  assert.equal(deleted.state, "committed");
  assert.deepEqual(blobs.remove(ID_A), deleted, "idempotent");
  assert.equal(store.info().usedBytes, 100);

  assert.equal(blobs.restore(ID_A).deletedAt, null);
  assert.equal(blobs.restore(ID_A).deletedAt, null, "idempotent");
  rejects(() => blobs.remove(ID_B), 404, "blob_not_found");
  rejects(() => blobs.restore(ID_B), 404, "blob_not_found");
});

test("deleting a pending blob marks it purging; purged() releases its quota", () => {
  const { store, blobs } = setup();
  blobs.reserve({ id: ID_A, size: 100, chunkCount: 2 });
  blobs.recordChunk(ID_A, 0, 50);
  assert.equal(blobs.remove(ID_A).state, "purging");
  assert.equal(blobs.remove(ID_A).state, "purging", "idempotent");
  rejects(() => blobs.authorizeChunk(ID_A, 1, 50), 404, "blob_not_found");
  rejects(() => blobs.restore(ID_A), 404, "blob_not_found");
  rejects(() => blobs.commit(ID_A), 404, "blob_not_found");
  rejects(() => blobs.reserve({ id: ID_A, size: 100, chunkCount: 2 }), 409, "blob_exists");
  assert.equal(store.info().usedBytes, 100, "counted until purged");

  assert.deepEqual(blobs.purged([ID_A]), { purged: 1, bytes: 100 });
  assert.deepEqual(blobs.purged([ID_A]), { purged: 0, bytes: 0 });
  assert.equal(store.info().usedBytes, 0);
  assert.equal(store.info().blobBytes, 0);
  rejects(() => blobs.get(ID_A), 404, "blob_not_found");
  rejects(() => blobs.purged(["nope"]), 400, "invalid_blob_ids");
});

test("blobsDue: trash after 30 days, abandoned uploads after 7, and purged() only drops purging blobs", () => {
  const { store, blobs, advance, now } = setup();
  upload(blobs, ID_A, [10]);
  blobs.remove(ID_A);
  blobs.reserve({ id: ID_B, size: 20, chunkCount: 1 });
  upload(blobs, ID_C, [30]); // committed and not deleted: never due

  assert.deepEqual(blobs.due(now() + 6 * DAY), []);
  assert.deepEqual(blobs.due(now() + 8 * DAY), [ID_B]);
  // An unfinished run's purging ids come back until purged.
  assert.deepEqual(blobs.due(now() + 29 * DAY), [ID_B]);
  assert.deepEqual(blobs.due(now() + 31 * DAY), [ID_A, ID_B]);
  // A committed blob that is not purging is ignored.
  assert.deepEqual(blobs.purged([ID_A, ID_B, ID_C]), { purged: 2, bytes: 30 });
  assert.equal(store.info().usedBytes, 30);
  assert.deepEqual(blobs.list(null, null).blobs.map((b) => b.id), [ID_C]);
  advance(365 * DAY);
  assert.deepEqual(blobs.due(now()), []);
});

test("list pages by id", () => {
  const { blobs } = setup();
  for (const id of [ID_C, ID_A, ID_B]) blobs.reserve({ id, size: 5, chunkCount: 1 });
  const first = blobs.list(null, "2");
  assert.deepEqual(first.blobs.map((b) => b.id), [ID_A, ID_B]);
  assert.equal(first.hasMore, true);
  assert.equal(first.next, ID_B);
  assert.ok(!("chunks" in first.blobs[0]));
  const second = blobs.list(first.next, "2");
  assert.deepEqual(second.blobs.map((b) => b.id), [ID_C]);
  assert.equal(second.hasMore, false);
  assert.deepEqual(blobs.list(ID_C, null), { blobs: [], hasMore: false, next: ID_C });
  rejects(() => blobs.list("bad", null), 400, "invalid_after");
  rejects(() => blobs.list(null, "0"), 400, "invalid_limit");
  rejects(() => blobs.list(null, String(BLOB_LIMITS.maxBlobListLimit + 1)), 400, "invalid_limit");
  rejects(() => blobs.get("x"), 400, "invalid_blob_id");
});

test("a snapshot carries committed blobs; import recreates the missing ones only", () => {
  const { store, blobs, now } = setup();
  upload(blobs, ID_A, [40, 60]);
  upload(blobs, ID_B, [7]);
  blobs.remove(ID_B);
  blobs.reserve({ id: ID_C, size: 9, chunkCount: 1 }); // pending: not part of a backup
  const snapshot = store.exportSnapshot();
  assert.deepEqual(snapshot.blobs?.map((b) => [b.id, b.chunkSizes, b.deletedAt !== null]), [[ID_A, [40, 60], false], [ID_B, [7], true]]);

  // ID_B is restored from the trash meanwhile, ID_A is lost (purged).
  blobs.restore(ID_B);
  blobs.remove(ID_A);
  assert.deepEqual(blobs.due(now() + 31 * DAY), [ID_A, ID_C]);
  blobs.purged([ID_A, ID_C]);
  assert.equal(store.info().usedBytes, 7);

  assert.deepEqual(store.importSnapshot(snapshot), { restored: 0, restoredBlobs: 1 });
  const a = blobs.get(ID_A);
  assert.deepEqual([a.state, a.chunks, a.deletedAt], ["committed", [0, 1], null]);
  assert.equal(blobs.get(ID_B).deletedAt, null, "an existing row is not touched");
  assert.equal(store.info().usedBytes, 107);
  assert.equal(store.info().blobBytes, 107);
  assert.deepEqual(blobs.readChunk(ID_A, 1), { size: 60 });

  // Snapshots from before blobs still import; broken blob entries reject the whole snapshot.
  const { blobs: _omitted, ...old } = snapshot;
  assert.deepEqual(store.importSnapshot(old), { restored: 0, restoredBlobs: 0 });
  const broken = { ...snapshot, blobs: [{ ...snapshot.blobs![0], chunkSizes: [40] }] };
  rejects(() => store.importSnapshot(broken), 400, "invalid_snapshot");
});

test("accounts created before blobs get the tables on first use", () => {
  const sql = nodeSql();
  const accountId = newAccountId();
  new AccountStore(sql).init(accountId, "old");
  sql.run("DROP TABLE blob_chunks");
  sql.run("DROP TABLE blobs");
  const store = new AccountStore(sql);
  assert.equal(store.info().blobBytes, 0);
  assert.equal(store.blobs.reserve({ id: ID_A, size: 1, chunkCount: 1 }).created, true);
});

test("a read-only token can read blobs but not write them (scope check at authenticate)", async () => {
  const { store } = setup();
  const reader = await store.createToken({ name: "viewer", scopes: ["sync:read"] });
  const hash = await hashToken(reader.token);
  assert.equal(store.authenticate(hash, "sync:read").name, "viewer");
  rejects(() => store.authenticate(hash, "sync:write"), 403, "insufficient_scope");
});

test("R2 keys", () => {
  assert.equal(blobKey("AAAAAAAAAAAAAAAA", ID_A, 12), `blobs/AAAAAAAAAAAAAAAA/${ID_A}/12`);
});
